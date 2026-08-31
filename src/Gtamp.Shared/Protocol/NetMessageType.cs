namespace Gtamp.Shared.Protocol
{
    /// <summary>
    /// Message identifiers carried inside a packet. Ranges are grouped so a new
    /// subsystem can claim a block without colliding with an existing one.
    /// The 0xF0-0xFF block is reserved for mod-defined messages routed by the
    /// Mod SDK (see docs/MOD_SDK.md).
    /// </summary>
    public enum NetMessageType : byte
    {
        None = 0x00,

        // 0x01-0x0F — connection lifecycle (connectionless or reliable).
        ConnectRequest = 0x01,
        ConnectAccept = 0x02,
        ConnectReject = 0x03,
        Disconnect = 0x04,
        KeepAlive = 0x05,

        /// <summary>One piece of a message too large for a single datagram.</summary>
        Fragment = 0x06,

        // 0x10-0x1F — timing and diagnostics.
        Ping = 0x10,
        Pong = 0x11,

        // 0x20-0x2F — replication.
        ClientStateUpdate = 0x20,
        Snapshot = 0x21,
        SnapshotAck = 0x22,
        ResyncRequest = 0x23,

        /// <summary>Client asks the server to give a locally created entity a network identity.</summary>
        EntitySpawnRequest = 0x24,

        /// <summary>The owning client streaming an entity it simulates.</summary>
        OwnedEntityUpdate = 0x25,

        /// <summary>Client asks to destroy, or give up ownership of, an entity.</summary>
        EntityReleaseRequest = 0x26,

        /// <summary>Client reports a hit it landed. The server decides whether it happened.</summary>
        DamageReport = 0x27,

        /// <summary>A mod calling a procedure on the other side and expecting an answer.</summary>
        ModRpcRequest = 0x28,

        ModRpcResponse = 0x29,

        /// <summary>
        /// A mod-defined event, routed by name.
        /// <para>
        /// Names rather than ids: assigning ids in registration order means the two
        /// sides only agree while they register in the same order, and a mod that adds
        /// an event on one side silently renumbers every later one on that side alone.
        /// The name costs a few bytes and removes both the ordering coupling and the
        /// sixteen-event ceiling.
        /// </para>
        /// </summary>
        ModEvent = 0x2A,

        // 0x30-0x3F — reliable world/entity events.
        EntityEvent = 0x30,
        ServerEvent = 0x31,
        ChatMessage = 0x32,

        // 0x40-0x4F — mod negotiation.
        ModManifest = 0x40,
        ModCompatibilityReport = 0x41,

        // 0x50-0x5F — administration and security.
        AdminCommand = 0x50,
        SecurityNotice = 0x51,

        // 0xF0-0xFF — reserved. Mod events are routed by name through ModEvent; this
        // range is held back for a future direct-routing optimisation.
        ModMessageFirst = 0xF0,
        ModMessageLast = 0xFF,
    }
}
