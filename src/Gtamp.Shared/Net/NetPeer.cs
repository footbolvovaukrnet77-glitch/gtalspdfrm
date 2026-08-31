using System;
using System.Collections.Generic;
using System.Net;
using Gtamp.Shared.Protocol;

namespace Gtamp.Shared.Net
{
    /// <summary>
    /// One end of a connected session: packet framing, acknowledgement, reliable
    /// retransmission, ordered delivery, RTT estimation and loss accounting.
    /// <para>
    /// Packet layout (session packets):
    /// <code>
    /// u32 magic | u8 kind | u32 sessionId | u16 seq | u16 ack | u32 ackBits | message*
    /// message := u8 flags | [u16 reliableSeq] | u8 type | varuint length | bytes payload
    /// </code>
    /// Acknowledgement is packet-level: a packet id carries the ids of the reliable
    /// messages it transported, so a single ack retires all of them at once and an
    /// unacked packet re-queues exactly its own messages. See docs/NETWORK_PROTOCOL.md.
    /// </para>
    /// </summary>
    public sealed class NetPeer
    {
        private const byte PacketKindSession = 1;
        private const byte MessageFlagReliable = 0x01;
        private const int SentWindow = 256;

        /// <summary>Largest payload a single message may carry, leaving room for the packet header and framing.</summary>
        public const int MaxMessagePayload = ProtocolConstants.MaxPacketSize - 32;

        /// <summary>
        /// How long an acknowledgement may wait for outbound traffic to piggyback on.
        /// Both ends normally send several times per second, so acks ride along for
        /// free; this only fires when a peer is otherwise silent. Without it a quiet
        /// peer would hold acks until the keep-alive, which inflates the measured RTT
        /// and delays every reliable retransmission decision by up to a second.
        /// </summary>
        public const double MaxAckDelay = 0.02;

        private readonly IDatagramTransport _transport;
        private readonly NetWriter _writer = new NetWriter(ProtocolConstants.MaxPacketSize);
        private readonly Queue<PendingMessage> _outUnreliable = new Queue<PendingMessage>();
        private readonly List<PendingMessage> _outReliable = new List<PendingMessage>();
        private readonly Dictionary<ushort, ReceivedMessage> _pendingOrdered = new Dictionary<ushort, ReceivedMessage>();
        private readonly Queue<ReceivedMessage> _delivered = new Queue<ReceivedMessage>();
        private readonly SentPacket[] _sentPackets = new SentPacket[SentWindow];

        private ushort _nextPacketSequence;
        private ushort _nextReliableSequence;
        private ushort _nextOrderedDelivery;
        private ushort _remoteSequence;
        private uint _remoteAckBits;
        private bool _hasRemoteSequence;
        private double _lastSendTime;
        private bool _ackPending;
        private double _ackPendingSince;

        public NetPeer(IDatagramTransport transport, IPEndPoint remote, uint sessionId, double now)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Remote = remote ?? throw new ArgumentNullException(nameof(remote));
            SessionId = sessionId;
            LastReceiveTime = now;
            _lastSendTime = now;
        }

        public IPEndPoint Remote { get; }

        public uint SessionId { get; }

        public NetStats Stats { get; } = new NetStats();

        public double LastReceiveTime { get; private set; }

        /// <summary>Highest packet id received from the remote peer. Snapshot acking piggybacks on this.</summary>
        public ushort RemoteSequence => _remoteSequence;

        public int UnackedReliableCount => _outReliable.Count;

        public void Send(NetMessageType type, byte[] payload, DeliveryMethod delivery)
        {
            if (payload == null)
            {
                payload = Array.Empty<byte>();
            }

            if (payload.Length > MaxMessagePayload)
            {
                // Higher layers must split their own payloads (the snapshot encoder
                // does). Silently truncating here would corrupt world state.
                throw new NetSerializationException(
                    $"Message {type} is {payload.Length} bytes; the per-message budget is {MaxMessagePayload}.");
            }

            if (delivery == DeliveryMethod.ReliableOrdered)
            {
                var message = new PendingMessage(type, payload, true, _nextReliableSequence++);
                _outReliable.Add(message);
            }
            else
            {
                _outUnreliable.Enqueue(new PendingMessage(type, payload, false, 0));
            }
        }

