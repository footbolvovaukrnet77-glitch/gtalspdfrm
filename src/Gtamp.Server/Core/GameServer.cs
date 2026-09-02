using System;
using System.Collections.Generic;
using System.Net;
using Gtamp.Server.Admin;
using Gtamp.Server.Entities;
using Gtamp.Server.Missions;
using Gtamp.Server.Mods;
using Gtamp.Server.Persistence;
using Gtamp.Server.Players;
using Gtamp.Server.Replication;
using Gtamp.Server.World;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.Security;
using Gtamp.Shared.World;

namespace Gtamp.Server.Core
{
    /// <summary>
    /// The authoritative simulation.
    /// <para>
    /// Time is passed in rather than read from a clock so the whole server can be
    /// driven deterministically from a test: <see cref="Tick"/> is the only entry
    /// point and it never blocks.
    /// </para>
    /// </summary>
    public sealed class GameServer : IDisposable
    {
        /// <summary>Legion Square. Used when a player has no stored position.</summary>
        public static readonly NetVector3 DefaultSpawn = new NetVector3(215.0f, -810.0f, 30.7f);

        /// <summary>How long an authority hold waits for its acknowledgement before giving up.</summary>
        public const double AuthorityHoldTimeoutSeconds = 10d;

        /// <summary>
        /// The model hold's own timeout. Longer than the rest because applying a model
        /// is not one frame's work on the client: it waits for the model to stream in
        /// and refuses to do it at all while the player is in a vehicle.
        /// </summary>
        public const double ModelHoldTimeoutSeconds = 20d;

        /// <summary>
        /// How far a gunshot is relayed. A rifle report carries a few hundred metres
        /// in the game and a muzzle flash is invisible long before that, so 250 m
        /// covers everyone who could notice and drops the rest. This filters
        /// *replication* only — no entity leaves the world at any distance.
        /// </summary>
        public const float ShotRelayRange = 250f;

        /// <summary>
        /// How far a claimed muzzle may sit from the shooter's own position. A barrel
        /// is under two metres from its owner; the rest is slack for the report
        /// interval and the round trip.
        /// </summary>
        public const float MaxMuzzleOffset = 10f;

        private const float ShotRelayRangeSquared = ShotRelayRange * ShotRelayRange;

        private const float MaxMuzzleOffsetSquared = MaxMuzzleOffset * MaxMuzzleOffset;

        private readonly IDatagramTransport _transport;
        private readonly Dictionary<IPEndPoint, PendingAuthentication> _pendingAuth =
            new Dictionary<IPEndPoint, PendingAuthentication>();
        private readonly List<IPEndPoint> _expiredAuth = new List<IPEndPoint>();
        private readonly IPersistenceStore _persistence;
        private readonly Random _random = new Random();
        private readonly List<PlayerSession> _reapBuffer = new List<PlayerSession>();

        private double _now;
        private double _lastTickTime;
        private double _lastSnapshotTime;
        private double _lastSaveTime;
        private bool _started;

        public GameServer(
            ServerConfig config,
            LogBus log,
            IDatagramTransport transport,
            IPersistenceStore? persistence = null,
            EntityRegistry? registry = null)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Log = log ?? throw new ArgumentNullException(nameof(log));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _persistence = persistence ?? new NullPersistenceStore();

            Registry = registry ?? EntityRegistry.CreateDefault();
            World = new ServerWorld(Registry, Log);
            AntiCheat = new AntiCheatEngine(new AntiCheatSettings { Level = config.AntiCheat });
            EntityValidator = new OwnedEntityValidator(AntiCheat.Settings);
            Combat = CombatSettings.CreateDefault();
            Combat.PlayerVersusPlayer = config.PlayerVersusPlayer;
            Combat.FriendlyFire = config.FriendlyFire;
            Combat.NpcDamage = config.NpcDamage;
            Combat.VehicleDamage = config.VehicleDamage;
            Combat.EnforceWeaponMatch = config.AntiCheat == AntiCheatLevel.Strict;

            foreach (CustomWeaponSetting weapon in config.CustomWeapons)
            {
                if (!weapon.IsValid)
                {
                    Log.Warning(
                        LogCategory.Server,
                        $"Ignoring a customWeapons entry: name '{weapon.Name}', damage {weapon.MaxDamagePerHit}, " +
                        $"range {weapon.MaxRange}. A weapon needs a name, a positive damage ceiling and a positive range.");
                    continue;
                }

                Combat.Add(weapon.ToProfile());
            }
            Entities = new NetworkedEntityManager(World, config, Log);
            Entities.OwnershipGranted += OnOwnershipGranted;

            Activities = new ActivityManager(World, Log);
            Rpc = new RpcDispatcher<PlayerSession>(Log);
            Mods = new ServerModSdk(Registry, World, Activities, Rpc, Combat, Log)
            {
                SendToSession = (session, type, payload, reliable) => session.Peer.Send(
                    type, payload, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable),
                SendToAll = (type, payload, reliable) => Broadcast(
                    type, payload, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable),
                SendToOthers = (sender, type, payload, reliable) => Broadcast(
                    type, payload, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, sender),
                ResolveSession = playerId => Players.TryGetByPlayerId(playerId, out PlayerSession session) ? session : null,
            };

            foreach (string relayed in config.RelayedModEvents)
            {
                Mods.RegisterRelay(relayed);
            }

            Manifest.SchemaHash = Registry.ComputeSchemaHash();
        }

        public ServerConfig Config { get; }

        public LogBus Log { get; }

        public EntityRegistry Registry { get; }

        public ServerWorld World { get; }

        public PlayerRegistry Players { get; } = new PlayerRegistry();

        public AntiCheatEngine AntiCheat { get; }

        /// <summary>Validates state updates from the clients that own non-player entities.</summary>
        public OwnedEntityValidator EntityValidator { get; }

        public CombatSettings Combat { get; }

        /// <summary>Identities that are not allowed in. Persisted, and checked before anything else.</summary>
        public BanList Bans { get; } = new BanList();

        /// <summary>
        /// The command table admin requests from clients are run against. Set by the
        /// host that owns the stdin console, so the two share one implementation; when
        /// nothing sets it, network admin commands report that rather than silently
        /// doing nothing.
        /// </summary>
        public IAdminSurface AdminSurface { get; set; } = new UnavailableAdminSurface();

        public NetworkedEntityManager Entities { get; }

        /// <summary>Missions, callouts, jobs — anything with objectives and participants.</summary>
        public ActivityManager Activities { get; }

        public RpcDispatcher<PlayerSession> Rpc { get; }

        /// <summary>What a server-side mod talks to.</summary>
        public ServerModSdk Mods { get; }

        /// <summary>Mods the server itself declares. Compared against each client's manifest at join.</summary>
        public ModManifest Manifest { get; } = new ModManifest();

        public double Now => _now;

        public bool IsRunning { get; private set; }

        public void Start(double now)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            IsRunning = true;
            _now = now;
            _lastTickTime = now;
            _lastSnapshotTime = now;
            _lastSaveTime = now;

            Registry.Lock();
            _persistence.Initialize();

            World.State.Environment.ClockScale = Config.ClockScale;
            RestoreWorld();

            Log.Success(LogCategory.Server, $"'{Config.ServerName}' listening on {_transport.LocalEndPoint}");
            Log.Info(LogCategory.Server, $"Tick {Config.TickRate} Hz, snapshots {Config.SnapshotRate} Hz, max {Config.MaxPlayers} players");
            Log.Info(LogCategory.Persistence, "Persistence: " + _persistence.Describe());
            Log.Info(LogCategory.Server, $"Anti-cheat level: {Config.AntiCheat}");

