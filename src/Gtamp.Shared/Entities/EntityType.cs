namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Built-in entity categories. Values above <see cref="ModDefinedFirst"/> are
    /// handed out to mods by the SDK, which is why this is a byte-backed enum with
    /// a reserved range rather than a closed set.
    /// </summary>
    public enum EntityType : byte
    {
        Unknown = 0,
        Player = 1,
        Vehicle = 2,
        Ped = 3,
        Object = 4,
        Weapon = 5,
        Projectile = 6,
        Pickup = 7,
        Door = 8,
        Mission = 9,
        Marker = 10,

        /// <summary>First id available to <c>RegisterEntity()</c> callers.</summary>
        ModDefinedFirst = 128,
        ModDefinedLast = 255,
    }
}
