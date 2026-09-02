using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Mods;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;

namespace Gtamp.Client.Entities
{
    /// <summary>
    /// Keeps the local game's vehicles, networked NPCs and objects in step with the
    /// replicated world.
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
        private readonly Dictionary<EntityId, RemoteNpc> _npcs = new Dictionary<EntityId, RemoteNpc>();
        private readonly Dictionary<EntityId, int> _objects = new Dictionary<EntityId, int>();
        private readonly Dictionary<EntityId, int> _appliedNpcAppearance = new Dictionary<EntityId, int>();
        private readonly Dictionary<EntityId, uint> _appliedNpcGroup = new Dictionary<EntityId, uint>();

        /// <summary>
        /// Whether each replicated vehicle was a wreck the last time it was drawn.
        /// <para>
        /// The explosion is played on the <i>transition</i> into destruction and only
        /// then. A vehicle that was already burnt when this client first saw it — a
        /// wreck left in the street before you joined — must not detonate again on
        /// arrival, and one that stays burnt must not detonate every frame.
        /// </para>
        /// </summary>
        private readonly Dictionary<EntityId, bool> _vehicleWasDestroyed = new Dictionary<EntityId, bool>();
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

        public int NpcCount => _npcs.Count;

        public int ObjectCount => _objects.Count;

        public IEnumerable<RemoteVehicle> Vehicles => _vehicles.Values;

        /// <summary>Explosions drawn for vehicles somebody else's game destroyed.</summary>
        public int VehicleExplosionsDrawn { get; private set; }

        public bool TryGetVehicle(EntityId id, out RemoteVehicle vehicle) => _vehicles.TryGetValue(id, out vehicle!);

        public IEnumerable<RemoteNpc> Npcs => _npcs.Values;

        public bool TryGetNpc(EntityId id, out RemoteNpc npc) => _npcs.TryGetValue(id, out npc!);

        /// <summary>The game handle of a replicated object, for the entity inspector.</summary>
        public bool TryGetObjectHandle(EntityId id, out int handle) => _objects.TryGetValue(id, out handle);

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

                    case PedEntity npc when npc.OwnerId != LocalPlayerId:
                        SyncNpc(view.ServerTime, npc);
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

        private void SyncNpc(double serverTime, PedEntity state)
        {
            if (!_npcs.TryGetValue(state.Id, out RemoteNpc? npc))
            {
                npc = new RemoteNpc(state.Id);
                _npcs[state.Id] = npc;
                _log.Debug(LogCategory.Entity, $"Networked NPC {state.Id} appeared.", $"entity:{state.Id.Value}");
            }

            npc.Push(serverTime, state);
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
            // An object may hang off a vehicle, a ped or another object. Whichever it
            // is, only this class knows what the local game has built for that id.
            _bridge.ApplyRemoteObject(handle, state, ResolveAttachParent(state.AttachedToId));
        }

        /// <summary>
        /// Local handle of whatever an object is attached to, or 0.
        /// <para>
        /// Objects attach to vehicles, to peds and to other objects, so all three
        /// tables are searched. `ResolveVehicleHandle` on the player manager covers a
        /// vehicle this client owns; an object's parent is always a replicated entity,
        /// because an object the client owns is not driven from here at all.
        /// </para>
        /// </summary>
        private int ResolveAttachParent(EntityId id)
        {
            if (!id.IsValid)
            {
                return 0;
            }

            if (_vehicles.TryGetValue(id, out RemoteVehicle? vehicle))
            {
                return vehicle.VehicleHandle;
            }

            if (_npcs.TryGetValue(id, out RemoteNpc? npc))
            {
                return npc.PedHandle;
            }

            return _objects.TryGetValue(id, out int handle) ? handle : 0;
        }

