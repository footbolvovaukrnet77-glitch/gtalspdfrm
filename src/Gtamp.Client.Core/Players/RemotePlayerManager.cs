using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Client.Mods;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;

namespace Gtamp.Client.Players
{
    /// <summary>
    /// Keeps the set of game peds in step with the players present in the
    /// replicated world, and drives them from the interpolation buffers.
    /// </summary>
    public sealed class RemotePlayerManager
    {
        private readonly IGameBridge _bridge;
        private readonly LogBus _log;
        private readonly Dictionary<EntityId, RemotePlayer> _players = new Dictionary<EntityId, RemotePlayer>();
        private readonly List<EntityId> _removalBuffer = new List<EntityId>();
        private readonly Dictionary<EntityId, int> _appliedAppearance = new Dictionary<EntityId, int>();
        private readonly Dictionary<EntityId, uint> _builtFromModel = new Dictionary<EntityId, uint>();

        private readonly MissingContentTracker _missingContent;

        public RemotePlayerManager(IGameBridge bridge, LogBus log, MissingContentTracker missingContent)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _missingContent = missingContent ?? throw new ArgumentNullException(nameof(missingContent));
        }

        /// <summary>The local player's entity, which must never be given a remote ped.</summary>
        public EntityId LocalEntityId { get; set; } = EntityId.None;

        /// <summary>
        /// Where the local player is, and whether blips and names are wanted. Set by
        /// the client each frame; without a viewer there is no distance to fade a
        /// name with, so markers are simply not drawn.
        /// </summary>
        public NetVector3 ViewerPosition { get; set; }

        public bool ShowBlips { get; set; } = true;

        public bool ShowNames { get; set; } = true;

        /// <summary>
        /// Turns the replicated id of a vehicle into the handle the local game has for
        /// it, or 0 when it has none.
        /// <para>
        /// A delegate rather than a reference to the two managers that know: a
        /// passenger's car may be a replicated one, or it may be the car this client
        /// owns and is driving, and this class should not have to know which.
        /// </para>
        /// </summary>
        public Func<EntityId, int>? ResolveVehicleHandle { get; set; }

        public int Count => _players.Count;

        public IEnumerable<RemotePlayer> Players => _players.Values;

        public bool TryGet(EntityId id, out RemotePlayer player) => _players.TryGetValue(id, out player!);

        /// <summary>
        /// Finds the player a game-side ped handle belongs to. The bridge speaks in
        /// handles because that is all the game gives it; everything above speaks in
        /// entity ids.
        /// </summary>
        public bool TryGetByPedHandle(int pedHandle, out RemotePlayer player)
        {
            if (pedHandle != 0)
            {
                foreach (RemotePlayer candidate in _players.Values)
                {
                    if (candidate.PedHandle == pedHandle)
                    {
                        player = candidate;
                        return true;
                    }
                }
            }

            player = null!;
            return false;
        }

        /// <summary>Feeds a freshly applied snapshot view into the interpolation buffers.</summary>
        public void Sync(EntitySnapshotView view)
        {
            foreach (NetEntity entity in view.Entities)
            {
                if (entity.Id == LocalEntityId || entity is not PlayerEntity player)
                {
                    continue;
                }

                if (!_players.TryGetValue(entity.Id, out RemotePlayer? remote))
                {
                    remote = new RemotePlayer(entity.Id, player.PlayerId, player.Name);
                    _players[entity.Id] = remote;
                    _log.Info(LogCategory.Client, $"{player.Name} appeared ({entity.Id}).", $"entity:{entity.Id.Value}");
                }

                remote.Push(view.ServerTime, player);
            }

            // Only a complete view can say somebody left: a snapshot the byte budget
            // cut short omits players who are still very much there, and despawning
            // them makes another player blink out of existence mid-conversation.
            _removalBuffer.Clear();
            if (view.IsComplete)
            {
                foreach (KeyValuePair<EntityId, RemotePlayer> pair in _players)
                {
                    if (!view.Contains(pair.Key))
                    {
                        _removalBuffer.Add(pair.Key);
                    }
                }
            }

            foreach (EntityId id in _removalBuffer)
            {
                Remove(id);
            }
        }

        /// <summary>Applies one interpolated frame per remote player. Called every game frame.</summary>
        public void Render(double renderTime)
        {
            foreach (RemotePlayer player in _players.Values)
            {
                if (!player.TrySample(renderTime, out RemotePedFrame frame))
                {
                    continue;
                }

                // GTA V cannot change a ped's model in place, so a model that no longer
                // matches the one the ped was built from means destroy and rebuild.
                //
                // This is not a rare case. A player's model arrives in their first
                // state update, which normally lands *after* the first snapshot that
                // makes them visible — so the first ped is built from whatever the
                // server had, usually nothing, and the bridge substitutes a default
                // body. Without this check that default is permanent, and every remote
                // player is the wrong character for the rest of the session.
                if (player.PedHandle != 0
                    && _builtFromModel.TryGetValue(player.EntityId, out uint builtFrom)
                    && builtFrom != player.ModelHash)
                {
                    _bridge.DestroyRemotePed(player.PedHandle);
                    player.PedHandle = 0;
                    _builtFromModel.Remove(player.EntityId);
                }

                if (player.PedHandle == 0 || !_bridge.IsRemotePedValid(player.PedHandle))
                {
                    // A player is the one case where substituting beats hiding: an
                    // invisible teammate is worse than one wearing the wrong body, and
                    // the bridge falls back to a default model. That fallback is
                    // recorded rather than allowed to pass as correct.
                    if (_bridge.GetModelAvailability(player.ModelHash) == ModelAvailability.Unavailable)
                    {
                        _missingContent.Report(
                            player.ModelHash, EntityType.Player, player.EntityId, substituted: true);
                    }

                    // A ped can be culled by the game itself (streaming, a mod cleanup
                    // pass); recreating it is normal, not an error.
                    player.PedHandle = _bridge.CreateRemotePed(player.ModelHash, frame.Position, frame.Heading);
                    if (player.PedHandle == 0)
                    {
                        continue;
                    }

                    _builtFromModel[player.EntityId] = player.ModelHash;

                    // Force the appearance onto a freshly created ped.
                    _appliedAppearance.Remove(player.EntityId);
                }

                ApplyAppearanceIfChanged(player);

                NetVector3 pedPosition = _bridge.TryGetRemotePedPosition(player.PedHandle, out NetVector3 position)
                    ? position
                    : frame.Position;

                RemotePedCommand command = RemotePedController.Decide(
                    in frame, pedPosition, VehicleHandleFor(in frame), PedHandleFor(frame.MeleeTargetId));
                _bridge.ApplyRemotePedCommand(player.PedHandle, in command);
                ApplyRoomIfChanged(player, in frame);
                ApplyMarker(player);
            }
        }

