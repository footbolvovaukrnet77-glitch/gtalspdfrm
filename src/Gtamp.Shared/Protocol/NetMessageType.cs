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

        // 0x10-0x1F — timing and diagnostics.
        Ping = 0x10,
        Pong = 0x11,

        // 0x20-0x2F — replication.
        ClientStateUpdate = 0x20,
        Snapshot = 0x21,
        SnapshotAck = 0x22,
        ResyncRequest = 0x23,

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

        // 0xF0-0xFF — reserved for the Mod SDK.
        ModMessageFirst = 0xF0,
        ModMessageLast = 0xFF,
    }
}
