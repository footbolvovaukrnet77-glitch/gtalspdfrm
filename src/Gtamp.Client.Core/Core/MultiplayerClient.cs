using System;
using System.Collections.Generic;
using System.Net;
using Gtamp.Client.Entities;
using Gtamp.Client.Missions;
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
        private double _lastPingTime;
        private double _lastUpdateTime;
        private double _now;

        private NetVector3 _lastReportedPosition;
        private int _lastReportedHealth;
        private bool _hasReportedState;

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
            RemoteEntities = new RemoteEntityManager(Bridge, Log);
            OwnedEntities = new OwnedEntityStreamer(Bridge, Registry, Log)
            {
                Send = (type, payload, delivery) => Connection.Peer?.Send(type, payload, delivery),
            };
            Rpc = new RpcDispatcher<object?>(Log);
            Activities = new ActivityWatcher(Log);

            Sdk = new ModSdk(Registry, Log, SendModEvent)
            {
                Rpc = Rpc,
                Activities = Activities,
                SendRpcRequest = request =>
                {
                    if (!Connection.IsConnected)
                    {
                        return false;
                    }

                    Connection.Peer!.Send(
                        NetMessageType.ModRpcRequest, request.Serialize(), DeliveryMethod.ReliableOrdered);

                    return true;
                },
            };

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

        /// <summary>Vehicles and objects simulated by somebody else.</summary>
        public RemoteEntityManager RemoteEntities { get; }

        /// <summary>Entities this client simulates and reports upward.</summary>
        public OwnedEntityStreamer OwnedEntities { get; }

        public ModSdk Sdk { get; }

        /// <summary>Request/response plumbing for mod procedures.</summary>
        public RpcDispatcher<object?> Rpc { get; }

        /// <summary>Turns replicated activity state into events for the mods that own them.</summary>
        public ActivityWatcher Activities { get; }

        public AdapterHost Adapters { get; }

        public ModEnvironment Environment { get; private set; } = new ModEnvironment();

        /// <summary>Entity id of the local player, assigned by the server at accept time.</summary>
        public EntityId LocalEntityId { get; private set; } = EntityId.None;

        public uint LocalPlayerId { get; private set; }

        public string ClientVersion { get; set; } = "0.1.0";

        public double Now => _now;

        /// <summary>
        /// Local estimate of the server clock, and the timeline remote players are
        /// interpolated on.
        /// <para>
        /// It advances with every frame rather than only when a snapshot lands.
        /// Driving interpolation straight from the last snapshot's timestamp would
        /// make the render time a 20 Hz staircase, so remote players would step once
        /// per snapshot no matter how fast the game renders — which defeats the point
        /// of interpolating at all.
        /// </para>
        /// </summary>
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
            RemoteEntities.Clear();
            OwnedEntities.Clear();

            ModManifest manifest = Environment.ToManifest(Registry.ComputeSchemaHash());
            Registry.Lock();

            Connection.Connect(
                endPoint, Config.PlayerName, Config.IdentityToken, Config.ServerPassword, manifest, ClientVersion, _now);
        }

        public void Disconnect(string reason = "player left")
        {
            Connection.Disconnect(DisconnectReason.ClientQuit, reason, _now);
            RemotePlayers.Clear();
            RemoteEntities.Clear();
            OwnedEntities.Clear();
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

            double frameDelta = _lastUpdateTime > 0 ? now - _lastUpdateTime : 0d;
            _lastUpdateTime = now;

            if (Connection.IsConnected)
            {
                EstimatedServerTime += frameDelta;

                SendLocalState(now);
                SendPeriodicPing(now);

                OwnedEntities.LocalPlayerId = LocalPlayerId;
                OwnedEntities.ExpirePendingSpawns(now);
                OwnedEntities.RegisterLocalVehicleIfNeeded(ReplicatedWorld.Current, now);
                OwnedEntities.Stream(ReplicatedWorld.Current, now, ClientUpdateInterval);

                // Rendered a fixed delay behind the estimated server clock, which is
                // what turns 20 Hz snapshots into smooth frame-rate movement.
                double renderTime = EstimatedServerTime - Config.InterpolationDelay;
                RemotePlayers.Render(renderTime);
                RemoteEntities.Render(renderTime);
            }

            Sdk.CurrentTime = now;
            Rpc.Update(now);
            Adapters.Update(now);
        }

        public void Dispose()
        {
            Adapters.Shutdown();
            RemotePlayers.Clear();
            RemoteEntities.Clear();
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

                        if (serverEvent.Kind == ServerEventKind.PlayerDied && serverEvent.PlayerId == LocalPlayerId)
                        {
                            Bridge.ShowSubtitle("~r~You are dead.~s~ Waiting to respawn...", 5000);
                        }
                        else
                        {
                            Bridge.ShowNotification(serverEvent.Text);
                        }

                        break;
                    }

                    case NetMessageType.EntityEvent:
                        OwnedEntities.HandleEntityEvent(EntityEventMessage.Deserialize(message.Payload));
                        break;

                    case NetMessageType.ModRpcRequest:
                    {
                        ModRpcRequestMessage request = ModRpcRequestMessage.Deserialize(message.Payload);
                        ModRpcResponseMessage response = Rpc.HandleRequest(request, null);
                        Connection.Peer?.Send(
                            NetMessageType.ModRpcResponse, response.Serialize(), DeliveryMethod.ReliableOrdered);
                        break;
                    }

                    case NetMessageType.ModRpcResponse:
                        Rpc.HandleResponse(ModRpcResponseMessage.Deserialize(message.Payload));
                        break;

                    case NetMessageType.ModEvent:
                    {
                        ModEventMessage modEvent = ModEventMessage.Deserialize(message.Payload);
                        if (!Sdk.Dispatch(modEvent.Name, modEvent.SenderPlayerId, modEvent.Payload)
                            && Config.VerboseLogging)
                        {
                            Log.Debug(LogCategory.Mod, $"No handler for mod event '{modEvent.Name}'.");
                        }

                        break;
                    }

                    case NetMessageType.Pong:
                        break;

                    default:
                        if (Config.VerboseLogging)
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

            SynchroniseServerClock(ReplicatedWorld.ServerTime);
            RemotePlayers.LocalEntityId = LocalEntityId;
            RemotePlayers.Sync(ReplicatedWorld.Current);
            RemoteEntities.LocalPlayerId = LocalPlayerId;
            RemoteEntities.Sync(ReplicatedWorld.Current);
            Activities.Sync(ReplicatedWorld.Current);
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
        /// Nudges the local clock estimate towards the authoritative one.
        /// <para>
        /// A hard assignment on every snapshot would make the render timeline jump
        /// backwards and forwards with network jitter, which shows up as remote
        /// players twitching. Small differences are corrected gradually; a large one
        /// means the estimate is genuinely wrong (a stall, or a fresh connection) and
        /// is snapped.
        /// </para>
        /// </summary>
        private void SynchroniseServerClock(double authoritative)
        {
            double error = authoritative - EstimatedServerTime;
            if (Math.Abs(error) > 0.5d)
            {
                EstimatedServerTime = authoritative;
                return;
            }

            EstimatedServerTime += error * 0.1d;
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

            // Compared against what was last *reported*, not against where the player
            // is *now*.
            //
            // The server's snapshot answers an earlier report, so measuring against the
            // current local state means every metre walked and every point of damage
            // taken since that report reads as disagreement. Correcting on that would
            // rubber-band an honest player at any real latency, and would undo damage
            // the server has simply not confirmed yet. Measuring against the reported
            // values isolates the only thing that matters: whether the server changed
            // what the client told it.
            NetVector3 referencePosition = _hasReportedState ? _lastReportedPosition : local.Position;
            int referenceHealth = _hasReportedState ? _lastReportedHealth : local.Health;

            float drift = NetVector3.Distance(referencePosition, authoritative.Position);
            int healthGap = Math.Abs(referenceHealth - authoritative.Health);

            // Health is corrected as well as position, because the server arbitrates
            // death and respawn: a respawn refills health and moves the player, and a
            // rejected update leaves the server's health standing. Position drift alone
            // would miss a death that happened where the player was already standing.
            if (drift <= Config.CorrectionThreshold && healthGap <= Config.HealthCorrectionThreshold)
            {
                return;
            }

            CorrectionsApplied++;
            Log.Debug(
                LogCategory.Network,
                $"Server correction: position off by {drift:0.##} m, health off by {healthGap}.");

            Bridge.ApplyLocalCorrection(
                authoritative.Position, authoritative.Heading, authoritative.Health, authoritative.Armor);

            // The correction is now what the server believes, so the next comparison
            // must be against it rather than against the report it superseded.
            _lastReportedPosition = authoritative.Position;
            _lastReportedHealth = authoritative.Health;
            _hasReportedState = true;
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
            RemoteEntities.Clear();
        }

        /// <summary>Seconds between outbound state reports, as negotiated in the handshake.</summary>
        public double ClientUpdateInterval =>
            1d / Math.Max(1, Connection.Accept?.ClientUpdateRate ?? ProtocolConstants.DefaultClientUpdateRate);

        /// <summary>
        /// Reports a hit this client believes it landed. It is a claim: the server
        /// decides whether it happened — see docs/SECURITY.md.
        /// </summary>
        public void ReportDamage(
            EntityId target, uint weaponHash, int damage, NetVector3 hitPosition, short hitBone = -1, bool melee = false)
        {
            if (!Connection.IsConnected)
            {
                return;
            }

            var report = new DamageReportMessage
            {
                TargetId = target,
                WeaponHash = weaponHash,
                Damage = damage,
                HitPosition = hitPosition,
                HitBone = hitBone,
                IsMelee = melee,
            };

            Connection.Peer!.Send(NetMessageType.DamageReport, report.Serialize(), DeliveryMethod.ReliableOrdered);
        }

        private void SendLocalState(double now)
        {
            double interval = ClientUpdateInterval;
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

            if (sample.Appearance != null)
            {
                update.Appearance.CopyFrom(sample.Appearance);
            }

            Connection.Peer?.Send(NetMessageType.ClientStateUpdate, update.Serialize(), DeliveryMethod.Unreliable);

            _lastReportedPosition = sample.Position;
            _lastReportedHealth = sample.Health;
            _hasReportedState = true;
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
            if (!Connection.IsConnected)
            {
                return false;
            }

            var message = new ModEventMessage { Name = eventName, Payload = payload };
            Connection.Peer!.Send(
                NetMessageType.ModEvent,
                message.Serialize(),
                reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);

            return true;
        }

        private void OnAccepted(ConnectAcceptMessage accept)
        {
            LocalEntityId = accept.PlayerEntityId;
            LocalPlayerId = accept.PlayerId;
            _hasReportedState = false;
            RemotePlayers.LocalEntityId = accept.PlayerEntityId;
            EstimatedServerTime = accept.ServerTime;

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
            // Every outstanding call fails now rather than waiting out its timeout: the
            // answer is never coming, and a mod holding a callback for five seconds
            // after a disconnect looks like a hang.
            Rpc.FailAllPending("the connection was lost");
            Activities.Clear();
            RemotePlayers.Clear();
            RemoteEntities.Clear();
            OwnedEntities.Clear();
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