        /// <summary>
        /// Drops what the snapshot says is gone.
        /// <para>
        /// Absence only means "gone" when the view is a complete picture. A snapshot
        /// the byte budget cut short leaves out entities that are alive and simply
        /// have not arrived yet, and deleting those means destroying and rebuilding
        /// the same car every frame the budget is tight — which is what a joining
        /// client saw as vehicles flickering in and out of the world.
        /// </para>
        /// </summary>
        private void RemoveVanished(EntitySnapshotView view)
        {
            bool absenceMeansGone = view.IsComplete;

            _removalBuffer.Clear();
            foreach (EntityId id in _vehicles.Keys)
            {
                if ((absenceMeansGone && !view.Contains(id)) || IsOwnedLocally(view, id))
                {
                    _removalBuffer.Add(id);
                }
            }

            foreach (EntityId id in _removalBuffer)
            {
                RemoveVehicle(id);
            }

            _removalBuffer.Clear();
            foreach (EntityId id in _npcs.Keys)
            {
                if ((absenceMeansGone && !view.Contains(id)) || IsOwnedLocally(view, id))
                {
                    _removalBuffer.Add(id);
                }
            }

            foreach (EntityId id in _removalBuffer)
            {
                RemoveNpc(id);
            }

            _removalBuffer.Clear();
            foreach (EntityId id in _objects.Keys)
            {
                if ((absenceMeansGone && !view.Contains(id)) || IsOwnedLocally(view, id))
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
                VehicleEntity? latestState = vehicle.Latest;
                int trailerHandle = latestState != null && latestState.TrailerId.IsValid
                    && _vehicles.TryGetValue(latestState.TrailerId, out RemoteVehicle? trailer)
                    ? trailer.VehicleHandle
                    : 0;

                _bridge.ApplyRemoteVehicle(vehicle.VehicleHandle, in frame, trailerHandle);
                PlayDestructionIfJustDestroyed(vehicle, in frame);
            }

            RenderNpcs(renderTime);
        }

        /// <summary>
        /// Drives every networked NPC, through the same controller a player's ped
        /// uses. The only differences from the player path are what a model failure
        /// means: an NPC whose model is missing is *substituted*, because an NPC is a
        /// piece of scenery with a role, and a crowd with a hole in it is worse than a
        /// crowd wearing the wrong jacket. A player is never substituted — you have to
        /// be able to recognise who you are looking at.
        /// </summary>
        private void RenderNpcs(double renderTime)
        {
            foreach (RemoteNpc npc in _npcs.Values)
            {
                if (!npc.TrySample(renderTime, out RemotePedFrame frame))
                {
                    continue;
                }

                if (npc.PedHandle == 0 || !_bridge.IsRemotePedValid(npc.PedHandle))
                {
                    bool substituted = _bridge.GetModelAvailability(npc.ModelHash) == ModelAvailability.Unavailable;
                    if (substituted)
                    {
                        _missingContent.Report(npc.ModelHash, EntityType.Ped, npc.EntityId, substituted: true);
                    }

                    npc.PedHandle = _bridge.CreateRemotePed(npc.ModelHash, frame.Position, frame.Heading);
                    if (npc.PedHandle == 0)
                    {
                        continue;
                    }

                    _appliedNpcAppearance.Remove(npc.EntityId);
                    _appliedNpcGroup.Remove(npc.EntityId);
                }

                NetVector3 pedPosition = _bridge.TryGetRemotePedPosition(npc.PedHandle, out NetVector3 position)
                    ? position
                    : frame.Position;

                int vehicleHandle = frame.VehicleId.IsValid && frame.VehicleSeat > -2
                    && _vehicles.TryGetValue(frame.VehicleId, out RemoteVehicle? ride)
                    ? ride.VehicleHandle
                    : 0;

                RemotePedCommand command = RemotePedController.Decide(in frame, pedPosition, vehicleHandle);
                _bridge.ApplyRemotePedCommand(npc.PedHandle, in command);
                ApplyNpcAppearanceIfChanged(npc);
                ApplyNpcRelationshipGroupIfChanged(npc);
            }
        }

