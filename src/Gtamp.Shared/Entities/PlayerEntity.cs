using System;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Movement and posture flags. One bitfield instead of a dozen booleans keeps
    /// the per-character delta small — this is the field that changes most often.
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
    /// A connected player's replicated body. Everything shared with a networked NPC
    /// lives on <see cref="CharacterEntity"/>; only what is specific to a human
    /// player is declared here.
    /// </summary>
    public sealed class PlayerEntity : CharacterEntity
    {
        public PlayerEntity(EntityId id)
            : base(id, EntityType.Player)
        {
        }

        /// <summary>Server-side player id. Stable across reconnects within a session.</summary>
        public uint PlayerId { get; set; }

        public string Name { get; set; } = string.Empty;

        public byte WantedLevel { get; set; }

        public override NetEntity Clone()
        {
            var clone = new PlayerEntity(Id)
            {
                PlayerId = PlayerId,
                Name = Name,
                WantedLevel = WantedLevel,
            };

            CopyCharacterTo(clone);
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
            CharacterFields.Declare(fields);

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
                    "WantedLevel",
                    (a, b) => a.WantedLevel != b.WantedLevel,
                    (w, e) => w.WriteByte(e.WantedLevel),
                    (r, e) => e.WantedLevel = r.ReadByte());
        }
    }
}
