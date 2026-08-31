using System;
using System.Collections.Generic;
using Gtamp.Server.Missions;
using Gtamp.Server.Players;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;

namespace Gtamp.Server.Mods
{
    /// <summary>
    /// What a server-side mod gets to talk to the framework.
    /// <para>
    /// Client-side mods already have <c>IModSdk</c>. This is its counterpart for
    /// logic that has to be authoritative — activity rules, RPC handlers, anything
    /// that decides rather than reports. Without it, "server-side mod support" would
    /// mean writing directly against <c>GameServer</c>, which is not an API and
    /// would break on every internal change.
    /// </para>
    /// </summary>
    public interface IServerModSdk
    {
        LogBus Log { get; }

        /// <summary>The authoritative world. Mutating it is allowed; that is the point.</summary>
        Gtamp.Server.World.ServerWorld World { get; }

        ActivityManager Activities { get; }

        /// <summary>Registers a networked entity type. Must happen before the first connection.</summary>
        byte RegisterEntity(INetEntitySerializer serializer);

        /// <summary>Registers an activity a client or the server can start.</summary>
        void RegisterActivity(ActivityDefinition definition);

        /// <summary>Registers a procedure clients can call and get an answer from.</summary>
        void RegisterRpc(string name, Func<PlayerSession, byte[], byte[]> handler);

        /// <summary>Registers a handler for a mod event sent by a client.</summary>
        void RegisterNetworkEvent(string eventName, Action<PlayerSession, byte[]> handler);

        /// <summary>
        /// Declares that a client-sent event of this name is forwarded verbatim to
        /// every other connected player.
        /// <para>
        /// The server does not parse the payload and cannot validate it, so a relayed
        /// event carries no authority: it is one client telling the others something,
        /// with the server acting as the postbox. Anything a mod needs the server to
        /// vouch for must go through <see cref="RegisterNetworkEvent"/> or an RPC
        /// instead. The relay exists because without it two clients running the same
        /// mod have no way to talk at all on a server that knows nothing about it.
        /// </para>
        /// </summary>
        void RegisterRelay(string eventName);

        /// <summary>Sends a mod event to one client.</summary>
        bool SendNetworkEvent(PlayerSession session, string eventName, byte[] payload, bool reliable = true);

        /// <summary>Sends a mod event to everyone.</summary>
        void BroadcastNetworkEvent(string eventName, byte[] payload, bool reliable = true);

        /// <summary>Calls a procedure the client registered, and receives the answer.</summary>
        void CallClientRpc(
            PlayerSession session, string name, byte[] payload, Action<RpcResult> callback, double timeoutSeconds = 5.0);
    }

    public sealed class ServerModSdk : IServerModSdk
    {
        private readonly EntityRegistry _registry;
        private readonly Dictionary<string, Action<PlayerSession, byte[]>> _eventHandlers =
            new Dictionary<string, Action<PlayerSession, byte[]>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _relayed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ServerModSdk(
            EntityRegistry registry,
            Gtamp.Server.World.ServerWorld world,
            ActivityManager activities,
            RpcDispatcher<PlayerSession> rpc,
            LogBus log)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            World = world ?? throw new ArgumentNullException(nameof(world));
            Activities = activities ?? throw new ArgumentNullException(nameof(activities));
            Rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
            Log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public LogBus Log { get; }

        public Gtamp.Server.World.ServerWorld World { get; }

        public ActivityManager Activities { get; }

        public RpcDispatcher<PlayerSession> Rpc { get; }

        /// <summary>Set by the server so the SDK can reach connected players.</summary>
        public Func<uint, PlayerSession?>? ResolveSession { get; set; }

        /// <summary>Set by the server: (session, messageType, payload, reliable).</summary>
        public Action<PlayerSession, NetMessageType, byte[], bool>? SendToSession { get; set; }

        /// <summary>Set by the server: (messageType, payload, reliable).</summary>
        public Action<NetMessageType, byte[], bool>? SendToAll { get; set; }

        /// <summary>Set by the server: (sender to exclude, messageType, payload, reliable).</summary>
        public Action<PlayerSession, NetMessageType, byte[], bool>? SendToOthers { get; set; }