        /// <summary>
        /// Puts an NPC in the relationship group the server gave it, on change only.
        /// <para>
        /// A remote ped is created in the local player's own group, so until this ran
        /// a suspect the server had marked hostile was an ally on every machine that
        /// drew it. The hash was in <see cref="PedEntity.RelationshipGroupHash"/> from
        /// the entity's first version and was read by nothing.
        /// </para>
        /// </summary>
        private void ApplyNpcRelationshipGroupIfChanged(RemoteNpc npc)
        {
            PedEntity? latest = npc.Latest;
            if (latest == null)
            {
                return;
            }

            if (_appliedNpcGroup.TryGetValue(npc.EntityId, out uint applied)
                && applied == latest.RelationshipGroupHash)
            {
                return;
            }

            _appliedNpcGroup[npc.EntityId] = latest.RelationshipGroupHash;
            _bridge.SetRemotePedRelationshipGroup(npc.PedHandle, latest.RelationshipGroupHash);
        }

        private void ApplyNpcAppearanceIfChanged(RemoteNpc npc)
        {
            if (_appliedNpcAppearance.TryGetValue(npc.EntityId, out int applied) && applied == npc.AppearanceVersion)
            {
                return;
            }

            PedEntity? latest = npc.Latest;
            if (latest == null)
            {
                return;
            }

            _appliedNpcAppearance[npc.EntityId] = npc.AppearanceVersion;
            _bridge.ApplyRemotePedAppearance(npc.PedHandle, latest.Appearance);
        }

        public void RemoveNpc(EntityId id)
        {
            if (!_npcs.TryGetValue(id, out RemoteNpc? npc))
            {
                return;
            }

            if (npc.PedHandle != 0)
            {
                _bridge.DestroyRemotePed(npc.PedHandle);
            }

            _npcs.Remove(id);
            _appliedNpcAppearance.Remove(id);
            _appliedNpcGroup.Remove(id);
        }

        /// <summary>
        /// Draws the explosion of a vehicle that was destroyed on somebody else's
        /// screen.
        /// <para>
        /// Until this ran, a car blowing up in front of another player turned into a
        /// blackened wreck between two frames on every screen but theirs: no fireball,
        /// no sound, no reason. The state that says it happened —
        /// <see cref="VehicleFlags.Burnt"/> — was declared from the first version of
        /// the flags, derived by nothing, sampled by nothing and read by nothing.
        /// </para>
        /// <para>
        /// The first sighting of a vehicle only records what it is; it never explodes.
        /// Otherwise every wreck already standing in the street would detonate again
        /// for each player who walked into view of it.
        /// </para>
        /// </summary>
        private void PlayDestructionIfJustDestroyed(RemoteVehicle vehicle, in RemoteVehicleFrame frame)
        {
            bool destroyed = (frame.Flags & VehicleFlags.Burnt) != 0;
            if (!_vehicleWasDestroyed.TryGetValue(vehicle.EntityId, out bool wasDestroyed))
            {
                // First sighting: remember the state, show nothing.
                _vehicleWasDestroyed[vehicle.EntityId] = destroyed;
                return;
            }

            if (destroyed == wasDestroyed)
            {
                return;
            }

            _vehicleWasDestroyed[vehicle.EntityId] = destroyed;
            if (destroyed)
            {
                VehicleExplosionsDrawn++;
                _bridge.PlayVehicleExplosion(vehicle.VehicleHandle);
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
            _vehicleWasDestroyed.Remove(id);
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

            foreach (RemoteNpc npc in _npcs.Values)
            {
                if (npc.PedHandle != 0)
                {
                    _bridge.DestroyRemotePed(npc.PedHandle);
                }
            }

            foreach (int handle in _objects.Values)
            {
                _bridge.DestroyRemoteObject(handle);
            }

            _vehicles.Clear();
            _npcs.Clear();
            _objects.Clear();
            _appliedVehicleAppearance.Clear();
            _vehicleWasDestroyed.Clear();
            _appliedNpcAppearance.Clear();
            _appliedNpcGroup.Clear();
        }
    }
}
