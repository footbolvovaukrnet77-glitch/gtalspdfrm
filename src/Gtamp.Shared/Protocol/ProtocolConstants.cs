namespace Gtamp.Shared.Protocol
{
    public static class ProtocolConstants
    {
        /// <summary>ASCII "GTMP". Cheap discriminator so stray UDP traffic is rejected before parsing.</summary>
        public const uint Magic = 0x504D5447;

        /// <summary>
        /// Wire-format version. Bumped whenever the framing, message ids or entity
        /// field layouts change in a way that is not backwards compatible.
        /// A mismatch is rejected during the handshake with a readable message.
        /// </summary>
        public const ushort ProtocolVersion = 1;

        /// <summary>
        /// Conservative payload budget. 1200 bytes keeps a datagram below the
        /// smallest MTU that survives the public internet without fragmentation.
        /// </summary>
        public const int MaxPacketSize = 1200;

        public const int DefaultPort = 27015;

        /// <summary>Simulation ticks per second. Also the authority rate for validation.</summary>
        public const int DefaultTickRate = 60;

        /// <summary>Snapshots per second sent to each client. Independent of tick rate.</summary>
        public const int DefaultSnapshotRate = 20;

        /// <summary>Client state samples per second sent up to the server.</summary>
        public const int DefaultClientUpdateRate = 30;

        /// <summary>Number of snapshots retained per client for delta baselines.</summary>
        public const int SnapshotHistory = 64;

        /// <summary>Reliable messages buffered out of order before the connection is considered broken.</summary>
        public const int MaxPendingReliable = 512;

        public const double KeepAliveInterval = 1.0;

        public const double ConnectionTimeout = 15.0;

        public const double HandshakeRetryInterval = 0.5;

        public const int HandshakeMaxAttempts = 20;

        public const int MaxPlayerNameLength = 32;
    }
}
