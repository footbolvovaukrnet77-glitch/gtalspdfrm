namespace Gtamp.Shared.Protocol
{
    public enum DisconnectReason : byte
    {
        Unknown = 0,
        ClientQuit = 1,
        ServerShutdown = 2,
        Timeout = 3,
        ProtocolMismatch = 4,
        ServerFull = 5,
        BadPassword = 6,
        Banned = 7,
        Kicked = 8,
        InvalidName = 9,
        AntiCheat = 10,
        InternalError = 11,
        IncompatibleMods = 12,
        AuthenticationFailed = 13,
    }

    public static class DisconnectReasonText
    {
        public static string Describe(DisconnectReason reason) => reason switch
        {
            DisconnectReason.ClientQuit => "client left the session",
            DisconnectReason.ServerShutdown => "server is shutting down",
            DisconnectReason.Timeout => "connection timed out",
            DisconnectReason.ProtocolMismatch => "protocol version mismatch",
            DisconnectReason.ServerFull => "server is full",
            DisconnectReason.BadPassword => "wrong server password",
            DisconnectReason.Banned => "banned from this server",
            DisconnectReason.Kicked => "kicked by an administrator",
            DisconnectReason.InvalidName => "invalid player name",
            DisconnectReason.AntiCheat => "rejected by anti-cheat",
            DisconnectReason.InternalError => "internal server error",
            DisconnectReason.IncompatibleMods => "required mods are incompatible",
            DisconnectReason.AuthenticationFailed => "could not prove ownership of this identity",
            _ => "unknown reason",
        };
    }
}
