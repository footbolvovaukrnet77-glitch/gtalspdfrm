using System;
using System.Collections.Generic;
using Gtamp.Server.Core;
using Gtamp.Server.Players;
using Gtamp.Server.World;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.Security;

namespace Gtamp.Server.Entities
{
    /// <summary>
    /// Owns the lifecycle and ownership of every non-player entity: adopting the
    /// things clients create, accepting the state their owners report, and moving
    /// ownership when the owner leaves or wanders off.
    /// </summary>
    public sealed class NetworkedEntityManager
    {
        private readonly ServerWorld _world;
        private readonly LogBus _log;
        private readonly ServerConfig _config;
        private readonly List<NetEntity> _scratch = new List<NetEntity>();

        private double _lastMigrationCheck;

        public NetworkedEntityManager(ServerWorld world, ServerConfig config, LogBus log)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public int SpawnsAccepted { get; private set; }

        public int SpawnsRejected { get; private set; }

        public int OwnershipMigrations { get; private set; }

        /// <summary>Adopts an entity a client created locally, giving it a network identity.</summary>
        public EntityEventMessage HandleSpawnRequest(
            PlayerSession session, EntitySpawnRequestMessage request, PlayerRegistry players)
        {
            if (!IsSpawnableType(request.Type))
            {
                SpawnsRejected++;
                return Reject(request, $"{request.Type} cannot be spawned by a client");
            }

            if (CountOwnedBy(session.PlayerId) >= _config.MaxEntitiesPerPlayer)
            {
                SpawnsRejected++;
                return Reject(
                    request,
                    $"you already own {_config.MaxEntitiesPerPlayer} entities, which is the per-player limit");
            }

            if (!IsInsideWorld(request.Position))
            {
                SpawnsRejected++;
                return Reject(request, $"spawn position {request.Position} is outside the world");
            }

            if (!_world.Registry.TryGet((byte)request.Type, out INetEntitySerializer serializer))
            {
                SpawnsRejected++;
                return Reject(request, $"this server has no serializer for {request.Type}");
            }

            NetEntity entity = serializer.Create(_world.AllocateEntityId());

            if (request.State.Length > 0)
            {
                try
                {
                    serializer.ReadFull(new NetReader(request.State), entity);
                }
                catch (NetSerializationException exception)
                {
                    SpawnsRejected++;
                    return Reject(request, "the supplied state could not be decoded: " + exception.Message);
                }
            }

            entity.Position = request.Position;
            entity.Heading = request.Heading;
            entity.Dimension = request.Dimension;
            entity.OwnerId = session.PlayerId;

            if (entity is VehicleEntity vehicle && vehicle.ModelHash == 0)
            {
                vehicle.ModelHash = request.ModelHash;
            }

            _world.Spawn(entity);
            SpawnsAccepted++;

            _log.Info(
                LogCategory.Entity,
                $"{session.Name} spawned {entity.Type} {entity.Id} (model 0x{request.ModelHash:X8}).",
                $"entity:{entity.Id.Value}");

            return new EntityEventMessage
            {
                Kind = EntityEventKind.SpawnAccepted,
                EntityId = entity.Id,
                RequestTag = request.RequestTag,
            };
        }