        /// <summary>
        /// The local ped standing in for a replicated character, or 0 when this client
        /// has not built one — a player too far away to have been streamed, or an id
        /// that names a networked NPC rather than a player.
        /// <para>
        /// A melee task with no target is not issued at all: a ped told to fight
        /// nobody swings at the air in a direction nobody chose, which is worse than
        /// the punch simply not appearing.
        /// </para>
        /// </summary>
        private int PedHandleFor(EntityId id)
        {
            if (!id.IsValid)
            {
                return 0;
            }

            // The local player first, and this is not a micro-optimisation: the one
            // player every client is certain to be asked about is the one it is not
            // drawing. When somebody punches you, your client has to tell their ped to
            // swing at your ped — and yours is in no remote-player list anywhere. A
            // first version of this searched only the remote players and therefore
            // returned nothing in exactly the most common case; a test caught it.
            if (id == LocalEntityId)
            {
                return _bridge.GetLocalPlayerPedHandle();
            }

            return _players.TryGetValue(id, out RemotePlayer target) ? target.PedHandle : 0;
        }

        /// <summary>
        /// Puts a remote player in the room the world says they are in, on change.
        /// <para>
        /// GTA V culls by room, so a player the engine believes is outdoors while they
        /// stand in a building is drawn through its wall, and one it believes is in the
        /// wrong room vanishes where they should be visible. Every replicated ped was
        /// outdoors as far as the engine was concerned until this existed.
        /// </para>
        /// </summary>
        private void ApplyRoomIfChanged(RemotePlayer player, in RemotePedFrame frame)
        {
            if (_appliedRoom.TryGetValue(player.EntityId, out uint applied) && applied == frame.RoomKey)
            {
                return;
            }

            _appliedRoom[player.EntityId] = frame.RoomKey;
            _bridge.SetRemotePedRoom(player.PedHandle, frame.RoomKey, frame.Position);
        }

        private readonly Dictionary<EntityId, uint> _appliedRoom = new Dictionary<EntityId, uint>();

        private int VehicleHandleFor(in RemotePedFrame frame)
        {
            if (!frame.VehicleId.IsValid || frame.VehicleSeat <= -2 || ResolveVehicleHandle == null)
            {
                return 0;
            }

            return ResolveVehicleHandle(frame.VehicleId);
        }

        /// <summary>
        /// Draws this player's map blip and floating name.
        /// <para>
        /// Fed from the latest replicated entity rather than the interpolated frame:
        /// the blip's colour comes from health, the wanted level and whether they are
        /// driving, none of which are interpolated, and a name label does not need to
        /// be a tenth of a second behind to look right.
        /// </para>
        /// </summary>
        private void ApplyMarker(RemotePlayer player)
        {
            PlayerEntity? latest = player.Latest;
            if (latest == null)
            {
                return;
            }

            PlayerMarker marker = PlayerMarkers.Decide(latest, ViewerPosition, ShowBlips, ShowNames);
            _bridge.ApplyPlayerMarker(player.PedHandle, in marker);
        }

        private void ApplyAppearanceIfChanged(RemotePlayer player)
        {
            if (_appliedAppearance.TryGetValue(player.EntityId, out int applied)
                && applied == player.AppearanceVersion)
            {
                return;
            }

            _appliedAppearance[player.EntityId] = player.AppearanceVersion;
            if (!player.Appearance.IsDefault)
            {
                _bridge.ApplyRemotePedAppearance(player.PedHandle, player.Appearance);
            }
        }

        public void Remove(EntityId id)
        {
            if (!_players.TryGetValue(id, out RemotePlayer? player))
            {
                return;
            }

            if (player.PedHandle != 0)
            {
                _bridge.DestroyRemotePed(player.PedHandle);
            }

            _players.Remove(id);
            _appliedAppearance.Remove(id);
            _builtFromModel.Remove(id);
            _log.Info(LogCategory.Client, $"{player.Name} left ({id}).", $"entity:{id.Value}");
        }

        public void Clear()
        {
            foreach (RemotePlayer player in _players.Values)
            {
                if (player.PedHandle != 0)
                {
                    _bridge.DestroyRemotePed(player.PedHandle);
                }
            }

            _players.Clear();
            _appliedAppearance.Clear();
        }
    }
}
