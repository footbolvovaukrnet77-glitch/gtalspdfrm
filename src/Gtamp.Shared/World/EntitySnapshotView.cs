using System;
using System.Collections.Generic;
using Gtamp.Shared.Entities;

namespace Gtamp.Shared.World
{
    /// <summary>
    /// An immutable picture of the world as of one snapshot id.
    /// <para>
    /// Delta compression only works if both sides agree on exactly which state a
    /// delta was written against. A single mutable "current world" is not enough:
    /// while an acknowledgement is in flight the server keeps encoding against an
    /// older baseline, so the client must be able to reconstruct that older baseline
    /// too. Both sides therefore keep a short history of views, and a delta always
    /// names the view it was written against.
    /// </para>
    /// <para>
    /// Views share entity references for everything that did not change, so keeping
    /// 64 of them costs one dictionary per snapshot rather than 64 deep copies.
    /// Entities inside a view must be treated as read-only.
    /// </para>
    /// </summary>
    public sealed class EntitySnapshotView
    {
        public static readonly EntitySnapshotView Empty = new EntitySnapshotView(
            0, 0, 0d, new Dictionary<EntityId, NetEntity>(), new WorldEnvironment());

        private readonly Dictionary<EntityId, NetEntity> _entities;

        private EntitySnapshotView(
            uint snapshotId,
            uint tick,
            double serverTime,
            Dictionary<EntityId, NetEntity> entities,
            WorldEnvironment environment)
        {
            SnapshotId = snapshotId;
            Tick = tick;
            ServerTime = serverTime;
            _entities = entities;
            Environment = environment;
        }

        public uint SnapshotId { get; }

        public uint Tick { get; }

        public double ServerTime { get; }

        public WorldEnvironment Environment { get; }

        public int Count => _entities.Count;

        public IEnumerable<NetEntity> Entities => _entities.Values;

        public IEnumerable<EntityId> Ids => _entities.Keys;

        public bool Contains(EntityId id) => _entities.ContainsKey(id);

        public bool TryGet(EntityId id, out NetEntity entity) => _entities.TryGetValue(id, out entity!);

        public NetEntity? GetOrNull(EntityId id) => _entities.TryGetValue(id, out NetEntity? entity) ? entity : null;

        /// <summary>Produces the next view. Unchanged entities are shared, not copied.</summary>
        public EntitySnapshotView Derive(
            uint snapshotId,
            uint tick,
            double serverTime,
            IReadOnlyDictionary<EntityId, NetEntity> changed,
            IReadOnlyCollection<EntityId> removed,
            WorldEnvironment environment)
        {
            var entities = new Dictionary<EntityId, NetEntity>(_entities);
            foreach (EntityId id in removed)
            {
                entities.Remove(id);
            }

            foreach (KeyValuePair<EntityId, NetEntity> pair in changed)
            {
                entities[pair.Key] = pair.Value;
            }

            return new EntitySnapshotView(snapshotId, tick, serverTime, entities, environment);
        }

        internal static EntitySnapshotView FromEntities(
            uint snapshotId,
            uint tick,
            double serverTime,
            Dictionary<EntityId, NetEntity> entities,
            WorldEnvironment environment) =>
            new EntitySnapshotView(snapshotId, tick, serverTime, entities, environment);
    }
}