        public byte RegisterEntity(INetEntitySerializer serializer)
        {
            if (_registry.IsLocked)
            {
                throw new InvalidOperationException(
                    $"Entity type '{serializer.TypeName}' was registered after the network layer started.");
            }

            if (serializer.TypeId < (byte)EntityType.ModDefinedFirst)
            {
                throw new ArgumentException(
                    $"Type id {serializer.TypeId} is inside the built-in range; mod entities must use " +
                    $"{(byte)EntityType.ModDefinedFirst}..{(byte)EntityType.ModDefinedLast}.",
                    nameof(serializer));
            }

            _registry.Register(serializer);
            Log.Info(LogCategory.Mod, $"Registered server entity type '{serializer.TypeName}' as id {serializer.TypeId}.");
            return serializer.TypeId;
        }

        public void RegisterActivity(ActivityDefinition definition) => Activities.Register(definition);

        public void RegisterRpc(string name, Func<PlayerSession, byte[], byte[]> handler) =>
            Rpc.RegisterHandler(name, handler);

        public void RegisterNetworkEvent(string eventName, Action<PlayerSession, byte[]> handler)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                throw new ArgumentException("Event name must not be empty.", nameof(eventName));
            }

            _eventHandlers[eventName] = handler ?? throw new ArgumentNullException(nameof(handler));
            Log.Info(LogCategory.Mod, $"Registered server network event '{eventName}'.");
        }

        public void RegisterRelay(string eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                throw new ArgumentException("Event name must not be empty.", nameof(eventName));
            }

            if (_relayed.Add(eventName))
            {
                Log.Info(
                    LogCategory.Mod,
                    $"Relaying mod event '{eventName}' between clients. The server does not interpret its payload.");
            }
        }

        public bool IsEventRelayed(string eventName) => _relayed.Contains(eventName);

        public IReadOnlyCollection<string> RelayedEvents => _relayed;

        public bool SendNetworkEvent(PlayerSession session, string eventName, byte[] payload, bool reliable = true)
        {
            if (SendToSession == null)
            {
                return false;
            }

            var message = new ModEventMessage { Name = eventName, Payload = payload ?? Array.Empty<byte>() };
            SendToSession(session, NetMessageType.ModEvent, message.Serialize(), reliable);
            return true;
        }

        public void BroadcastNetworkEvent(string eventName, byte[] payload, bool reliable = true)
        {
            if (SendToAll == null)
            {
                return;
            }

            var message = new ModEventMessage { Name = eventName, Payload = payload ?? Array.Empty<byte>() };
            SendToAll(NetMessageType.ModEvent, message.Serialize(), reliable);
        }

        public void CallClientRpc(
            PlayerSession session, string name, byte[] payload, Action<RpcResult> callback, double timeoutSeconds = 5.0)
        {
            if (SendToSession == null)
            {
                callback(RpcResult.Failed("the server is not able to send right now"));
                return;
            }

            ModRpcRequestMessage request = Rpc.BeginCall(name, payload, callback, CurrentTime, timeoutSeconds);
            SendToSession(session, NetMessageType.ModRpcRequest, request.Serialize(), true);
        }

        /// <summary>Server time, injected so the SDK does not need its own clock.</summary>
        public double CurrentTime { get; set; }

        /// <summary>Routes an inbound mod event to its handler. Returns false when the name is unknown.</summary>
        public bool Dispatch(string eventName, PlayerSession sender, byte[] payload)
        {
            byte[] body = payload ?? Array.Empty<byte>();
            bool handled = false;

            if (_relayed.Contains(eventName) && SendToOthers != null)
            {
                var relay = new ModEventMessage
                {
                    Name = eventName,
                    SenderPlayerId = sender.PlayerId,
                    Payload = body,
                };
                SendToOthers(sender, NetMessageType.ModEvent, relay.Serialize(), true);
                handled = true;
            }

            if (_eventHandlers.TryGetValue(eventName, out Action<PlayerSession, byte[]>? handler))
            {
                try
                {
                    handler(sender, body);
                }
                catch (Exception exception)
                {
                    Log.Error(LogCategory.Mod, $"Handler for mod event '{eventName}' threw.", exception);
                }

                handled = true;
            }

            return handled;
        }

        public bool IsEventRegistered(string eventName) => _eventHandlers.ContainsKey(eventName);
    }
}
