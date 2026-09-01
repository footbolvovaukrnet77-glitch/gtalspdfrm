using System;
using System.Collections.Generic;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Vehicle booleans, packed into one field.
    /// <para>
    /// Nineteen separate replicated fields would each cost a mask bit and a byte on
    /// every delta that touched any of them; as one varint they cost three bytes
    /// total and change together, which is how they actually behave — a siren going
    /// on usually coincides with lights and a horn.
    /// </para>
    /// </summary>
    [Flags]
    public enum VehicleFlags : uint
    {
        None = 0,

        // Read from the owner's vehicle and applied to every replicated copy.
        EngineRunning = 1 << 0,
        Lights = 1 << 1,
        HighBeams = 1 << 2,
        SirenActive = 1 << 3,
        SirenMuted = 1 << 4,
        HornActive = 1 << 5,
        Locked = 1 << 6,
        RoofOpen = 1 << 7,
        LeftIndicator = 1 << 9,
        RightIndicator = 1 << 10,
        InteriorLight = 1 << 11,
        TaxiLight = 1 << 12,
        Undriveable = 1 << 15,
        SearchLight = 1 << 18,

        // --- carried, but derived from something else that is already replicated ---
        //
        // These are not gaps and are not applied. Each one is a restatement of state
        // that arrives by another route, and applying it a second time would be a
        // second chance to disagree with the first.

        /// <summary>Both indicators at once. GTA V has no hazard state of its own — it is the two lights.</summary>
        HazardLights = 1 << 8,

        /// <summary>Follows from <see cref="VehicleEntity.Brake"/>, which is replicated and applied.</summary>
        BrakeLights = 1 << 13,

        /// <summary>Follows from engine and body health, which are replicated.</summary>
        Burnt = 1 << 14,

        /// <summary>Superseded by <see cref="VehicleEntity.NeonLayout"/>, which says which strips as well as whether.</summary>
        NeonEnabled = 1 << 17,

        /// <summary>
        /// **Not replicated, and cannot be.** <c>SET_VEHICLE_HANDBRAKE</c> has no
        /// paired getter, so this can be written and never read. It was applied for a
        /// while from a value nothing sampled, which released the handbrake of every
        /// replicated car on every frame — a write-only flag is worse than an absent
        /// one. Kept as a bit so a mod that tracks it itself has somewhere to put it.
        /// </summary>
        Handbrake = 1 << 16,
    }

    /// <summary>
    /// Per-door open and broken bits. GTA V has at most eight door indices, so both
    /// states fit in one 16-bit word.
    /// </summary>
    public readonly struct VehicleDoorStates : IEquatable<VehicleDoorStates>
    {
        public const int DoorCount = 8;

        public VehicleDoorStates(ushort packed)
        {
            Packed = packed;
        }

        public ushort Packed { get; }

        public bool IsOpen(int door) => (Packed & (1 << door)) != 0;

        public bool IsBroken(int door) => (Packed & (1 << (door + DoorCount))) != 0;

        public VehicleDoorStates WithOpen(int door, bool open) =>
            new VehicleDoorStates(Set(Packed, door, open));

        public VehicleDoorStates WithBroken(int door, bool broken) =>
            new VehicleDoorStates(Set(Packed, door + DoorCount, broken));

        private static ushort Set(ushort packed, int bit, bool value) =>
            (ushort)(value ? packed | (1 << bit) : packed & ~(1 << bit));

        public bool Equals(VehicleDoorStates other) => Packed == other.Packed;

        public override bool Equals(object? obj) => obj is VehicleDoorStates other && Equals(other);

        public override int GetHashCode() => Packed;
    }

    /// <summary>Per-wheel burst and puncture bits, same packing as the doors.</summary>
    public readonly struct VehicleTireStates : IEquatable<VehicleTireStates>
    {
        public const int TireCount = 8;

        public VehicleTireStates(ushort packed)
        {
            Packed = packed;
        }

        public ushort Packed { get; }

        public bool IsBurst(int tire) => (Packed & (1 << tire)) != 0;

        public bool IsPunctured(int tire) => (Packed & (1 << (tire + TireCount))) != 0;

        public VehicleTireStates WithBurst(int tire, bool burst) =>
            new VehicleTireStates(Set(Packed, tire, burst));

        public VehicleTireStates WithPunctured(int tire, bool punctured) =>
            new VehicleTireStates(Set(Packed, tire + TireCount, punctured));

        private static ushort Set(ushort packed, int bit, bool value) =>
            (ushort)(value ? packed | (1 << bit) : packed & ~(1 << bit));

        public bool Equals(VehicleTireStates other) => Packed == other.Packed;

        public override bool Equals(object? obj) => obj is VehicleTireStates other && Equals(other);

        public override int GetHashCode() => Packed;
    }

    /// <summary>The six paint indices, grouped because they are set together at spawn.</summary>
    public readonly struct VehicleColors : IEquatable<VehicleColors>
    {
        public VehicleColors(byte primary, byte secondary, byte pearlescent, byte wheel, byte interior, byte dashboard)
        {
            Primary = primary;
            Secondary = secondary;
            Pearlescent = pearlescent;
            Wheel = wheel;
            Interior = interior;
            Dashboard = dashboard;
        }

        public byte Primary { get; }

        public byte Secondary { get; }

        public byte Pearlescent { get; }

        public byte Wheel { get; }

        public byte Interior { get; }

        public byte Dashboard { get; }

        public bool Equals(VehicleColors other) =>
            Primary == other.Primary && Secondary == other.Secondary && Pearlescent == other.Pearlescent
            && Wheel == other.Wheel && Interior == other.Interior && Dashboard == other.Dashboard;

        public override bool Equals(object? obj) => obj is VehicleColors other && Equals(other);

        public override int GetHashCode() => (Primary << 24) | (Secondary << 16) | (Pearlescent << 8) | Wheel;

        public void Write(NetWriter writer)
        {
            writer.WriteByte(Primary);
            writer.WriteByte(Secondary);
            writer.WriteByte(Pearlescent);
            writer.WriteByte(Wheel);
            writer.WriteByte(Interior);
            writer.WriteByte(Dashboard);
        }

        public static VehicleColors Read(NetReader reader) => new VehicleColors(
            reader.ReadByte(), reader.ReadByte(), reader.ReadByte(),
            reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
    }

    /// <summary>One installed tuning part.</summary>
    public readonly struct VehicleMod : IEquatable<VehicleMod>
    {
        public VehicleMod(byte type, short index)
        {
            Type = type;
            Index = index;
        }

        /// <summary>GTA V mod slot (spoiler, engine, suspension, ...).</summary>
        public byte Type { get; }

        /// <summary>Index within the slot; -1 means stock.</summary>
        public short Index { get; }

        public bool Equals(VehicleMod other) => Type == other.Type && Index == other.Index;

        public override bool Equals(object? obj) => obj is VehicleMod other && Equals(other);

        public override int GetHashCode() => (Type << 16) | (ushort)Index;
    }

    /// <summary>Who is in which seat.</summary>
    public readonly struct VehicleOccupant : IEquatable<VehicleOccupant>
    {
        public VehicleOccupant(sbyte seat, EntityId occupant)
        {
            Seat = seat;
            Occupant = occupant;
        }

        /// <summary>-1 is the driver seat; 0 and up are passengers.</summary>
        public sbyte Seat { get; }

        public EntityId Occupant { get; }

        public bool Equals(VehicleOccupant other) => Seat == other.Seat && Occupant == other.Occupant;

        public override bool Equals(object? obj) => obj is VehicleOccupant other && Equals(other);

        public override int GetHashCode() => (Seat << 24) ^ (int)Occupant.Value;
    }

    internal static class VehicleStateLists
    {
        public const int MaxMods = 64;
        public const int MaxOccupants = 16;

        public static bool ModsEqual(List<VehicleMod> a, List<VehicleMod> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Equals(b[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool OccupantsEqual(List<VehicleOccupant> a, List<VehicleOccupant> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!a[i].Equals(b[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