        /// <summary>Packs queued messages into datagrams and hands them to the transport.</summary>
        public void Flush(double now)
        {
            bool wroteAnything = false;
            int reliableCursor = 0;

            // Emit a packet even with nothing queued when an acknowledgement is due,
            // or when the keep-alive interval has elapsed.
            bool ackDue = _ackPending && now - _ackPendingSince >= MaxAckDelay;
            bool forceKeepAlive = ackDue || now - _lastSendTime >= ProtocolConstants.KeepAliveInterval;

            while (true)
            {
                var carried = new List<ushort>();
                _writer.Reset();
                ushort sequence = _nextPacketSequence;
                WriteHeader(_writer, sequence);
                int headerLength = _writer.Length;

                while (_outUnreliable.Count > 0)
                {
                    PendingMessage message = _outUnreliable.Peek();
                    if (!TryWriteMessage(_writer, message))
                    {
                        break;
                    }

                    _outUnreliable.Dequeue();
                }

                for (; reliableCursor < _outReliable.Count; reliableCursor++)
                {
                    PendingMessage message = _outReliable[reliableCursor];
                    if (message.NextSendTime > now)
                    {
                        continue;
                    }

                    if (!TryWriteMessage(_writer, message))
                    {
                        break;
                    }

                    if (message.SendCount > 0)
                    {
                        Stats.ReliableRetransmits++;
                    }

                    message.SendCount++;
                    message.NextSendTime = now + RetransmissionTimeout();
                    carried.Add(message.ReliableSequence);
                }

                bool empty = _writer.Length == headerLength;
                if (empty && (wroteAnything || !forceKeepAlive))
                {
                    break;
                }

                _nextPacketSequence++;
                RecordSentPacket(sequence, now, carried);
                _transport.Send(Remote, _writer.Buffer, 0, _writer.Length);
                Stats.PacketsSent++;
                Stats.BytesSent += _writer.Length;
                _lastSendTime = now;
                _ackPending = false;
                wroteAnything = true;

                if (_outUnreliable.Count == 0 && reliableCursor >= _outReliable.Count)
                {
                    break;
                }
            }
        }

        /// <summary>Parses one inbound datagram. Returns false if it is not a valid session packet for this peer.</summary>
        public bool HandleDatagram(byte[] data, double now)
        {
            NetReader reader;
            try
            {
                reader = new NetReader(data);
                if (reader.ReadUInt32() != ProtocolConstants.Magic)
                {
                    return false;
                }

                if (reader.ReadByte() != PacketKindSession)
                {
                    return false;
                }

                if (reader.ReadUInt32() != SessionId)
                {
                    return false;
                }

                ushort sequence = reader.ReadUInt16();
                ushort ack = reader.ReadUInt16();
                uint ackBits = reader.ReadUInt32();

                Stats.PacketsReceived++;
                Stats.BytesReceived += data.Length;
                LastReceiveTime = now;

                AcknowledgeRemote(sequence);
                ProcessIncomingAcks(ack, ackBits, now);

                if (!_ackPending)
                {
                    _ackPending = true;
                    _ackPendingSince = now;
                }

                while (!reader.EndOfData)
                {
                    ReadMessage(reader);
                }

                return true;
            }
            catch (NetSerializationException)
            {
                // A malformed packet is dropped, never fatal. The caller logs it
                // under the NETWORK/SECURITY category.
                Stats.MessagesDropped++;
                return false;
            }
        }

        public bool TryDequeue(out ReceivedMessage message)
        {
            if (_delivered.Count == 0)
            {
                message = default;
                return false;
            }

            message = _delivered.Dequeue();
            return true;
        }

        public bool IsTimedOut(double now, double timeout = ProtocolConstants.ConnectionTimeout) =>
            now - LastReceiveTime > timeout;

        private void WriteHeader(NetWriter writer, ushort sequence)
        {
            writer.WriteUInt32(ProtocolConstants.Magic);
            writer.WriteByte(PacketKindSession);
            writer.WriteUInt32(SessionId);
            writer.WriteUInt16(sequence);
            writer.WriteUInt16(_remoteSequence);
            writer.WriteUInt32(_remoteAckBits);
        }

        private bool TryWriteMessage(NetWriter writer, PendingMessage message)
        {
            int needed = 1 + (message.Reliable ? 2 : 0) + 1 + VarUIntSize((uint)message.Payload.Length) + message.Payload.Length;
            if (writer.Length + needed > ProtocolConstants.MaxPacketSize)
            {
                return false;
            }

            writer.WriteByte(message.Reliable ? MessageFlagReliable : (byte)0);
            if (message.Reliable)
            {
                writer.WriteUInt16(message.ReliableSequence);
            }

            writer.WriteByte((byte)message.Type);
            writer.WriteVarUInt((uint)message.Payload.Length);
            writer.WriteBytes(message.Payload, 0, message.Payload.Length);
            return true;
        }

        private void ReadMessage(NetReader reader)
        {
            byte flags = reader.ReadByte();
            bool reliable = (flags & MessageFlagReliable) != 0;
            ushort reliableSequence = reliable ? reader.ReadUInt16() : (ushort)0;
            var type = (NetMessageType)reader.ReadByte();
            byte[] payload = reader.ReadByteArray(ProtocolConstants.MaxPacketSize);

            var message = new ReceivedMessage(
                type, payload, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);

            if (!reliable)
            {
                _delivered.Enqueue(message);
                return;
            }

            if (!SequenceMath.GreaterThanOrEqual(reliableSequence, _nextOrderedDelivery)
                || _pendingOrdered.ContainsKey(reliableSequence))
            {
                // Duplicate produced by a retransmission that crossed its own ack.
                return;
            }

            if (_pendingOrdered.Count >= ProtocolConstants.MaxPendingReliable)
            {
                Stats.MessagesDropped++;
                return;
            }

            _pendingOrdered[reliableSequence] = message;
            while (_pendingOrdered.TryGetValue(_nextOrderedDelivery, out ReceivedMessage next))
            {
                _pendingOrdered.Remove(_nextOrderedDelivery);
                _delivered.Enqueue(next);
                _nextOrderedDelivery++;
            }
        }

