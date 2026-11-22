using System;
using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;

namespace Gtamp.Server.Replication
{
    /// <summary>
    /// Per-client replication bookkeeping: which snapshot the client last confirmed,
    /// the history needed to encode against it, and the staleness counters that keep
    /// distant entities flowing at a reduced rate instead of being dropped.
    /// </summary>
    public sealed class ClientReplicationState
    {
        private readonly Dictionary<EntityId, uint> _lastSentSnapshot = new Dictionary<EntityId, uint>();

        public SnapshotHistory History { get; } = new SnapshotHistory();

        /// <summary>Snapshot id the client confirmed applying. 0 until the first ack arrives.</summary>
        public uint AcknowledgedSnapshotId { get; private set; }

        public uint NextSnapshotId { get; private set; } = 1;

        public EntitySnapshotView Baseline { get; private set; } = EntitySnapshotView.Empty;

        /// <summary>Set when the client asked for a resync; the next snapshot is a full one.</summary>
        public bool ResyncRequested { get; private set; }

        public int SnapshotsSent { get; private set; }

        public int FullSnapshotsSent { get; private set; }

        public uint AllocateSnapshotId() => NextSnapshotId++;

        public void RecordSent(SnapshotWriteResult result, uint tick)
        {
            History.Store(result.ResultingView);
            SnapshotsSent++;
            if (result.BaselineId == 0)
            {
                FullSnapshotsSent++;
            }

            foreach (EntityId id in result.WrittenStates.Keys)
            {
                _lastSentSnapshot[id] = tick;
            }

            foreach (EntityId id in result.RemovedIds)
            {
                _lastSentSnapshot.Remove(id);
            }

            ResyncRequested = false;
        }

        public void Acknowledge(uint snapshotId)
        {
            if (snapshotId <= AcknowledgedSnapshotId)
            {
                return;
            }

            if (!History.TryGet(snapshotId, out EntitySnapshotView view))
            {
                // The client acknowledged a snapshot we have already dropped from
                // history. Fall back to a full snapshot rather than guessing.
                RequestResync();
                return;
            }

            AcknowledgedSnapshotId = snapshotId;
            Baseline = view;
        }

        public void RequestResync()
        {
            ResyncRequested = true;
            AcknowledgedSnapshotId = 0;
            Baseline = EntitySnapshotView.Empty;
            History.Clear();
        }

        public uint LastSentTick(EntityId id) => _lastSentSnapshot.TryGetValue(id, out uint tick) ? tick : 0;

        public void Reset()
        {
            _lastSentSnapshot.Clear();
            History.Clear();
            AcknowledgedSnapshotId = 0;
            Baseline = EntitySnapshotView.Empty;
            ResyncRequested = true;
        }
    }

    /// <summary>
    /// Decides the order in which entities are offered to the snapshot writer.
    /// <para>
    /// <b>This is the only place distance is allowed to matter.</b> Distance changes
    /// priority, and therefore how often a far-away entity is refreshed when the
    /// byte budget is tight. It never removes an entity from the server world and it
    /// never stops an entity being sent eventually — staleness pushes anything that
    /// has been waiting to the front (master prompt sections 28-29).
    /// </para>
    /// </summary>
    public static class ReplicationPriority
    {
        /// <summary>Entities closer than this are always treated as maximum priority.</summary>
        public const float NearDistance = 150f;

        /// <summary>
        /// A dimension is a parallel copy of the world: entities in one are not
        /// replicated to a viewer in another.
        /// <para>
        /// This is a <em>replication</em> filter and nothing more, exactly like
        /// distance. Every entity stays in the server world at every dimension, which
        /// is what <c>WorldStateTests</c> and <c>StressTests</c> assert. Strict
        /// equality rather than "dimension 0 sees everything": a rule with an
        /// exception in it is one that has to be reasoned about at every call site.
        /// </para>
        /// </summary>
        public static bool SharesDimension(NetEntity entity, uint viewerDimension) =>
            entity.Dimension == viewerDimension;

        public static List<NetEntity> Order(
            IEnumerable<NetEntity> entities,
            NetVector3 viewer,
            uint currentTick,
            ClientReplicationState state,
            EntityId excludeId,
            uint viewerDimension = 0)
        {
            var scored = new List<KeyValuePair<double, NetEntity>>();
            foreach (NetEntity entity in entities)
            {
                if (entity.Id == excludeId || !SharesDimension(entity, viewerDimension))
                {
                    continue;
                }

                scored.Add(new KeyValuePair<double, NetEntity>(Score(entity, viewer, currentTick, state), entity));
            }

            // Entity id breaks ties so the order is total. Without it List.Sort is
            // free to return equal-scoring entities in a different order on every
            // call, and two snapshots written against the same baseline would carry
            // different halves of the world for no reason.
            scored.Sort(static (a, b) =>
            {
                int byScore = b.Key.CompareTo(a.Key);
                return byScore != 0 ? byScore : a.Value.Id.Value.CompareTo(b.Value.Id.Value);
            });

            var ordered = new List<NetEntity>(scored.Count);
            foreach (KeyValuePair<double, NetEntity> pair in scored)
            {
                ordered.Add(pair.Value);
            }

            return ordered;
        }

        public static double Score(NetEntity entity, NetVector3 viewer, uint currentTick, ClientReplicationState state)
        {
            uint lastSent = state.LastSentTick(entity.Id);

            // Until the client acknowledges a snapshot, every snapshot is a full one
            // written against nothing, and "what I have already sent" must not enter
            // the score. It used to: an entity written into full snapshot 1 stopped
            // being top priority, so full snapshot 2 — still written against nothing,
            // because the acknowledgement had not had time to arrive — carried the
            // *other* half of the world and described a world the first half was not
            // in. A joining client watched the same cars be created and destroyed
            // once per snapshot until the first ack landed. Ordering while
            // unacknowledged is therefore a pure function of the world, so every
            // full snapshot in that burst carries the same entities and simply
            // repeats itself until one gets through.
            bool converged = state.AcknowledgedSnapshotId != 0;

            // Never sent before: highest possible priority, so a joining client
            // converges on the full world as fast as the budget allows.
            if (converged && lastSent == 0)
            {
                return double.MaxValue;
            }

            float distance = NetVector3.Distance(entity.Position, viewer);
            double proximity = distance <= NearDistance ? 1d : NearDistance / distance;

            // Staleness grows without bound, so a distant entity that has not been
            // refreshed for a while eventually outranks a nearby idle one.
            double staleness = converged && currentTick > lastSent ? currentTick - lastSent : 0d;

            // Players outrank scenery of the same distance.
            double typeWeight = entity.Type == EntityType.Player ? 4d : 1d;

            return (proximity * typeWeight * 1000d) + staleness;
        }
    }
}
