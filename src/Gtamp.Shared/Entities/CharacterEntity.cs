using System;
using Gtamp.Shared.Core;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// State shared by anything on two legs — a connected player and a networked NPC
    /// carry the same body, weapon and vehicle-seat state, and the client drives both
    /// through the same ped controller.
    /// <para>
    /// Declaring those fields once, in <see cref="CharacterFields"/>, keeps the two
    /// wire layouts in step. Two hand-maintained copies of thirteen field
    /// declarations would drift, and a drift between them is a silent decode
    /// corruption rather than a compile error.
    /// </para>
    /// </summary>
    public abstract class CharacterEntity : NetEntity
    {
        protected CharacterEntity(EntityId id, EntityType type)
            : base(id, type)
        {
        }

        public uint ModelHash { get; set; }

        public int Health { get; set; } = 200;

        public int MaxHealth { get; set; } = 200;

        public int Armor { get; set; }

        public PlayerFlags Flags { get; set; }

        public MovementState Movement { get; set; }

        public uint CurrentWeaponHash { get; set; }

        public int Ammo { get; set; }

        public NetVector3 AimPosition { get; set; }

        public EntityId VehicleId { get; set; }

        /// <summary>GTA V seat index; -1 is the driver seat, -2 means "not in a vehicle".</summary>
        public sbyte VehicleSeat { get; set; } = -2;

        /// <summary>Hash of the currently played animation or scenario, 0 when none.</summary>
        public uint AnimationHash { get; set; }

        public PedAppearance Appearance { get; } = new PedAppearance();

        public bool IsAlive => Health > 0 && (Flags & PlayerFlags.Dead) == 0;

        public bool IsInVehicle => VehicleId.IsValid && VehicleSeat > -2;

        public bool HasFlag(PlayerFlags flag) => (Flags & flag) == flag;

        public void SetFlag(PlayerFlags flag, bool value)
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

        protected void CopyCharacterTo(CharacterEntity target)
        {
            target.ModelHash = ModelHash;
            target.Health = Health;
            target.MaxHealth = MaxHealth;
            target.Armor = Armor;
            target.Flags = Flags;
            target.Movement = Movement;
            target.CurrentWeaponHash = CurrentWeaponHash;
            target.Ammo = Ammo;
            target.AimPosition = AimPosition;
            target.VehicleId = VehicleId;
            target.VehicleSeat = VehicleSeat;
            target.AnimationHash = AnimationHash;
            target.Appearance.CopyFrom(Appearance);
            CopyBaseTo(target);
        }
    }

    /// <summary>The shared field declarations. Called by both character serializers.</summary>
    public static class CharacterFields
    {
        public static void Declare<T>(EntityFieldSet<T> fields)
            where T : CharacterEntity
        {
            fields
                .Add(
                    "ModelHash",
                    (a, b) => a.ModelHash != b.ModelHash,
                    (w, e) => w.WriteUInt32(e.ModelHash),
                    (r, e) => e.ModelHash = r.ReadUInt32())
                .Add(
                    "Health",
                    (a, b) => a.Health != b.Health,
                    (w, e) => w.WriteVarInt(e.Health),
                    (r, e) => e.Health = r.ReadVarInt())
                .Add(
                    "MaxHealth",
                    (a, b) => a.MaxHealth != b.MaxHealth,
                    (w, e) => w.WriteVarInt(e.MaxHealth),
                    (r, e) => e.MaxHealth = r.ReadVarInt())
                .Add(
                    "Armor",
                    (a, b) => a.Armor != b.Armor,
                    (w, e) => w.WriteVarInt(e.Armor),
                    (r, e) => e.Armor = r.ReadVarInt())
                .Add(
                    "Flags",
                    (a, b) => a.Flags != b.Flags,
                    (w, e) => w.WriteVarUInt((uint)e.Flags),
                    (r, e) => e.Flags = (PlayerFlags)r.ReadVarUInt())
                .Add(
                    "Movement",
                    (a, b) => a.Movement != b.Movement,
                    (w, e) => w.WriteByte((byte)e.Movement),
                    (r, e) => e.Movement = (MovementState)r.ReadByte())
                .Add(
                    "CurrentWeaponHash",
                    (a, b) => a.CurrentWeaponHash != b.CurrentWeaponHash,
                    (w, e) => w.WriteUInt32(e.CurrentWeaponHash),
                    (r, e) => e.CurrentWeaponHash = r.ReadUInt32())
                .Add(
                    "Ammo",
                    (a, b) => a.Ammo != b.Ammo,
                    (w, e) => w.WriteVarInt(e.Ammo),
                    (r, e) => e.Ammo = r.ReadVarInt())
                .Add(
                    "AimPosition",
                    (a, b) => !a.AimPosition.Equals(b.AimPosition),
                    (w, e) => w.WriteQuantizedPosition(e.AimPosition),
                    (r, e) => e.AimPosition = r.ReadQuantizedPosition())
                .Add(
                    "VehicleId",
                    (a, b) => a.VehicleId != b.VehicleId,
                    (w, e) => w.WriteVarUInt(e.VehicleId.Value),
                    (r, e) => e.VehicleId = new EntityId(r.ReadVarUInt()))
                .Add(
                    "VehicleSeat",
                    (a, b) => a.VehicleSeat != b.VehicleSeat,
                    (w, e) => w.WriteSByte(e.VehicleSeat),
                    (r, e) => e.VehicleSeat = r.ReadSByte())
                .Add(
                    "AnimationHash",
                    (a, b) => a.AnimationHash != b.AnimationHash,
                    (w, e) => w.WriteUInt32(e.AnimationHash),
                    (r, e) => e.AnimationHash = r.ReadUInt32())
                .Add(
                    "Appearance",
                    (a, b) => !a.Appearance.ValueEquals(b.Appearance),
                    (w, e) => e.Appearance.Write(w),
                    (r, e) => e.Appearance.Read(r));
        }
    }
}