        /// <summary>
        /// Applies a state update from an entity's owner.
        /// <para>
        /// The payload is decoded into a fresh instance rather than into the live
        /// entity, so a validation failure leaves the world untouched. Decoding
        /// straight into the entity and then rejecting would leave it half-written
        /// with attacker-chosen values, which is worse than the update the check was
        /// meant to stop.
        /// </para>
        /// </summary>
        public bool HandleOwnedUpdate(
            PlayerSession session, OwnedEntityUpdateMessage update, OwnedEntityValidator validator, double now)
        {
            if (!_world.TryGet(update.EntityId, out NetEntity existing))
            {
                return false;
            }

            if (existing.OwnerId != session.PlayerId)
            {
                _log.Debug(
                    LogCategory.Security,
                    $"{session} reported state for {update.EntityId}, which is owned by player {existing.OwnerId}.");
                return false;
            }

            if (!_world.Registry.TryGet((byte)existing.Type, out INetEntitySerializer serializer))
            {
                return false;
            }

            NetEntity candidate = serializer.Create(existing.Id);
            try
            {
                serializer.ReadFull(new NetReader(update.State), candidate);
            }
            catch (NetSerializationException exception)
            {
                _log.Warning(
                    LogCategory.Security,
                    $"{session} sent an undecodable update for {update.EntityId}: {exception.Message}");
                return false;
            }

            ValidationOutcome outcome = validator.Validate(existing, candidate, now);
            if (!outcome.Accepted)
            {
                foreach (ViolationRecord violation in outcome.Violations)
                {
                    _log.Warning(LogCategory.Security, $"{session}: {violation.Kind} — {violation.Detail}");
                }

                return false;
            }

            // Ownership and identity are the server's, never the client's.
            candidate.OwnerId = existing.OwnerId;
            candidate.NetworkVersion = existing.NetworkVersion + 1;
            candidate.LastUpdateTick = _world.Tick;

            _world.State.AddOrReplace(candidate);
            return true;
        }

        public EntityEventMessage? HandleRelease(PlayerSession session, EntityReleaseRequestMessage request, PlayerRegistry players)
        {
            if (!_world.TryGet(request.EntityId, out NetEntity entity))
            {
                return null;
            }

            if (entity.OwnerId != session.PlayerId && !session.IsAdmin)
            {
                _log.Debug(
                    LogCategory.Security,
                    $"{session} tried to release {request.EntityId}, owned by player {entity.OwnerId}.");
                return null;
            }

            if (request.Kind == EntityReleaseKind.Destroy)
            {
                _world.Destroy(entity.Id);
                return new EntityEventMessage { Kind = EntityEventKind.Destroyed, EntityId = entity.Id };
            }

            entity.OwnerId = 0;
            _world.Touch(entity);
            ReassignOwner(entity, players, excludePlayerId: session.PlayerId);
            return new EntityEventMessage { Kind = EntityEventKind.OwnershipRevoked, EntityId = entity.Id };
        }

        /// <summary>
        /// Hands every entity a departing player owned to whoever is closest, or to the
        /// server if nobody is near.
        /// <para>
        /// Server-owned entities keep existing and keep their state; they simply stop
        /// being simulated by anyone. A parked car stays parked, which is right — the
        /// alternative, deleting it when its owner leaves, would make the world
        /// evaporate around the players who stayed.
        /// </para>
        /// </summary>
        public void ReleaseAllOwnedBy(uint playerId, PlayerRegistry players)
        {
            _scratch.Clear();
            foreach (NetEntity entity in _world.State.Entities)
            {
                if (entity.OwnerId == playerId && entity.Type != EntityType.Player)
                {
                    _scratch.Add(entity);
                }
            }

            foreach (NetEntity entity in _scratch)
            {
                entity.OwnerId = 0;
                _world.Touch(entity);
                ReassignOwner(entity, players, excludePlayerId: playerId);
            }

            if (_scratch.Count > 0)
            {
                _log.Info(LogCategory.Entity, $"Reassigned {_scratch.Count} entity(ies) from departing player {playerId}.");
            }
        }

        /// <summary>Periodically moves ownership to whoever is best placed to simulate an entity.</summary>
        public void UpdateOwnership(PlayerRegistry players, double now)
        {
            if (now - _lastMigrationCheck < _config.OwnershipCheckIntervalSeconds)
            {
                return;
            }

            _lastMigrationCheck = now;

            foreach (NetEntity entity in _world.State.Entities)
            {
                if (entity.Type == EntityType.Player)
                {
                    continue;
                }

                MigrateIfBetterOwnerExists(entity, players);
            }
        }

