using System;
using System.Collections.Generic;
using System.Net;
using Gtamp.Server.Entities;
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

        private readonly IDatagramTransport _transport;
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
            Entities = new NetworkedEntityManager(World, config, Log);
            Entities.OwnershipGranted += OnOwnershipGranted;
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

        public NetworkedEntityManager Entities { get; }

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

            double delta = now - _lastTickTime;
            if (delta >= Config.TickIntervalSeconds)
            {
                _lastTickTime = now;
                World.AdvanceTick(delta);
            }

            UpdateDeaths();
            Entities.UpdateOwnership(Players, now);

            if (now - _lastSnapshotTime >= Config.SnapshotIntervalSeconds)
            {
                _lastSnapshotTime = now;
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

                case NetMessageType.KeepAlive:
                    break;

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
            entity.ModelHash = update.ModelHash;
            entity.CurrentWeaponHash = update.CurrentWeaponHash;
            entity.Ammo = update.Ammo;
            entity.AimPosition = update.AimPosition;
            entity.InteriorId = update.InteriorId;
            entity.AnimationHash = update.AnimationHash;
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

                EntitySnapshotView baseline = session.Replication.ResyncRequested
                    ? EntitySnapshotView.Empty
                    : session.Replication.Baseline;

                List<NetEntity> order = ReplicationPriority.Order(
                    World.State.Entities, viewer, World.Tick, session.Replication, EntityId.None);

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

                SnapshotWriteResult result = SnapshotCodec.Write(
                    World.State, baseline, Registry, order, snapshotId, budget);

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
                if (!session.PendingRemoval && session.Peer.IsTimedOut(_now))
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

            return World.Spawn(entity);
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

        private void RestoreWorld()
        {
            if (!Config.PersistenceEnabled || !_persistence.Enabled)
            {
                ApplyConfiguredStartConditions();
                return;
            }

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
