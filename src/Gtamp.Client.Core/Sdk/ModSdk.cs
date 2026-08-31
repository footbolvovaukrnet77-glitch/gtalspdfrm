using System;
using System.Collections.Generic;
using Gtamp.Client.Mods;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;

namespace Gtamp.Client.Sdk
{
    /// <summary>
    /// Handler for a mod-defined network event. The payload is whatever the sending
    /// side wrote; the SDK does not interpret it.
    /// </summary>
    public delegate void ModNetworkEventHandler(uint senderPlayerId, byte[] payload);

    /// <summary>
    /// What a mod or adapter gets to talk to the multiplayer core.
    /// <para>
    /// Master prompt section 21 lists fifteen <c>Register*</c> names. The ones below
    /// are implemented and working. The remainder — <c>RegisterRPC</c>,
    /// <c>RegisterMission</c>, <c>RegisterCustomWeapon</c> — are Phase 6 and Phase 9
    /// work; they throw <see cref="NotSupportedException"/> with a pointer to the
    /// roadmap rather than silently doing nothing, because a registration that looks
    /// like it worked and then never fires is far worse to debug than a hard failure
    /// at load time. See docs/MOD_SDK.md for the full mapping.
    /// </para>
    /// </summary>
    public interface IModSdk
    {
        LogBus Log { get; }

        /// <summary>Declares the mod's identity. Must be called before any other registration.</summary>
        void RegisterMod(ModDescriptor descriptor);

        /// <summary>Registers a new networked entity type. Returns the assigned wire type id.</summary>
        byte RegisterEntity(INetEntitySerializer serializer);

        /// <summary>Alias of <see cref="RegisterEntity"/> kept for readability at mod call sites.</summary>
        byte RegisterSerializer(INetEntitySerializer serializer);

        byte RegisterVehicle(INetEntitySerializer serializer);

        byte RegisterPed(INetEntitySerializer serializer);

        byte RegisterObject(INetEntitySerializer serializer);

        /// <summary>Registers a named event. Returns the assigned wire message id.</summary>
        byte RegisterNetworkEvent(string eventName, ModNetworkEventHandler handler);

        /// <summary>Sends a previously registered event to the server.</summary>
        void SendNetworkEvent(string eventName, byte[] payload, bool reliable = true);

        /// <summary>Declares a custom state key carried on an entity's CustomData.</summary>
        void RegisterState(string key, string description);

        /// <summary>Reads a custom state value from an entity, or null when unset.</summary>
        string? GetState(NetEntity entity, string key);

        /// <summary>Writes a custom state value. Replicated with the entity's next delta.</summary>
        void SetState(NetEntity entity, string key, string value);

        /// <summary>Declares a dimension id so two mods do not silently share one.</summary>
        uint RegisterDimension(string name);

        /// <summary>Declares a custom interior so its id survives a restart.</summary>
        void RegisterInterior(string name, int interiorId);

        void RegisterRPC(string name, Func<byte[], byte[]> handler);

        void RegisterMission(string missionId, object definition);

        void RegisterCustomWeapon(string weaponId, object definition);
    }

    /// <summary>Default SDK implementation, owned by the client and handed to every adapter.</summary>
    public sealed class ModSdk : IModSdk
    {
        private readonly EntityRegistry _registry;
        private readonly Dictionary<string, byte> _eventIds = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<byte, ModNetworkEventHandler> _eventHandlers = new Dictionary<byte, ModNetworkEventHandler>();
        private readonly Dictionary<string, string> _states = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _dimensions = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _interiors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ModDescriptor> _mods = new List<ModDescriptor>();

        private byte _nextEntityTypeId = (byte)EntityType.ModDefinedFirst;
        private byte _nextEventId = (byte)NetMessageType.ModMessageFirst;
        private uint _nextDimension = 1;

        public ModSdk(EntityRegistry registry, LogBus log, Func<string, byte[], bool, bool> sendEvent)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Log = log ?? throw new ArgumentNullException(nameof(log));
            SendEvent = sendEvent ?? throw new ArgumentNullException(nameof(sendEvent));
        }

        public LogBus Log { get; }

        /// <summary>Injected by the client: (eventName, payload, reliable) -&gt; sent.</summary>
        private Func<string, byte[], bool, bool> SendEvent { get; }

        public IReadOnlyList<ModDescriptor> RegisteredMods => _mods;

        public IReadOnlyDictionary<string, string> RegisteredStates => _states;

