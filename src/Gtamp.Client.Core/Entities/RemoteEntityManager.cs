using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Mods;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;

namespace Gtamp.Client.Entities
{
    /// <summary>
    /// Keeps the local game's vehicles and objects in step with the replicated world.
    /// <para>
    /// Entities this client owns are excluded: it simulates those itself and reports
    /// them upward. Applying the server's echo of our own state back onto the vehicle
    /// we are driving would fight the local physics every snapshot.
    /// </para>
    /// </summary>
    public sealed class RemoteEntityManager
    {
        private readonly IGameBridge _bridge;
        private readonly LogBus _log;

        private readonly Dictionary<EntityId, RemoteVehicle> _vehicles = new Dictionary<EntityId, RemoteVehicle>();
        private readonly Dictionary<EntityId, int> _objects = new Dictionary<EntityId, int>();
        private readonly Dictionary<EntityId, int> _appliedVehicleAppearance = new Dictionary<EntityId, int>();
        private readonly List<EntityId> _removalBuffer = new List<EntityId>();

        private readonly MissingContentTracker _missingContent;

        public RemoteEntityManager(IGameBridge bridge, LogBus log, MissingContentTracker missingContent)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _missingContent = missingContent ?? throw new ArgumentNullException(nameof(missingContent));
        }

        /// <summary>Player id of this client, so entities it owns are left alone.</summary>
        public uint LocalPlayerId { get; set; }

        public int VehicleCount => _vehicles.Count;

        public int ObjectCount => _objects.Count;

        public IEnumerable<RemoteVehicle> Vehicles => _vehicles.Values;

        public bool TryGetVehicle(EntityId id, out RemoteVehicle vehicle) => _vehicles.TryGetValue(id, out vehicle!);

        /// <summary>Feeds a freshly applied snapshot view into the buffers and reconciles lifetimes.</summary>
        public void Sync(EntitySnapshotView view)
        {
            foreach (NetEntity entity in view.Entities)
            {
                switch (entity)
                {
                    case VehicleEntity vehicle when vehicle.OwnerId != LocalPlayerId:
                        SyncVehicle(view.ServerTime, vehicle);
                        break;

                    case ObjectEntity prop when prop.OwnerId != LocalPlayerId:
                        SyncObject(prop);
                        break;
                }
            }

            RemoveVanished(view);
        }

        private void SyncVehicle(double serverTime, VehicleEntity state)
        {
            if (!_vehicles.TryGetValue(state.Id, out RemoteVehicle? vehicle))
            {
                vehicle = new RemoteVehicle(state.Id);
                _vehicles[state.Id] = vehicle;
                _log.Debug(LogCategory.Entity, $"Vehicle {state.Id} appeared.", $"entity:{state.Id.Value}");
            }

            vehicle.Push(serverTime, state);
        }

        private void SyncObject(ObjectEntity state)
        {
            if (!_objects.TryGetValue(state.Id, out int handle) || !_bridge.IsRemoteObjectValid(handle))
            {
                // An object has no sensible stand-in — a missing prop replaced by a
                // different prop is worse than an absence, because it looks correct.
                if (_bridge.GetModelAvailability(state.ModelHash) == ModelAvailability.Unavailable)
                {
                    _missingContent.Report(state.ModelHash, EntityType.Object, state.Id, substituted: false);
                    return;
                }

                handle = _bridge.CreateRemoteObject(state.ModelHash, state.Position, state.Heading);
                if (handle == 0)
                {
                    return;
                }

                _objects[state.Id] = handle;
            }

            // Objects are placed, not interpolated: a prop that moves smoothly is a
            // physics object, and a physics object has an owner reporting it.
            _bridge.ApplyRemoteObject(handle, state);
        }

        private void RemoveVanished(EntitySnapshotView view)
        {
            _removalBuffer.Clear();
            foreach (EntityId id in _vehicles.Keys)
            {
                if (!view.Contains(id) || IsOwnedLocally(view, id))
                {
                    _removalBuffer.Add(id);
                }
            }

            foreach (EntityId id in _removalBuffer)
            {
                RemoveVehicle(id);
            }

            _removalBuffer.Clear();
            foreach (EntityId id in _objects.Keys)
            {
                if (!view.Contains(id) || IsOwnedLocally(view, id))
                {
                    _removalBuffer.Add(id);
                }
            }

            foreach (EntityId id in _removalBuffer)
            {
                RemoveObject(id);
            }
        }

        private bool IsOwnedLocally(EntitySnapshotView view, EntityId id) =>
            LocalPlayerId != 0 && view.TryGet(id, out NetEntity entity) && entity.OwnerId == LocalPlayerId;

        /// <summary>Applies one interpolated frame per replicated vehicle. Called every game frame.</summary>
        public void Render(double renderTime)
        {
            foreach (RemoteVehicle vehicle in _vehicles.Values)
            {
                if (!vehicle.TrySample(renderTime, out RemoteVehicleFrame frame))
                {
                    continue;
                }

                if (vehicle.VehicleHandle == 0 || !_bridge.IsRemoteVehicleValid(vehicle.VehicleHandle))
                {
                    // No substitution for vehicles either: putting another player in a
                    // different car than they are actually driving desynchronises every
                    // judgement the viewer makes about it.
                    if (_bridge.GetModelAvailability(vehicle.ModelHash) == ModelAvailability.Unavailable)
                    {
                        _missingContent.Report(
                            vehicle.ModelHash, EntityType.Vehicle, vehicle.EntityId, substituted: false);
                        continue;
                    }

                    vehicle.VehicleHandle = _bridge.CreateRemoteVehicle(vehicle.ModelHash, frame.Position, frame.Heading);
                    if (vehicle.VehicleHandle == 0)
                    {
                        continue;
                    }

                    _appliedVehicleAppearance.Remove(vehicle.EntityId);
                }

                ApplyAppearanceIfChanged(vehicle);
                _bridge.ApplyRemoteVehicle(vehicle.VehicleHandle, in frame);
            }
        }

        private void ApplyAppearanceIfChanged(RemoteVehicle vehicle)
        {
            if (_appliedVehicleAppearance.TryGetValue(vehicle.EntityId, out int applied)
                && applied == vehicle.AppearanceVersion)
            {
                return;
            }

            VehicleEntity? latest = vehicle.Latest;
            if (latest == null)
            {
                return;
            }

            _appliedVehicleAppearance[vehicle.EntityId] = vehicle.AppearanceVersion;
            _bridge.ApplyRemoteVehicleAppearance(vehicle.VehicleHandle, latest);
        }

        public void RemoveVehicle(EntityId id)
        {
            if (!_vehicles.TryGetValue(id, out RemoteVehicle? vehicle))
            {
                return;
            }

            if (vehicle.VehicleHandle != 0)
            {
                _bridge.DestroyRemoteVehicle(vehicle.VehicleHandle);
            }

            _vehicles.Remove(id);
            _appliedVehicleAppearance.Remove(id);
        }

        public void RemoveObject(EntityId id)
        {
            if (_objects.TryGetValue(id, out int handle))
            {
                _bridge.DestroyRemoteObject(handle);
                _objects.Remove(id);
            }
        }

        public void Clear()
        {
            foreach (RemoteVehicle vehicle in _vehicles.Values)
            {
                if (vehicle.VehicleHandle != 0)
                {
                    _bridge.DestroyRemoteVehicle(vehicle.VehicleHandle);
                }
            }

            foreach (int handle in _objects.Values)
            {
                _bridge.DestroyRemoteObject(handle);
            }

            _vehicles.Clear();
            _objects.Clear();
            _appliedVehicleAppearance.Clear();
        }
    }
}
