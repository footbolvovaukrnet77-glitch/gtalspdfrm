using System;
using System.Collections.Generic;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Wire representation of one entity type. The replication layer only ever
    /// talks to this interface, so a mod-supplied serializer is indistinguishable
    /// from a built-in one.
    /// </summary>
    public interface INetEntitySerializer
    {
        byte TypeId { get; }

        string TypeName { get; }

        IReadOnlyList<string> FieldNames { get; }

        NetEntity Create(EntityId id);

        void WriteFull(NetWriter writer, NetEntity entity);

        void ReadFull(NetReader reader, NetEntity entity);

        void WriteDelta(NetWriter writer, NetEntity baseline, NetEntity current);

        void ReadDelta(NetReader reader, NetEntity entity);

        bool HasChanges(NetEntity baseline, NetEntity current);
    }

    /// <summary>Typed base that wires an <see cref="EntityFieldSet{T}"/> up to the registry.</summary>
    public abstract class EntitySerializer<T> : INetEntitySerializer
        where T : NetEntity
    {
        private EntityFieldSet<T>? _fields;

        protected EntitySerializer(byte typeId, string typeName)
        {
            TypeId = typeId;
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        }

        public byte TypeId { get; }

        public string TypeName { get; }

        public IReadOnlyList<string> FieldNames => Fields.FieldNames;

        protected EntityFieldSet<T> Fields
        {
            get
            {
                if (_fields == null)
                {
                    var set = new EntityFieldSet<T>();
                    AddCommonFields(set);
                    DeclareFields(set);
                    _fields = set.Seal();
                }

                return _fields;
            }
        }

        public abstract NetEntity Create(EntityId id);

        protected abstract void DeclareFields(EntityFieldSet<T> fields);

        /// <summary>
        /// Fields every entity carries. Declared first so their bit indices are
        /// identical across all types, which keeps the entity inspector generic.
        /// </summary>
        protected static void AddCommonFields(EntityFieldSet<T> fields)
        {
            fields
                .Add(
                    "OwnerId",
                    (a, b) => a.OwnerId != b.OwnerId,
                    (w, e) => w.WriteVarUInt(e.OwnerId),
                    (r, e) => e.OwnerId = r.ReadVarUInt())
                .Add(
                    "Position",
                    (a, b) => !a.Position.Equals(b.Position),
                    (w, e) => w.WriteQuantizedPosition(e.Position),
                    (r, e) => e.Position = r.ReadQuantizedPosition())
                .Add(
                    "Velocity",
                    (a, b) => !a.Velocity.Equals(b.Velocity),
                    (w, e) => w.WriteQuantizedVelocity(e.Velocity),
                    (r, e) => e.Velocity = r.ReadQuantizedVelocity())
                .Add(
                    "Heading",
                    (a, b) => Math.Abs(a.Heading - b.Heading) > 0.0001f,
                    (w, e) => w.WriteAngleDegrees(e.Heading),
                    (r, e) => e.Heading = r.ReadAngleDegrees())
                .Add(
                    "Dimension",
                    (a, b) => a.Dimension != b.Dimension,
                    (w, e) => w.WriteVarUInt(e.Dimension),
                    (r, e) => e.Dimension = r.ReadVarUInt())
                .Add(
                    "InteriorId",
                    (a, b) => a.InteriorId != b.InteriorId,
                    (w, e) => w.WriteVarInt(e.InteriorId),
                    (r, e) => e.InteriorId = r.ReadVarInt())
                .Add(
                    "CustomData",
                    (a, b) => !CustomDataEquals(a, b),
                    (w, e) => WriteCustomData(w, e),
                    (r, e) => ReadCustomData(r, e));
        }

        public void WriteFull(NetWriter writer, NetEntity entity) => Fields.WriteFull(writer, Cast(entity));

        public void ReadFull(NetReader reader, NetEntity entity) => Fields.ReadFull(reader, Cast(entity));

        public void WriteDelta(NetWriter writer, NetEntity baseline, NetEntity current) =>
            Fields.WriteDelta(writer, Cast(baseline), Cast(current));

        public void ReadDelta(NetReader reader, NetEntity entity) => Fields.ReadDelta(reader, Cast(entity));

        public bool HasChanges(NetEntity baseline, NetEntity current) =>
            Fields.HasChanges(Cast(baseline), Cast(current));

        private static T Cast(NetEntity entity)
        {
            if (entity is T typed)
            {
                return typed;
            }

            throw new NetSerializationException(
                $"Entity {entity.Id} is a {entity.GetType().Name} but the serializer expects {typeof(T).Name}.");
        }

        private static bool CustomDataEquals(NetEntity a, NetEntity b)
        {
            if (a.CustomData.Count != b.CustomData.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> pair in a.CustomData)
            {
                if (!b.CustomData.TryGetValue(pair.Key, out string? other) || !string.Equals(pair.Value, other, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static void WriteCustomData(NetWriter writer, NetEntity entity)
        {
            writer.WriteVarUInt((uint)entity.CustomData.Count);
            foreach (KeyValuePair<string, string> pair in entity.CustomData)
            {
                writer.WriteString(pair.Key);
                writer.WriteString(pair.Value);
            }
        }

        private static void ReadCustomData(NetReader reader, NetEntity entity)
        {
            uint count = reader.ReadVarUInt();
            if (count > 256)
            {
                throw new NetSerializationException($"CustomData entry count {count} exceeds the limit of 256.");
            }

            entity.CustomData.Clear();
            for (uint i = 0; i < count; i++)
            {
                string key = reader.ReadString(128);
                string value = reader.ReadString(1024);
                entity.CustomData[key] = value;
            }
        }
    }
}