        public void RegisterMod(ModDescriptor descriptor)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            _mods.Add(descriptor);
            Log.Info(LogCategory.Mod, $"Registered mod '{descriptor.Id}' {descriptor.Version}.");
        }

        public byte RegisterEntity(INetEntitySerializer serializer)
        {
            if (serializer == null)
            {
                throw new ArgumentNullException(nameof(serializer));
            }

            if (_registry.IsLocked)
            {
                throw new InvalidOperationException(
                    $"Entity type '{serializer.TypeName}' was registered after the network layer started. " +
                    "All RegisterEntity calls must happen during adapter initialisation.");
            }

            if (serializer.TypeId != 0 && serializer.TypeId < (byte)EntityType.ModDefinedFirst)
            {
                throw new ArgumentException(
                    $"Type id {serializer.TypeId} is inside the built-in range; mod entities must use " +
                    $"{(byte)EntityType.ModDefinedFirst}..{(byte)EntityType.ModDefinedLast}.",
                    nameof(serializer));
            }

            if (_nextEntityTypeId > (byte)EntityType.ModDefinedLast)
            {
                throw new InvalidOperationException("All mod entity type ids are in use.");
            }

            _registry.Register(serializer);
            byte assigned = serializer.TypeId;
            if (assigned >= _nextEntityTypeId)
            {
                _nextEntityTypeId = (byte)(assigned + 1);
            }

            Log.Info(LogCategory.Mod, $"Registered entity type '{serializer.TypeName}' as id {assigned}.");
            return assigned;
        }

        public byte RegisterSerializer(INetEntitySerializer serializer) => RegisterEntity(serializer);

        public byte RegisterVehicle(INetEntitySerializer serializer) => RegisterEntity(serializer);

        public byte RegisterPed(INetEntitySerializer serializer) => RegisterEntity(serializer);

        public byte RegisterObject(INetEntitySerializer serializer) => RegisterEntity(serializer);

        /// <summary>Next unused mod entity type id, for a mod that wants one assigned rather than chosen.</summary>
        public byte NextEntityTypeId => _nextEntityTypeId;

        public byte RegisterNetworkEvent(string eventName, ModNetworkEventHandler handler)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                throw new ArgumentException("Event name must not be empty.", nameof(eventName));
            }

            if (_eventIds.TryGetValue(eventName, out byte existing))
            {
                _eventHandlers[existing] = handler ?? throw new ArgumentNullException(nameof(handler));
                return existing;
            }

            if (_nextEventId > (byte)NetMessageType.ModMessageLast)
            {
                throw new InvalidOperationException(
                    $"All {(byte)NetMessageType.ModMessageLast - (byte)NetMessageType.ModMessageFirst + 1} " +
                    "mod message ids are in use.");
            }

            byte id = _nextEventId++;
            _eventIds[eventName] = id;
            _eventHandlers[id] = handler ?? throw new ArgumentNullException(nameof(handler));
            Log.Info(LogCategory.Mod, $"Registered network event '{eventName}' as message 0x{id:X2}.");
            return id;
        }

        public void SendNetworkEvent(string eventName, byte[] payload, bool reliable = true)
        {
            if (!_eventIds.ContainsKey(eventName))
            {
                throw new InvalidOperationException($"Network event '{eventName}' was never registered.");
            }

            if (!SendEvent(eventName, payload ?? Array.Empty<byte>(), reliable))
            {
                Log.Warning(LogCategory.Mod, $"Event '{eventName}' was dropped: not connected to a server.");
            }
        }

        /// <summary>Routes an inbound mod message to its handler. Returns false when the id is unknown.</summary>
        public bool Dispatch(byte messageId, uint senderPlayerId, byte[] payload)
        {
            if (!_eventHandlers.TryGetValue(messageId, out ModNetworkEventHandler? handler))
            {
                return false;
            }

            try
            {
                handler(senderPlayerId, payload);
            }
            catch (Exception exception)
            {
                Log.Error(LogCategory.Mod, $"Handler for mod message 0x{messageId:X2} threw.", exception);
            }

            return true;
        }

        public bool TryGetEventId(string eventName, out byte id) => _eventIds.TryGetValue(eventName, out id);

        public void RegisterState(string key, string description)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("State key must not be empty.", nameof(key));
            }

            _states[key] = description ?? string.Empty;
        }

        public string? GetState(NetEntity entity, string key) =>
            entity.CustomData.TryGetValue(key, out string? value) ? value : null;

        public void SetState(NetEntity entity, string key, string value)
        {
            if (!_states.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"State key '{key}' was never registered. Call RegisterState first so /entity can describe it.");
            }

            entity.CustomData[key] = value;
        }

        public uint RegisterDimension(string name)
        {
            if (_dimensions.TryGetValue(name, out uint existing))
            {
                return existing;
            }

            uint id = _nextDimension++;
            _dimensions[name] = id;
            Log.Info(LogCategory.Mod, $"Registered dimension '{name}' as {id}.");
            return id;
        }

        public void RegisterInterior(string name, int interiorId)
        {
            _interiors[name] = interiorId;
            Log.Info(LogCategory.Mod, $"Registered interior '{name}' as {interiorId}.");
        }

        public void RegisterRPC(string name, Func<byte[], byte[]> handler) => throw NotYet("RegisterRPC", 6);

        public void RegisterMission(string missionId, object definition) => throw NotYet("RegisterMission", 6);

        public void RegisterCustomWeapon(string weaponId, object definition) => throw NotYet("RegisterCustomWeapon", 9);

        private static NotSupportedException NotYet(string member, int phase) => new NotSupportedException(
            $"{member} is not implemented yet; it lands in Phase {phase}. " +
            "See docs/ROADMAP.md. Use RegisterNetworkEvent for now.");
    }
}
