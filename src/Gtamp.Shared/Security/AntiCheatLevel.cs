namespace Gtamp.Shared.Security
{
    /// <summary>
    /// Master prompt section 31. Even at <see cref="Off"/> the protocol-level checks
    /// (bounds, NaN, packet rate) stay on — those are not anti-cheat, they are what
    /// keeps a malformed packet from corrupting the world.
    /// </summary>
    public enum AntiCheatLevel : byte
    {
        Off = 0,
        Basic = 1,
        Standard = 2,
        Strict = 3,
        Custom = 4,
    }

    public enum ViolationAction : byte
    {
        Ignore = 0,
        Log = 1,
        Warn = 2,
        Kick = 3,
        Ban = 4,
    }

    public enum ViolationKind : byte
    {
        None = 0,
        InvalidPosition = 1,
        SpeedHack = 2,
        Teleport = 3,
        HealthHack = 4,
        ArmorHack = 5,
        GodMode = 6,
        InfiniteAmmo = 7,
        EntityOwnership = 8,
        EntitySpam = 9,
        PacketRate = 10,
        InvalidEvent = 11,
        WeaponNotOwned = 12,
        DamageOutOfRange = 13,
    }
}
