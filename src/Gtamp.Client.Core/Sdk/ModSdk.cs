using System;
using System.Collections.Generic;
using Gtamp.Client.Missions;
using Gtamp.Client.Mods;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.Security;

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

        /// <summary>
        /// Registers an entity type and records which mod owns it, so the entity
        /// inspector and every bug report can name the mod behind an entity of that
        /// type. Without a name the type is still registered — it is simply reported as
        /// unattributed, which is honest and less useful.
        /// </summary>
        byte RegisterEntity(INetEntitySerializer serializer, string mod);

        /// <summary>Alias of <see cref="RegisterEntity"/> kept for readability at mod call sites.</summary>
        byte RegisterSerializer(INetEntitySerializer serializer);

        byte RegisterVehicle(INetEntitySerializer serializer);

        byte RegisterPed(INetEntitySerializer serializer);

        byte RegisterObject(INetEntitySerializer serializer);

        /// <summary>Registers a named event. Events are routed by name, not by id.</summary>
        void RegisterNetworkEvent(string eventName, ModNetworkEventHandler handler);

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

        /// <summary>Registers a procedure the server can call on this client.</summary>
        void RegisterRPC(string name, Func<byte[], byte[]> handler);

        /// <summary>Calls a procedure the server registered and receives the answer.</summary>
        void CallServerRpc(string name, byte[] payload, Action<RpcResult> callback, double timeoutSeconds = 5.0);

        /// <summary>
        /// Registers the local half of an activity: blips, markers, UI. The server
        /// decides what happens; this reacts to it.
        /// </summary>
        void RegisterMission(string definitionId, IActivityHandler handler);

        /// <summary>
        /// Registers a weapon this build does not know about, locally.
        /// <para>
        /// <b>What this does:</b> maps the weapon's joaat hash back to its name, so
        /// the console, the entity inspector and bug reports say
        /// <c>WEAPON_MYMOD_RAILGUN</c> instead of <c>0x3F2A91C4</c>. Pass a
        /// <see cref="WeaponProfile"/> as <paramref name="definition"/> to describe
        /// its range and damage, or null to register the name alone.
        /// </para>
        /// <para>
        /// <b>What this does not do, and cannot:</b> grant the weapon a damage or
        /// range envelope. Combat is arbitrated on the server, and a client that
        /// could declare its own weapon's ceiling could declare any ceiling it liked.
        /// The authoritative registration is <c>IServerModSdk.RegisterWeapon</c>, or
        /// a <c>customWeapons</c> entry in <c>server.json</c>. Registering here and
        /// not there leaves the weapon working under the server's permissive default
        /// envelope — see docs/MOD_SDK.md.
        /// </para>
        /// </summary>
        void RegisterCustomWeapon(string weaponId, object definition);
    }

    /// <summary>Default SDK implementation, owned by the client and handed to every adapter.</summary>
    public sealed class ModSdk : IModSdk
    {
        private readonly EntityRegistry _registry;
        private readonly Dictionary<string, ModNetworkEventHandler> _eventHandlers =
            new Dictionary<string, ModNetworkEventHandler>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _states = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _dimensions = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _interiors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ModDescriptor> _mods = new List<ModDescriptor>();
        private readonly Dictionary<uint, string> _customWeapons = new Dictionary<uint, string>();
        private readonly Dictionary<uint, WeaponProfile> _localWeaponProfiles = new Dictionary<uint, WeaponProfile>();

        private byte _nextEntityTypeId = (byte)EntityType.ModDefinedFirst;
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

        public byte RegisterEntity(INetEntitySerializer serializer) => RegisterEntityCore(serializer, null);

        public byte RegisterEntity(INetEntitySerializer serializer, string mod) =>
            RegisterEntityCore(serializer, mod);

        private byte RegisterEntityCore(INetEntitySerializer serializer, string? mod)
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

            _registry.Register(serializer, mod);
            byte assigned = serializer.TypeId;
            if (assigned >= _nextEntityTypeId)
            {
                _nextEntityTypeId = (byte)(assigned + 1);
            }

            Log.Info(
                LogCategory.Mod,
                string.IsNullOrWhiteSpace(mod)
                    ? $"Registered entity type '{serializer.TypeName}' as id {assigned}."
                    : $"Registered entity type '{serializer.TypeName}' as id {assigned} for mod '{mod}'.");
            return assigned;
        }

        public byte RegisterSerializer(INetEntitySerializer serializer) => RegisterEntity(serializer);

        public byte RegisterSerializer(INetEntitySerializer serializer, string mod) => RegisterEntity(serializer, mod);

        public byte RegisterVehicle(INetEntitySerializer serializer) => RegisterEntity(serializer);

        public byte RegisterVehicle(INetEntitySerializer serializer, string mod) => RegisterEntity(serializer, mod);

        public byte RegisterPed(INetEntitySerializer serializer) => RegisterEntity(serializer);

        public byte RegisterPed(INetEntitySerializer serializer, string mod) => RegisterEntity(serializer, mod);

        public byte RegisterObject(INetEntitySerializer serializer) => RegisterEntity(serializer);

        public byte RegisterObject(INetEntitySerializer serializer, string mod) => RegisterEntity(serializer, mod);

        /// <summary>Next unused mod entity type id, for a mod that wants one assigned rather than chosen.</summary>
        public byte NextEntityTypeId => _nextEntityTypeId;

        public void RegisterNetworkEvent(string eventName, ModNetworkEventHandler handler)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                throw new ArgumentException("Event name must not be empty.", nameof(eventName));
            }

            _eventHandlers[eventName] = handler ?? throw new ArgumentNullException(nameof(handler));
            Log.Info(LogCategory.Mod, $"Registered network event '{eventName}'.");
        }

        public void SendNetworkEvent(string eventName, byte[] payload, bool reliable = true)
        {
            if (!_eventHandlers.ContainsKey(eventName))
            {
                throw new InvalidOperationException($"Network event '{eventName}' was never registered.");
            }

            if (!SendEvent(eventName, payload ?? Array.Empty<byte>(), reliable))
            {
                Log.Warning(LogCategory.Mod, $"Event '{eventName}' was dropped: not connected to a server.");
            }
        }

        /// <summary>Routes an inbound mod event to its handler. Returns false when the name is unknown.</summary>
        public bool Dispatch(string eventName, uint senderPlayerId, byte[] payload)
        {
            if (!_eventHandlers.TryGetValue(eventName, out ModNetworkEventHandler? handler))
            {
                return false;
            }

            try
            {
                handler(senderPlayerId, payload);
            }
            catch (Exception exception)
            {
                Log.Error(LogCategory.Mod, $"Handler for mod event '{eventName}' threw.", exception);
            }

            return true;
        }

        public bool IsEventRegistered(string eventName) => _eventHandlers.ContainsKey(eventName);

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

        /// <summary>Set by the client so the SDK can reach the RPC layer.</summary>
        public RpcDispatcher<object?>? Rpc { get; set; }

        /// <summary>Set by the client: sends an RPC request and returns false when offline.</summary>
        public Func<ModRpcRequestMessage, bool>? SendRpcRequest { get; set; }

        /// <summary>Set by the client so activity handlers reach the watcher.</summary>
        public ActivityWatcher? Activities { get; set; }

        public void RegisterRPC(string name, Func<byte[], byte[]> handler)
        {
            if (Rpc == null)
            {
                throw new InvalidOperationException("The RPC layer is not available on this SDK instance.");
            }

            Rpc.RegisterHandler(name, (_, payload) => handler(payload));
            Log.Info(LogCategory.Mod, $"Registered RPC handler '{name}'.");
        }

        public void CallServerRpc(string name, byte[] payload, Action<RpcResult> callback, double timeoutSeconds = 5.0)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            if (Rpc == null || SendRpcRequest == null)
            {
                callback(RpcResult.Failed("the client is not connected"));
                return;
            }

            ModRpcRequestMessage request = Rpc.BeginCall(
                name, payload ?? Array.Empty<byte>(), callback, CurrentTime, timeoutSeconds);

            if (!SendRpcRequest(request))
            {
                // Fail immediately rather than leaving the caller waiting out a timeout
                // for a call that was never sent.
                Rpc.HandleResponse(new ModRpcResponseMessage
                {
                    CallId = request.CallId,
                    Success = false,
                    Error = "the client is not connected",
                });
            }
        }

        /// <summary>Client time, injected so the SDK does not need its own clock.</summary>
        public double CurrentTime { get; set; }

        public void RegisterMission(string definitionId, IActivityHandler handler)
        {
            if (Activities == null)
            {
                throw new InvalidOperationException("The activity system is not available on this SDK instance.");
            }

            Activities.RegisterHandler(definitionId, handler);
        }

        public void RegisterCustomWeapon(string weaponId, object definition)
        {
            if (string.IsNullOrWhiteSpace(weaponId))
            {
                throw new ArgumentException("A weapon id must not be empty.", nameof(weaponId));
            }

            string name = weaponId.Trim();
            uint hash = GameHash.Joaat(name);

            _customWeapons[hash] = name;

            if (definition is WeaponProfile profile)
            {
                _localWeaponProfiles[hash] = profile;
                Log.Info(
                    LogCategory.Mod,
                    $"Registered weapon '{name}' (0x{hash:X8}) locally: {profile.MaxDamagePerHit} damage " +
                    $"within {profile.MaxRange:0} m. The server arbitrates combat, so this describes the " +
                    "weapon here — it does not set the envelope hits are validated against.");
            }
            else
            {
                Log.Info(LogCategory.Mod, $"Registered the name of weapon '{name}' (0x{hash:X8}).");
            }
        }

        /// <summary>Names a weapon hash, falling back to the hash itself. Used by the console.</summary>
        public string DescribeWeapon(uint weaponHash)
        {
            if (weaponHash == 0)
            {
                return "none";
            }

            return _customWeapons.TryGetValue(weaponHash, out string? name)
                ? $"{name} (0x{weaponHash:X8})"
                : $"0x{weaponHash:X8}";
        }

        /// <summary>Weapon hashes a mod has named on this client.</summary>
        public IReadOnlyDictionary<uint, string> CustomWeapons => _customWeapons;

        /// <summary>
        /// Locally declared envelopes. Descriptive only — the server's table is the
        /// one damage is validated against.
        /// </summary>
        public IReadOnlyDictionary<uint, WeaponProfile> LocalWeaponProfiles => _localWeaponProfiles;
    }
}
