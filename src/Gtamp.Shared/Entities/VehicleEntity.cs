using System;
using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// A networked vehicle (master prompt section 10).
    /// <para>
    /// <b>Ownership matters more here than anywhere else.</b> RAGE's vehicle physics
    /// is deterministic only given identical inputs, timestep and collision state,
    /// and none of those hold across two machines. So one client owns the
    /// simulation, the server validates and replicates what that owner reports, and
    /// everyone else interpolates. See docs/ENGINE_ANALYSIS.md §4.2.
    /// </para>
    /// </summary>
    public sealed class VehicleEntity : NetEntity
    {
        public VehicleEntity(EntityId id)
            : base(id, EntityType.Vehicle)
        {
        }

        public uint ModelHash { get; set; }

        /// <summary>Pitch in degrees. <see cref="NetEntity.Heading"/> carries yaw.</summary>
        public float Pitch { get; set; }

        public float Roll { get; set; }

        public NetVector3 AngularVelocity { get; set; }

        public float EngineHealth { get; set; } = 1000f;

        public float BodyHealth { get; set; } = 1000f;

        public float PetrolTankHealth { get; set; } = 1000f;

        /// <summary>Litres remaining, or the mod's own scale.</summary>
        public float FuelLevel { get; set; } = 65f;

        /// <summary>0..15, GTA V's own dirt scale.</summary>
        public float DirtLevel { get; set; }

        /// <summary>-1..1. Replicated so a non-owning client can blend wheel and pedal animation.</summary>
        public float Throttle { get; set; }

        public float Brake { get; set; }

        /// <summary>Steering angle in radians, -1..1 of full lock.</summary>
        public float Steering { get; set; }

        /// <summary>0..1 of redline.</summary>
        public float EngineRpm { get; set; }

        /// <summary>-1 reverse, 0 neutral, 1+ forward.</summary>
        public sbyte Gear { get; set; }

        public VehicleFlags Flags { get; set; }

        public byte RadioStation { get; set; }

        public VehicleDoorStates Doors { get; set; }

        /// <summary>One bit per window; set means intact.</summary>
        public byte Windows { get; set; } = 0xFF;

        public VehicleTireStates Tires { get; set; }

        public VehicleColors Colors { get; set; }

        /// <summary>-1 means no livery.</summary>
        public sbyte Livery { get; set; } = -1;

        public byte WheelType { get; set; }

        public string LicensePlate { get; set; } = string.Empty;

        public byte PlateType { get; set; }

        /// <summary>One bit per extra; set means enabled.</summary>
        public ushort Extras { get; set; }

        /// <summary>Packed RGBA.</summary>
        public uint NeonColor { get; set; }

        /// <summary>One bit per neon strip: left, right, front, back.</summary>
        public byte NeonLayout { get; set; }

        public List<VehicleMod> Mods { get; } = new List<VehicleMod>();

        public List<VehicleOccupant> Occupants { get; } = new List<VehicleOccupant>();

        public EntityId TrailerId { get; set; }

        public EntityId AttachedToId { get; set; }

        public bool IsDriveable => EngineHealth > 0f && (Flags & VehicleFlags.Undriveable) == 0;

        public bool HasFlag(VehicleFlags flag) => (Flags & flag) == flag;

        public void SetFlag(VehicleFlags flag, bool value)
        {
            if (value)
            {
                Flags |= flag;
            }
            else
            {
                Flags &= ~flag;
            }
        }

        public EntityId GetOccupant(sbyte seat)
        {
            foreach (VehicleOccupant occupant in Occupants)
            {
                if (occupant.Seat == seat)
                {
                    return occupant.Occupant;
                }
            }

            return EntityId.None;
        }

        public EntityId Driver => GetOccupant(-1);

        public void SetOccupant(sbyte seat, EntityId occupant)
        {
            for (int i = 0; i < Occupants.Count; i++)
            {
                if (Occupants[i].Seat != seat)
                {
                    continue;
                }

                if (occupant.IsValid)
                {
                    Occupants[i] = new VehicleOccupant(seat, occupant);
                }
                else
                {
                    Occupants.RemoveAt(i);
                }

                return;
            }

            if (occupant.IsValid)
            {
                Occupants.Add(new VehicleOccupant(seat, occupant));
            }
        }

        public void RemoveOccupant(EntityId occupant)
        {
            for (int i = Occupants.Count - 1; i >= 0; i--)
            {
                if (Occupants[i].Occupant == occupant)
                {
                    Occupants.RemoveAt(i);
                }
            }
        }

        public override NetEntity Clone()
        {
            var clone = new VehicleEntity(Id)
            {
                ModelHash = ModelHash,
                Pitch = Pitch,
                Roll = Roll,
                AngularVelocity = AngularVelocity,
                EngineHealth = EngineHealth,
                BodyHealth = BodyHealth,
                PetrolTankHealth = PetrolTankHealth,
                FuelLevel = FuelLevel,
                DirtLevel = DirtLevel,
                Throttle = Throttle,
                Brake = Brake,
                Steering = Steering,
                EngineRpm = EngineRpm,
                Gear = Gear,
                Flags = Flags,
                RadioStation = RadioStation,
                Doors = Doors,
                Windows = Windows,
                Tires = Tires,
                Colors = Colors,
                Livery = Livery,
                WheelType = WheelType,
                LicensePlate = LicensePlate,
                PlateType = PlateType,
                Extras = Extras,
                NeonColor = NeonColor,
                NeonLayout = NeonLayout,
                TrailerId = TrailerId,
                AttachedToId = AttachedToId,
            };

            clone.Mods.AddRange(Mods);
            clone.Occupants.AddRange(Occupants);
            CopyBaseTo(clone);
            return clone;
        }
    }

    public sealed class VehicleEntitySerializer : EntitySerializer<VehicleEntity>
    {
        public VehicleEntitySerializer()
            : base((byte)EntityType.Vehicle, "vehicle")
        {
        }

        public override NetEntity Create(EntityId id) => new VehicleEntity(id);

        protected override void DeclareFields(EntityFieldSet<VehicleEntity> fields)
        {
            fields
                .Add(
                    "ModelHash",
                    (a, b) => a.ModelHash != b.ModelHash,
                    (w, e) => w.WriteUInt32(e.ModelHash),
                    (r, e) => e.ModelHash = r.ReadUInt32())
                .Add(
                    "Pitch",
                    (a, b) => Math.Abs(a.Pitch - b.Pitch) > 0.01f,
                    (w, e) => w.WriteAngleDegrees(e.Pitch),
                    (r, e) => e.Pitch = r.ReadAngleDegrees())
                .Add(
                    "Roll",
                    (a, b) => Math.Abs(a.Roll - b.Roll) > 0.01f,
                    (w, e) => w.WriteAngleDegrees(e.Roll),
                    (r, e) => e.Roll = r.ReadAngleDegrees())
                .Add(
                    "AngularVelocity",
                    (a, b) => !a.AngularVelocity.Equals(b.AngularVelocity),
                    (w, e) => w.WriteQuantizedVelocity(e.AngularVelocity),
                    (r, e) => e.AngularVelocity = r.ReadQuantizedVelocity())
                .Add(
                    "EngineHealth",
                    (a, b) => Math.Abs(a.EngineHealth - b.EngineHealth) > 0.5f,
                    (w, e) => w.WriteVarInt((int)e.EngineHealth),
                    (r, e) => e.EngineHealth = r.ReadVarInt())
                .Add(
                    "BodyHealth",
                    (a, b) => Math.Abs(a.BodyHealth - b.BodyHealth) > 0.5f,
                    (w, e) => w.WriteVarInt((int)e.BodyHealth),
                    (r, e) => e.BodyHealth = r.ReadVarInt())
                .Add(
                    "PetrolTankHealth",
                    (a, b) => Math.Abs(a.PetrolTankHealth - b.PetrolTankHealth) > 0.5f,
                    (w, e) => w.WriteVarInt((int)e.PetrolTankHealth),
                    (r, e) => e.PetrolTankHealth = r.ReadVarInt())
                .Add(
                    "FuelLevel",
                    (a, b) => Math.Abs(a.FuelLevel - b.FuelLevel) > 0.25f,
                    (w, e) => w.WriteSingle(e.FuelLevel),
                    (r, e) => e.FuelLevel = r.ReadSingle())
                .Add(
                    "DirtLevel",
                    (a, b) => Math.Abs(a.DirtLevel - b.DirtLevel) > 0.1f,
                    (w, e) => w.WriteUnit(e.DirtLevel / 15f),
                    (r, e) => e.DirtLevel = r.ReadUnit() * 15f)
                .Add(
                    "Throttle",
                    (a, b) => Math.Abs(a.Throttle - b.Throttle) > 0.02f,
                    (w, e) => w.WriteUnit((e.Throttle + 1f) / 2f),
                    (r, e) => e.Throttle = (r.ReadUnit() * 2f) - 1f)
                .Add(
                    "Brake",
                    (a, b) => Math.Abs(a.Brake - b.Brake) > 0.02f,
                    (w, e) => w.WriteUnit(e.Brake),
                    (r, e) => e.Brake = r.ReadUnit())
                .Add(
                    "Steering",
                    (a, b) => Math.Abs(a.Steering - b.Steering) > 0.02f,
                    (w, e) => w.WriteUnit((e.Steering + 1f) / 2f),
                    (r, e) => e.Steering = (r.ReadUnit() * 2f) - 1f)
                .Add(
                    "Drivetrain",
                    (a, b) => Math.Abs(a.EngineRpm - b.EngineRpm) > 0.02f || a.Gear != b.Gear,
                    (w, e) =>
                    {
                        w.WriteUnit(e.EngineRpm);
                        w.WriteSByte(e.Gear);
                    },
                    (r, e) =>
                    {
                        e.EngineRpm = r.ReadUnit();
                        e.Gear = r.ReadSByte();
                    })
                .Add(
                    "Flags",
                    (a, b) => a.Flags != b.Flags,
                    (w, e) => w.WriteVarUInt((uint)e.Flags),
                    (r, e) => e.Flags = (VehicleFlags)r.ReadVarUInt())
                .Add(
                    "RadioStation",
                    (a, b) => a.RadioStation != b.RadioStation,
                    (w, e) => w.WriteByte(e.RadioStation),
                    (r, e) => e.RadioStation = r.ReadByte())
                .Add(
                    "Doors",
                    (a, b) => !a.Doors.Equals(b.Doors),
                    (w, e) => w.WriteUInt16(e.Doors.Packed),
                    (r, e) => e.Doors = new VehicleDoorStates(r.ReadUInt16()))
                .Add(
                    "Windows",
                    (a, b) => a.Windows != b.Windows,
                    (w, e) => w.WriteByte(e.Windows),
                    (r, e) => e.Windows = r.ReadByte())
                .Add(
                    "Tires",
                    (a, b) => !a.Tires.Equals(b.Tires),
                    (w, e) => w.WriteUInt16(e.Tires.Packed),
                    (r, e) => e.Tires = new VehicleTireStates(r.ReadUInt16()))
                .Add(
                    "Colors",
                    (a, b) => !a.Colors.Equals(b.Colors),
                    (w, e) => e.Colors.Write(w),
                    (r, e) => e.Colors = VehicleColors.Read(r))
                .Add(
                    "Styling",
                    (a, b) => a.Livery != b.Livery || a.WheelType != b.WheelType,
                    (w, e) =>
                    {
                        w.WriteSByte(e.Livery);
                        w.WriteByte(e.WheelType);
                    },
                    (r, e) =>
                    {
                        e.Livery = r.ReadSByte();
                        e.WheelType = r.ReadByte();
                    })
                .Add(
                    "Plate",
                    (a, b) => !string.Equals(a.LicensePlate, b.LicensePlate, StringComparison.Ordinal)
                              || a.PlateType != b.PlateType,
                    (w, e) =>
                    {
                        w.WriteString(e.LicensePlate);
                        w.WriteByte(e.PlateType);
                    },
                    (r, e) =>
                    {
                        e.LicensePlate = r.ReadString(16);
                        e.PlateType = r.ReadByte();
                    })
                .Add(
                    "Extras",
                    (a, b) => a.Extras != b.Extras,
                    (w, e) => w.WriteUInt16(e.Extras),
                    (r, e) => e.Extras = r.ReadUInt16())
                .Add(
                    "Neon",
                    (a, b) => a.NeonColor != b.NeonColor || a.NeonLayout != b.NeonLayout,
                    (w, e) =>
                    {
                        w.WriteUInt32(e.NeonColor);
                        w.WriteByte(e.NeonLayout);
                    },
                    (r, e) =>
                    {
                        e.NeonColor = r.ReadUInt32();
                        e.NeonLayout = r.ReadByte();
                    })
                .Add(
                    "Mods",
                    (a, b) => !VehicleStateLists.ModsEqual(a.Mods, b.Mods),
                    WriteMods,
                    ReadMods)
                .Add(
                    "Occupants",
                    (a, b) => !VehicleStateLists.OccupantsEqual(a.Occupants, b.Occupants),
                    WriteOccupants,
                    ReadOccupants)
                .Add(
                    "TrailerId",
                    (a, b) => a.TrailerId != b.TrailerId,
                    (w, e) => w.WriteVarUInt(e.TrailerId.Value),
                    (r, e) => e.TrailerId = new EntityId(r.ReadVarUInt()))
                .Add(
                    "AttachedToId",
                    (a, b) => a.AttachedToId != b.AttachedToId,
                    (w, e) => w.WriteVarUInt(e.AttachedToId.Value),
                    (r, e) => e.AttachedToId = new EntityId(r.ReadVarUInt()));
        }

        private static void WriteMods(NetWriter writer, VehicleEntity entity)
        {
            writer.WriteVarUInt((uint)entity.Mods.Count);
            foreach (VehicleMod mod in entity.Mods)
            {
                writer.WriteByte(mod.Type);
                writer.WriteVarInt(mod.Index);
            }
        }

        private static void ReadMods(NetReader reader, VehicleEntity entity)
        {
            uint count = reader.ReadVarUInt();
            if (count > VehicleStateLists.MaxMods)
            {
                throw new NetSerializationException(
                    $"Vehicle declares {count} mods; the limit is {VehicleStateLists.MaxMods}.");
            }

            entity.Mods.Clear();
            for (uint i = 0; i < count; i++)
            {
                entity.Mods.Add(new VehicleMod(reader.ReadByte(), (short)reader.ReadVarInt()));
            }
        }

        private static void WriteOccupants(NetWriter writer, VehicleEntity entity)
        {
            writer.WriteVarUInt((uint)entity.Occupants.Count);
            foreach (VehicleOccupant occupant in entity.Occupants)
            {
                writer.WriteSByte(occupant.Seat);
                writer.WriteVarUInt(occupant.Occupant.Value);
            }
        }

        private static void ReadOccupants(NetReader reader, VehicleEntity entity)
        {
            uint count = reader.ReadVarUInt();
            if (count > VehicleStateLists.MaxOccupants)
            {
                throw new NetSerializationException(
                    $"Vehicle declares {count} occupants; the limit is {VehicleStateLists.MaxOccupants}.");
            }

            entity.Occupants.Clear();
            for (uint i = 0; i < count; i++)
            {
                entity.Occupants.Add(new VehicleOccupant(reader.ReadSByte(), new EntityId(reader.ReadVarUInt())));
            }
        }
    }
}
