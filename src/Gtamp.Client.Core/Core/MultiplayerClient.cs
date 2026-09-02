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
using Gtamp.Shared.Security;
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

        /// <summary>Reused every frame; hits are rare and allocating a list per frame for nothing is not.</summary>
        private readonly List<LocalHitSample> _hits = new List<LocalHitSample>();
        private readonly IDatagramTransport _transport;
        private readonly IdentityKey? _identity;

        /// <summary>Sequence stamped on outgoing state updates, so the server can echo it back.</summary>
        private uint _updateSequence;

        /// <summary>The newest sequence the server has confirmed seeing, from the snapshot header.</summary>
        private uint _serverAcknowledgedSequence;

        /// <summary>
        /// What this client reported, kept by sequence number.
        /// <para>
        /// A snapshot answers one particular report — the one whose sequence it echoes
        /// — and the only meaningful question about it is whether the server changed
        /// <em>that</em> report. Comparing it against the newest report instead reads
        /// every metre walked since as disagreement; comparing it against nothing at
        /// all cannot tell a rejection from a snapshot written before the report
        /// arrived. So the reports are kept until the server says which one it saw.
        /// </para>
        /// <para>
        /// 64 entries is a little over two seconds at the 30 Hz client update rate,
        /// which is far more round trip than any playable link. An answer older than
        /// that is not judged at all rather than judged against the wrong report.
        /// </para>
        /// </summary>
        /// <summary>
        /// How often a refused model change is retried, and how many times before it is
        /// given up on. Four times a second for ten seconds: long enough to cover a
        /// model streaming in or a player getting out of a car, short enough that a
        /// model this client does not have is reported while the player is still
        /// wondering why nothing happened.
        /// </summary>
        private const double ModelRetryIntervalSeconds = 0.25d;

        private const int MaxModelAttempts = 40;

        private readonly ReportedState[] _reportHistory = new ReportedState[64];

        private readonly struct ReportedState
        {
            public ReportedState(
                uint sequence, NetVector3 position, int health, byte wantedLevel, uint modelHash)
            {
                Sequence = sequence;
                Position = position;
                Health = health;
                WantedLevel = wantedLevel;
                ModelHash = modelHash;
            }

            public uint Sequence { get; }

            public NetVector3 Position { get; }

            public int Health { get; }

            public byte WantedLevel { get; }

            public uint ModelHash { get; }
        }

        private double _lastStateSendTime;
        private double _lastPingTime;
        private double _lastUpdateTime;
        private double _now;

        private NetVector3 _lastReportedPosition;
        private int _lastReportedHealth;
        private byte _lastReportedWantedLevel;
        private uint _lastReportedModelHash;
        private bool _hasReportedState;

        /// <summary>
        /// A model the server set that this client has not managed to apply yet. The
        /// game refuses the change while the player is in a vehicle or dead, and the
        /// model itself may still be streaming, so it is retried rather than dropped.
        /// </summary>
        private uint _pendingModelHash;
        private double _nextModelAttempt;
        private int _modelAttempts;

        /// <summary>Said once per session, not once per snapshot that carries the model.</summary>
        private bool _modelDeclinedForLspdfr;

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

            // The keypair is loaded once and held for the life of the client: signing
            // is on the connect path, and importing a key per attempt would put a
            // key-derivation cost inside a retry loop.
            _identity = Config.LoadIdentity();
            Connection.Identity = _identity;

            foreach (string unknown in Config.UnknownKeys)
            {
                Log.Warning(
                    LogCategory.Client,
                    $"client.ini setting '{unknown}' is not recognised by this build and has no effect. " +
                    "Check the spelling against docs/INSTALL.md.");
            }

            if (Config.IdentityRegenerated)
            {
                Log.Warning(
                    LogCategory.Security,
                    "The stored identity secret could not be read, so a new identity was generated. " +
                    "Servers will treat this installation as a new player and your saved character will not " +
                    "come back. Restore IdentitySecret in client.ini from a backup to get it back.");
            }
            else if (_identity == null)
            {
                Log.Warning(
                    LogCategory.Security,
                    "No signing identity is configured. Servers that require authentication will refuse " +
                    "this client. See docs/SECURITY.md.");
            }
            MissingContent = new MissingContentTracker(Log);
            RemotePlayers = new RemotePlayerManager(Bridge, Log, MissingContent);
            RemoteEntities = new RemoteEntityManager(Bridge, Log, MissingContent);
            OwnedEntities = new OwnedEntityStreamer(Bridge, Registry, Log)
            {
                Send = (type, payload, delivery) => Connection.Peer?.Send(type, payload, delivery),
            };
            // A remote player's car may be a replicated vehicle or the one this client
            // owns and is driving — a passenger in your own car is the ordinary case,
            // and the two live in different places.
            RemotePlayers.ResolveVehicleHandle = id =>
            {
                if (RemoteEntities.TryGetVehicle(id, out RemoteVehicle vehicle) && vehicle.VehicleHandle != 0)
                {
                    return vehicle.VehicleHandle;
                }

                return OwnedEntities.TryGetHandle(id, out int handle) ? handle : 0;
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

        /// <summary>
        /// Model hashes this client could not resolve. Reported by /diagnostics,
        /// /mods and the bug report, because the alternative is an entity that never
        /// appears with nothing anywhere saying why.
        /// </summary>
        public MissingContentTracker MissingContent { get; }

        /// <summary>Entity id of the local player, assigned by the server at accept time.</summary>
        public EntityId LocalEntityId { get; private set; } = EntityId.None;

        public uint LocalPlayerId { get; private set; }

        public string ClientVersion { get; set; } = "0.1.0";

        /// <summary>
        /// Where the client writes its own files. Set by the host, which is the only
        /// part that knows where GTA V is installed; defaults to the working directory
        /// so a bundle written from a test lands somewhere rather than throwing.
        /// </summary>
        public string LogDirectory { get; set; } = ".";

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

        /// <summary>
        /// Resync requests not sent because one was already outstanding.
        /// <para>
        /// Reported rather than hidden: a large number here is not a fault, it is this
        /// client refusing to ask the same question hundreds of times in one frame. A
        /// large number with <see cref="ResyncsRequested"/> also large is the interesting
        /// case, and it means the baseline keeps going missing.
        /// </para>
        /// </summary>
        public int ResyncsSuppressed { get; private set; }

        /// <summary>
        /// True between asking for a full snapshot and receiving one.
        /// <para>
        /// Without this, one undecodable delta produced a request per snapshot for as
        /// long as it took the answer to arrive — in a real session, a hundred and eighty
        /// reliable messages in a single millisecond, repeatedly, followed by silence and
        /// a connection timeout.
        /// </para>
        /// </summary>
        private bool _resyncPending;

        private double _resyncRequestedAt;

        /// <summary>
        /// How long a resync request is assumed to still be in flight. Long enough to
        /// cover a round trip and the server's next tick on any playable link, short
        /// enough that a request genuinely lost on the way is asked again rather than
        /// leaving the client frozen on a view it can no longer advance.
        /// </summary>
        private const double ResyncRetrySeconds = 2d;

        /// <summary>How many times the server has had to correct the local player's position.</summary>
        public int CorrectionsApplied { get; private set; }

        /// <summary>
        /// How many times the server has set this player's wanted level. Nonzero only
        /// when the server actually took it over — a restored save, an admin command —
        /// so it stays at zero through a whole ordinary session.
        /// </summary>
        public int WantedLevelCorrectionsApplied { get; private set; }

        /// <summary>
        /// How many times this client's maximum health has been brought into line with
        /// the server's. Repeatedly nonzero means the game is refusing the ceiling.
        /// </summary>
        public int MaxHealthCorrectionsApplied { get; private set; }

        /// <summary>Models the server has set for this player, and models it gave up on.</summary>
        public int ModelChangesApplied { get; private set; }

        public int ModelChangesRefused { get; private set; }

        /// <summary>
        /// Rounds this client has reported firing, and rounds it has drawn for other
        /// players. Both are read by the overlay, `netstat` and the diagnostic bundle
        /// — a counter nothing reads is how three earlier defects stayed invisible.
        /// </summary>
        public int ShotsFired { get; private set; }

        public int ShotsSeen { get; private set; }

        /// <summary>Hits this client has claimed against other players. Read by the overlay and `netstat`.</summary>
        public int HitsReported { get; private set; }

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

            AdapterDirectory = adapterDirectory;
            Adapters.LoadFrom(adapterDirectory, Sdk, Environment);
        }

        /// <summary>Where adapters are loaded from, remembered so they can be re-scanned.</summary>
        public string AdapterDirectory { get; private set; } = string.Empty;

        /// <summary>The file the configuration came from, so it can be re-read.</summary>
        public string ConfigPath { get; set; } = string.Empty;

        /// <summary>
        /// Re-reads <c>client.ini</c> and applies the settings that can safely change
        /// mid-session.
        /// <para>
        /// Not all of them can. The identity keypair is loaded once and held for the
        /// life of the client, because swapping it under a live session would leave
        /// the server holding a proof for a key the client no longer has; the address
        /// and port belong to the connection that is already open. Those are reported
        /// as needing a reconnect rather than silently ignored, which is the failure
        /// this command existed to avoid in the first place.
        /// </para>
        /// </summary>
        public ConfigReloadResult ReloadConfig()
        {
            var result = new ConfigReloadResult();

            if (string.IsNullOrEmpty(ConfigPath))
            {
                result.Error = "this client was not started from a configuration file";
                return result;
            }

            ClientConfig fresh;
            try
            {
                fresh = ClientConfig.Load(ConfigPath);
            }
            catch (Exception exception)
            {
                result.Error = exception.Message;
                return result;
            }

            Apply("InterpolationDelay", Config.InterpolationDelay, fresh.InterpolationDelay, result,
                () => Config.InterpolationDelay = fresh.InterpolationDelay);
            Apply("CorrectionThreshold", Config.CorrectionThreshold, fresh.CorrectionThreshold, result,
                () => Config.CorrectionThreshold = fresh.CorrectionThreshold);
            Apply("HealthCorrectionThreshold", Config.HealthCorrectionThreshold, fresh.HealthCorrectionThreshold, result,
                () => Config.HealthCorrectionThreshold = fresh.HealthCorrectionThreshold);
            Apply("ShowNetworkOverlay", Config.ShowNetworkOverlay, fresh.ShowNetworkOverlay, result,
                () => Config.ShowNetworkOverlay = fresh.ShowNetworkOverlay);
            Apply("ShowPlayerBlips", Config.ShowPlayerBlips, fresh.ShowPlayerBlips, result,
                () => Config.ShowPlayerBlips = fresh.ShowPlayerBlips);
            Apply("ShowPlayerNames", Config.ShowPlayerNames, fresh.ShowPlayerNames, result,
                () => Config.ShowPlayerNames = fresh.ShowPlayerNames);
            Apply("VerboseLogging", Config.VerboseLogging, fresh.VerboseLogging, result,
                () => Config.VerboseLogging = fresh.VerboseLogging);
            Apply("ConsoleKey", Config.ConsoleKey, fresh.ConsoleKey, result,
                () => Config.ConsoleKey = fresh.ConsoleKey);
            Apply("PlayerName", Config.PlayerName, fresh.PlayerName, result,
                () => Config.PlayerName = fresh.PlayerName);

            if (!string.Equals(fresh.IdentitySecret, Config.IdentitySecret, StringComparison.Ordinal))
            {
                result.NeedsReconnect.Add("IdentitySecret — the signing key is held for the life of the client");
            }

            if (!string.Equals(fresh.ServerAddress, Config.ServerAddress, StringComparison.Ordinal)
                || fresh.ServerPort != Config.ServerPort)
            {
                result.NeedsReconnect.Add("ServerAddress/ServerPort — reconnect to use them");
            }

            result.Success = true;
            return result;
        }

        private static void Apply<T>(
            string name, T current, T updated, ConfigReloadResult result, Action assign)
        {
            if (Equals(current, updated))
            {
                return;
            }

            assign();
            result.Applied.Add($"{name}: {current} -> {updated}");
        }

        /// <summary>Re-scans the adapter directory. See <see cref="AdapterHost.ReloadFrom"/> for what it cannot do.</summary>
        public IReadOnlyList<string> ReloadAdapters()
        {
            if (string.IsNullOrEmpty(AdapterDirectory))
            {
                return Array.Empty<string>();
            }

            return Adapters.ReloadFrom(AdapterDirectory, Sdk, Environment);
        }

        /// <summary>
        /// Non-null when connecting is refused, and why. Set by the host when the game
        /// API it needs is unusable — see <see cref="ScriptHostCompatibility"/>.
        /// <para>
        /// The refusal is deliberate. A client that connects but cannot spawn a ped or
        /// read a vehicle looks connected: the player list fills, the ping is fine, and
        /// nothing else in the world happens. That is the failure this project keeps
        /// finding — state that arrives correctly and never reaches the thing it
        /// describes — and here it can be refused at the door instead.
        /// </para>
        /// </summary>
        public string? BlockReason { get; set; }

        public void Connect(string host, int port)
        {
            if (!string.IsNullOrEmpty(BlockReason))
            {
                Log.Error(LogCategory.Client, BlockReason!);
                return;
            }

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
            _resyncPending = false;

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
                SendLocalShots();
                SendLocalHits();
                SendPeriodicPing(now);

                OwnedEntities.LocalPlayerId = LocalPlayerId;
                OwnedEntities.ExpirePendingSpawns(now);
                OwnedEntities.RegisterLocalVehicleIfNeeded(ReplicatedWorld.Current, now);
                OwnedEntities.Stream(ReplicatedWorld.Current, now, ClientUpdateInterval);

                // Rendered a fixed delay behind the estimated server clock, which is
                // what turns 20 Hz snapshots into smooth frame-rate movement.
                double renderTime = EstimatedServerTime - Config.InterpolationDelay;

                // Blips and names are drawn relative to where the local player is, so
                // the viewer has to be refreshed before rendering rather than read
                // from a snapshot that is a tenth of a second behind them.
                RemotePlayers.ShowBlips = Config.ShowPlayerBlips;
                RemotePlayers.ShowNames = Config.ShowPlayerNames;
                RemotePlayers.ViewerPosition = _hasReportedState ? _lastReportedPosition : RemotePlayers.ViewerPosition;
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
            _identity?.Dispose();
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

                    case NetMessageType.SecurityNotice:
                    {
                        SecurityNoticeMessage notice = SecurityNoticeMessage.Deserialize(message.Payload);
                        LogLevel level = notice.Kind switch
                        {
                            SecurityNoticeKind.PermissionDenied => LogLevel.Warning,
                            SecurityNoticeKind.Warning => LogLevel.Warning,
                            _ => LogLevel.Info,
                        };

                        Log.Write(level, LogCategory.Security, notice.Text);
                        if (notice.Kind != SecurityNoticeKind.CommandResult)
                        {
                            Bridge.ShowNotification(notice.Text);
                        }

                        break;
                    }

                    case NetMessageType.WeaponShot:
                    {
                        WeaponShotMessage shot = WeaponShotMessage.Deserialize(message.Payload);
                        if (RemotePlayers.TryGet(shot.ShooterId, out RemotePlayer shooter)
                            && shooter.PedHandle != 0)
                        {
                            Bridge.PlayRemoteShot(shooter.PedHandle, shot.WeaponHash, shot.Origin, shot.Impact);
                            ShotsSeen++;
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

            if (header != null && header.IsFullSnapshot)
            {
                // The answer to the request, whichever request it was. Anything that
                // cannot be decoded from here is a new problem and may ask again.
                _resyncPending = false;
            }

            if (header != null && header.AcknowledgedClientUpdate > _serverAcknowledgedSequence)
            {
                _serverAcknowledgedSequence = header.AcknowledgedClientUpdate;
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

            ApplyAuthoritativeMaxHealth(authoritative, local);
            ApplyAuthoritativeWantedLevel(authoritative, local);
            ApplyAuthoritativeModel(authoritative, local);

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

            if (_hasReportedState)
            {
                // Judge this snapshot against the report it actually answers.
                ReportedState answered = _reportHistory[_serverAcknowledgedSequence % _reportHistory.Length];
                if (answered.Sequence != _serverAcknowledgedSequence || _serverAcknowledgedSequence == 0)
                {
                    // The server has not confirmed any report yet, or the one it
                    // confirmed has aged out of the ring. Either way there is nothing
                    // sound to compare against, and guessing is what produces the
                    // rubber-band. Wait for the next snapshot.
                    return;
                }

                referencePosition = answered.Position;
                referenceHealth = answered.Health;
            }

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
            // The correction is now what the server believes, so the report it
            // answered is rewritten to match: judging the next snapshot against the
            // superseded value would correct the same disagreement twice.
            _reportHistory[_serverAcknowledgedSequence % _reportHistory.Length] = new ReportedState(
                _serverAcknowledgedSequence,
                authoritative.Position,
                authoritative.Health,
                _lastReportedWantedLevel,
                _lastReportedModelHash);

            _lastReportedPosition = authoritative.Position;
            _lastReportedHealth = authoritative.Health;
            _hasReportedState = true;
        }

        /// <summary>
        /// Applies the server's maximum health to the local player.
        /// <para>
        /// This one needs no comparison against a report, because the client never
        /// reports its maximum: any difference is the server's value not yet applied.
        /// It travelled in every snapshot from the first version of the entity and was
        /// applied by nothing.
        /// </para>
        /// <para>
        /// It is not cosmetic. The anti-cheat measures reported health against the
        /// server's maximum, so a player whose game says 300 — an LSPDFR install with a
        /// mod that raises it is the ordinary case — reported 300 against a ceiling of
        /// 200 and tripped <c>HealthHack</c> on every update once the join grace ran
        /// out. At `Strict` that is a kick for having a mod installed.
        /// </para>
        /// </summary>
        private void ApplyAuthoritativeMaxHealth(PlayerEntity authoritative, LocalPlayerSample local)
        {
            if (authoritative.MaxHealth <= 0 || authoritative.MaxHealth == local.MaxHealth)
            {
                return;
            }

            Log.Debug(
                LogCategory.Client,
                $"Server maximum health is {authoritative.MaxHealth}; this game says {local.MaxHealth}.");

            MaxHealthCorrectionsApplied++;
            Bridge.SetLocalMaxHealth(authoritative.MaxHealth);
        }

        /// <summary>
        /// Applies a wanted level the server set rather than one the client reported.
        /// <para>
        /// The local game owns the wanted level in normal play: cops raise it, a corner
        /// turned clears it, and the client reports it upward. The server sets one of
        /// its own in exactly two situations — restoring a player who logged out with
        /// stars, and an admin issuing them — and until this ran, neither reached the
        /// game. A restored three-star player spawned clean, reported zero, and the
        /// server's own saved value was gone inside one update.
        /// </para>
        /// <para>
        /// Compared against the report the snapshot answers, not against the level
        /// right now, for the same reason the position correction is: a level the
        /// player picked up half a round trip ago is not a disagreement, and correcting
        /// on it would clear stars the game had legitimately just given them.
        /// </para>
        /// </summary>
        private void ApplyAuthoritativeWantedLevel(PlayerEntity authoritative, LocalPlayerSample local)
        {
            byte reported;
            if (_hasReportedState)
            {
                ReportedState answered = _reportHistory[_serverAcknowledgedSequence % _reportHistory.Length];
                if (answered.Sequence != _serverAcknowledgedSequence || _serverAcknowledgedSequence == 0)
                {
                    // We have reported, and the server has confirmed none of it. Every
                    // difference here reads the same whether the server changed the
                    // level or simply has not heard us yet, and acting on that would
                    // clear the stars the client is at that moment reporting. Waiting
                    // costs one round trip; a wanted level the server really did set is
                    // still in the next snapshot.
                    return;
                }

                reported = answered.WantedLevel;
            }
            else
            {
                // Nothing reported yet — the first snapshot after connecting, which is
                // the one that carries a restored save. Here the local level is the
                // right comparison: it is what this client would report.
                reported = local.WantedLevel;
            }

            if (authoritative.WantedLevel == reported)
            {
                return;
            }

            WantedLevelCorrectionsApplied++;
            Log.Debug(
                LogCategory.Network,
                $"Server set the wanted level to {authoritative.WantedLevel}; this client reported {reported}.");

            Bridge.SetLocalWantedLevel(authoritative.WantedLevel);

            // What the server said is now what this client has reported, so the same
            // difference is not acted on a second time while the change is in flight.
            _lastReportedWantedLevel = authoritative.WantedLevel;
            if (_hasReportedState)
            {
                ReportedState answered = _reportHistory[_serverAcknowledgedSequence % _reportHistory.Length];
                _reportHistory[_serverAcknowledgedSequence % _reportHistory.Length] = new ReportedState(
                    answered.Sequence,
                    answered.Position,
                    answered.Health,
                    authoritative.WantedLevel,
                    answered.ModelHash);
            }
        }

        /// <summary>
        /// Applies a model the server set for the local player.
        /// <para>
        /// The client reports its own model upward and the server takes it, which is
        /// right until the server has a model of its own: a restored save, or a mod
        /// handing out a skin. Neither reached the game. A player's saved model was
        /// read out of the database on connect and overwritten by their client's next
        /// update, and a server that set a skin watched its own value disappear.
        /// </para>
        /// <para>
        /// Unlike the wanted level this cannot always be done at the moment it is
        /// asked: the game builds a new ped for the player, refuses to do so sanely in
        /// a vehicle or dead, and may not have the model streamed in yet. So it is
        /// retried on a cooldown and then given up on <i>out loud</i> — a skin that
        /// never arrives is a missing mod, and the player should be told rather than
        /// left looking like somebody else to everyone but themselves.
        /// </para>
        /// </summary>
        private void ApplyAuthoritativeModel(PlayerEntity authoritative, LocalPlayerSample local)
        {
            if (_pendingModelHash != 0)
            {
                RetryPendingModel(local);
                return;
            }

            if (authoritative.ModelHash == 0)
            {
                return;
            }

            uint reported;
            if (_hasReportedState)
            {
                ReportedState answered = _reportHistory[_serverAcknowledgedSequence % _reportHistory.Length];
                if (answered.Sequence != _serverAcknowledgedSequence || _serverAcknowledgedSequence == 0)
                {
                    // Nothing of ours confirmed yet: a difference here is as likely to
                    // be the server not having heard us. Same rule as the wanted level.
                    return;
                }

                reported = answered.ModelHash;
            }
            else
            {
                reported = local.ModelHash;
            }

            if (authoritative.ModelHash == reported || authoritative.ModelHash == local.ModelHash)
            {
                return;
            }

            // LSPDFR owns the player character: going on duty is a model change, and
            // SET_PLAYER_MODEL does not dress the existing ped, it destroys it and builds
            // another. Anything holding the old one -- LSPDFR's own character manager,
            // mid-way through its duty menu -- is left with an invalid handle.
            //
            // That is not a hypothesis. In one real session the server's model was
            // applied 0.4 s after LSPDFR started building its on-duty character, and
            // LSPDFR died on `Rage.Ped.get_IsFemale` inside `Persona.FromExistingPed`,
            // taking the game with it.
            //
            // So on an LSPDFR install the model is not applied at all. The compromise is
            // stated rather than hidden: other players see the model the server holds,
            // this screen keeps the one LSPDFR chose, and a saved appearance does not
            // come back on connect. A co-op framework silently rebuilding the character
            // another mod is in the middle of using is the worse of the two.
            if (Environment.Lspdfr)
            {
                if (!_modelDeclinedForLspdfr)
                {
                    _modelDeclinedForLspdfr = true;
                    Log.Warning(
                        LogCategory.Client,
                        $"The server set this player's model to 0x{authoritative.ModelHash:X8}, and it is "
                        + "not being applied because LSPD First Response is installed. Changing the model "
                        + "rebuilds the player's ped, which invalidates the one LSPDFR is holding and has "
                        + "crashed it mid-callout. Other players see the server's model; this screen keeps "
                        + "LSPDFR's. See TROUBLESHOOTING.md.");
                }

                ModelChangesRefused++;
                return;
            }

            _pendingModelHash = authoritative.ModelHash;
            _nextModelAttempt = 0d;
            _modelAttempts = 0;
            RetryPendingModel(local);
        }

        /// <summary>One attempt at the pending model change, at most four times a second.</summary>
        private void RetryPendingModel(LocalPlayerSample local)
        {
            if (_now < _nextModelAttempt)
            {
                return;
            }

            _nextModelAttempt = _now + ModelRetryIntervalSeconds;
            _modelAttempts++;

            if (Bridge.TrySetLocalPlayerModel(_pendingModelHash))
            {
                Log.Info(
                    LogCategory.Client,
                    $"Server set this player's model to 0x{_pendingModelHash:X8}.");

                ModelChangesApplied++;
                _lastReportedModelHash = _pendingModelHash;
                _pendingModelHash = 0;
                return;
            }

            if (_modelAttempts < MaxModelAttempts)
            {
                return;
            }

            // Given up on, and said so. Silence here is how a player ends up looking
            // like one character to themselves and another to everyone else.
            Log.Warning(
                LogCategory.Client,
                $"The server set this player's model to 0x{_pendingModelHash:X8} and it could not be " +
                $"applied after {_modelAttempts} attempts. The model is probably not installed on this " +
                "client; other players see the model the server has, not the one on screen here.");

            ModelChangesRefused++;
            _pendingModelHash = 0;
        }

        /// <summary>
        /// Asks the server for a full snapshot, at most once per <see cref="ResyncRetrySeconds"/>.
        /// <para>
        /// <b>One request at a time.</b> Snapshots arrive faster than a round trip, so
        /// between asking and being answered there are always more deltas this client
        /// still cannot decode — every one of them used to ask again. In a real session
        /// that was a hundred and eighty reliable messages inside one millisecond, three
        /// times over, each burst followed by silence and a timeout.
        /// </para>
        /// <para>
        /// <b>Nothing is cleared.</b> This used to reset the replicated world and delete
        /// every remote ped, which was wrong twice over: the full snapshot replaces the
        /// view anyway, so the only effect was that other players vanished for a round
        /// trip — and clearing the snapshot history guaranteed that every snapshot
        /// already queued behind the failing one also failed, which is what turned one
        /// missing baseline into a storm. The view stays until there is a better one.
        /// </para>
        /// </summary>
        private void RequestResync(string reason)
        {
            if (_resyncPending && _now - _resyncRequestedAt < ResyncRetrySeconds)
            {
                ResyncsSuppressed++;
                return;
            }

            _resyncPending = true;
            _resyncRequestedAt = _now;
            ResyncsRequested++;
            Log.Warning(LogCategory.Network, $"Requesting a resync: {reason}");

            var request = new ResyncRequestMessage
            {
                Reason = reason,
                LastAppliedSnapshotId = ReplicatedWorld.LastAppliedSnapshotId,
            };

            Connection.Peer?.Send(NetMessageType.ResyncRequest, request.Serialize(), DeliveryMethod.ReliableOrdered);
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

        /// <summary>
        /// Reports the rounds the local player fired this frame.
        /// <para>
        /// Every frame, unlike the state report: a shot is an event, not a state, and
        /// at the state rate most of a burst would fall between two samples. It is
        /// sent unreliably for the same reason — a muzzle flash retransmitted after
        /// the bullet has already been arbitrated is worse than a missing one.
        /// </para>
        /// </summary>
        private void SendLocalShots()
        {
            if (!Bridge.IsPlayerReady)
            {
                return;
            }

            LocalShotSample shot = Bridge.SampleLocalShots();
            if (shot.Rounds <= 0)
            {
                return;
            }

            // ShooterId is left empty. The server stamps it from the session, because
            // a client that names its own shooter can name somebody else.
            var message = new WeaponShotMessage
            {
                WeaponHash = shot.WeaponHash,
                Origin = shot.Origin,
                Impact = shot.Impact,
            };

            byte[] payload = message.Serialize();
            for (int i = 0; i < shot.Rounds; i++)
            {
                Connection.Peer?.Send(NetMessageType.WeaponShot, payload, DeliveryMethod.Unreliable);
            }

            ShotsFired += shot.Rounds;
        }

        /// <summary>
        /// Reports the hits the local player landed since the last frame.
        /// <para>
        /// Until this existed, <see cref="ReportDamage"/> was called by nothing but
        /// tests: the combat arbiter, the weapon envelopes, the kill feed and the
        /// whole death-and-respawn path were reachable only from the test suite, and
        /// in a real game no player could damage another at all.
        /// </para>
        /// </summary>
        private void SendLocalHits()
        {
            if (!Bridge.IsPlayerReady || !LocalEntityId.IsValid)
            {
                return;
            }

            _hits.Clear();
            Bridge.SampleLocalHits(_hits);

            foreach (LocalHitSample hit in _hits)
            {
                if (!RemotePlayers.TryGetByPedHandle(hit.PedHandle, out RemotePlayer victim))
                {
                    // A ped this client drew for a player who has since left. The hit
                    // is real but there is nobody to attribute it to.
                    continue;
                }

                ReportDamage(victim.EntityId, hit.WeaponHash, hit.Damage, hit.HitPosition, hit.HitBone, hit.IsMelee);
                HitsReported++;
            }
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
                WeaponTint = sample.WeaponTint,
                AimPosition = sample.AimPosition,
                InteriorId = sample.InteriorId,
                WantedLevel = sample.WantedLevel,
                AnimationHash = sample.AnimationHash,
                Ragdoll = sample.Ragdoll,
            };

            if (sample.WeaponComponents != null)
            {
                update.WeaponComponents.AddRange(sample.WeaponComponents);
            }

            if (sample.Appearance != null)
            {
                update.Appearance.CopyFrom(sample.Appearance);
            }

            update.UpdateSequence = ++_updateSequence;
            Connection.Peer?.Send(NetMessageType.ClientStateUpdate, update.Serialize(), DeliveryMethod.Unreliable);

            _reportHistory[_updateSequence % _reportHistory.Length] =
                new ReportedState(
                    _updateSequence, sample.Position, sample.Health, sample.WantedLevel, sample.ModelHash);

            _lastReportedPosition = sample.Position;
            _lastReportedHealth = sample.Health;
            _lastReportedWantedLevel = sample.WantedLevel;
            _lastReportedModelHash = sample.ModelHash;
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
            Bridge.SetWind(environment.WindSpeed, environment.WindDirection);
            Bridge.SetBlackout(environment.Blackout);
        }

        /// <summary>
        /// Asks the server to run an administrative command. Whether it may is the
        /// server's decision — this only carries the request and shows the answer.
        /// </summary>
        public bool SendAdminCommand(string commandLine)
        {
            if (!Connection.IsConnected || string.IsNullOrWhiteSpace(commandLine))
            {
                return false;
            }

            var message = new AdminCommandMessage { CommandLine = commandLine.Trim() };
            Connection.Peer!.Send(NetMessageType.AdminCommand, message.Serialize(), DeliveryMethod.ReliableOrdered);
            return true;
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
