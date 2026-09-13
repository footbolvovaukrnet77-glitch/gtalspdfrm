using System;
using System.Collections.Generic;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;

namespace Gtamp.Server.World
{
    /// <summary>
    /// The authoritative world plus the id allocator.
    /// <para>
    /// This class is the one place allowed to add or remove entities, so the
    /// "never cull by distance" invariant has a single enforcement point. There is
    /// deliberately no <c>RemoveDistant</c>-style method anywhere in the server.
    /// </para>
    /// </summary>
    public sealed class ServerWorld
    {
        private readonly LogBus _log;
        private uint _nextEntityId = 1;

        public ServerWorld(EntityRegistry registry, LogBus log)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public EntityRegistry Registry { get; }

        public WorldState State { get; } = new WorldState();

        public uint Tick => State.Tick;

        public int EntityCount => State.Count;

        public EntityId AllocateEntityId() => new EntityId(_nextEntityId++);

        /// <summary>Restores the allocator after loading persisted entities, so ids are never reused.</summary>
        public void ReserveEntityIdsUpTo(uint highestUsedId)
        {
            if (highestUsedId >= _nextEntityId)
            {
                _nextEntityId = highestUsedId + 1;
            }
        }

        public T Spawn<T>(T entity)
            where T : NetEntity
        {
            State.Add(entity);
            entity.LastUpdateTick = State.Tick;
            entity.NetworkVersion++;
            _log.Debug(LogCategory.Entity, $"Spawned {entity.Type} {entity.Id}", $"entity:{entity.Id.Value}");
            return entity;
        }

        public bool Destroy(EntityId id)
        {
            if (!State.Remove(id))
            {
                return false;
            }

            _log.Debug(LogCategory.Entity, $"Destroyed entity {id}", $"entity:{id.Value}");
            return true;
        }

        public bool TryGet(EntityId id, out NetEntity entity) => State.TryGet(id, out entity);

        public PlayerEntity? GetPlayer(EntityId id) => State.Get<PlayerEntity>(id);

        public IEnumerable<PlayerEntity> Players => State.OfType<PlayerEntity>();

        /// <summary>Marks an entity as mutated this tick. Drives NetworkVersion in the entity inspector.</summary>
        public void Touch(NetEntity entity)
        {
            entity.NetworkVersion++;
            entity.LastUpdateTick = State.Tick;
        }

        public void AdvanceTick(double deltaSeconds)
        {
            State.Tick++;
            State.ServerTime += deltaSeconds;
            State.Environment.AdvanceClock(deltaSeconds);
        }
    }
}