        private void MigrateIfBetterOwnerExists(NetEntity entity, PlayerRegistry players)
        {
            float ownerDistance = float.MaxValue;
            if (entity.OwnerId != 0 && players.TryGetByPlayerId(entity.OwnerId, out PlayerSession owner))
            {
                PlayerEntity? ownerEntity = _world.GetPlayer(owner.EntityId);
                if (ownerEntity != null)
                {
                    ownerDistance = NetVector3.Distance(ownerEntity.Position, entity.Position);
                }
            }

            // A close-enough owner keeps the entity. Migrating on every small change
            // would thrash ownership between two players standing near each other.
            if (ownerDistance <= _config.OwnershipHandoffDistance)
            {
                return;
            }

            PlayerSession? best = FindNearestPlayer(entity.Position, players, out float bestDistance);
            if (best == null || bestDistance > _config.OwnershipHandoffDistance)
            {
                if (entity.OwnerId != 0)
                {
                    entity.OwnerId = 0;
                    _world.Touch(entity);
                    OwnershipMigrations++;
                    _log.Debug(LogCategory.Entity, $"{entity.Type} {entity.Id} handed back to the server; nobody is near it.");
                }

                return;
            }

            if (best.PlayerId == entity.OwnerId)
            {
                return;
            }

            entity.OwnerId = best.PlayerId;
            _world.Touch(entity);
            OwnershipMigrations++;

            _log.Debug(
                LogCategory.Entity,
                $"{entity.Type} {entity.Id} is now simulated by {best.Name} ({bestDistance:0.#} m away).");

            OwnershipGranted?.Invoke(best, entity.Id);
        }

        private void ReassignOwner(NetEntity entity, PlayerRegistry players, uint excludePlayerId)
        {
            PlayerSession? best = FindNearestPlayer(entity.Position, players, out float distance, excludePlayerId);
            if (best == null || distance > _config.OwnershipHandoffDistance)
            {
                return;
            }

            entity.OwnerId = best.PlayerId;
            _world.Touch(entity);
            OwnershipMigrations++;
            OwnershipGranted?.Invoke(best, entity.Id);
        }

        private PlayerSession? FindNearestPlayer(
            NetVector3 position, PlayerRegistry players, out float distance, uint excludePlayerId = 0)
        {
            PlayerSession? best = null;
            distance = float.MaxValue;

            foreach (PlayerSession session in players.Sessions)
            {
                if (session.PendingRemoval || session.PlayerId == excludePlayerId)
                {
                    continue;
                }

                PlayerEntity? entity = _world.GetPlayer(session.EntityId);
                if (entity == null)
                {
                    continue;
                }

                float candidate = NetVector3.Distance(entity.Position, position);
                if (candidate < distance)
                {
                    distance = candidate;
                    best = session;
                }
            }

            return best;
        }

        /// <summary>Raised when a player is handed simulation of an entity, so the server can tell them.</summary>
        public event Action<PlayerSession, EntityId>? OwnershipGranted;

        public int CountOwnedBy(uint playerId)
        {
            int count = 0;
            foreach (NetEntity entity in _world.State.Entities)
            {
                if (entity.OwnerId == playerId && entity.Type != EntityType.Player)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsSpawnableType(EntityType type) =>
            type == EntityType.Vehicle
            || type == EntityType.Ped
            || type == EntityType.Object
            || (byte)type >= (byte)EntityType.ModDefinedFirst;

        private static bool IsInsideWorld(NetVector3 position) =>
            Math.Abs(position.X) <= Quantize.WorldExtentXY
            && Math.Abs(position.Y) <= Quantize.WorldExtentXY
            && Math.Abs(position.Z) <= Quantize.WorldExtentZ;

        private static EntityEventMessage Reject(EntitySpawnRequestMessage request, string detail) =>
            new EntityEventMessage
            {
                Kind = EntityEventKind.SpawnRejected,
                EntityId = EntityId.None,
                RequestTag = request.RequestTag,
                Detail = detail,
            };
    }
}
