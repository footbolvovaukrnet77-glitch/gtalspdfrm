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

        /// <summary>
        /// Largest message that can be reassembled from fragments. Bounded because the
        /// receiver has to buffer every fragment until the set is complete, and a peer
        /// that announces a huge message and then never finishes it would otherwise be
        /// able to exhaust memory.
        /// </summary>
        public const int MaxFragmentedMessage = 256 * 1024;

        /// <summary>Fragment sets being reassembled at once. Also a memory bound.</summary>
        public const int MaxConcurrentFragmentSets = 8;

        /// <summary>Header inside a fragment payload: group id, index, count, inner type.</summary>
        private const int FragmentHeaderSize = 5;

        private readonly IDatagramTransport _transport;
        private readonly NetWriter _writer = new NetWriter(ProtocolConstants.MaxPacketSize);
        private readonly Queue<PendingMessage> _outUnreliable = new Queue<PendingMessage>();
        private readonly List<PendingMessage> _outReliable = new List<PendingMessage>();
        private readonly Dictionary<ushort, ReceivedMessage> _pendingOrdered = new Dictionary<ushort, ReceivedMessage>();
        private readonly Queue<ReceivedMessage> _delivered = new Queue<ReceivedMessage>();
        private readonly SentPacket[] _sentPackets = new SentPacket[SentWindow];

        private readonly Dictionary<ushort, FragmentSet> _fragments = new Dictionary<ushort, FragmentSet>();

        private ushort _nextFragmentGroup;
        /// <summary>
        /// Packet sequence numbers start at 1. Zero is reserved for "I have not
        /// received anything from you yet".
        /// <para>
        /// Without that reservation a freshly created peer sends its very first
        /// packet carrying <c>ack = 0</c> — its uninitialised remote sequence — and
        /// the other side reads that as an acknowledgement of its packet 0. Anything
        /// reliable in that packet is then dropped from the retransmission list and
        /// never sent again; because reliable delivery is ordered, every later
        /// reliable message queues behind a sequence that will never arrive and the
        /// channel wedges silently for the life of the connection.
        /// </para>
        /// </summary>
        private ushort _nextPacketSequence = 1;
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
                if (delivery != DeliveryMethod.ReliableOrdered)
                {
                    // Unreliable fragmentation is a trap: losing any one fragment loses
                    // the whole message, so the effective loss rate is multiplied by the
                    // fragment count. Anything big enough to need splitting is worth
                    // sending reliably.
                    throw new NetSerializationException(
                        $"Message {type} is {payload.Length} bytes. Unreliable messages must fit in " +
                        $"{MaxMessagePayload} bytes; send it reliably to have it fragmented.");
                }

                SendFragmented(type, payload);
                return;
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

        /// <summary>
        /// Splits an oversized message into fragments, each an ordinary reliable
        /// message. Ordering and retransmission come for free from the reliable
        /// channel, so reassembly does not need its own acknowledgement scheme.
        /// </summary>
        private void SendFragmented(NetMessageType type, byte[] payload)
        {
            int chunkSize = MaxMessagePayload - FragmentHeaderSize;
            int count = (payload.Length + chunkSize - 1) / chunkSize;

            if (count > byte.MaxValue || payload.Length > MaxFragmentedMessage)
            {
                throw new NetSerializationException(
                    $"Message {type} is {payload.Length} bytes; the fragmented limit is {MaxFragmentedMessage}.");
            }

            ushort group = _nextFragmentGroup++;
            for (int index = 0; index < count; index++)
            {
                int offset = index * chunkSize;
                int length = Math.Min(chunkSize, payload.Length - offset);

                var fragment = new byte[FragmentHeaderSize + length];
                fragment[0] = (byte)group;
                fragment[1] = (byte)(group >> 8);
                fragment[2] = (byte)index;
                fragment[3] = (byte)count;
                fragment[4] = (byte)type;
                Array.Copy(payload, offset, fragment, FragmentHeaderSize, length);

                _outReliable.Add(new PendingMessage(NetMessageType.Fragment, fragment, true, _nextReliableSequence++));
            }

            Stats.FragmentsSent += count;
        }

        /// <summary>
        /// Accumulates a fragment and returns the reassembled message once the set is
        /// complete. Reliable delivery is ordered, so fragments normally arrive in
        /// sequence; the index is still honoured so a future unordered channel does not
        /// silently corrupt a message.
        /// </summary>
        private bool TryReassemble(ReceivedMessage message, out ReceivedMessage complete)
        {
            complete = default;
            byte[] payload = message.Payload;

            if (payload.Length < FragmentHeaderSize)
            {
                Stats.MessagesDropped++;
                return false;
            }

            ushort group = (ushort)(payload[0] | (payload[1] << 8));
            byte index = payload[2];
            byte count = payload[3];
            var innerType = (NetMessageType)payload[4];
            int chunkLength = payload.Length - FragmentHeaderSize;

            if (count == 0 || index >= count)
            {
                Stats.MessagesDropped++;
                return false;
            }

            if (!_fragments.TryGetValue(group, out FragmentSet? set))
            {
                if (_fragments.Count >= MaxConcurrentFragmentSets)
                {
                    // A peer opening more fragment sets than it finishes. Dropping the
                    // oldest keeps memory bounded without punishing a slow but honest
                    // sender.
                    ushort oldest = 0;
                    bool found = false;
                    foreach (ushort key in _fragments.Keys)
                    {
                        oldest = key;
                        found = true;
                        break;
                    }

                    if (found)
                    {
                        _fragments.Remove(oldest);
                        Stats.MessagesDropped++;
                    }
                }

                set = new FragmentSet(count, innerType);
                _fragments[group] = set;
            }

            if (!set.Accept(index, payload, FragmentHeaderSize, chunkLength, out string? error))
            {
                _fragments.Remove(group);
                Stats.MessagesDropped++;
                return false;
            }

            if (!set.IsComplete)
            {
                return false;
            }

            _fragments.Remove(group);
            complete = new ReceivedMessage(set.InnerType, set.Assemble(), DeliveryMethod.ReliableOrdered);
            return true;
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
                if (_nextPacketSequence == 0)
                {
                    // Skip the reserved value on wraparound. The cost is one ack bit
                    // every 65536 packets, which shows up as a single spurious
                    // retransmission and nothing else.
                    _nextPacketSequence = 1;
                }

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
                _nextOrderedDelivery++;

                if (next.Type == NetMessageType.Fragment)
                {
                    if (TryReassemble(next, out ReceivedMessage complete))
                    {
                        _delivered.Enqueue(complete);
                    }

                    continue;
                }

                _delivered.Enqueue(next);
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
            if (ack == 0)
            {
                // The remote has not received a packet from us yet, so it is not
                // acknowledging anything and nothing is stale. See _nextPacketSequence.
                return;
            }

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

        /// <summary>One message being reassembled from its fragments.</summary>
        private sealed class FragmentSet
        {
            private readonly byte[]?[] _chunks;
            private int _received;
            private int _totalLength;

            public FragmentSet(byte count, NetMessageType innerType)
            {
                _chunks = new byte[count][];
                InnerType = innerType;
            }

            public NetMessageType InnerType { get; }

            public bool IsComplete => _received == _chunks.Length;

            public bool Accept(byte index, byte[] source, int offset, int length, out string? error)
            {
                error = null;

                if (_chunks[index] != null)
                {
                    // A duplicate fragment from a retransmission that crossed its ack.
                    return true;
                }

                if (_totalLength + length > MaxFragmentedMessage)
                {
                    error = "the fragment set exceeds the reassembly limit";
                    return false;
                }

                var chunk = new byte[length];
                Array.Copy(source, offset, chunk, 0, length);
                _chunks[index] = chunk;
                _received++;
                _totalLength += length;
                return true;
            }

            public byte[] Assemble()
            {
                var result = new byte[_totalLength];
                int position = 0;
                foreach (byte[]? chunk in _chunks)
                {
                    if (chunk == null)
                    {
                        continue;
                    }

                    Array.Copy(chunk, 0, result, position, chunk.Length);
                    position += chunk.Length;
                }

                return result;
            }
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
