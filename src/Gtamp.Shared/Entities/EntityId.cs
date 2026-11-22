using System;
using System.Globalization;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Server-assigned network identity. Ids are never reused within a server
    /// lifetime, so a stale reference resolves to "gone" instead of silently
    /// pointing at a different entity after a respawn.
    /// </summary>
    public readonly struct EntityId : IEquatable<EntityId>, IComparable<EntityId>
    {
        public static readonly EntityId None = new EntityId(0);

        public EntityId(uint value)
        {
            Value = value;
        }

        public uint Value { get; }

        public bool IsValid => Value != 0;

        public bool Equals(EntityId other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is EntityId other && Equals(other);

        public override int GetHashCode() => (int)Value;

        public int CompareTo(EntityId other) => Value.CompareTo(other.Value);

        public static bool operator ==(EntityId a, EntityId b) => a.Value == b.Value;

        public static bool operator !=(EntityId a, EntityId b) => a.Value != b.Value;

        public override string ToString() => "#" + Value.ToString(CultureInfo.InvariantCulture);
    }
}
