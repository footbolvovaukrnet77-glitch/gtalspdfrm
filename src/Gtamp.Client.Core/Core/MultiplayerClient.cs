using System;
using System.Collections.Generic;
using System.Net;
using Gtamp.Client.Mods;
using Gtamp.Client.Network;
using Gtamp.Client.Players;
using Gtamp.Client.Sdk;
using Gtamp.Client.Ui;
using Gtamp.Client.World;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.World;

namespace Gtamp.Client.Core
{
    /// <summary>
    /// The client-side multiplayer engine.
    /// <para>
    /// It owns the connection, the replicated world, the remote players and the mod
    /// adapters, and it knows nothing about GTA V beyond <see cref="IGameBridge"/>.
    /// The host script's job is to call <see cref="Update"/> once per frame and to
    /// render the console.
    /// </para>
    /// </summary>
    public sealed class MultiplayerClient : IDisposable
    {
        private readonly List<ReceivedMessage> _inbox = new List<ReceivedMessage>();
        private readonly IDatagramTransport _transport;

        private double _lastStateSendTime;
        private double _lastEnvironmentApplyTime;
        private double _lastPingTime;
        private double _now;

        public MultiplayerClient(
            ClientConfig config,
            IGameBridge bridge,
            LogBus log,
            IDatagramTransport transport,
            DeveloperConsole? console = null,
            EntityRegistry? registry = null)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            Log = log ?? throw new ArgumentNullException(nameof(log));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));

            Console = console ?? new DeveloperConsole();
            Registry = registry ?? EntityRegistry.CreateDefault();
            ReplicatedWorld = new ReplicatedWorld(Registry);
            Connection = new ClientConnection(_transport, Log);
            RemotePlayers = new RemotePlayerManager(Bridge, Log);
            Sdk = new ModSdk(Registry, Log, SendModEvent);
            Adapters = new AdapterHost(Log);

            Connection.Accepted += OnAccepted;
            Connection.Disconnected += OnDisconnected;
            Connection.Rejected += OnRejected;

            ClientCommands.Register(this);
        }

        public ClientConfig Config { get; }

        public IGameBridge Bridge { get; }

        public LogBus Log { get; }

        public DeveloperConsole Console { get; }

        public EntityRegistry Registry { get; }

        public ReplicatedWorld ReplicatedWorld { get; }

        public ClientConnection Connection { get; }

        public RemotePlayerManager RemotePlayers { get; }

        public ModSdk Sdk { get; }

        public AdapterHost Adapters { get; }

        public ModEnvironment Environment { get; private set; } = new ModEnvironment();

        /// <summary>Entity id of the local player, assigned by the server at accept time.</summary>
        public EntityId LocalEntityId { get; private set; } = EntityId.None;

        public uint LocalPlayerId { get; private set; }

        public string ClientVersion { get; set; } = "0.1.0";

        public double Now => _now;

        /// <summary>Server clock estimate, used as the timeline for interpolation.</summary>
        public double EstimatedServerTime { get; private set; }

        public int SnapshotsApplied => ReplicatedWorld.SnapshotsApplied;

        public int ResyncsRequested { get; private set; }

        /// <summary>How many times the server has had to correct the local player's position.</summary>
        public int CorrectionsApplied { get; private set; }

        public bool IsConnected => Connection.IsConnected;

        /// <summary>Scans for installed mods and starts any adapters that apply.</summary>
        public void InitializeMods(string gameDirectory, string adapterDirectory)
        {
            Environment = ModEnvironment.Detect(gameDirectory);
            Log.Info(
                LogCategory.Mod,
                $"Detected {Environment.Mods.Count} mod file(s): " +
                $"ScriptHookV={Yes(Environment.ScriptHookV)}, SHVDN={Yes(Environment.ScriptHookVDotNet)}, " +
                $"RPH={Yes(Environment.RagePluginHook)}, LSPDFR={Yes(Environment.Lspdfr)}");

            Adapters.LoadFrom(adapterDirectory, Sdk, Environment);
        }

        public void Connect(string host, int port)
        {
            if (Connection.State == ClientConnectionState.Connecting)
            {
                Log.Warning(LogCategory.Client, "A connection attempt is already in progress.");
                return;
            }

            IPEndPoint endPoint;
            try
            {
                endPoint = ResolveEndPoint(host, port);
            }
            catch (Exception exception)
            {
                Log.Error(LogCategory.Network, $"Could not resolve '{host}:{port}': {exception.Message}");
                return;
            }

            ReplicatedWorld.Reset();
            RemotePlayers.Clear();

            ModManifest manifest = Environment.ToManifest(Registry.ComputeSchemaHash());
            Registry.Lock();

            Connection.Connect(
                endPoint, Config.PlayerName, Config.IdentityToken, Config.ServerPassword, manifest, ClientVersion, _now);
        }

        public void Disconnect(string reason = "player left")
        {
            Connection.Disconnect(DisconnectReason.ClientQuit, reason, _now);
            RemotePlayers.Clear();
            ReplicatedWorld.Reset();
            LocalEntityId = EntityId.None;
        }

        /// <summary>One frame of client work. <paramref name="now"/> is monotonic seconds.</summary>
        public void Update(double now)
        {
            _now = now;

            _inbox.Clear();
            Connection.Update(now, _inbox);
            foreach (ReceivedMessage message in _inbox)
            {
                HandleMessage(message);
            }

            if (Connection.IsConnected)
            {
                EstimatedServerTime += now - (_lastEnvironmentApplyTime > 0 ? _lastEnvironmentApplyTime : now);
                _lastEnvironmentApplyTime = now;

                SendLocalState(now);
                SendPeriodicPing(now);

                // Remote players are rendered a fixed delay behind the newest snapshot,
                // which is what turns 20 Hz snapshots into smooth 60 Hz movement.
                RemotePlayers.Render(ReplicatedWorld.ServerTime - Config.InterpolationDelay);
            }

            Adapters.Update(now);
        }

        public void Dispose()
        {
            Adapters.Shutdown();
            RemotePlayers.Clear();
            Connection.Disconnect(DisconnectReason.ClientQuit, "client shutting down", _now);
            _transport.Dispose();
        }

        // ------------------------------------------------------------------
        private void HandleMessage(ReceivedMessage message)
        {
            try
            {
                switch (message.Type)
                {
                    case NetMessageType.Snapshot:
                        HandleSnapshot(message.Payload);
                        break;

                    case NetMessageType.ChatMessage:
                    {
                        ChatMessage chat = ChatMessage.Deserialize(message.Payload);
                        Log.Info(LogCategory.Client, $"[chat] {chat.SenderName}: {chat.Text}");
                        Bridge.ShowNotification($"~b~{chat.SenderName}~s~: {chat.Text}");
                        break;
                    }

                    case NetMessageType.ServerEvent:
                    {
                        ServerEventMessage serverEvent = ServerEventMessage.Deserialize(message.Payload);
                        Log.Info(LogCategory.Server, serverEvent.Text);
                        Bridge.ShowNotification(serverEvent.Text);
                        break;
                    }

                    case NetMessageType.Pong:
                        break;

                    default:
                        if ((byte)message.Type >= (byte)NetMessageType.ModMessageFirst)
                        {
                            if (!Sdk.Dispatch((byte)message.Type, 0, message.Payload) && Config.VerboseLogging)
                            {
                                Log.Debug(LogCategory.Mod, $"No handler for mod message 0x{(byte)message.Type:X2}.");
                            }
                        }
                        else if (Config.VerboseLogging)
                        {
                            Log.Debug(LogCategory.Network, $"Unhandled message {message.Type}.");
                        }

                        break;
                }
            }
            catch (NetSerializationException exception)
            {
                Log.Error(LogCategory.Network, $"Could not decode a {message.Type} message.", exception);
            }
        }

        private void HandleSnapshot(byte[] payload)
        {
            if (!ReplicatedWorld.TryApply(payload, out SnapshotHeader? header, out string error))
            {
                if (error.Length == 0)
                {
                    // A stale or duplicate snapshot; nothing is wrong.
                    return;
                }

                RequestResync(error);
                return;
            }

            EstimatedServerTime = ReplicatedWorld.ServerTime;
            RemotePlayers.LocalEntityId = LocalEntityId;
            RemotePlayers.Sync(ReplicatedWorld.Current);
            ApplyEnvironment();
            ApplyServerCorrection();

            var ack = new SnapshotAckMessage { SnapshotId = ReplicatedWorld.LastAppliedSnapshotId };
            Connection.Peer?.Send(NetMessageType.SnapshotAck, ack.Serialize(), DeliveryMethod.Unreliable);

            if (Config.VerboseLogging && header != null && header.IsFullSnapshot)
            {
                Log.Debug(
                    LogCategory.Network,
                    $"Applied full snapshot {header.SnapshotId}: {header.CreatedIds.Count} entities.");
            }
        }

        /// <summary>
        /// Closes the server-authority loop for the local player.
        /// <para>
        /// The client moves its own character locally — anything else would feel
        /// terrible at any latency — but the server decides where that character
        /// actually is. When the two disagree by more than the configured threshold
        /// the server wins and the client snaps. Without this the server can reject a
        /// movement (an anti-cheat trip, a validation failure) and the player would
        /// keep walking in a world that no longer agrees with them.
        /// </para>
        /// </summary>
        private void ApplyServerCorrection()
        {
            if (!LocalEntityId.IsValid || !Bridge.IsPlayerReady)
            {
                return;
            }

            PlayerEntity? authoritative = ReplicatedWorld.GetPlayer(LocalEntityId);
            if (authoritative == null)
            {
                return;
            }

            LocalPlayerSample local = Bridge.SampleLocalPlayer();
            float drift = NetVector3.Distance(local.Position, authoritative.Position);
            if (drift <= Config.CorrectionThreshold)
            {
                return;
            }

            CorrectionsApplied++;
            Log.Debug(
                LogCategory.Network,
                $"Server correction: local position was {drift:0.##} m from the authoritative one.");

            Bridge.ApplyLocalCorrection(
                authoritative.Position, authoritative.Heading, authoritative.Health, authoritative.Armor);
        }

        private void RequestResync(string reason)
        {
            ResyncsRequested++;
            Log.Warning(LogCategory.Network, $"Requesting a resync: {reason}");

            var request = new ResyncRequestMessage
            {
                Reason = reason,
                LastAppliedSnapshotId = ReplicatedWorld.LastAppliedSnapshotId,
            };

            Connection.Peer?.Send(NetMessageType.ResyncRequest, request.Serialize(), DeliveryMethod.ReliableOrdered);
            ReplicatedWorld.Reset();
            RemotePlayers.Clear();
        }

        private void SendLocalState(double now)
        {
            double interval = 1d / Math.Max(1, Connection.Accept?.ClientUpdateRate ?? ProtocolConstants.DefaultClientUpdateRate);
            if (now - _lastStateSendTime < interval || !Bridge.IsPlayerReady)
            {
                return;
            }

            _lastStateSendTime = now;
            LocalPlayerSample sample = Bridge.SampleLocalPlayer();

            var update = new ClientStateUpdateMessage
            {
                ClientTime = now,
                AcknowledgedSnapshotId = ReplicatedWorld.LastAppliedSnapshotId,
                Position = sample.Position,
                Velocity = sample.Velocity,
                Heading = sample.Heading,
                Health = sample.Health,
                Armor = sample.Armor,
                Flags = sample.Flags,
                Movement = sample.Movement,
                ModelHash = sample.ModelHash,
                CurrentWeaponHash = sample.CurrentWeaponHash,
                Ammo = sample.Ammo,
                AimPosition = sample.AimPosition,
                InteriorId = sample.InteriorId,
                AnimationHash = sample.AnimationHash,
            };

            Connection.Peer?.Send(NetMessageType.ClientStateUpdate, update.Serialize(), DeliveryMethod.Unreliable);
        }

        private void SendPeriodicPing(double now)
        {
            if (now - _lastPingTime < 1d)
            {
                return;
            }

            _lastPingTime = now;
            var ping = new TimeSyncMessage { ClientTime = now, ServerTime = 0 };
            Connection.Peer?.Send(NetMessageType.Ping, ping.Serialize(), DeliveryMethod.Unreliable);
        }

        private void ApplyEnvironment()
        {
            WorldEnvironment environment = ReplicatedWorld.Environment;
            Bridge.SetClock(environment.Hours, environment.Minutes, environment.Seconds);
            Bridge.SetWeather(environment.WeatherHash, environment.NextWeatherHash, environment.WeatherTransition);
        }

        private bool SendModEvent(string eventName, byte[] payload, bool reliable)
        {
            if (!Connection.IsConnected || !Sdk.TryGetEventId(eventName, out byte id))
            {
                return false;
            }

            Connection.Peer!.Send(
                (NetMessageType)id,
                payload,
                reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);

            return true;
        }

        private void OnAccepted(ConnectAcceptMessage accept)
        {
            LocalEntityId = accept.PlayerEntityId;
            LocalPlayerId = accept.PlayerId;
            RemotePlayers.LocalEntityId = accept.PlayerEntityId;
            EstimatedServerTime = accept.ServerTime;
            _lastEnvironmentApplyTime = _now;

            Bridge.ShowNotification($"Connected to ~g~{accept.ServerName}~s~ as {Config.PlayerName}.");

            foreach (ModCompatibilityEntry entry in accept.ModReport)
            {
                if (entry.Status != ModCompatibility.Compatible)
                {
                    Log.Warning(LogCategory.Mod, $"{entry.ModId}: {entry.Status} — {entry.Detail}");
                }
            }
        }

        private void OnDisconnected(DisconnectReason reason, string text)
        {
            RemotePlayers.Clear();
            ReplicatedWorld.Reset();
            LocalEntityId = EntityId.None;
            Bridge.ShowNotification($"~r~Disconnected~s~: {text}");
        }

        private void OnRejected(DisconnectReason reason, string text) =>
            Bridge.ShowNotification($"~r~Connection refused~s~: {text}");

        private static IPEndPoint ResolveEndPoint(string host, int port)
        {
            if (IPAddress.TryParse(host, out IPAddress? address))
            {
                return new IPEndPoint(address, port);
            }

            IPAddress[] resolved = Dns.GetHostAddresses(host);
            if (resolved.Length == 0)
            {
                throw new InvalidOperationException($"'{host}' did not resolve to any address.");
            }

            return new IPEndPoint(resolved[0], port);
        }

        private static string Yes(bool value) => value ? "yes" : "no";
    }
}