        private void AcknowledgeRemote(ushort sequence)
        {
            if (!_hasRemoteSequence)
            {
                _hasRemoteSequence = true;
                _remoteSequence = sequence;
                _remoteAckBits = 0;
                return;
            }

            if (SequenceMath.GreaterThan(sequence, _remoteSequence))
            {
                int shift = SequenceMath.Difference(sequence, _remoteSequence);
                _remoteAckBits = shift >= 32 ? 0u : (_remoteAckBits << shift) | (1u << (shift - 1));
                _remoteSequence = sequence;
            }
            else
            {
                int distance = SequenceMath.Difference(_remoteSequence, sequence);
                if (distance >= 1 && distance <= 32)
                {
                    _remoteAckBits |= 1u << (distance - 1);
                }
            }
        }

        private void ProcessIncomingAcks(ushort ack, uint ackBits, double now)
        {
            AckPacket(ack, now);
            for (int i = 0; i < 32; i++)
            {
                if ((ackBits & (1u << i)) != 0)
                {
                    AckPacket((ushort)(ack - i - 1), now);
                }
            }

            ExpireStalePackets(ack);
        }

        private void AckPacket(ushort sequence, double now)
        {
            ref SentPacket record = ref _sentPackets[sequence % SentWindow];
            if (!record.InUse || record.Sequence != sequence || record.Acked)
            {
                return;
            }

            record.Acked = true;
            SampleRoundTrip(now - record.SendTime);

            if (record.ReliableSequences == null)
            {
                return;
            }

            foreach (ushort reliableSequence in record.ReliableSequences)
            {
                for (int i = _outReliable.Count - 1; i >= 0; i--)
                {
                    if (_outReliable[i].ReliableSequence == reliableSequence)
                    {
                        _outReliable.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// A packet older than the ack window can never be acknowledged any more,
        /// so count it as lost exactly once. This is what feeds the loss figure in
        /// the network debugger.
        /// </summary>
        private void ExpireStalePackets(ushort ack)
        {
            for (int i = 0; i < SentWindow; i++)
            {
                ref SentPacket record = ref _sentPackets[i];
                if (!record.InUse || record.Acked || record.CountedLost)
                {
                    continue;
                }

                if (SequenceMath.Difference(ack, record.Sequence) > 32)
                {
                    record.CountedLost = true;
                    Stats.PacketsLost++;
                }
            }
        }

        private void RecordSentPacket(ushort sequence, double now, List<ushort> reliableSequences)
        {
            ref SentPacket record = ref _sentPackets[sequence % SentWindow];
            record.InUse = true;
            record.Sequence = sequence;
            record.SendTime = now;
            record.Acked = false;
            record.CountedLost = false;
            record.ReliableSequences = reliableSequences.Count > 0 ? reliableSequences : null;
        }

        private void SampleRoundTrip(double sample)
        {
            if (sample < 0)
            {
                return;
            }

            if (Stats.RoundTripTime <= 0)
            {
                Stats.RoundTripTime = sample;
                Stats.RoundTripVariance = sample / 2d;
                return;
            }

            // Jacobson/Karels smoothing, the same estimator TCP uses.
            double error = sample - Stats.RoundTripTime;
            Stats.RoundTripTime += 0.125 * error;
            Stats.RoundTripVariance += 0.25 * (Math.Abs(error) - Stats.RoundTripVariance);
        }

        private double RetransmissionTimeout()
        {
            double rto = Stats.RoundTripTime + (4d * Stats.RoundTripVariance);
            if (rto < 0.05)
            {
                rto = 0.05;
            }

            return rto > 1.0 ? 1.0 : rto;
        }

        private static int VarUIntSize(uint value)
        {
            int size = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                size++;
            }

            return size;
        }

        private sealed class PendingMessage
        {
            public PendingMessage(NetMessageType type, byte[] payload, bool reliable, ushort reliableSequence)
            {
                Type = type;
                Payload = payload;
                Reliable = reliable;
                ReliableSequence = reliableSequence;
            }

            public NetMessageType Type { get; }

            public byte[] Payload { get; }

            public bool Reliable { get; }

            public ushort ReliableSequence { get; }

            public int SendCount { get; set; }

            public double NextSendTime { get; set; }
        }

        private struct SentPacket
        {
            public bool InUse;
            public ushort Sequence;
            public double SendTime;
            public bool Acked;
            public bool CountedLost;
            public List<ushort>? ReliableSequences;
        }
    }
}
