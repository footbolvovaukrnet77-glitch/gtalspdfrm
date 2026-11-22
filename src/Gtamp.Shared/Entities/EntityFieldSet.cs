using System;
using System.Collections.Generic;
using Gtamp.Shared.Net;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Declarative description of an entity's replicated fields.
    /// <para>
    /// Each field supplies a change test, a writer and a reader. Full state is a
    /// straight write of every field; a delta writes a 64-bit mask followed by only
    /// the fields that differ from the baseline. Adding a field to an entity — or a
    /// mod adding one through the SDK — is a one-line registration, not a rewrite of
    /// the networking layer, which is the requirement from ENTITY_SYSTEM.md.
    /// </para>
    /// </summary>
    public sealed class EntityFieldSet<T>
        where T : NetEntity
    {
        public const int MaxFields = 64;

        private readonly List<Field> _fields = new List<Field>();
        private bool _sealed;

        public int Count => _fields.Count;

        public IReadOnlyList<string> FieldNames
        {
            get
            {
                var names = new string[_fields.Count];
                for (int i = 0; i < _fields.Count; i++)
                {
                    names[i] = _fields[i].Name;
                }

                return names;
            }
        }

        public EntityFieldSet<T> Add(
            string name,
            Func<T, T, bool> changed,
            Action<NetWriter, T> write,
            Action<NetReader, T> read)
        {
            if (_sealed)
            {
                throw new InvalidOperationException("Fields cannot be added after the set has been sealed.");
            }

            if (_fields.Count >= MaxFields)
            {
                throw new InvalidOperationException(
                    $"An entity type may declare at most {MaxFields} replicated fields; " +
                    "group additional state into a component or CustomData.");
            }

            _fields.Add(new Field(name, changed, write, read));
            return this;
        }

        public EntityFieldSet<T> Seal()
        {
            _sealed = true;
            return this;
        }

        public void WriteFull(NetWriter writer, T entity)
        {
            for (int i = 0; i < _fields.Count; i++)
            {
                _fields[i].Write(writer, entity);
            }
        }

        public void ReadFull(NetReader reader, T entity)
        {
            for (int i = 0; i < _fields.Count; i++)
            {
                _fields[i].Read(reader, entity);
            }
        }

        /// <summary>Returns the number of fields actually written.</summary>
        public int WriteDelta(NetWriter writer, T baseline, T current)
        {
            ulong mask = 0;
            for (int i = 0; i < _fields.Count; i++)
            {
                if (_fields[i].Changed(baseline, current))
                {
                    mask |= 1UL << i;
                }
            }

            writer.WriteVarUInt64(mask);
            int written = 0;
            for (int i = 0; i < _fields.Count; i++)
            {
                if ((mask & (1UL << i)) != 0)
                {
                    _fields[i].Write(writer, current);
                    written++;
                }
            }

            return written;
        }

        public void ReadDelta(NetReader reader, T entity)
        {
            ulong mask = reader.ReadVarUInt64();
            for (int i = 0; i < _fields.Count; i++)
            {
                if ((mask & (1UL << i)) != 0)
                {
                    _fields[i].Read(reader, entity);
                }
            }
        }

        /// <summary>True when the two entities differ in at least one replicated field.</summary>
        public bool HasChanges(T baseline, T current)
        {
            for (int i = 0; i < _fields.Count; i++)
            {
                if (_fields[i].Changed(baseline, current))
                {
                    return true;
                }
            }

            return false;
        }

        private readonly struct Field
        {
            public Field(string name, Func<T, T, bool> changed, Action<NetWriter, T> write, Action<NetReader, T> read)
            {
                Name = name;
                Changed = changed;
                Write = write;
                Read = read;
            }

            public string Name { get; }

            public Func<T, T, bool> Changed { get; }

            public Action<NetWriter, T> Write { get; }

            public Action<NetReader, T> Read { get; }
        }
    }
}
