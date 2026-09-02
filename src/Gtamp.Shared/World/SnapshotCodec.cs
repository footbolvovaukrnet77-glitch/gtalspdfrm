using System;
using System.Collections.Generic;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.World
{
    /// <summary>Everything the sender needs to remember about a snapshot it just wrote.</summary>
    public sealed class SnapshotWriteResult
    {
        public uint SnapshotId { get; set; }

        public uint BaselineId { get; set; }

        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>Entity states actually written, as clones the view can hold on to.</summary>
        public Dictionary<EntityId, NetEntity> WrittenStates { get; } = new Dictionary<EntityId, NetEntity>();

        public List<EntityId> RemovedIds { get; } = new List<EntityId>();

        public int NewEntityCount { get; set; }

        public int DeltaEntityCount { get; set; }

        /// <summary>Entities that had changes but did not fit in the byte budget; they go out next snapshot.</summary>
        public int DeferredCount { get; set; }

        /// <summary>Whether <see cref="ResultingView"/> holds every entity replicated to this client.</summary>
        public bool DescribesWholeWorld { get; set; }

        public bool EnvironmentIncluded { get; set; }

        /// <summary>The view the client will hold once it acknowledges this snapshot.</summary>
        public EntitySnapshotView ResultingView { get; set; } = EntitySnapshotView.Empty;
    }

    public sealed class SnapshotHeader
    {
        public uint SnapshotId { get; set; }

        public uint BaselineId { get; set; }

        public uint Tick { get; set; }

        public double ServerTime { get; set; }

        /// <summary>
        /// The sequence number of the last client state update this snapshot's
        /// contents take into account, for the client this snapshot was written for.
        /// <para>
        /// It is what lets the receiving client tell "the server rejected what I told
        /// it" from "the server had not heard me yet when it wrote this". Without it
        /// the two are indistinguishable, and correcting on the second undoes changes
        /// the server has in fact accepted.
        /// </para>
        /// </summary>
        public uint AcknowledgedClientUpdate { get; set; }

        public List<EntityId> CreatedIds { get; } = new List<EntityId>();

        public List<EntityId> UpdatedIds { get; } = new List<EntityId>();

        public List<EntityId> RemovedIds { get; } = new List<EntityId>();

        public bool IsFullSnapshot => BaselineId == 0;

        /// <summary>
        /// Whether the view this snapshot produces holds the whole replicated world.
        /// <para>
        /// False when the byte budget left entities out, here or in any earlier
        /// snapshot whose gap this one did not close. They are not lost and they are
        /// not removed — they go out in a following snapshot. A receiver must not read
        /// absence from an incomplete snapshot as "this entity is gone".
        /// </para>
        /// </summary>
        public bool DescribesWholeWorld { get; set; } = true;
    }

    public sealed class SnapshotApplyResult
    {
        public SnapshotHeader Header { get; set; } = new SnapshotHeader();

        public EntitySnapshotView View { get; set; } = EntitySnapshotView.Empty;
    }

    /// <summary>
    /// Wire format of the world snapshot message.
    /// <code>
    /// varuint snapshotId | varuint baselineId | varuint tick | f64 serverTime
    /// u8 flags (bit0 = environment present, bit1 = view holds the whole world) | [environment]
    /// varuint removedCount | varuint removedId*
    /// varuint entityCount
    ///   varuint entityId | u8 entryFlags (bit0 = full state) | [u8 typeId] | fields
    /// </code>
    /// baselineId 0 means "written against nothing": a full snapshot.
    /// </summary>
    public static class SnapshotCodec
    {
        private const byte FlagEnvironmentPresent = 0x01;

        /// <summary>
        /// Set when the view the receiver ends up with holds every entity the server
        /// is replicating to it. Clear means "some are still on their way" — never
        /// "the rest are gone".
        /// </summary>
        private const byte FlagWholeWorld = 0x02;
        private const byte EntryFlagFullState = 0x01;

        /// <summary>
        /// Writes one snapshot for one client.
        /// </summary>
        /// <param name="world">Authoritative world state.</param>
        /// <param name="baseline">View the client is known to hold; <see cref="EntitySnapshotView.Empty"/> forces a full snapshot.</param>
        /// <param name="registry">Entity type table.</param>
        /// <param name="order">Candidate entities, most important first. Entities omitted here simply keep their baseline state on the client.</param>
        /// <param name="visible">
        /// Whether an entity should be replicated to this client at all. Null means
        /// everything is. This filters <em>replication</em> and nothing else — the
        /// entity stays in the world, and an entity that stops being visible is
        /// reported as removed rather than left frozen at its last known state.
        /// </param>
        /// <param name="snapshotId">Id assigned to this snapshot; must be non-zero and increasing.</param>
        /// <param name="byteBudget">Hard cap on the produced payload.</param>
        public static SnapshotWriteResult Write(
            WorldState world,
            EntitySnapshotView baseline,
            EntityRegistry registry,
            IReadOnlyList<NetEntity> order,
            uint snapshotId,
            int byteBudget,
            uint acknowledgedClientUpdate = 0,
            Func<NetEntity, bool>? visible = null)
        {
            if (snapshotId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(snapshotId), "Snapshot id 0 is reserved for 'no baseline'.");
            }

            uint baselineId = baseline.SnapshotId;
            var result = new SnapshotWriteResult { SnapshotId = snapshotId, BaselineId = baselineId };

            foreach (EntityId id in baseline.Ids)
            {
                // Gone from the world, or no longer visible to this client. Both have
                // to be reported: an entity dropped from the candidate list without
                // being removed keeps its baseline copy on the client for ever, frozen
                // at whatever it was doing when it left view.
                if (!world.Contains(id))
                {
                    result.RemovedIds.Add(id);
                    continue;
                }

                if (visible != null && world.TryGet(id, out NetEntity existing) && !visible(existing))
                {
                    result.RemovedIds.Add(id);
                }
            }

            bool environmentChanged = baselineId == 0 || !baseline.Environment.ValueEquals(world.Environment);

            var writer = new NetWriter(Math.Min(Math.Max(byteBudget, 64), 2048));
            writer.WriteVarUInt(snapshotId);
            writer.WriteVarUInt(baselineId);
            writer.WriteVarUInt(world.Tick);
            writer.WriteDouble(world.ServerTime);
            writer.WriteVarUInt(acknowledgedClientUpdate);
            // Whether the world fits is only known once the budget runs out, so the
            // slot is written now and patched below rather than reordering the header.
            int flagsOffset = writer.Length;
            writer.WriteByte(environmentChanged ? FlagEnvironmentPresent : (byte)0);
            if (environmentChanged)
            {
                WriteEnvironment(writer, world.Environment);
                result.EnvironmentIncluded = true;
            }

            writer.WriteVarUInt((uint)result.RemovedIds.Count);
            foreach (EntityId id in result.RemovedIds)
            {
                writer.WriteVarUInt(id.Value);
            }

            // The entity count is only known once the budget runs out, so entries are
            // staged in a second buffer and the count is prefixed afterwards.
            var body = new NetWriter(Math.Min(Math.Max(byteBudget, 64), 2048));
            int entityCount = 0;

            foreach (NetEntity entity in order)
            {
                INetEntitySerializer serializer = registry.Get((byte)entity.Type);
                bool isNew = !baseline.TryGet(entity.Id, out NetEntity baselineEntity);

                if (!isNew && !serializer.HasChanges(baselineEntity, entity))
                {
                    continue;
                }

                var entry = new NetWriter(256);
                entry.WriteVarUInt(entity.Id.Value);
                entry.WriteByte(isNew ? EntryFlagFullState : (byte)0);
                if (isNew)
                {
                    entry.WriteByte((byte)entity.Type);
                    serializer.WriteFull(entry, entity);
                }
                else
                {
                    serializer.WriteDelta(entry, baselineEntity, entity);
                }

                // 5 bytes of slack covers the worst-case varint entity-count prefix.
                if (writer.Length + body.Length + entry.Length + 5 > byteBudget)
                {
                    result.DeferredCount++;
                    continue;
                }

                body.WriteBytes(entry.Buffer, 0, entry.Length);
                entityCount++;
                if (isNew)
                {
                    result.NewEntityCount++;
                }
                else
                {
                    result.DeltaEntityCount++;
                }

                result.WrittenStates[entity.Id] = entity.Clone();
            }

            // Completeness is a property of the world the receiver ends up holding,
            // not of this one packet: a snapshot that deferred nothing still leaves
            // the receiver short if its baseline was already missing entities. So it
            // is counted against the world rather than inferred from DeferredCount,
            // which is what lets it recover once the backlog has gone out.
            int replicated = 0;
            foreach (NetEntity entity in world.Entities)
            {
                if (visible == null || visible(entity))
                {
                    replicated++;
                }
            }

            int carried = baseline.Count - result.RemovedIds.Count;
            foreach (EntityId id in result.WrittenStates.Keys)
            {
                if (!baseline.Contains(id))
                {
                    carried++;
                }
            }

            bool wholeWorld = carried >= replicated;
            if (wholeWorld)
            {
                writer.Buffer[flagsOffset] |= FlagWholeWorld;
            }

            writer.WriteVarUInt((uint)entityCount);
            writer.WriteBytes(body.Buffer, 0, body.Length);
            result.Payload = writer.ToArray();
            result.DescribesWholeWorld = wholeWorld;
            result.ResultingView = baseline.Derive(
                snapshotId,
                world.Tick,
                world.ServerTime,
                result.WrittenStates,
                result.RemovedIds,
                world.Environment.Clone(),
                complete: wholeWorld);

            return result;
        }

        /// <summary>Cheap peek at the ids so the receiver can locate the baseline view before decoding.</summary>
        public static void ReadIds(byte[] payload, out uint snapshotId, out uint baselineId)
        {
            var reader = new NetReader(payload);
            snapshotId = reader.ReadVarUInt();
            baselineId = reader.ReadVarUInt();
        }

        /// <summary>
        /// Decodes a snapshot against the baseline view it names, producing the new view.
        /// The caller must pass the view whose <see cref="EntitySnapshotView.SnapshotId"/>
        /// equals the payload's baselineId, or <see cref="EntitySnapshotView.Empty"/> for a
        /// full snapshot; anything else is rejected instead of silently desynchronising.
        /// </summary>
        public static SnapshotApplyResult Apply(byte[] payload, EntitySnapshotView baseline, EntityRegistry registry)
        {
            var reader = new NetReader(payload);
            var header = new SnapshotHeader
            {
                SnapshotId = reader.ReadVarUInt(),
                BaselineId = reader.ReadVarUInt(),
                Tick = reader.ReadVarUInt(),
                ServerTime = reader.ReadDouble(),
                AcknowledgedClientUpdate = reader.ReadVarUInt(),
            };

            if (baseline.SnapshotId != header.BaselineId)
            {
                throw new NetSerializationException(
                    $"Snapshot {header.SnapshotId} was written against baseline {header.BaselineId} " +
                    $"but view {baseline.SnapshotId} was supplied; a resync is required.");
            }

            WorldEnvironment environment = baseline.Environment.Clone();
            byte flags = reader.ReadByte();
            header.DescribesWholeWorld = (flags & FlagWholeWorld) != 0;
            if ((flags & FlagEnvironmentPresent) != 0)
            {
                ReadEnvironment(reader, environment);
            }

            uint removedCount = reader.ReadVarUInt();
            for (uint i = 0; i < removedCount; i++)
            {
                header.RemovedIds.Add(new EntityId(reader.ReadVarUInt()));
            }

            var changed = new Dictionary<EntityId, NetEntity>();
            uint entityCount = reader.ReadVarUInt();
            for (uint i = 0; i < entityCount; i++)
            {
                var id = new EntityId(reader.ReadVarUInt());
                byte entryFlags = reader.ReadByte();
                bool full = (entryFlags & EntryFlagFullState) != 0;

                if (full)
                {
                    byte typeId = reader.ReadByte();
                    INetEntitySerializer serializer = ResolveSerializer(registry, typeId, id);
                    NetEntity entity = serializer.Create(id);
                    serializer.ReadFull(reader, entity);
                    changed[id] = entity;
                    header.CreatedIds.Add(id);
                }
                else
                {
                    if (!baseline.TryGet(id, out NetEntity baselineEntity))
                    {
                        throw new NetSerializationException(
                            $"Delta for entity {id} which is absent from baseline {header.BaselineId}; a resync is required.");
                    }

                    NetEntity entity = baselineEntity.Clone();
                    ResolveSerializer(registry, (byte)entity.Type, id).ReadDelta(reader, entity);
                    changed[id] = entity;
                    header.UpdatedIds.Add(id);
                }
            }

            return new SnapshotApplyResult
            {
                Header = header,
                View = baseline.Derive(
                    header.SnapshotId,
                    header.Tick,
                    header.ServerTime,
                    changed,
                    header.RemovedIds,
                    environment,
                    complete: header.DescribesWholeWorld),
            };
        }

        private static INetEntitySerializer ResolveSerializer(EntityRegistry registry, byte typeId, EntityId id)
        {
            if (registry.TryGet(typeId, out INetEntitySerializer serializer))
            {
                return serializer;
            }

            throw new NetSerializationException(
                $"Entity {id} uses type id {typeId}, which this build has no serializer for. " +
                "The server is running a mod that is not installed here (see /diagnostics).");
        }

        private static void WriteEnvironment(NetWriter writer, WorldEnvironment environment)
        {
            writer.WriteVarUInt((uint)environment.TimeOfDaySeconds);
            writer.WriteSingle(environment.ClockScale);
            writer.WriteUInt32(environment.WeatherHash);
            writer.WriteUInt32(environment.NextWeatherHash);
            writer.WriteUnit(environment.WeatherTransition);
            writer.WriteSingle(environment.WindSpeed);
            writer.WriteAngleDegrees(environment.WindDirection);
            writer.WriteBool(environment.Blackout);
        }

        private static void ReadEnvironment(NetReader reader, WorldEnvironment environment)
        {
            environment.TimeOfDaySeconds = (int)reader.ReadVarUInt();
            environment.ClockScale = reader.ReadSingle();
            environment.WeatherHash = reader.ReadUInt32();
            environment.NextWeatherHash = reader.ReadUInt32();
            environment.WeatherTransition = reader.ReadUnit();
            environment.WindSpeed = reader.ReadSingle();
            environment.WindDirection = reader.ReadAngleDegrees();
            environment.Blackout = reader.ReadBool();
        }
    }
}
