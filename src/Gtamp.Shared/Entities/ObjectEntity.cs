using System;
using Gtamp.Shared.Core;

namespace Gtamp.Shared.Entities
{
    [Flags]
    public enum ObjectFlags : uint
    {
        None = 0,

        /// <summary>Physics is disabled; the object stays exactly where it is put.</summary>
        Frozen = 1 << 0,

        HasCollision = 1 << 1,
        Visible = 1 << 2,

        /// <summary>Physics-driven rather than placed. Dynamic objects need an owner.</summary>
        Dynamic = 1 << 3,

        Broken = 1 << 4,

        /// <summary>Players can interact with it; a mod decides what that means.</summary>
        Interactive = 1 << 5,
    }

    /// <summary>
    /// A networked prop (master prompt section 14): a dropped weapon, a barrier, a
    /// piece of evidence, anything a mod spawns and wants everyone to see.
    /// </summary>
    public sealed class ObjectEntity : NetEntity
    {
        public ObjectEntity(EntityId id)
            : base(id, EntityType.Object)
        {
            Flags = ObjectFlags.HasCollision | ObjectFlags.Visible;
        }

        public uint ModelHash { get; set; }

        public float Pitch { get; set; }

        public float Roll { get; set; }

        public ObjectFlags Flags { get; set; }

        public int Health { get; set; } = 1000;

        /// <summary>Entity this object is attached to, or none.</summary>
        public EntityId AttachedToId { get; set; }

        /// <summary>Offset from the attachment parent, in its local space.</summary>
        public NetVector3 AttachOffset { get; set; }

        /// <summary>Bone index on the parent, -1 for the parent's origin.</summary>
        public short AttachBone { get; set; } = -1;

        public bool IsAttached => AttachedToId.IsValid;

        public bool HasFlag(ObjectFlags flag) => (Flags & flag) == flag;

        public void SetFlag(ObjectFlags flag, bool value)
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
            var clone = new ObjectEntity(Id)
            {
                ModelHash = ModelHash,
                Pitch = Pitch,
                Roll = Roll,
                Flags = Flags,
                Health = Health,
                AttachedToId = AttachedToId,
                AttachOffset = AttachOffset,
                AttachBone = AttachBone,
            };

            CopyBaseTo(clone);
            return clone;
        }
    }

    public sealed class ObjectEntitySerializer : EntitySerializer<ObjectEntity>
    {
        public ObjectEntitySerializer()
            : base((byte)EntityType.Object, "object")
        {
        }

        public override NetEntity Create(EntityId id) => new ObjectEntity(id);

        protected override void DeclareFields(EntityFieldSet<ObjectEntity> fields)
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
                    "Flags",
                    (a, b) => a.Flags != b.Flags,
                    (w, e) => w.WriteVarUInt((uint)e.Flags),
                    (r, e) => e.Flags = (ObjectFlags)r.ReadVarUInt())
                .Add(
                    "Health",
                    (a, b) => a.Health != b.Health,
                    (w, e) => w.WriteVarInt(e.Health),
                    (r, e) => e.Health = r.ReadVarInt())
                .Add(
                    "Attachment",
                    (a, b) => a.AttachedToId != b.AttachedToId
                              || !a.AttachOffset.Equals(b.AttachOffset)
                              || a.AttachBone != b.AttachBone,
                    (w, e) =>
                    {
                        w.WriteVarUInt(e.AttachedToId.Value);
                        w.WriteQuantizedPosition(e.AttachOffset);
                        w.WriteInt16(e.AttachBone);
                    },
                    (r, e) =>
                    {
                        e.AttachedToId = new EntityId(r.ReadVarUInt());
                        e.AttachOffset = r.ReadQuantizedPosition();
                        e.AttachBone = r.ReadInt16();
                    });
        }
    }
}
