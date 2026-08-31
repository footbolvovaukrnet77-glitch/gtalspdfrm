using System;
using System.Collections.Generic;
using Gtamp.Shared.Entities;

namespace Gtamp.Shared.World
{
    /// <summary>
    /// The authoritative world on the server, and the replicated mirror on the client.
    /// <para>
    /// <b>Invariant (master prompt sections 28 and 60):</b> nothing is ever removed
    /// from this collection because of distance. Entities leave only when they are
    /// genuinely destroyed. Distance influences <em>how often</em> an entity is sent
    /// to a given client, never whether the server keeps knowing about it.
    /// </para>
    /// </summary>
    public sealed class WorldState
    {
        private readonly Dictionary<EntityId, NetEntity> _entities = new Dictionary<EntityId, NetEntity>();

        public uint Tick { get; set; }

        public double ServerTime { get; set; }

        public WorldEnvironment Environment { get; } = new WorldEnvironment();

        public int Count => _entities.Count;

        public IEnumerable<NetEntity> Entities => _entities.Values;

        public IEnumerable<EntityId> Ids => _entities.Keys;

        public void Add(NetEntity entity)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (_entities.ContainsKey(entity.Id))
            {
                throw new InvalidOperationException($"Entity {entity.Id} is already present in the world.");
            }

            _entities[entity.Id] = entity;
        }

        public void AddOrReplace(NetEntity entity) => _entities[entity.Id] = entity;

        public bool Remove(EntityId id) => _entities.Remove(id);

        public bool Contains(EntityId id) => _entities.ContainsKey(id);

        public bool TryGet(EntityId id, out NetEntity entity) => _entities.TryGetValue(id, out entity!);

        public T? Get<T>(EntityId id)
            where T : NetEntity
        {
            return _entities.TryGetValue(id, out NetEntity? entity) ? entity as T : null;
        }

        public IEnumerable<T> OfType<T>()
            where T : NetEntity
        {
            foreach (NetEntity entity in _entities.Values)
            {
                if (entity is T typed)
                {
                    yield return typed;
                }
            }
        }

        public void Clear() => _entities.Clear();
    }
}