            // Loud, and at Warning, because a setting that was typed and does nothing
            // is indistinguishable from one that was applied and ignored — and the
            // operator is the only person who can tell the difference.
            foreach (string unknown in Config.UnknownKeys)
            {
                Log.Warning(
                    LogCategory.Server,
                    $"server.ini setting '{unknown}' is not recognised by this build and has no effect. " +
                    "Check the spelling against docs/INSTALL.md.");
            }
        }

        /// <summary>Advances the simulation to <paramref name="now"/>. Safe to call more often than the tick rate.</summary>
        public void Tick(double now)
        {
            if (!IsRunning)
            {
                return;
            }

            _now = now;
            ReceiveDatagrams();
            ProcessSessionMessages();
            ExpirePendingAuthentications();

            double delta = now - _lastTickTime;
            if (delta >= Config.TickIntervalSeconds)
            {
                _lastTickTime = now;
                World.AdvanceTick(delta);
            }

            UpdateDeaths();
            Entities.UpdateOwnership(Players, now);

            Mods.CurrentTime = now;
            Activities.Update(now);
            Activities.CleanUpFinished(now, Config.FinishedActivityLingerSeconds);
            Rpc.Update(now);

            if (now - _lastSnapshotTime >= Config.SnapshotIntervalSeconds)
            {
                _lastSnapshotTime = now;

                // Immediately before the snapshot, so the list a client receives
                // agrees with the seats the same snapshot carries on the characters.
                RebuildVehicleOccupants();
                SendSnapshots();
            }

            DropTimedOutSessions();
            FlushPeers();

            if (Config.PersistenceEnabled
                && Config.SaveIntervalSeconds > 0
                && now - _lastSaveTime >= Config.SaveIntervalSeconds)
            {
                _lastSaveTime = now;
                SaveWorld("periodic");
            }
        }

        public void Stop(DisconnectReason reason = DisconnectReason.ServerShutdown)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            foreach (PlayerSession session in new List<PlayerSession>(Players.Sessions))
            {
                SendDisconnect(session, reason, DisconnectReasonText.Describe(reason));
                session.Peer.Flush(_now);
                PersistPlayer(session);
                Players.Remove(session);
            }

            SaveWorld("shutdown");
            Log.Info(LogCategory.Server, "Server stopped.");
        }

        public void Dispose()
        {
            Stop();
            _persistence.Dispose();
            _transport.Dispose();
        }

        // ------------------------------------------------------------------
        // Inbound
        // ------------------------------------------------------------------
        private void ReceiveDatagrams()
        {
            while (_transport.TryReceive(out IPEndPoint source, out byte[] payload))
            {
                // Connectionless packets are checked first, even from an endpoint that
                // already has a session: a repeated connect request means our accept
                // was lost, and routing it to the session peer would drop it silently
                // and strand the client until it gave up.
                if (ConnectionlessPacket.TryRead(payload, out NetMessageType type, out byte[] body))
                {
                    if (type == NetMessageType.ConnectRequest)
                    {
                        HandleConnectRequest(source, body);
                    }
                    else if (type == NetMessageType.ConnectProof)
                    {
                        HandleConnectProof(source, body);
                    }
                    else if (Config.VerboseNetworkLogging)
                    {
                        Log.Debug(LogCategory.Network, $"Ignored connectionless {type} from {source}");
                    }

                    continue;
                }

                if (Players.TryGetByEndPoint(source, out PlayerSession session))
                {
                    if (!session.Peer.HandleDatagram(payload, _now) && Config.VerboseNetworkLogging)
                    {
                        Log.Debug(LogCategory.Network, $"Dropped an undecodable packet from {source}");
                    }

                    continue;
                }

                if (Config.VerboseNetworkLogging)
                {
                    Log.Debug(LogCategory.Network, $"Ignored {payload.Length} bytes from unknown peer {source}");
                }
            }
        }

        /// <summary>How long a challenge stays answerable.</summary>
        public const double AuthenticationTimeoutSeconds = 15.0;

        private void HandleConnectRequest(IPEndPoint source, byte[] body)
        {
            ConnectRequestMessage request;
            try
            {
                request = ConnectRequestMessage.Deserialize(body);
            }
            catch (NetSerializationException exception)
            {
                Log.Warning(LogCategory.Security, $"Malformed connect request from {source}: {exception.Message}");
                return;
            }

            // A retry of a request we already answered: resend the same accept. The
            // handshake has to be idempotent because its reply is the one packet with
            // no reliability layer behind it yet.
            if (Players.TryGetByEndPoint(source, out PlayerSession established)
                && string.Equals(established.IdentityToken, request.IdentityToken, StringComparison.Ordinal)
                && established.HandshakeNonce == request.ClientNonce)
            {
                _transport.SendConnectionless(source, NetMessageType.ConnectAccept, established.AcceptPayload);
                if (Config.VerboseNetworkLogging)
                {
                    Log.Debug(LogCategory.Network, $"Re-sent the accept to {established}; the first one was lost.");
                }

                return;
            }

            if (request.ProtocolVersion != ProtocolConstants.ProtocolVersion)
            {
                RejectConnection(
                    source,
                    request.ClientNonce,
                    DisconnectReason.ProtocolMismatch,
                    $"server speaks protocol {ProtocolConstants.ProtocolVersion}, client speaks {request.ProtocolVersion}");
                return;
            }

            string name = SanitizeName(request.PlayerName);
            if (name.Length == 0)
            {
                RejectConnection(source, request.ClientNonce, DisconnectReason.InvalidName, "player name is empty or unusable");
                return;
            }

            if (!string.IsNullOrEmpty(Config.Password) && !string.Equals(Config.Password, request.Password, StringComparison.Ordinal))
            {
                RejectConnection(source, request.ClientNonce, DisconnectReason.BadPassword, "wrong password");
                return;
            }

            List<ModCompatibilityEntry> report = ModCompatibilityChecker.Compare(Manifest, request.Manifest);
            if (Config.EnforceRequiredMods && ModCompatibilityChecker.HasBlockingIssue(report))
            {
                RejectConnection(
                    source,
                    request.ClientNonce,
                    DisconnectReason.IncompatibleMods,
                    BuildBlockingModMessage(report));
                return;
            }

            BanEntry? ban = Bans.Find(request.IdentityToken, DateTime.UtcNow);
            if (ban != null)
            {
                RejectConnection(
                    source,
                    request.ClientNonce,
                    DisconnectReason.Banned,
                    ban.ExpiresAt.HasValue
                        ? $"banned until {ban.ExpiresAt.Value:yyyy-MM-dd HH:mm}Z: {ban.Reason}"
                        : $"banned: {ban.Reason}");
                return;
            }

            if (Config.RequireAuthentication)
            {
                if (!IdentityKey.LooksLikePublicKey(request.IdentityToken))
                {
                    RejectConnection(
                        source,
                        request.ClientNonce,
                        DisconnectReason.AuthenticationFailed,
                        "this server requires a signing identity; the client sent a legacy identity token");
                    return;
                }

                Challenge(source, request, name, report);
                return;
            }

            Admit(source, request, name, report);
        }

        /// <summary>
        /// Issues the proof-of-possession challenge, or re-issues the identical one
        /// for a retried request.
        /// <para>
        /// Re-issuing the same nonce matters. The challenge is connectionless and so
        /// unreliable; if it is lost the client retries its connect request, and a
        /// fresh nonce would invalidate a proof already in flight for the first one —
        /// telling a legitimate player their key is wrong.
        /// </para>
        /// </summary>
        private void Challenge(
            IPEndPoint source, ConnectRequestMessage request, string name, List<ModCompatibilityEntry> report)
        {
            if (!_pendingAuth.TryGetValue(source, out PendingAuthentication? pending)
                || pending.Request.ClientNonce != request.ClientNonce)
            {
                pending = new PendingAuthentication(
                    request,
                    name,
                    report,
                    IdentityKey.CreateServerNonce(),
                    Config.EncryptSessions ? EphemeralKeyExchange.Create() : null,
                    _now);

                _pendingAuth[source] = pending;
            }

            var challenge = new ConnectChallengeMessage
            {
                ClientNonce = request.ClientNonce,
                ServerNonce = pending.ServerNonce,
                ServerName = Config.ServerName,
                EphemeralPublicKey = pending.Exchange?.PublicKey ?? Array.Empty<byte>(),
            };

            _transport.SendConnectionless(source, NetMessageType.ConnectChallenge, challenge.Serialize());
        }

        private void HandleConnectProof(IPEndPoint source, byte[] body)
        {
            ConnectProofMessage proof;
            try
            {
                proof = ConnectProofMessage.Deserialize(body);
            }
            catch (NetSerializationException exception)
            {
                Log.Warning(LogCategory.Security, $"Malformed connect proof from {source}: {exception.Message}");
                return;
            }

            if (!_pendingAuth.TryGetValue(source, out PendingAuthentication? pending)
                || pending.Request.ClientNonce != proof.ClientNonce)
            {
                // The proof for a handshake that already succeeded. This is the normal
                // case when the accept was lost: the client resends its proof, not its
                // request, so the accept has to be resendable from here too — exactly
                // as it is from the request path. Without this a lost accept strands
                // the client for good, because the pending record is gone and the
                // request it would retry with is never sent again.
                if (Players.TryGetByEndPoint(source, out PlayerSession settled)
                    && settled.HandshakeNonce == proof.ClientNonce
                    && string.Equals(settled.IdentityToken, proof.PublicKey, StringComparison.Ordinal))
                {
                    _transport.SendConnectionless(source, NetMessageType.ConnectAccept, settled.AcceptPayload);
                    if (Config.VerboseNetworkLogging)
                    {
                        Log.Debug(LogCategory.Network, $"Re-sent the accept to {settled} after a repeated proof.");
                    }

                    return;
                }

                // Otherwise a proof for a challenge that has expired. Dropped rather
                // than rejected: rejecting would let a stray duplicate tear down a
                // session that is working.
                if (Config.VerboseNetworkLogging)
                {
                    Log.Debug(LogCategory.Security, $"Unmatched connect proof from {source}.");
                }

                return;
            }

            // The key in the proof must be the one the request claimed. Without this
            // check a client could claim one identity and prove another.
            if (!string.Equals(proof.PublicKey, pending.Request.IdentityToken, StringComparison.Ordinal))
            {
                _pendingAuth.Remove(source);
                Log.Warning(
                    LogCategory.Security,
                    $"{source} proved a different identity than it claimed. Rejected.");
                RejectConnection(
                    source, proof.ClientNonce, DisconnectReason.AuthenticationFailed, "the proof names a different identity");
                return;
            }

            byte[] expected = IdentityKey.BuildChallenge(
                proof.ClientNonce,
                pending.ServerNonce,
                Config.ServerName,
                pending.Exchange?.PublicKey,
                proof.EphemeralPublicKey);

            if (!IdentityKey.Verify(proof.PublicKey, expected, proof.Signature))
            {
                _pendingAuth.Remove(source);
                Log.Warning(
                    LogCategory.Security,
                    $"{source} failed to prove identity {IdentityKey.FingerprintOf(proof.PublicKey)}.");
                RejectConnection(
                    source, proof.ClientNonce, DisconnectReason.AuthenticationFailed, "signature did not verify");
                return;
            }

            byte[]? sessionSecret = null;
            if (pending.Exchange != null)
            {
                if (!EphemeralKeyExchange.IsWellFormed(proof.EphemeralPublicKey))
                {
                    _pendingAuth.Remove(source);
                    RejectConnection(
                        source,
                        proof.ClientNonce,
                        DisconnectReason.AuthenticationFailed,
                        "this server encrypts sessions and the client sent no key exchange");
                    return;
                }

                try
                {
                    sessionSecret = pending.Exchange.Agree(proof.EphemeralPublicKey);
                }
                catch (Exception exception)
                {
                    // A point that is not on the curve, or any other malformed key.
                    // Refused rather than allowed to fall back to plaintext: silently
                    // downgrading is how encryption stops meaning anything.
                    _pendingAuth.Remove(source);
                    Log.Warning(LogCategory.Security, $"Key agreement with {source} failed: {exception.Message}");
                    RejectConnection(
                        source, proof.ClientNonce, DisconnectReason.AuthenticationFailed, "key agreement failed");
                    return;
                }
                finally
                {
                    pending.Exchange.Dispose();
                }
            }

            _pendingAuth.Remove(source);
            Admit(source, pending.Request, pending.Name, pending.ModReport, sessionSecret);
        }

        /// <summary>Creates the session. Everything before this point is admission control.</summary>
        private void Admit(
            IPEndPoint source,
            ConnectRequestMessage request,
            string name,
            List<ModCompatibilityEntry> report,
            byte[]? sessionSecret = null)
        {
            // A reconnect from an identity that is still "connected" replaces the old
            // session; the previous socket is almost always already dead.
            PlayerSession? existing = Players.FindByIdentity(request.IdentityToken);
            bool reconnect = existing != null;
            if (existing != null)
            {
                Log.Info(LogCategory.Server, $"{existing.Name} reconnected from {source}; replacing the previous session.");
                RemoveSession(existing, DisconnectReason.ClientQuit, notifyPeer: false, announce: false);
            }

            if (Players.Count >= Config.MaxPlayers)
            {
                RejectConnection(source, request.ClientNonce, DisconnectReason.ServerFull, $"server is full ({Config.MaxPlayers} players)");
                return;
            }

            uint sessionId = NextSessionId();
            var peer = new NetPeer(_transport, source, sessionId, _now);

            if (sessionSecret != null)
            {
                // Attached before the accept is sent, but the accept itself is
                // connectionless and therefore not encrypted — it has to be readable
                // by a client that does not have a peer yet. Everything after it is.
                peer.Crypto = SessionCrypto.FromSharedSecret(sessionSecret, isServer: true);
            }
            var session = new PlayerSession(Players.AllocatePlayerId(), peer, name, request.IdentityToken)
            {
                ConnectedAt = _now,
                Manifest = request.Manifest,
                Bandwidth = new BandwidthShaper(Config.SnapshotByteBudget, Config.MinimumSnapshotByteBudget),
            };

            PersistedPlayer? saved = _persistence.LoadPlayer(request.IdentityToken);
            PlayerEntity entity = CreatePlayerEntity(session, saved);
            session.EntityId = entity.Id;
            if (saved != null)
            {
                session.Role = (PlayerRole)saved.Role;
            }

            Players.Add(session);

            // The player is being placed into the world by the server, at a position
            // their client has not simulated towards — their persisted one, or the
            // default spawn. Their in-flight updates describe somewhere else entirely.
            HoldClientAuthority(session);

            var accept = new ConnectAcceptMessage
            {
                SessionId = sessionId,
                PlayerId = session.PlayerId,
                PlayerEntityId = entity.Id,
                ClientNonce = request.ClientNonce,
                TickRate = Config.TickRate,
                SnapshotRate = Config.SnapshotRate,
                ClientUpdateRate = ProtocolConstants.DefaultClientUpdateRate,
                ServerTime = World.State.ServerTime,
                ServerName = Config.ServerName,
                ServerVersion = BuildInfo.Version,
                Restored = saved != null,
            };

            accept.ModReport.AddRange(report);
            session.HandshakeNonce = request.ClientNonce;
            session.AcceptPayload = accept.Serialize();
            _transport.SendConnectionless(source, NetMessageType.ConnectAccept, session.AcceptPayload);

            Log.Success(
                LogCategory.Server,
                $"{name} joined as player {session.PlayerId} (entity {entity.Id}) from {source}" +
                (saved != null ? " — restored from persistence" : string.Empty));

            BroadcastServerEvent(
                reconnect ? ServerEventKind.PlayerReconnected : ServerEventKind.PlayerJoined,
                session.PlayerId,
                $"{name} joined the session.",
                exclude: session);

            foreach (ModCompatibilityEntry entry in report)
            {
                if (entry.Status != ModCompatibility.Compatible)
                {
                    Log.Warning(LogCategory.Mod, $"{name}: {entry.ModId} — {entry.Status}: {entry.Detail}");
                }
            }
        }

        /// <summary>
        /// A connect request waiting on its proof. Held by endpoint, for at most
        /// <see cref="AuthenticationTimeoutSeconds"/>, so a client that starts a
        /// handshake and never finishes it costs one small record and not a slot.
        /// </summary>
        private sealed class PendingAuthentication
        {
            public PendingAuthentication(
                ConnectRequestMessage request,
                string name,
                List<ModCompatibilityEntry> modReport,
                byte[] serverNonce,
                EphemeralKeyExchange? exchange,
                double issuedAt)
            {
                Request = request;
                Name = name;
                ModReport = modReport;
                ServerNonce = serverNonce;
                Exchange = exchange;
                IssuedAt = issuedAt;
            }

            /// <summary>Discarded once the session key is derived; a fresh one per connection.</summary>
            public EphemeralKeyExchange? Exchange { get; }

            public ConnectRequestMessage Request { get; }

            public string Name { get; }

            public List<ModCompatibilityEntry> ModReport { get; }

            public byte[] ServerNonce { get; }

            public double IssuedAt { get; }
        }

        private void ExpirePendingAuthentications()
        {
            if (_pendingAuth.Count == 0)
            {
                return;
            }

            _expiredAuth.Clear();
            foreach (KeyValuePair<IPEndPoint, PendingAuthentication> pair in _pendingAuth)
            {
                if (_now - pair.Value.IssuedAt > AuthenticationTimeoutSeconds)
                {
                    _expiredAuth.Add(pair.Key);
                }
            }

            foreach (IPEndPoint endPoint in _expiredAuth)
            {
                _pendingAuth.Remove(endPoint);
                if (Config.VerboseNetworkLogging)
                {
                    Log.Debug(LogCategory.Security, $"Authentication from {endPoint} expired unanswered.");
                }
            }
        }

        private void ProcessSessionMessages()
        {
            foreach (PlayerSession session in Players.Sessions)
            {
                while (session.Peer.TryDequeue(out ReceivedMessage message))
                {
                    try
                    {
                        HandleMessage(session, message);
                    }
                    catch (NetSerializationException exception)
                    {
                        Log.Warning(
                            LogCategory.Security,
                            $"Undecodable {message.Type} from {session}: {exception.Message}");
                    }
                }
            }

            ReapPendingSessions();
        }

        private void HandleMessage(PlayerSession session, ReceivedMessage message)
        {
            switch (message.Type)
            {
                case NetMessageType.ClientStateUpdate:
                    HandleClientStateUpdate(session, ClientStateUpdateMessage.Deserialize(message.Payload));
                    break;

                case NetMessageType.SnapshotAck:
                    session.Replication.Acknowledge(SnapshotAckMessage.Deserialize(message.Payload).SnapshotId);
                    break;

                case NetMessageType.ResyncRequest:
                {
                    ResyncRequestMessage resync = ResyncRequestMessage.Deserialize(message.Payload);
                    Log.Warning(
                        LogCategory.Network,
                        $"{session} requested a resync after snapshot {resync.LastAppliedSnapshotId}: {resync.Reason}");
                    session.Replication.RequestResync();
                    break;
                }

                case NetMessageType.ChatMessage:
                {
                    ChatMessage chat = ChatMessage.Deserialize(message.Payload);
                    string text = chat.Text.Trim();
                    if (text.Length == 0)
                    {
                        break;
                    }

                    var outgoing = new ChatMessage { PlayerId = session.PlayerId, SenderName = session.Name, Text = text };
                    Broadcast(NetMessageType.ChatMessage, outgoing.Serialize(), DeliveryMethod.ReliableOrdered);
                    Log.Info(LogCategory.Server, $"[chat] {session.Name}: {text}");
                    break;
                }

                case NetMessageType.Ping:
                {
                    TimeSyncMessage ping = TimeSyncMessage.Deserialize(message.Payload);
                    var pong = new TimeSyncMessage { ClientTime = ping.ClientTime, ServerTime = World.State.ServerTime };
                    session.Peer.Send(NetMessageType.Pong, pong.Serialize(), DeliveryMethod.Unreliable);
                    break;
                }

                case NetMessageType.Disconnect:
                {
                    DisconnectMessage disconnect = DisconnectMessage.Deserialize(message.Payload);
                    Log.Info(LogCategory.Server, $"{session} disconnected: {DisconnectReasonText.Describe(disconnect.Reason)}");
                    session.PendingRemoval = true;
                    break;
                }

                case NetMessageType.EntitySpawnRequest:
                {
                    EntitySpawnRequestMessage request = EntitySpawnRequestMessage.Deserialize(message.Payload);
                    EntityEventMessage reply = Entities.HandleSpawnRequest(session, request, Players);
                    session.Peer.Send(NetMessageType.EntityEvent, reply.Serialize(), DeliveryMethod.ReliableOrdered);

                    if (reply.Kind == EntityEventKind.SpawnRejected)
                    {
                        Log.Warning(LogCategory.Entity, $"Refused a spawn from {session}: {reply.Detail}");
                    }

                    break;
                }

                case NetMessageType.OwnedEntityUpdate:
                    Entities.HandleOwnedUpdate(
                        session, OwnedEntityUpdateMessage.Deserialize(message.Payload), EntityValidator, _now);
                    break;

                case NetMessageType.EntityReleaseRequest:
                {
                    EntityEventMessage? reply = Entities.HandleRelease(
                        session, EntityReleaseRequestMessage.Deserialize(message.Payload), Players);

                    if (reply != null)
                    {
                        Broadcast(NetMessageType.EntityEvent, reply.Serialize(), DeliveryMethod.ReliableOrdered);
                    }

                    break;
                }

                case NetMessageType.DamageReport:
                    HandleDamageReport(session, DamageReportMessage.Deserialize(message.Payload));
                    break;

                case NetMessageType.WeaponShot:
                    HandleWeaponShot(session, WeaponShotMessage.Deserialize(message.Payload));
                    break;

                case NetMessageType.ModRpcRequest:
                {
                    ModRpcRequestMessage request = ModRpcRequestMessage.Deserialize(message.Payload);
                    ModRpcResponseMessage response = Rpc.HandleRequest(request, session);
                    session.Peer.Send(
                        NetMessageType.ModRpcResponse, response.Serialize(), DeliveryMethod.ReliableOrdered);
                    break;
                }

                case NetMessageType.ModRpcResponse:
                    Rpc.HandleResponse(ModRpcResponseMessage.Deserialize(message.Payload));
                    break;

                case NetMessageType.KeepAlive:
                    break;

                case NetMessageType.AdminCommand:
                    HandleAdminCommand(session, AdminCommandMessage.Deserialize(message.Payload));
                    break;

                case NetMessageType.ModEvent:
                {
                    ModEventMessage modEvent = ModEventMessage.Deserialize(message.Payload);
                    if (!Mods.Dispatch(modEvent.Name, session, modEvent.Payload) && Config.VerboseNetworkLogging)
                    {
                        Log.Debug(LogCategory.Mod, $"No server handler for mod event '{modEvent.Name}'.");
                    }

                    break;
                }

                default:
                    if (Config.VerboseNetworkLogging)
                    {
                        Log.Debug(LogCategory.Network, $"Unhandled message {message.Type} from {session}");
                    }

                    break;
            }
        }

        private void HandleClientStateUpdate(PlayerSession session, ClientStateUpdateMessage update)
        {
            session.Replication.Acknowledge(update.AcknowledgedSnapshotId);
            session.LastStateUpdateAt = _now;

            // Recorded even when the update is then ignored or rejected. The client is
            // asking "have you seen my report yet", and the honest answer is yes —
            // what the server did with it is a separate question, answered by the
            // state in the snapshot itself.
            if (update.UpdateSequence > session.LastProcessedUpdateSequence)
            {
                session.LastProcessedUpdateSequence = update.UpdateSequence;
            }

            PlayerEntity? entity = World.GetPlayer(session.EntityId);
            if (entity == null)
            {
                return;
            }

            if (IsAuthorityHeld(session, update.AcknowledgedSnapshotId))
            {
                return;
            }

            var proposal = new PlayerStateProposal
            {
                Position = update.Position,
                Velocity = update.Velocity,
                AimPosition = update.AimPosition,
                Heading = update.Heading,
                Health = update.Health,
                Armor = update.Armor,
                InVehicle = (update.Flags & PlayerFlags.InVehicle) != 0,
                Invincible = (update.Flags & PlayerFlags.Invincible) != 0,
            };

            ValidationOutcome outcome = AntiCheat.ValidatePlayerState(entity, proposal, session.Validation, _now);
            if (!outcome.Accepted)
            {
                HandleViolations(session, outcome);

                // The update is discarded. The server's own state stands and the next
                // snapshot carries it back to the client as a correction.
                return;
            }

            entity.Position = update.Position;
            entity.Velocity = update.Velocity;
            entity.Heading = update.Heading;
            entity.Armor = update.Armor;
            entity.Movement = update.Movement;

            // Held while the client has not yet applied a model the server set. This
            // one is held for longer than the rest by nature: the client cannot change
            // a player's model in a vehicle, and has to wait for it to stream in.
            if (!IsModelHeld(session, update.AcknowledgedSnapshotId))
            {
                entity.ModelHash = update.ModelHash;
            }
            entity.CurrentWeaponHash = update.CurrentWeaponHash;
            entity.Ammo = update.Ammo;
            entity.WeaponTint = update.WeaponTint;
            entity.WeaponComponents.Clear();
            entity.WeaponComponents.AddRange(update.WeaponComponents);
            entity.AimPosition = update.AimPosition;
            entity.InteriorId = update.InteriorId;

            // Clamped, not trusted: the field is a byte on the wire and GTA V has six
            // levels, so a client claiming 200 would be replicated and printed as 200.
            //
            // Held while the server has a wanted level of its own that the client has
            // not seen yet. A player who logged out with three stars had them restored
            // into the entity, saved, printed by the admin console — and erased by the
            // first update from a client whose fresh session is at zero, before the
            // snapshot carrying them was ever written.
            if (!IsWantedHeld(session, update.AcknowledgedSnapshotId))
            {
                entity.WantedLevel = update.WantedLevel > 5 ? (byte)5 : update.WantedLevel;
            }
            entity.AnimationHash = update.AnimationHash;

            // The pose is only meaningful while the flag is set, and a stale one is
            // worse than none: it would be replayed onto the next fall.
            entity.Ragdoll = (update.Flags & PlayerFlags.Ragdoll) != 0 ? update.Ragdoll : RagdollPose.None;
            entity.Appearance.CopyFrom(update.Appearance);

            ApplyHealth(session, entity, update);
            World.Touch(entity);
        }

        /// <summary>
        /// Health and the death flag are arbitrated, not copied.
        /// <para>
        /// A dead player's client keeps sending updates, and the client is not allowed
        /// to decide when it stops being dead — otherwise a trainer's "heal" key
        /// resurrects instantly and the respawn timer means nothing. So while the
        /// server considers a player dead, their reported health is ignored entirely
        /// and only <see cref="Respawn"/> brings them back.
        /// </para>
        /// </summary>
        private void ApplyHealth(PlayerSession session, PlayerEntity entity, ClientStateUpdateMessage update)
        {
            if (session.IsDead)
            {
                entity.Health = 0;
                entity.SetFlag(PlayerFlags.Dead, true);
                return;
            }

            bool clientReportsDeath = update.Health <= 0 || (update.Flags & PlayerFlags.Dead) != 0;
            if (clientReportsDeath)
            {
                Kill(session, entity);
                return;
            }

            if (IsHealthHeld(session, update.AcknowledgedSnapshotId))
            {
                // Keep everything except the vitals the server has just decided.
                entity.Flags = (update.Flags & ~PlayerFlags.Dead) | (entity.Flags & PlayerFlags.Dead);
                return;
            }

            entity.Health = update.Health;
            entity.Flags = update.Flags;
        }

        /// <summary>True while the client has not yet seen a health change the server made.</summary>
        private bool IsHealthHeld(PlayerSession session, uint acknowledgedSnapshotId)
        {
            if (session.PendingHealthHold)
            {
                return true;
            }

            if (session.HealthHoldSnapshot == 0)
            {
                return false;
            }

            if (acknowledgedSnapshotId >= session.HealthHoldSnapshot || _now >= session.HealthHoldExpiry)
            {
                session.HealthHoldSnapshot = 0;
                session.HealthHoldExpiry = 0;
                return false;
            }

            return true;
        }

        /// <summary>Declares that the server has just changed a player's vitals.</summary>
        private void HoldHealthAuthority(PlayerSession session)
        {
            session.PendingHealthHold = true;
            session.HealthHoldSnapshot = 0;
        }

        /// <summary>True while the client has not yet seen a wanted level the server set.</summary>
        private bool IsWantedHeld(PlayerSession session, uint acknowledgedSnapshotId)
        {
            if (session.PendingWantedHold)
            {
                return true;
            }

            if (session.WantedHoldSnapshot == 0)
            {
                return false;
            }

            if (acknowledgedSnapshotId >= session.WantedHoldSnapshot || _now >= session.WantedHoldExpiry)
            {
                session.WantedHoldSnapshot = 0;
                session.WantedHoldExpiry = 0;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Declares that the server has just set a player's wanted level itself, so the
        /// client's reports are ignored until it has seen the change. The hold expires
        /// on a timeout as well as on acknowledgement: a client that cannot apply it —
        /// an older build, or a game that refuses the level — must not freeze the field
        /// for the rest of the session.
        /// </summary>
        private void HoldWantedAuthority(PlayerSession session)
        {
            session.PendingWantedHold = true;
            session.WantedHoldSnapshot = 0;
        }

        /// <summary>True while the client has not yet applied a model the server set.</summary>
        private bool IsModelHeld(PlayerSession session, uint acknowledgedSnapshotId)
        {
            if (session.PendingModelHold)
            {
                return true;
            }

            if (session.ModelHoldSnapshot == 0)
            {
                return false;
            }

            if (acknowledgedSnapshotId >= session.ModelHoldSnapshot || _now >= session.ModelHoldExpiry)
            {
                session.ModelHoldSnapshot = 0;
                session.ModelHoldExpiry = 0;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Sets a player's model from the server side. The client may take several
        /// seconds to apply it, or fail to — it says so in its log and its diagnostic
        /// bundle when it gives up — so the server's value is what other players see
        /// either way.
        /// </summary>
        public bool SetPlayerModel(PlayerSession session, uint modelHash)
        {
            PlayerEntity? entity = World.GetPlayer(session.EntityId);
            if (entity == null || modelHash == 0)
            {
                return false;
            }

            entity.ModelHash = modelHash;
            session.PendingModelHold = true;
            session.ModelHoldSnapshot = 0;
            World.Touch(entity);
            return true;
        }

        /// <summary>
        /// Sets a player's wanted level from the server side: a restored save, an admin
        /// command, a mod. Returns false when the session has no entity in the world.
        /// </summary>
        public bool SetWantedLevel(PlayerSession session, byte level)
        {
            PlayerEntity? entity = World.GetPlayer(session.EntityId);
            if (entity == null)
            {
                return false;
            }

            entity.WantedLevel = level > 5 ? (byte)5 : level;
            HoldWantedAuthority(session);
            World.Touch(entity);
            return true;
        }

        /// <summary>Kills a player from the server side, e.g. an admin command.</summary>
        public bool KillPlayer(PlayerSession session)
        {
            PlayerEntity? entity = World.GetPlayer(session.EntityId);
            if (entity == null || session.IsDead)
            {
                return false;
            }

            Kill(session, entity);
            return true;
        }

        private void Kill(PlayerSession session, PlayerEntity entity)
        {
            session.DiedAt = _now;
            HoldHealthAuthority(session);
            entity.Health = 0;
            entity.SetFlag(PlayerFlags.Dead, true);
            entity.Velocity = NetVector3.Zero;
            World.Touch(entity);

            Log.Info(LogCategory.Server, $"{session.Name} died at {entity.Position}.");
            BroadcastServerEvent(ServerEventKind.PlayerDied, session.PlayerId, $"{session.Name} died.", null);
        }

        private void UpdateDeaths()
        {
            foreach (PlayerSession session in Players.Sessions)
            {
                if (!session.IsDead || session.PendingRemoval)
                {
                    continue;
                }

                if (_now - session.DiedAt < Config.RespawnDelaySeconds)
                {
                    continue;
                }

                Respawn(session);
            }
        }

        /// <summary>
        /// Rebuilds every vehicle's occupant list from the characters that report
        /// riding in it.
        /// <para>
        /// The seat is replicated on the character — `VehicleId` and `VehicleSeat` —
        /// because that is the direction the client can actually observe it. The list
        /// on the vehicle is the same fact indexed the other way, which is what a mod
        /// asking "who is in this car" needs, and it was validated, cloned and
        /// persisted while never being filled in by anybody.
        /// </para>
        /// <para>
        /// Derived here rather than reported by clients, because a client reporting
        /// who else is in its car is a client asserting something about other players.
        /// </para>
        /// </summary>
        private void RebuildVehicleOccupants()
        {
            foreach (NetEntity entity in World.State.Entities)
            {
                if (entity is VehicleEntity vehicle && vehicle.Occupants.Count > 0)
                {
                    vehicle.Occupants.Clear();
                }
            }

            foreach (NetEntity entity in World.State.Entities)
            {
                if (entity is not CharacterEntity character
                    || !character.VehicleId.IsValid
                    || character.VehicleSeat <= -2)
                {
                    continue;
                }

                if (World.TryGet(character.VehicleId, out NetEntity found)
                    && found is VehicleEntity ride
                    && ride.Occupants.Count < VehicleStateLists.MaxOccupants)
                {
                    ride.SetOccupant(character.VehicleSeat, character.Id);
                }
            }
        }

        /// <summary>
        /// Moves a player somewhere the server chose.
        /// <para>
        /// Writing <see cref="NetEntity.Position"/> directly is not enough: the client
        /// keeps reporting where it thinks it is, and the next update it sends drags
        /// the player straight back. Any server-initiated move has to hold the client's
        /// authority until it has seen the move, which is what this does.
        /// </para>
        /// </summary>
        public bool TeleportPlayer(PlayerSession session, NetVector3 position, float heading, uint dimension = 0, int interiorId = 0)
        {
            PlayerEntity? entity = World.GetPlayer(session.EntityId);
            if (entity == null)
            {
                return false;
            }

            entity.Position = position;
            entity.Heading = heading;
            entity.Velocity = NetVector3.Zero;
            entity.Dimension = dimension;
            entity.InteriorId = interiorId;
            World.Touch(entity);

            HoldClientAuthority(session);
            Log.Info(LogCategory.Server, $"{session.Name} was moved to {position}.");
            return true;
        }

        /// <summary>Moves a dead player to the nearest hospital and restores them.</summary>
        public void Respawn(PlayerSession session)
        {
            PlayerEntity? entity = World.GetPlayer(session.EntityId);
            if (entity == null)
            {
                session.DiedAt = 0;
                return;
            }

            RespawnPoint point = RespawnPoints.Nearest(entity.Position);

            session.DiedAt = 0;
            entity.Position = point.Position;
            entity.Heading = point.Heading;
            entity.Velocity = NetVector3.Zero;
            entity.Health = entity.MaxHealth;
            entity.Armor = 0;
            entity.SetFlag(PlayerFlags.Dead, false);
            entity.SetFlag(PlayerFlags.Ragdoll, false);
            entity.Ragdoll = RagdollPose.None;
            entity.InteriorId = 0;
            World.Touch(entity);

            // The server just teleported the player and refilled their health. Their
            // client is still reporting a corpse where they fell; ignore it until they
            // have seen the respawn.
            HoldClientAuthority(session);

            Log.Info(LogCategory.Server, $"{session.Name} respawned at {point.Name}.");
            BroadcastServerEvent(
                ServerEventKind.PlayerRespawned, session.PlayerId, $"{session.Name} respawned at {point.Name}.", null);
        }

        /// <summary>
        /// True while this particular update predates the client seeing a
        /// server-initiated move.
        /// <para>
        /// The test is against the id carried by <em>this</em> update, not against the
        /// session's high-water mark. Snapshot acknowledgements travel in their own
        /// unreliable message as well as piggybacked here, so a standalone
        /// acknowledgement can overtake a state update sent before it. Releasing on the
        /// high-water mark would then let that older update through, and it still
        /// describes the position the server just moved the player out of.
        /// </para>
        /// </summary>
        private bool IsAuthorityHeld(PlayerSession session, uint acknowledgedSnapshotId)
        {
            // The move has happened but the snapshot announcing it has not gone out
            // yet, so there is no id for the client to acknowledge. Anything arriving
            // in this window necessarily predates the move.
            if (session.PendingAuthorityHold)
            {
                return true;
            }

            if (session.AuthorityHoldSnapshot == 0)
            {
                return false;
            }

            if (acknowledgedSnapshotId >= session.AuthorityHoldSnapshot)
            {
                ReleaseAuthorityHold(session, "acknowledged");
                return false;
            }

            if (_now >= session.AuthorityHoldExpiry)
            {
                // The acknowledgement never arrived. Holding forever would leave the
                // player unable to move at all, which is worse than accepting a
                // possibly stale update and correcting from there.
                Log.Warning(
                    LogCategory.Network,
                    $"{session} never acknowledged snapshot {session.AuthorityHoldSnapshot}; releasing the authority hold.");

                ReleaseAuthorityHold(session, "timed out");
                return false;
            }

            return true;
        }

        private void ReleaseAuthorityHold(PlayerSession session, string reason)
        {
            session.AuthorityHoldSnapshot = 0;
            session.AuthorityHoldExpiry = 0;

            // The client is about to resume from a position the server chose, so the
            // movement budget and health baseline both restart from here.
            session.Validation.GrantGrace(_now, Config.ServerMoveGraceSeconds);

            if (Config.VerboseNetworkLogging)
            {
                Log.Debug(LogCategory.Network, $"Authority hold on {session} released ({reason}).");
            }
        }

        /// <summary>
        /// Declares that the server has just moved this player, so their in-flight
        /// updates must be ignored until they have seen it.
        /// </summary>
        private void HoldClientAuthority(PlayerSession session)
        {
            session.PendingAuthorityHold = true;
            session.AuthorityHoldSnapshot = 0;
        }

        /// <summary>
        /// Resolves a hit one client claims to have landed.
        /// <para>
        /// The server cannot raycast — it has no map — so the shot itself is the
        /// client's word. What the server can check is everything around it: that both
        /// parties exist, that the attacker is alive, that the target is in range for
        /// the weapon, that the damage is within what that weapon could do, and that
        /// the server's own rules allow the hit at all.
        /// </para>
        /// </summary>
        private void HandleDamageReport(PlayerSession session, DamageReportMessage report)
        {
            PlayerEntity? attacker = World.GetPlayer(session.EntityId);
            if (attacker == null)
            {
                return;
            }

            World.TryGet(report.TargetId, out NetEntity target);

            DamageResolution resolution = CombatArbiter.Resolve(
                attacker, target, report.WeaponHash, report.Damage, Combat);

            if (!resolution.Accepted)
            {
                Log.Debug(
                    LogCategory.Security,
                    $"{session} damage claim on {report.TargetId} refused: {resolution.Verdict} — {resolution.Detail}");

                if (resolution.Verdict == DamageVerdict.RejectedOutOfRange
                    || resolution.Verdict == DamageVerdict.RejectedWeaponNotHeld)
                {
                    session.Validation.Count(ViolationKind.DamageOutOfRange);
                }

                return;
            }

            if (resolution.Clamped)
            {
                Log.Warning(LogCategory.Security, $"{session}: {resolution.Detail}");
                session.Validation.Count(ViolationKind.DamageOutOfRange);
            }

            CombatArbiter.Apply(target!, resolution);
            World.Touch(target!);

            if (target is PlayerEntity damaged
                && Players.TryGetByPlayerId(damaged.PlayerId, out PlayerSession damagedSession))
            {
                // The victim's client still believes it is undamaged. Ignore its
                // vitals until it has seen the hit, or it will report the damage away.
                HoldHealthAuthority(damagedSession);
            }

            if (target is PlayerEntity victim && resolution.Fatal
                && Players.TryGetByPlayerId(victim.PlayerId, out PlayerSession victimSession)
                && !victimSession.IsDead)
            {
                Log.Info(LogCategory.Server, $"{victimSession.Name} was killed by {session.Name}.");
                Kill(victimSession, victim);
            }
        }

        /// <summary>
        /// Relays one gunshot to the players near enough to see it.
        /// <para>
        /// It carries no damage and is not arbitrated — that is
        /// <see cref="HandleDamageReport"/>'s job, against the server's own world. The
        /// only claim in this message the server trusts is that a shot happened; who
        /// fired it is overwritten from the session, because a client that names its
        /// own shooter can name somebody else and put a muzzle flash in an innocent
        /// player's hands.
        /// </para>
        /// </summary>
        private void HandleWeaponShot(PlayerSession session, WeaponShotMessage shot)
        {
            PlayerEntity? shooter = World.GetPlayer(session.EntityId);
            if (shooter == null || !session.Shots.TryTake(_now))
            {
                return;
            }

            // The muzzle is a claim too. It is not arbitrated — nothing hangs on it —
            // but it decides where the flash is drawn and who is close enough to see
            // it, so a shot claiming to come from across the street is dropped rather
            // than relayed. The slack covers the report interval and the round trip:
            // the server's position for this player is a report old.
            if (NetVector3.DistanceSquared(shooter.Position, shot.Origin) > MaxMuzzleOffsetSquared)
            {
                return;
            }

            shot.ShooterId = session.EntityId;
            byte[] payload = shot.Serialize();

            foreach (PlayerSession other in Players.Sessions)
            {
                if (other == session || other.PendingRemoval)
                {
                    continue;
                }

                // Distance is a *replication* filter, never a world-state one: the
                // shot is relayed to whoever could see it and dropped for everyone
                // else, and no entity is touched either way.
                // Measured from the shooter's *server* position, not the claimed
                // muzzle: who hears a shot is the server's decision to make.
                PlayerEntity? listener = World.GetPlayer(other.EntityId);
                if (listener == null
                    || NetVector3.DistanceSquared(listener.Position, shooter.Position) > ShotRelayRangeSquared)
                {
                    continue;
                }

                other.Peer.Send(NetMessageType.WeaponShot, payload, DeliveryMethod.Unreliable);
            }
        }

        private void OnOwnershipGranted(PlayerSession session, EntityId entityId)
        {
            var message = new EntityEventMessage { Kind = EntityEventKind.OwnershipGranted, EntityId = entityId };
            session.Peer.Send(NetMessageType.EntityEvent, message.Serialize(), DeliveryMethod.ReliableOrdered);
        }

        private void HandleViolations(PlayerSession session, ValidationOutcome outcome)
        {
            foreach (ViolationRecord violation in outcome.Violations)
            {
                switch (violation.Action)
                {
                    case ViolationAction.Ignore:
                        break;

                    case ViolationAction.Log:
                        Log.Debug(LogCategory.Security, $"{session}: {violation.Kind} — {violation.Detail}");
                        break;

                    case ViolationAction.Warn:
                        Log.Warning(LogCategory.Security, $"{session}: {violation.Kind} — {violation.Detail}");
                        break;

                    case ViolationAction.Kick:
                    case ViolationAction.Ban:
                        Log.Error(LogCategory.Security, $"{session}: {violation.Kind} — {violation.Detail}");
                        if (AntiCheat.ShouldEscalate(session.Validation))
                        {
                            Kick(session, DisconnectReason.AntiCheat, $"{violation.Kind}: {violation.Detail}");
                        }

                        break;
                }
            }
        }

        // ------------------------------------------------------------------
        // Outbound
        // ------------------------------------------------------------------
        private void SendSnapshots()
        {
            foreach (PlayerSession session in Players.Sessions)
            {
                if (session.PendingRemoval)
                {
                    continue;
                }

                PlayerEntity? viewerEntity = World.GetPlayer(session.EntityId);
                NetVector3 viewer = viewerEntity?.Position ?? DefaultSpawn;
                uint viewerDimension = viewerEntity?.Dimension ?? 0;

                EntitySnapshotView baseline = session.Replication.ResyncRequested
                    ? EntitySnapshotView.Empty
                    : session.Replication.Baseline;

                List<NetEntity> order = ReplicationPriority.Order(
                    World.State.Entities, viewer, World.Tick, session.Replication, EntityId.None, viewerDimension);

                session.Bandwidth?.Update(session.Peer.Stats, _now);
                int budget = session.Bandwidth?.CurrentBudget ?? Config.SnapshotByteBudget;

                uint snapshotId = session.Replication.AllocateSnapshotId();

                if (session.PendingAuthorityHold)
                {
                    // This is the snapshot that carries the server's move, so it is the
                    // one the client has to acknowledge before it regains authority.
                    session.PendingAuthorityHold = false;
                    session.AuthorityHoldSnapshot = snapshotId;
                    session.AuthorityHoldExpiry = _now + AuthorityHoldTimeoutSeconds;
                }

                if (session.PendingHealthHold)
                {
                    session.PendingHealthHold = false;
                    session.HealthHoldSnapshot = snapshotId;
                    session.HealthHoldExpiry = _now + AuthorityHoldTimeoutSeconds;
                }

                if (session.PendingWantedHold)
                {
                    session.PendingWantedHold = false;
                    session.WantedHoldSnapshot = snapshotId;
                    session.WantedHoldExpiry = _now + AuthorityHoldTimeoutSeconds;
                }

                if (session.PendingModelHold)
                {
                    session.PendingModelHold = false;
                    session.ModelHoldSnapshot = snapshotId;

                    // Longer than the others on purpose: the client has to stream the
                    // model in and may be waiting to get out of a car.
                    session.ModelHoldExpiry = _now + ModelHoldTimeoutSeconds;
                }

                SnapshotWriteResult result = SnapshotCodec.Write(
                    World.State, baseline, Registry, order, snapshotId, budget,
                    session.LastProcessedUpdateSequence,
                    entity => ReplicationPriority.SharesDimension(entity, viewerDimension));

                session.Peer.Send(NetMessageType.Snapshot, result.Payload, DeliveryMethod.Unreliable);
                session.Replication.RecordSent(result, World.Tick);
                session.LastSnapshotSentAt = _now;

                if (Config.VerboseNetworkLogging && result.DeferredCount > 0)
                {
                    Log.Debug(
                        LogCategory.Network,
                        $"{session}: snapshot {snapshotId} deferred {result.DeferredCount} entities " +
                        $"({result.Payload.Length}/{budget} bytes used)");
                }
            }
        }

        private void FlushPeers()
        {
            foreach (PlayerSession session in Players.Sessions)
            {
                session.Peer.Flush(_now);
            }
        }

        private void DropTimedOutSessions()
        {
            foreach (PlayerSession session in Players.Sessions)
            {
                if (session.PendingRemoval)
                {
                    continue;
                }

                // A broken ordered channel is not silence — the peer is still answering
                // — but nothing reliable will ever be delivered on it again, so the
                // session is over. Saying which of the two happened is the whole point:
                // they are indistinguishable from the outside and have different causes.
                if (session.Peer.Fault != null)
                {
                    Log.Warning(LogCategory.Network, $"{session} can no longer deliver: {session.Peer.Fault}.");
                    session.PendingRemoval = true;
                }
                else if (session.Peer.IsTimedOut(_now))
                {
                    Log.Warning(LogCategory.Network, $"{session} timed out after {ProtocolConstants.ConnectionTimeout:0} s of silence.");
                    session.PendingRemoval = true;
                }
            }

            ReapPendingSessions();
        }

        private void ReapPendingSessions()
        {
            _reapBuffer.Clear();
            foreach (PlayerSession session in Players.Sessions)
            {
                if (session.PendingRemoval)
                {
                    _reapBuffer.Add(session);
                }
            }

            foreach (PlayerSession session in _reapBuffer)
            {
                RemoveSession(session, DisconnectReason.ClientQuit, notifyPeer: false, announce: true);
            }
        }

        private void RemoveSession(PlayerSession session, DisconnectReason reason, bool notifyPeer, bool announce)
        {
            if (notifyPeer)
            {
                SendDisconnect(session, reason, DisconnectReasonText.Describe(reason));
                session.Peer.Flush(_now);
            }

            PersistPlayer(session);
            Entities.ReleaseAllOwnedBy(session.PlayerId, Players);
            Activities.RemoveParticipantEverywhere(session.PlayerId);

            // The player's body leaves the world, but their saved state does not: a
            // reconnect restores it (master prompt section 25).
            if (session.EntityId.IsValid && Config.KeepDisconnectedBodySeconds <= 0)
            {
                World.Destroy(session.EntityId);
            }

            Players.Remove(session);
            Log.Info(LogCategory.Server, $"{session.Name} left the session. {Players.Count} player(s) remain.");

            if (announce)
            {
                BroadcastServerEvent(ServerEventKind.PlayerLeft, session.PlayerId, $"{session.Name} left the session.", null);
            }
        }

        public void Kick(PlayerSession session, DisconnectReason reason, string detail)
        {
            Log.Warning(LogCategory.Security, $"Kicking {session}: {detail}");
            SendDisconnect(session, reason, detail);
            session.Peer.Flush(_now);
            session.PendingRemoval = true;
        }

        private void SendDisconnect(PlayerSession session, DisconnectReason reason, string text)
        {
            var message = new DisconnectMessage { Reason = reason, Text = text };
            session.Peer.Send(NetMessageType.Disconnect, message.Serialize(), DeliveryMethod.ReliableOrdered);
        }

        public void Broadcast(NetMessageType type, byte[] payload, DeliveryMethod delivery, PlayerSession? exclude = null)
        {
            foreach (PlayerSession session in Players.Sessions)
            {
                if (session != exclude && !session.PendingRemoval)
                {
                    session.Peer.Send(type, payload, delivery);
                }
            }
        }

        public void BroadcastServerEvent(ServerEventKind kind, uint playerId, string text, PlayerSession? exclude)
        {
            var message = new ServerEventMessage { Kind = kind, PlayerId = playerId, Text = text };
            Broadcast(NetMessageType.ServerEvent, message.Serialize(), DeliveryMethod.ReliableOrdered, exclude);
        }

        private void RejectConnection(IPEndPoint target, uint nonce, DisconnectReason reason, string message)
        {
            var reject = new ConnectRejectMessage { Reason = reason, Message = message, ClientNonce = nonce };
            _transport.SendConnectionless(target, NetMessageType.ConnectReject, reject.Serialize());
            Log.Warning(LogCategory.Server, $"Rejected {target}: {message}");
        }

        // ------------------------------------------------------------------
        // World and persistence
        // ------------------------------------------------------------------
        private PlayerEntity CreatePlayerEntity(PlayerSession session, PersistedPlayer? saved)
        {
            var entity = new PlayerEntity(World.AllocateEntityId())
            {
                PlayerId = session.PlayerId,
                OwnerId = session.PlayerId,
                Name = session.Name,
                Position = saved != null ? new NetVector3(saved.X, saved.Y, saved.Z) : DefaultSpawn,
                Heading = saved?.Heading ?? 0f,
                Health = saved?.Health ?? 200,
                MaxHealth = saved?.MaxHealth ?? 200,
                Armor = saved?.Armor ?? 0,
                ModelHash = saved?.ModelHash ?? 0,
                WantedLevel = saved?.WantedLevel ?? 0,
                Dimension = saved?.Dimension ?? 0,
                InteriorId = saved?.InteriorId ?? 0,
            };

            if (entity.WantedLevel > 0)
            {
                // Restored, so the client has to be told before its own reports count.
                HoldWantedAuthority(session);
            }

            if (entity.ModelHash != 0)
            {
                // The same for a restored model, which the client's fresh session knows
                // nothing about and would otherwise overwrite with whatever character
                // single player left it as.
                session.PendingModelHold = true;
                session.ModelHoldSnapshot = 0;
            }

            return World.Spawn(entity);
        }

        /// <summary>Writes one player's record now. Used when a role changes.</summary>
        public void SavePlayer(PlayerSession session) => PersistPlayer(session);

        /// <summary>Sends a security notice to one client. Silently does nothing if they have gone.</summary>
        public void NotifyPlayer(PlayerSession session, SecurityNoticeKind kind, string text)
        {
            if (session.PendingRemoval)
            {
                return;
            }

            var notice = new SecurityNoticeMessage { Kind = kind, Text = text };
            session.Peer.Send(NetMessageType.SecurityNotice, notice.Serialize(), DeliveryMethod.ReliableOrdered);
        }

        /// <summary>
        /// Runs an administrative command on a player's behalf.
        /// <para>
        /// The same command table the stdin console uses, so there is one
        /// implementation rather than two that drift. Authorisation happens here and
        /// only here: the client sends a string and is told what happened, which means
        /// a modified client gains nothing by pretending to be an admin.
        /// </para>
        /// </summary>
        private void HandleAdminCommand(PlayerSession session, AdminCommandMessage message)
        {
            string line = message.CommandLine?.Trim() ?? string.Empty;
            if (line.Length == 0)
            {
                return;
            }

            if (!AdminPermissions.IsAllowed(session.Role, line))
            {
                Log.Warning(
                    LogCategory.Security,
                    $"{session} tried to run '{AdminPermissions.FirstWord(line)}' as {session.Role}. Refused.");

                NotifyPlayer(
                    session,
                    SecurityNoticeKind.PermissionDenied,
                    $"'{AdminPermissions.FirstWord(line)}' needs more than the {session.Role} role. " +
                    $"You may run: {string.Join(", ", new List<string>(AdminPermissions.CommandsFor(session.Role)))}");
                return;
            }

            Log.Info(LogCategory.Security, $"{session} ({session.Role}) ran '{line}'.");

            string result;
            try
            {
                result = AdminSurface.Execute(line);
            }
            catch (Exception exception)
            {
                // A command that throws must not take the tick thread with it.
                Log.Error(LogCategory.Security, $"Admin command '{line}' threw.", exception);
                result = "The command failed on the server; the server log has the detail.";
            }

            NotifyPlayer(session, SecurityNoticeKind.CommandResult, result);
        }

        private void PersistPlayer(PlayerSession session)
        {
            if (!Config.PersistenceEnabled || !_persistence.Enabled)
            {
                return;
            }

            PlayerEntity? entity = World.GetPlayer(session.EntityId);
            if (entity == null)
            {
                return;
            }

            _persistence.SavePlayer(new PersistedPlayer
            {
                IdentityToken = session.IdentityToken,
                Name = session.Name,
                X = entity.Position.X,
                Y = entity.Position.Y,
                Z = entity.Position.Z,
                Heading = entity.Heading,
                Health = entity.Health,
                MaxHealth = entity.MaxHealth,
                Armor = entity.Armor,
                ModelHash = entity.ModelHash,
                WantedLevel = entity.WantedLevel,
                Dimension = entity.Dimension,
                InteriorId = entity.InteriorId,
                Role = (int)session.Role,
                LastSeenUtc = DateTime.UtcNow,
            });
        }

        public void SaveWorld(string reason)
        {
            if (!Config.PersistenceEnabled || !_persistence.Enabled)
            {
                return;
            }

            try
            {
                foreach (PlayerSession session in Players.Sessions)
                {
                    PersistPlayer(session);
                }

                uint highest = 0;
                foreach (EntityId id in World.State.Ids)
                {
                    if (id.Value > highest)
                    {
                        highest = id.Value;
                    }
                }

                SaveEntities();

                _persistence.SaveWorld(new PersistedWorld
                {
                    TimeOfDaySeconds = World.State.Environment.TimeOfDaySeconds,
                    ClockScale = World.State.Environment.ClockScale,
                    WeatherHash = World.State.Environment.WeatherHash,
                    NextWeatherHash = World.State.Environment.NextWeatherHash,
                    WeatherTransition = World.State.Environment.WeatherTransition,
                    Blackout = World.State.Environment.Blackout,
                    HighestEntityId = highest,
                    SchemaHash = Registry.ComputeSchemaHash(),
                    SavedAtUtc = DateTime.UtcNow,
                });

                Log.Debug(LogCategory.Persistence, $"World saved ({reason}).");
            }
            catch (Exception exception)
            {
                Log.Error(LogCategory.Persistence, "World save failed.", exception);
            }
        }

        /// <summary>
        /// Writes every non-player entity as its own serializer's full-state blob.
        /// <para>
        /// The blob is opaque to the persistence layer, which is what lets a
        /// mod-defined entity survive a restart on a server with no compiled knowledge
        /// of that mod: the bytes go in and come back out, and the serializer that
        /// understands them is supplied by the mod at load time.
        /// </para>
        /// </summary>
        private void SaveEntities()
        {
            var blobs = new List<PersistedEntity>();

            foreach (NetEntity entity in World.State.Entities)
            {
                // Players are stored by identity token, not by entity id: their entity
                // is recreated on reconnect and its id will be different.
                if (entity.Type == EntityType.Player)
                {
                    continue;
                }

                if (!Registry.TryGet((byte)entity.Type, out INetEntitySerializer serializer))
                {
                    continue;
                }

                var writer = new NetWriter(512);
                serializer.WriteFull(writer, entity);

                blobs.Add(new PersistedEntity
                {
                    EntityId = entity.Id.Value,
                    TypeId = (byte)entity.Type,
                    State = writer.ToArray(),
                    Dimension = entity.Dimension,
                });
            }

            _persistence.SaveEntities(blobs);
        }

        /// <summary>
        /// Loads the ban list. Expired entries are dropped on the way in and written
        /// back, so the file does not grow forever with bans nobody is serving.
        /// </summary>
        private void RestoreBans()
        {
            IReadOnlyList<BanEntry> stored = _persistence.LoadBans();
            if (stored.Count == 0)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            var live = new List<BanEntry>();
            foreach (BanEntry ban in stored)
            {
                if (!ban.IsExpired(now))
                {
                    live.Add(ban);
                }
            }

            Bans.Replace(live);
            Log.Info(
                LogCategory.Security,
                $"Loaded {live.Count} ban(s)" +
                (live.Count < stored.Count ? $"; {stored.Count - live.Count} had expired and were dropped." : "."));

            if (live.Count < stored.Count)
            {
                _persistence.SaveBans(live);
            }
        }

        /// <summary>Adds a ban, disconnects the player if they are on, and writes it to disk immediately.</summary>
        public bool AddBan(BanEntry entry)
        {
            if (!Bans.Add(entry))
            {
                return false;
            }

            _persistence.SaveBans(new List<BanEntry>(Bans.Entries));

            foreach (PlayerSession session in Players.Sessions)
            {
                if (string.Equals(session.IdentityToken, entry.PublicKey, StringComparison.Ordinal))
                {
                    RemoveSession(session, DisconnectReason.Banned, notifyPeer: true, announce: true);
                    break;
                }
            }

            Log.Warning(
                LogCategory.Security,
                $"Banned {IdentityKey.FingerprintOf(entry.PublicKey)} " +
                $"({(string.IsNullOrEmpty(entry.PlayerName) ? "unknown name" : entry.PlayerName)}): {entry.Reason}");

            return true;
        }

        public bool RemoveBan(string publicKey)
        {
            if (!Bans.Remove(publicKey))
            {
                return false;
            }

            _persistence.SaveBans(new List<BanEntry>(Bans.Entries));
            Log.Info(LogCategory.Security, $"Lifted the ban on {IdentityKey.FingerprintOf(publicKey)}.");
            return true;
        }

        /// <summary>Restores persisted entities, skipping anything this build cannot decode.</summary>
        private void RestoreEntities(uint savedSchemaHash)
        {
            if (savedSchemaHash != Registry.ComputeSchemaHash())
            {
                // The field layouts have changed since the save. Misinterpreting a blob
                // would be far worse than losing it.
                return;
            }

            int restored = 0;
            int skipped = 0;

            foreach (PersistedEntity blob in _persistence.LoadEntities())
            {
                if (!Registry.TryGet(blob.TypeId, out INetEntitySerializer serializer))
                {
                    // A mod that was loaded when the world was saved and is not now.
                    skipped++;
                    continue;
                }

                try
                {
                    NetEntity entity = serializer.Create(new EntityId(blob.EntityId));
                    serializer.ReadFull(new NetReader(blob.State), entity);

                    // Nobody is simulating it yet; the ownership pass hands it to
                    // whoever turns up near it.
                    entity.OwnerId = 0;
                    entity.Dimension = blob.Dimension;
                    World.State.AddOrReplace(entity);
                    restored++;
                }
                catch (NetSerializationException exception)
                {
                    skipped++;
                    Log.Warning(
                        LogCategory.Persistence,
                        $"Could not restore entity {blob.EntityId}: {exception.Message}");
                }
            }

            if (restored > 0 || skipped > 0)
            {
                Log.Info(
                    LogCategory.Persistence,
                    $"Restored {restored} entity(ies) from persistence" +
                    (skipped > 0 ? $"; skipped {skipped} this build cannot decode." : "."));
            }
        }

        private void RestoreWorld()
        {
            if (!Config.PersistenceEnabled || !_persistence.Enabled)
            {
                ApplyConfiguredStartConditions();
                return;
            }

            RestoreBans();

            PersistedWorld? saved = _persistence.LoadWorld();
            if (saved == null)
            {
                ApplyConfiguredStartConditions();
                Log.Info(LogCategory.Persistence, "No saved world found; starting from the configured defaults.");
                return;
            }

            uint currentSchema = Registry.ComputeSchemaHash();
            if (saved.SchemaHash != currentSchema)
            {
                Log.Warning(
                    LogCategory.Persistence,
                    $"Saved world was written with entity schema {saved.SchemaHash:X8} but this build produces {currentSchema:X8}. " +
                    "Player records are still restored; stored entity blobs are skipped.");
            }

            World.State.Environment.TimeOfDaySeconds = saved.TimeOfDaySeconds;
            World.State.Environment.WeatherHash = saved.WeatherHash;
            World.State.Environment.NextWeatherHash = saved.NextWeatherHash;
            World.State.Environment.WeatherTransition = saved.WeatherTransition;
            World.State.Environment.Blackout = saved.Blackout;
            World.ReserveEntityIdsUpTo(saved.HighestEntityId);
            RestoreEntities(saved.SchemaHash);

            Log.Success(
                LogCategory.Persistence,
                $"World restored from {saved.SavedAtUtc:u} (clock {World.State.Environment.Hours:00}:{World.State.Environment.Minutes:00}).");
        }

        private void ApplyConfiguredStartConditions()
        {
            if (Config.TryParseStartTime(out int hours, out int minutes))
            {
                World.State.Environment.SetTime(hours, minutes, 0);
            }

            if (!string.IsNullOrWhiteSpace(Config.StartWeather))
            {
                World.State.Environment.WeatherHash = GameHash.Joaat(Config.StartWeather);
            }
        }

        private uint NextSessionId()
        {
            uint id;
            do
            {
                id = (uint)_random.Next(1, int.MaxValue);
            }
            while (SessionIdInUse(id));

            return id;
        }

        private bool SessionIdInUse(uint id)
        {
            foreach (PlayerSession session in Players.Sessions)
            {
                if (session.Peer.SessionId == id)
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildBlockingModMessage(List<ModCompatibilityEntry> report)
        {
            var parts = new List<string>();
            foreach (ModCompatibilityEntry entry in report)
            {
                if (entry.BlocksConnection)
                {
                    parts.Add($"{entry.ModId} ({entry.Status}: {entry.Detail})");
                }
            }

            return "required mods are missing or incompatible: " + string.Join("; ", parts.ToArray());
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(ProtocolConstants.MaxPlayerNameLength);
            foreach (char c in name.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ')
                {
                    builder.Append(c);
                }

                if (builder.Length >= ProtocolConstants.MaxPlayerNameLength)
                {
                    break;
                }
            }

            return builder.ToString().Trim();
        }
    }
}
