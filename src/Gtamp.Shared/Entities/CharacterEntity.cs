using System;
using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// State shared by anything on two legs — a connected player and a networked NPC
    /// carry the same body, weapon and vehicle-seat state, and the client drives both
    /// through the same ped controller.
    /// <para>
    /// Declaring those fields once, in <see cref="CharacterFields"/>, keeps the two
    /// wire layouts in step. Two hand-maintained copies of fifteen field
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

        /// <summary>
        /// Weapon tint index, and the components fitted to the weapon in hand —
        /// suppressor, scope, extended clip, grip, flashlight.
        /// <para>
        /// Grouped into one replicated field because they change together and rarely:
        /// a player fits a suppressor once and carries it for the rest of the session.
        /// Held on the character rather than in an inventory because only the weapon
        /// in hand is visible, and the visible thing is what this replicates.
        /// </para>
        /// </summary>
        public byte WeaponTint { get; set; }

        public List<uint> WeaponComponents { get; } = new List<uint>();

        /// <summary>
        /// Cap on components accepted from the wire. A real weapon carries at most a
        /// handful; the cap is what stops a hostile client describing a weapon with a
        /// million attachments and making every other client allocate it.
        /// </summary>
        public const int MaxWeaponComponents = 12;

        public NetVector3 AimPosition { get; set; }

        public EntityId VehicleId { get; set; }

        /// <summary>GTA V seat index; -1 is the driver seat, -2 means "not in a vehicle".</summary>
        public sbyte VehicleSeat { get; set; } = -2;

        /// <summary>Hash of the currently played animation or scenario, 0 when none.</summary>
        public uint AnimationHash { get; set; }

        /// <summary>
        /// Limb positions while this character is ragdolling, and
        /// <see cref="RagdollPose.None"/> otherwise.
        /// <para>
        /// Clearing it the moment the ragdoll ends is not tidiness — it is what keeps
        /// the field out of the delta. A pose left behind would carry its last value
        /// forever and cost nothing, right up until the character ragdolls again and
        /// the driver reads a pose from a fall that happened ten minutes ago.
        /// </para>
        /// </summary>
        public RagdollPose Ragdoll { get; set; }

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
            target.WeaponTint = WeaponTint;
            target.WeaponComponents.Clear();
            target.WeaponComponents.AddRange(WeaponComponents);
            target.AimPosition = AimPosition;
            target.VehicleId = VehicleId;
            target.VehicleSeat = VehicleSeat;
            target.AnimationHash = AnimationHash;
            target.Ragdoll = Ragdoll;
            target.Appearance.CopyFrom(Appearance);
            CopyBaseTo(target);
        }
    }

    /// <summary>The shared field declarations. Called by both character serializers.</summary>
    public static class CharacterFields
    {
        /// <summary>
        /// Order-sensitive comparison. The game enumerates a weapon's components in a
        /// stable order, so an order change is a real change rather than noise, and
        /// sorting to compare would cost an allocation on the delta path every frame.
        /// </summary>
        private static bool SameComponents(List<uint> a, List<uint> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

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
                    "WeaponAttachments",
                    (a, b) => a.WeaponTint != b.WeaponTint || !SameComponents(a.WeaponComponents, b.WeaponComponents),
                    (w, e) =>
                    {
                        w.WriteByte(e.WeaponTint);
                        w.WriteVarUInt((uint)e.WeaponComponents.Count);
                        foreach (uint component in e.WeaponComponents)
                        {
                            w.WriteUInt32(component);
                        }
                    },
                    (r, e) =>
                    {
                        e.WeaponTint = r.ReadByte();
                        uint count = r.ReadVarUInt();
                        if (count > CharacterEntity.MaxWeaponComponents)
                        {
                            throw new NetSerializationException(
                                $"A character claims {count} weapon components; the limit is {CharacterEntity.MaxWeaponComponents}.");
                        }

                        e.WeaponComponents.Clear();
                        for (uint i = 0; i < count; i++)
                        {
                            e.WeaponComponents.Add(r.ReadUInt32());
                        }
                    })
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
                    "RagdollPose",
                    (a, b) => a.Ragdoll != b.Ragdoll,
                    (w, e) => e.Ragdoll.Write(w),
                    (r, e) => e.Ragdoll = RagdollPose.Read(r))
                .Add(
                    "Appearance",
                    (a, b) => !a.Appearance.ValueEquals(b.Appearance),
                    (w, e) => e.Appearance.Write(w),
                    (r, e) => e.Appearance.Read(r));
        }
    }
}
