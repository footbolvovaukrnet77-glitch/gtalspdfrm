using System;
using System.Collections.Generic;
using System.Net;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.Security;

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

        /// <summary>
        /// The key this client signs challenges with. Set by the client from
        /// <c>client.ini</c>; null means the identity is a legacy token and any server
        /// that requires authentication will say so.
        /// </summary>
        public IdentityKey? Identity { get; set; }

        /// <summary>Challenges answered on this connection attempt. Reported by /net.</summary>
        public int ChallengesAnswered { get; private set; }

        /// <summary>The proof for the current attempt, resent by the retry timer until the accept arrives.</summary>
        private byte[]? _pendingProof;

        private EphemeralKeyExchange? _exchange;
        private byte[]? _sessionSecret;

        /// <summary>True once packets on this session are encrypted and authenticated.</summary>
        public bool IsEncrypted => Peer?.Crypto != null;

        /// <summary>
        /// Packets that arrived on this session and failed authentication: forged,
        /// corrupted in flight, or replayed into the wrong direction.
        /// <para>
        /// Surfaced because the count is the difference between "the network is bad"
        /// and "someone is injecting packets", and those two have entirely different
        /// answers. Zero on a healthy session, including a lossy one — a packet
        /// mangled by the network is dropped by the UDP checksum long before it
        /// reaches the MAC.
        /// </para>
        /// </summary>
        public int RejectedPackets => Peer?.Crypto?.Rejected ?? 0;

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
            _pendingProof = null;
            _exchange?.Dispose();
            _exchange = null;
            _sessionSecret = null;
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

            // Before the timeout, because a broken ordered channel *looks* like a
            // timeout: the peer keeps answering, delivers nothing reliable, and the
            // session dies fifteen seconds later with nothing said about why.
            if (State == ClientConnectionState.Connected && Peer.Fault != null)
            {
                _log.Error(LogCategory.Network, "The connection can no longer deliver: " + Peer.Fault);
                SetDisconnected(DisconnectReason.Timeout, Peer.Fault);
                return;
            }

            if (State == ClientConnectionState.Connected && Peer.IsTimedOut(now))
            {
                _log.Error(LogCategory.Network, "Connection timed out.");
                SetDisconnected(DisconnectReason.Timeout, "connection timed out");
                return;
            }

            Peer.Flush(now);
        }

        /// <summary>
        /// One retry step. Once a challenge has arrived, the retry re-sends the
        /// <em>proof</em> rather than starting the handshake again.
        /// <para>
        /// Authentication made the handshake four legs instead of two, and on a lossy
        /// link the chance of all four surviving is the square of the chance for two —
        /// at 60% loss that is 2.6% per attempt instead of 16%. Retrying from the
        /// beginning throws away a challenge that did arrive; re-sending the proof
        /// keeps it, so after the challenge lands each retry needs one packet through
        /// instead of three.
        /// </para>
        /// </summary>
        private void SendConnectRequest(double now)
        {
            if (_request == null || _server == null)
            {
                return;
            }

            _attempts++;
            _lastAttemptTime = now;

            if (_pendingProof != null)
            {
                _transport.SendConnectionless(_server, NetMessageType.ConnectProof, _pendingProof);
                return;
            }

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

                    if (_sessionSecret != null)
                    {
                        Peer.Crypto = SessionCrypto.FromSharedSecret(_sessionSecret, isServer: false);
                        Array.Clear(_sessionSecret, 0, _sessionSecret.Length);
                        _sessionSecret = null;

                        // The private half has done its one job. Holding it for the
                        // life of the session would give up the forward secrecy the
                        // ephemeral exchange exists for.
                        _exchange?.Dispose();
                        _exchange = null;
                    }
                    State = ClientConnectionState.Connected;
                    _log.Success(
                        LogCategory.Network,
                        $"Connected to '{accept.ServerName}' as player {accept.PlayerId} (entity {accept.PlayerEntityId})" +
                        (accept.Restored ? " — previous state restored" : string.Empty));

                    Accepted?.Invoke(accept);
                    break;
                }

                case NetMessageType.ConnectChallenge:
                {
                    ConnectChallengeMessage challenge;
                    try
                    {
                        challenge = ConnectChallengeMessage.Deserialize(body);
                    }
                    catch (NetSerializationException exception)
                    {
                        _log.Error(LogCategory.Network, "Could not decode the server's challenge.", exception);
                        return;
                    }

                    if (challenge.ClientNonce != _request.ClientNonce)
                    {
                        // A challenge for an earlier attempt. Answering it would prove
                        // an identity against a nonce the server has already retired.
                        return;
                    }

                    if (Identity == null)
                    {
                        LastError = "this server requires a signing identity and this client has none";
                        State = ClientConnectionState.Failed;
                        _log.Error(LogCategory.Network, "Connection failed: " + LastError);
                        Rejected?.Invoke(DisconnectReason.AuthenticationFailed, LastError);
                        return;
                    }

                    // A fresh exchange per attempt. Reusing one across retries would
                    // mean a captured proof stays valid for the key it agreed.
                    _exchange?.Dispose();
                    _exchange = null;
                    _sessionSecret = null;

                    byte[] clientEphemeral = Array.Empty<byte>();
                    if (EphemeralKeyExchange.IsWellFormed(challenge.EphemeralPublicKey))
                    {
                        try
                        {
                            _exchange = EphemeralKeyExchange.Create();
                            clientEphemeral = _exchange.PublicKey;
                            _sessionSecret = _exchange.Agree(challenge.EphemeralPublicKey);
                        }
                        catch (Exception exception)
                        {
                            LastError = "could not agree a session key: " + exception.Message;
                            State = ClientConnectionState.Failed;
                            _log.Error(LogCategory.Network, "Connection failed: " + LastError);
                            Rejected?.Invoke(DisconnectReason.AuthenticationFailed, LastError);
                            return;
                        }
                    }

                    byte[] payload = IdentityKey.BuildChallenge(
                        challenge.ClientNonce,
                        challenge.ServerNonce,
                        challenge.ServerName,
                        challenge.EphemeralPublicKey,
                        clientEphemeral);

                    var proof = new ConnectProofMessage
                    {
                        ClientNonce = challenge.ClientNonce,
                        PublicKey = Identity.PublicKey,
                        Signature = Identity.Sign(payload),
                        EphemeralPublicKey = clientEphemeral,
                    };

                    ChallengesAnswered++;
                    _pendingProof = proof.Serialize();
                    _transport.SendConnectionless(_server, NetMessageType.ConnectProof, _pendingProof);

                    // The retry timer is deliberately left running. If the proof is
                    // lost the client re-sends its connect request, and the server
                    // answers with the same challenge — so the handshake recovers
                    // without a second retry mechanism of its own.
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
