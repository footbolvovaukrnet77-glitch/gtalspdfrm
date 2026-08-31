using System;
using System.Collections.Generic;
using Gtamp.Client.Core;
using Gtamp.Shared.Diagnostics;
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

        public RemotePlayerManager(IGameBridge bridge, LogBus log)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>The local player's entity, which must never be given a remote ped.</summary>
        public EntityId LocalEntityId { get; set; } = EntityId.None;

        public int Count => _players.Count;

        public IEnumerable<RemotePlayer> Players => _players.Values;

        public bool TryGet(EntityId id, out RemotePlayer player) => _players.TryGetValue(id, out player!);

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

            _removalBuffer.Clear();
            foreach (KeyValuePair<EntityId, RemotePlayer> pair in _players)
            {
                if (!view.Contains(pair.Key))
                {
                    _removalBuffer.Add(pair.Key);
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

                if (player.PedHandle == 0 || !_bridge.IsRemotePedValid(player.PedHandle))
                {
                    // A ped can be culled by the game itself (streaming, a mod cleanup
                    // pass); recreating it is normal, not an error.
                    player.PedHandle = _bridge.CreateRemotePed(player.ModelHash, frame.Position, frame.Heading);
                    if (player.PedHandle == 0)
                    {
                        continue;
                    }
                }

                _bridge.UpdateRemotePed(player.PedHandle, in frame);
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
        }
    }
}
