using System;
using System.Collections.Generic;

namespace Gtamp.Shared.Entities
{
    /// <summary>
    /// Maps a wire type id to its serializer. Built-in types are registered at
    /// construction; the Mod SDK's <c>RegisterEntity()</c> adds more at load time,
    /// before the first connection is accepted.
    /// </summary>
    public sealed class EntityRegistry
    {
        private readonly Dictionary<byte, INetEntitySerializer> _byId = new Dictionary<byte, INetEntitySerializer>();
        private readonly Dictionary<string, INetEntitySerializer> _byName =
            new Dictionary<string, INetEntitySerializer>(StringComparer.OrdinalIgnoreCase);

        private bool _locked;

        public static EntityRegistry CreateDefault()
        {
            var registry = new EntityRegistry();
            registry.Register(new PlayerEntitySerializer());
            return registry;
        }

        public IEnumerable<INetEntitySerializer> Serializers => _byId.Values;

        public void Register(INetEntitySerializer serializer)
        {
            if (serializer == null)
            {
                throw new ArgumentNullException(nameof(serializer));
            }

            if (_locked)
            {
                throw new InvalidOperationException(
                    "Entity types must be registered before the network layer starts; " +
                    "registering later would desynchronise clients that already negotiated the type table.");
            }

            if (_byId.ContainsKey(serializer.TypeId))
            {
                throw new InvalidOperationException(
                    $"Entity type id {serializer.TypeId} is already taken by '{_byId[serializer.TypeId].TypeName}'.");
            }

            if (_byName.ContainsKey(serializer.TypeName))
            {
                throw new InvalidOperationException($"Entity type name '{serializer.TypeName}' is already registered.");
            }

            _byId[serializer.TypeId] = serializer;
            _byName[serializer.TypeName] = serializer;
        }

        /// <summary>Called once the first client is accepted; makes the type table immutable.</summary>
        public void Lock() => _locked = true;

        public bool IsLocked => _locked;

        public INetEntitySerializer Get(byte typeId)
        {
            if (_byId.TryGetValue(typeId, out INetEntitySerializer? serializer))
            {
                return serializer;
            }

            throw new KeyNotFoundException(
                $"No serializer registered for entity type id {typeId}. " +
                "The peer is running a mod set this side does not have.");
        }

        public bool TryGet(byte typeId, out INetEntitySerializer serializer) => _byId.TryGetValue(typeId, out serializer!);

        public bool TryGetByName(string typeName, out INetEntitySerializer serializer) =>
            _byName.TryGetValue(typeName, out serializer!);

        public NetEntity Create(byte typeId, EntityId id) => Get(typeId).Create(id);

        /// <summary>
        /// Stable fingerprint of the registered type table. Client and server compare
        /// it during the handshake: a mismatch means the two sides would disagree on
        /// field layouts, which is reported as a mod incompatibility rather than
        /// surfacing later as a corrupt snapshot.
        /// </summary>
        public uint ComputeSchemaHash()
        {
            var ids = new List<byte>(_byId.Keys);
            ids.Sort();

            uint hash = 2166136261u;
            foreach (byte id in ids)
            {
                INetEntitySerializer serializer = _byId[id];
                hash = Fnv(hash, id.ToString());
                hash = Fnv(hash, serializer.TypeName);
                foreach (string field in serializer.FieldNames)
                {
                    hash = Fnv(hash, field);
                }
            }

            return hash;
        }

        private static uint Fnv(uint hash, string value)
        {
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            hash ^= (uint)'|';
            hash *= 16777619u;
            return hash;
        }
    }
}
