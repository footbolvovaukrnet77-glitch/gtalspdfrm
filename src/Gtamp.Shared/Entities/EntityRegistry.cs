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

        /// <summary>
        /// Which mod registered each type id, for the ones a mod registered.
        /// <para>
        /// The framework&#39;s own five types are not in here, and their absence is the
        /// answer rather than a hole: an entity whose type nobody claims came from this
        /// framework. Master prompt section 44 asks the entity inspector to name the mod
        /// behind an entity, and without this the answer to "what put a type 0x42 in my
        /// world" was nothing at all — which is the least useful thing a diagnostic can
        /// say in a framework whose whole premise is third-party mods.
        /// </para>
        /// </summary>
        private readonly Dictionary<byte, string> _typeOwners = new Dictionary<byte, string>();

        private bool _locked;

        public static EntityRegistry CreateDefault()
        {
            var registry = new EntityRegistry();
            registry.Register(new PlayerEntitySerializer());
            registry.Register(new VehicleEntitySerializer());
            registry.Register(new PedEntitySerializer());
            registry.Register(new ObjectEntitySerializer());
            registry.Register(new ActivityEntitySerializer());
            return registry;
        }

        public IEnumerable<INetEntitySerializer> Serializers => _byId.Values;

        public void Register(INetEntitySerializer serializer) => Register(serializer, null);

        /// <summary>
        /// Registers an entity type, optionally recording the mod that owns it.
        /// <paramref name="mod"/> is null for the framework&#39;s own types.
        /// </summary>
        public void Register(INetEntitySerializer serializer, string? mod)
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
            if (!string.IsNullOrWhiteSpace(mod))
            {
                _typeOwners[serializer.TypeId] = mod!.Trim();
            }
        }

        /// <summary>
        /// The mod that registered a type id, or null when the framework itself did.
        /// Printed by the entity inspector and by the bug report, which is the whole
        /// reason it is recorded.
        /// </summary>
        public string? OwnerOf(byte typeId) =>
            _typeOwners.TryGetValue(typeId, out string? mod) ? mod : null;

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
