using System;
using System.Collections.Generic;
using System.Net;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;

namespace Gtamp.Client.Network
{
    public enum ClientConnectionState : byte
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Disconnecting = 3,
        Failed = 4,
    }

    /// <summary>
    /// Client side of the handshake and session.
    /// <para>
    /// The connect request is connectionless and retried on a timer, because the
    /// first packet of a UDP session is exactly the one with nothing to retransmit
    /// it. Once accepted, everything moves to a <see cref="NetPeer"/> keyed by the
    /// session id the server assigned.
    /// </para>
    /// </summary>
    public sealed class ClientConnection
    {
        private readonly IDatagramTransport _transport;
        private readonly LogBus _log;
        private readonly Random _random = new Random();

        private IPEndPoint? _server;
        private ConnectRequestMessage? _request;
        private double _lastAttemptTime;
        private int _attempts;

        public ClientConnection(IDatagramTransport transport, LogBus log)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public ClientConnectionState State { get; private set; } = ClientConnectionState.Disconnected;

        public NetPeer? Peer { get; private set; }

        public ConnectAcceptMessage? Accept { get; private set; }

        public string LastError { get; private set; } = string.Empty;

        public IPEndPoint? ServerEndPoint => _server;

        public bool IsConnected => State == ClientConnectionState.Connected && Peer != null;

        public event Action<ConnectAcceptMessage>? Accepted;

        public event Action<DisconnectReason, string>? Rejected;

        public event Action<DisconnectReason, string>? Disconnected;

        public void Connect(
            IPEndPoint server,
            string playerName,
            string identityToken,
            string password,
            ModManifest manifest,
            string clientVersion,
            double now)
        {
            _server = server;
            _attempts = 0;
            _lastAttemptTime = 0;
            LastError = string.Empty;
            Accept = null;
            Peer = null;
            State = ClientConnectionState.Connecting;

            _request = new ConnectRequestMessage
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                ClientVersion = clientVersion,
                PlayerName = playerName,
                IdentityToken = identityToken,
                Password = password,
                ClientNonce = (uint)_random.Next(1, int.MaxValue),
                Manifest = manifest,
            };

            _log.Info(LogCategory.Network, $"Connecting to {server} as '{playerName}'...");
            SendConnectRequest(now);
        }

        public void Disconnect(DisconnectReason reason, string text, double now)
        {
            if (Peer != null && State == ClientConnectionState.Connected)
            {
                var message = new DisconnectMessage { Reason = reason, Text = text };
                Peer.Send(NetMessageType.Disconnect, message.Serialize(), DeliveryMethod.ReliableOrdered);
                Peer.Flush(now);
            }

            SetDisconnected(reason, text);
        }

        /// <summary>Pumps the transport. Returns the messages that arrived on the session.</summary>
        public void Update(double now, List<ReceivedMessage> into)
        {
            while (_transport.TryReceive(out IPEndPoint source, out byte[] payload))
            {
                if (_server != null && !source.Equals(_server))
                {
                    continue;
                }

                if (Peer != null && ConnectionlessPacket.IsSessionPacket(payload))
                {
                    Peer.HandleDatagram(payload, now);
                    continue;
                }

                if (ConnectionlessPacket.TryRead(payload, out NetMessageType type, out byte[] body))
                {
                    HandleConnectionless(type, body, now);
                }
            }

            if (State == ClientConnectionState.Connecting && now - _lastAttemptTime >= ProtocolConstants.HandshakeRetryInterval)
            {
                if (_attempts >= ProtocolConstants.HandshakeMaxAttempts)
                {
                    LastError = $"no reply from {_server} after {_attempts} attempts";
                    State = ClientConnectionState.Failed;
                    _log.Error(LogCategory.Network, "Connection failed: " + LastError);
                    Rejected?.Invoke(DisconnectReason.Timeout, LastError);
                    return;
                }

                SendConnectRequest(now);
            }

            if (Peer == null)
            {
                return;
            }

            while (Peer.TryDequeue(out ReceivedMessage message))
            {
                if (message.Type == NetMessageType.Disconnect)
                {
                    DisconnectMessage disconnect = DisconnectMessage.Deserialize(message.Payload);
                    _log.Warning(LogCategory.Network, $"Server closed the connection: {disconnect.Text}");
                    SetDisconnected(disconnect.Reason, disconnect.Text);
                    return;
                }

                into.Add(message);
            }

            if (State == ClientConnectionState.Connected && Peer.IsTimedOut(now))
            {
                _log.Error(LogCategory.Network, "Connection timed out.");
                SetDisconnected(DisconnectReason.Timeout, "connection timed out");
                return;
            }

            Peer.Flush(now);
        }

        private void SendConnectRequest(double now)
        {
            if (_request == null || _server == null)
            {
                return;
            }

            _attempts++;
            _lastAttemptTime = now;
            _transport.SendConnectionless(_server, NetMessageType.ConnectRequest, _request.Serialize());
        }

        private void HandleConnectionless(NetMessageType type, byte[] body, double now)
        {
            if (State != ClientConnectionState.Connecting || _request == null || _server == null)
            {
                return;
            }

            switch (type)
            {
                case NetMessageType.ConnectAccept:
                {
                    ConnectAcceptMessage accept;
                    try
                    {
                        accept = ConnectAcceptMessage.Deserialize(body);
                    }
                    catch (NetSerializationException exception)
                    {
                        _log.Error(LogCategory.Network, "Could not decode the server's accept packet.", exception);
                        return;
                    }

                    if (accept.ClientNonce != _request.ClientNonce)
                    {
                        // A late reply to an earlier attempt; ignore it rather than
                        // adopting a session id that belongs to a stale handshake.
                        return;
                    }

                    Accept = accept;
                    Peer = new NetPeer(_transport, _server, accept.SessionId, now);
                    State = ClientConnectionState.Connected;
                    _log.Success(
                        LogCategory.Network,
                        $"Connected to '{accept.ServerName}' as player {accept.PlayerId} (entity {accept.PlayerEntityId})" +
                        (accept.Restored ? " — previous state restored" : string.Empty));

                    Accepted?.Invoke(accept);
                    break;
                }

                case NetMessageType.ConnectReject:
                {
                    ConnectRejectMessage reject = ConnectRejectMessage.Deserialize(body);
                    if (reject.ClientNonce != 0 && reject.ClientNonce != _request.ClientNonce)
                    {
                        return;
                    }

                    LastError = reject.Message;
                    State = ClientConnectionState.Failed;
                    _log.Error(LogCategory.Network, $"Server refused the connection: {reject.Message}");
                    Rejected?.Invoke(reject.Reason, reject.Message);
                    break;
                }
            }
        }

        private void SetDisconnected(DisconnectReason reason, string text)
        {
            ClientConnectionState previous = State;
            State = ClientConnectionState.Disconnected;
            Peer = null;
            Accept = null;
            LastError = text;

            if (previous == ClientConnectionState.Connected)
            {
                Disconnected?.Invoke(reason, text);
            }
        }
    }
}
