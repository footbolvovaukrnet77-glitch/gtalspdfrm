namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Built-in entity categories. Values above <see cref="ModDefinedFirst"/> are
    /// handed out to mods by the SDK, which is why this is a byte-backed enum with
    /// a reserved range rather than a closed set.
    /// <para>
    /// <b>Half of these are reserved ids, not features.</b> An id here is an allocation
    /// in the wire format; a working entity type additionally needs a class and a
    /// serializer registered in <see cref="EntityRegistry"/>. The two are not the same
    /// thing, and a reader who cannot tell them apart will assume <c>Projectile</c>
    /// replicates something. It does not: nothing produces or consumes it.
    /// </para>
    /// <para>
    /// The unimplemented ids stay rather than being deleted, because renumbering a wire
    /// format to close a gap breaks every client that already speaks it. What was
    /// missing was saying so, and <c>EntityTypeTests</c> now enforces this comment
    /// against the registry so it cannot quietly go stale.
    /// </para>
    /// </summary>
    public enum EntityType : byte
    {
        Unknown = 0,

        // ---- Implemented: a class, a serializer, and tests ----
        Player = 1,
        Vehicle = 2,
        Ped = 3,
        Object = 4,

        // ---- Reserved: an id and nothing else ----

        /// <summary>Reserved. A dropped weapon as a world entity; not implemented.</summary>
        Weapon = 5,

        /// <summary>
        /// Reserved, not implemented. Grenades, rockets and bullets in flight are the
        /// obvious gap next to what other co-op mods replicate, and closing it means
        /// deciding what a projectile even is on a client that did not fire it — a
        /// visual echo, or something the damage arbiter accepts hits from. That is a
        /// design question, not a missing serializer, so the id waits.
        /// </summary>
        Projectile = 6,

        /// <summary>Reserved. World pickups; not implemented.</summary>
        Pickup = 7,

        /// <summary>Reserved. Doors with their own state, such as garages; not implemented.</summary>
        Door = 8,

        /// <summary>Implemented, as the activity system: missions, callouts and jobs.</summary>
        Mission = 9,

        /// <summary>Reserved. Map markers as replicated entities; not implemented.</summary>
        Marker = 10,

        /// <summary>First id available to <c>RegisterEntity()</c> callers.</summary>
        ModDefinedFirst = 128,
        ModDefinedLast = 255,
    }
}
