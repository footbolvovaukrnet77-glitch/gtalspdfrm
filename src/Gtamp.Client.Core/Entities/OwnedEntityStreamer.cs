using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.World;

namespace Gtamp.Client.Entities
{
    /// <summary>
    /// Registers the things this client creates with the server, and streams the
    /// state of the ones it owns.
    /// <para>
    /// A client cannot invent an entity id — ids belong to the server, and a client
    /// choosing its own could collide with another player's. So it describes what it
    /// made, correlates the answer by tag, and only then starts reporting state.
    /// </para>
    /// </summary>
    public sealed class OwnedEntityStreamer
    {
        /// <summary>Give up on a spawn request that has not been answered in this long, and ask again.</summary>
        public const double SpawnRequestTimeout = 3.0;

        private readonly IGameBridge _bridge;
        private readonly LogBus _log;
        private readonly EntityRegistry _registry;

        /// <summary>
        /// How long an owned entity may be missing from the replicated view before the
        /// client gives up on it.
        /// <para>
        /// The server's acceptance is reliable and its snapshots are not, so the id
        /// routinely arrives before the entity does. Forgetting on the first miss would
        /// make the client re-request a spawn it already has — and the server, having
        /// no way to tell the retry from a new vehicle, would create a duplicate.
        /// </para>
        /// </summary>
        public const double MissingEntityGrace = 5.0;

        private readonly Dictionary<EntityId, int> _ownedHandles = new Dictionary<EntityId, int>();
        private readonly Dictionary<EntityId, double> _lastSeenInView = new Dictionary<EntityId, double>();
        private readonly Dictionary<uint, PendingSpawn> _pending = new Dictionary<uint, PendingSpawn>();
        private readonly Dictionary<int, EntityId> _handleToEntity = new Dictionary<int, EntityId>();
        private readonly List<EntityId> _removalBuffer = new List<EntityId>();

        private uint _nextRequestTag = 1;
        private double _lastStreamTime;

        public OwnedEntityStreamer(IGameBridge bridge, EntityRegistry registry, LogBus log)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public uint LocalPlayerId { get; set; }

        /// <summary>Sends a message to the server. Injected so this class does not know about the connection.</summary>
        public Action<NetMessageType, byte[], DeliveryMethod>? Send { get; set; }

        public int OwnedCount => _ownedHandles.Count;

        public int PendingSpawnCount => _pending.Count;

        public int SpawnsRequested { get; private set; }

        public int SpawnsRejected { get; private set; }

        /// <summary>Game handle of the entity this client owns, or 0.</summary>
        public bool TryGetHandle(EntityId id, out int handle) => _ownedHandles.TryGetValue(id, out handle);

        /// <summary>
        /// Notices the local player has got into a vehicle the server does not know
        /// about, and asks the server to adopt it.
        /// </summary>
        public void RegisterLocalVehicleIfNeeded(EntitySnapshotView view, double now)
        {
            int handle = _bridge.GetLocalPlayerVehicleHandle();
            if (handle == 0)
            {
                return;
            }

            if (_handleToEntity.ContainsKey(handle))
            {
                return;
            }

            foreach (PendingSpawn pending in _pending.Values)
            {
                if (pending.GameHandle == handle && now - pending.SentAt < SpawnRequestTimeout)
                {
                    return;
                }
            }

            uint model = _bridge.GetVehicleModel(handle);
            if (model == 0)
            {
                return;
            }

            var state = new VehicleEntity(EntityId.None);
            if (!_bridge.TryReadVehicle(handle, state))
            {
                return;
            }

            var writer = new NetWriter(256);
            _registry.Get((byte)EntityType.Vehicle).WriteFull(writer, state);

            uint tag = _nextRequestTag++;
            var request = new EntitySpawnRequestMessage
            {
                Type = EntityType.Vehicle,
                ModelHash = model,
                Position = state.Position,
                Heading = state.Heading,
                Dimension = state.Dimension,
                RequestTag = tag,
                State = writer.ToArray(),
            };

            _pending[tag] = new PendingSpawn(handle, now);
            SpawnsRequested++;
            Send?.Invoke(NetMessageType.EntitySpawnRequest, request.Serialize(), DeliveryMethod.ReliableOrdered);

            _log.Debug(LogCategory.Entity, $"Asked the server to adopt local vehicle handle {handle} (model 0x{model:X8}).");
        }

