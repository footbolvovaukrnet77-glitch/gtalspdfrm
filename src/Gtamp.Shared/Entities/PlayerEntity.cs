using System;
using Gtamp.Shared.Core;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Movement and posture flags. One bitfield instead of a dozen booleans keeps
    /// the per-player delta small — this is the field that changes most often.
    /// </summary>
    [Flags]
    public enum PlayerFlags : uint
    {
        None = 0,
        Crouching = 1 << 0,
        Sprinting = 1 << 1,
        Jumping = 1 << 2,
        Falling = 1 << 3,
        Swimming = 1 << 4,
        Diving = 1 << 5,
        Climbing = 1 << 6,
        Ragdoll = 1 << 7,
        Dead = 1 << 8,
        Aiming = 1 << 9,
        Shooting = 1 << 10,
        Reloading = 1 << 11,
        Melee = 1 << 12,
        InVehicle = 1 << 13,
        EnteringVehicle = 1 << 14,
        Parachuting = 1 << 15,
        InCover = 1 << 16,
        Invincible = 1 << 17,
    }

    /// <summary>Coarse locomotion state; the client picks the matching animation set.</summary>
    public enum MovementState : byte
    {
        Idle = 0,
        Walk = 1,
        Run = 2,
        Sprint = 3,
    }

    /// <summary>
    /// A connected player's replicated body. Phase 1 and 2 fields are live; the
    /// remaining items from the master prompt's section 9 (clothing components,
    /// props, scenario tasks, full inventory) are tracked in ROADMAP.md and land in
    /// Phase 2/3 — they are deliberately absent rather than stubbed to nothing.
    /// </summary>
    public sealed class PlayerEntity : NetEntity
    {
        public PlayerEntity(EntityId id)
            : base(id, EntityType.Player)
        {
        }

        /// <summary>Server-side player id. Stable across reconnects within a session.</summary>
        public uint PlayerId { get; set; }

        public string Name { get; set; } = string.Empty;

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

        public byte WantedLevel { get; set; }

        /// <summary>Hash of the currently played animation or scenario, 0 when none.</summary>
        public uint AnimationHash { get; set; }

        /// <summary>Clothing components and props. Changes rarely; replicated whole when it does.</summary>
        public PedAppearance Appearance { get; } = new PedAppearance();

        public bool IsAlive => Health > 0 && (Flags & PlayerFlags.Dead) == 0;

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

        public override NetEntity Clone()
        {
            var clone = new PlayerEntity(Id)
            {
                PlayerId = PlayerId,
                Name = Name,
                ModelHash = ModelHash,
                Health = Health,
                MaxHealth = MaxHealth,
                Armor = Armor,
                Flags = Flags,
                Movement = Movement,
                CurrentWeaponHash = CurrentWeaponHash,
                Ammo = Ammo,
                AimPosition = AimPosition,
                VehicleId = VehicleId,
                VehicleSeat = VehicleSeat,
                WantedLevel = WantedLevel,
                AnimationHash = AnimationHash,
            };

            clone.Appearance.CopyFrom(Appearance);
            CopyBaseTo(clone);
            return clone;
        }
    }

    public sealed class PlayerEntitySerializer : EntitySerializer<PlayerEntity>
    {
        public PlayerEntitySerializer()
            : base((byte)EntityType.Player, "player")
        {
        }

        public override NetEntity Create(EntityId id) => new PlayerEntity(id);

        protected override void DeclareFields(EntityFieldSet<PlayerEntity> fields)
        {
            fields
                .Add(
                    "PlayerId",
                    (a, b) => a.PlayerId != b.PlayerId,
                    (w, e) => w.WriteVarUInt(e.PlayerId),
                    (r, e) => e.PlayerId = r.ReadVarUInt())
                .Add(
                    "Name",
                    (a, b) => !string.Equals(a.Name, b.Name, StringComparison.Ordinal),
                    (w, e) => w.WriteString(e.Name),
                    (r, e) => e.Name = r.ReadString(64))
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
                    "WantedLevel",
                    (a, b) => a.WantedLevel != b.WantedLevel,
                    (w, e) => w.WriteByte(e.WantedLevel),
                    (r, e) => e.WantedLevel = r.ReadByte())
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
