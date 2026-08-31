using Gtamp.Shared.Protocol;

namespace Gtamp.Shared.Net
{
    public enum DeliveryMethod : byte
    {
        /// <summary>Fire and forget. Used for state that is superseded by the next update.</summary>
        Unreliable = 0,

        /// <summary>Retransmitted until acknowledged and delivered in send order.</summary>
        ReliableOrdered = 1,
    }

    public readonly struct ReceivedMessage
    {
        public ReceivedMessage(NetMessageType type, byte[] payload, DeliveryMethod delivery)
        {
            Type = type;
            Payload = payload;
            Delivery = delivery;
        }

        public NetMessageType Type { get; }

        public byte[] Payload { get; }

        public DeliveryMethod Delivery { get; }

        public NetReader CreateReader() => new NetReader(Payload);
    }

    /// <summary>Comparisons for 16-bit sequence numbers that wrap around.</summary>
    public static class SequenceMath
    {
        public static bool GreaterThan(ushort a, ushort b) =>
            ((a > b) && (a - b <= 32768)) || ((a < b) && (b - a > 32768));

        public static bool GreaterThanOrEqual(ushort a, ushort b) => a == b || GreaterThan(a, b);

        /// <summary>Signed distance a - b accounting for wraparound.</summary>
        public static int Difference(ushort a, ushort b) => (short)(a - b);
    }

    /// <summary>Live counters surfaced by the in-game network debugger (F8 -&gt; net).</summary>
    public sealed class NetStats
    {
        public int PacketsSent { get; internal set; }

        public int PacketsReceived { get; internal set; }

        public long BytesSent { get; internal set; }

        public long BytesReceived { get; internal set; }

        public int PacketsLost { get; internal set; }

        public int ReliableRetransmits { get; internal set; }

        public int MessagesDropped { get; internal set; }

        /// <summary>Smoothed round-trip time in seconds.</summary>
        public double RoundTripTime { get; internal set; }

        /// <summary>RTT variance estimate, used for the retransmission timeout.</summary>
        public double RoundTripVariance { get; internal set; }

        public double PacketLoss => PacketsSent > 0 ? (double)PacketsLost / PacketsSent : 0d;

        public int PingMilliseconds => (int)(RoundTripTime * 1000d);

        public void Reset()
        {
            PacketsSent = 0;
            PacketsReceived = 0;
            BytesSent = 0;
            BytesReceived = 0;
            PacketsLost = 0;
            ReliableRetransmits = 0;
            MessagesDropped = 0;
            RoundTripTime = 0;
            RoundTripVariance = 0;
        }
    }
}