        /// <summary>Applies the server's answer to a spawn request, or an ownership change.</summary>
        public void HandleEntityEvent(EntityEventMessage message)
        {
            switch (message.Kind)
            {
                case EntityEventKind.SpawnAccepted:
                    if (_pending.TryGetValue(message.RequestTag, out PendingSpawn accepted))
                    {
                        _pending.Remove(message.RequestTag);
                        _ownedHandles[message.EntityId] = accepted.GameHandle;
                        _handleToEntity[accepted.GameHandle] = message.EntityId;
                        _lastSeenInView[message.EntityId] = 0d;
                        _log.Info(
                            LogCategory.Entity,
                            $"The server adopted our vehicle as {message.EntityId}.",
                            $"entity:{message.EntityId.Value}");
                    }

                    break;

                case EntityEventKind.SpawnRejected:
                    if (_pending.TryGetValue(message.RequestTag, out PendingSpawn _))
                    {
                        _pending.Remove(message.RequestTag);
                    }

                    SpawnsRejected++;
                    _log.Warning(LogCategory.Entity, "The server refused our spawn: " + message.Detail);
                    break;

                case EntityEventKind.OwnershipRevoked:
                case EntityEventKind.Destroyed:
                    Forget(message.EntityId);
                    break;

                case EntityEventKind.OwnershipGranted:
                    // The entity is ours to simulate now, but we do not have a game
                    // handle for it: it was created by whoever owned it before. The
                    // handle arrives when the local game entity is matched up, which is
                    // Phase 4 work — until then, ownership of somebody else's vehicle is
                    // accepted and simply not streamed.
                    _log.Debug(LogCategory.Entity, $"We now own {message.EntityId}, but have no local handle for it yet.");
                    break;
            }
        }

        /// <summary>Reports the state of everything this client owns.</summary>
        public void Stream(EntitySnapshotView view, double now, double interval)
        {
            if (now - _lastStreamTime < interval)
            {
                return;
            }

            _lastStreamTime = now;

            _removalBuffer.Clear();
            foreach (KeyValuePair<EntityId, int> pair in _ownedHandles)
            {
                if (!view.TryGet(pair.Key, out NetEntity entity))
                {
                    // Not in the view yet — usually just a snapshot that has not caught
                    // up with the reliable acceptance. Give it a moment before deciding
                    // the entity is really gone.
                    if (!_lastSeenInView.TryGetValue(pair.Key, out double lastSeen))
                    {
                        _lastSeenInView[pair.Key] = now;
                    }
                    else if (now - lastSeen > MissingEntityGrace)
                    {
                        _removalBuffer.Add(pair.Key);
                    }

                    continue;
                }

                _lastSeenInView[pair.Key] = now;

                if (entity.OwnerId != LocalPlayerId)
                {
                    // Ownership moved away while we were driving; stop reporting.
                    _removalBuffer.Add(pair.Key);
                    continue;
                }

                if (entity is not VehicleEntity)
                {
                    continue;
                }

                if (!_bridge.IsRemoteVehicleValid(pair.Value) && _bridge.GetVehicleModel(pair.Value) == 0)
                {
                    _removalBuffer.Add(pair.Key);
                    continue;
                }

                var state = new VehicleEntity(pair.Key);
                if (!_bridge.TryReadVehicle(pair.Value, state))
                {
                    continue;
                }

                var writer = new NetWriter(256);
                _registry.Get((byte)EntityType.Vehicle).WriteFull(writer, state);

                var update = new OwnedEntityUpdateMessage { EntityId = pair.Key, State = writer.ToArray() };
                Send?.Invoke(NetMessageType.OwnedEntityUpdate, update.Serialize(), DeliveryMethod.Unreliable);
            }

            foreach (EntityId id in _removalBuffer)
            {
                Forget(id);
            }
        }

        /// <summary>Drops timed-out spawn requests so a lost reply does not wedge the vehicle forever.</summary>
        public void ExpirePendingSpawns(double now)
        {
            _removalBuffer.Clear();
            var expired = new List<uint>();
            foreach (KeyValuePair<uint, PendingSpawn> pair in _pending)
            {
                if (now - pair.Value.SentAt > SpawnRequestTimeout)
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (uint tag in expired)
            {
                _pending.Remove(tag);
            }
        }

        public void Forget(EntityId id)
        {
            if (_ownedHandles.TryGetValue(id, out int handle))
            {
                _handleToEntity.Remove(handle);
                _ownedHandles.Remove(id);
            }

            _lastSeenInView.Remove(id);
        }

        public void Clear()
        {
            _ownedHandles.Clear();
            _handleToEntity.Clear();
            _lastSeenInView.Clear();
            _pending.Clear();
        }

        private readonly struct PendingSpawn
        {
            public PendingSpawn(int gameHandle, double sentAt)
            {
                GameHandle = gameHandle;
                SentAt = sentAt;
            }

            public int GameHandle { get; }

            public double SentAt { get; }
        }
    }
}
