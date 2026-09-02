using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Gtamp.Client.Mods;
using Gtamp.Client.Sdk;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Interop;
using Gtamp.Shared.Mods;

namespace Gtamp.Adapters.Lspdfr
{
    /// <summary>
    /// LSPDFR integration (master prompt section 18).
    /// <para>
    /// <b>Why nothing here references LSPDFR.</b> LSPDFR ships no redistributable
    /// SDK. <c>LSPD First Response.dll</c> is installed with the mod and its licence
    /// does not permit redistributing it, so no assembly in this repository can
    /// reference it at compile time. On top of that LSPDFR is an RPH plugin, which
    /// puts it on RPH's <c>GameFiber</c> scheduler rather than the ScriptHookVDotNet
    /// script thread this adapter runs on. Both problems are solved in the same
    /// place: <c>Gtamp.RphBridge.dll</c>, loaded by RPH, binds to
    /// <c>LSPD_First_Response.Mod.API.Functions</c> by reflection, polls it on its
    /// own fiber, and publishes changes over the in-process channel. This adapter
    /// only ever reads text off that channel.
    /// </para>
    /// <para>
    /// <b>What works.</b> Detection and version reporting; the live LSPDFR state of
    /// the local player (on duty, callout running and its name, current traffic stop,
    /// active pursuit, player state) received from the bridge; that state broadcast
    /// to the other players on the server and their states received back, so a server
    /// full of LSPDFR players can see who is on duty and who is in a pursuit.
    /// </para>
    /// <para>
    /// <b>What does not, and why.</b> Callout scripts, suspect AI and pursuit
    /// behaviour are not replicated, and cannot be by this route. LSPDFR's public
    /// <c>API.Functions</c> surface exposes <i>whether</i> a callout is running and
    /// which one, not the decisions inside it; there is no supported way to drive
    /// another player's LSPDFR into the same callout state. What crosses the wire is
    /// therefore the observable facts, not the simulation. The peds and vehicles an
    /// LSPDFR callout spawns still replicate — as peds and vehicles, through the
    /// ordinary entity system, owned by the client that spawned them — so players do
    /// see each other's callout traffic; they just do not share callout logic. See
    /// docs/LSPDFR_INTEGRATION.md.
    /// </para>
    /// </summary>
    public sealed class LspdfrAdapter : IModAdapter
    {
        /// <summary>The network event both the local and remote halves use.</summary>
        public const string StateEvent = "lspdfr.event";

        private readonly Dictionary<string, string> _localState =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<uint, Dictionary<string, string>> _remoteState =
            new Dictionary<uint, Dictionary<string, string>>();

        private ReflectionProbe? _probe;
        private LogBus? _log;
        private IModSdk? _sdk;
        private BridgeLink? _link;
        private int _pluginCount;
        private int _updatesFromBridge;
        private int _updatesFromPeers;
        private bool _bridgeSeen;

        public string Id => "lspdfr";

        public string DisplayName => "LSPD First Response";

        public bool IsAvailable(ModEnvironment environment) => environment.Lspdfr;

        public void Initialize(IModSdk sdk, ModEnvironment environment)
        {
            _log = sdk.Log;
            _sdk = sdk;
            _pluginCount = environment.LspdfrPlugins.Count;

            sdk.RegisterMod(new ModDescriptor
            {
                Id = "lspdfr.adapter",
                Name = "GTAMP LSPDFR adapter",
                Version = typeof(LspdfrAdapter).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                Requirement = ModNetworkRequirement.Optional,
            });

            Assembly? lspdfr = ReflectionProbe.FindLoadedAssembly("LSPD First Response");
            if (lspdfr != null)
            {
                _probe = new ReflectionProbe(lspdfr);
                _log.Success(LogCategory.Mod, $"LSPDFR {_probe.Version} is loaded in this process.");
            }
            else
            {
                _log.Info(
                    LogCategory.Mod,
                    $"LSPDFR {environment.LspdfrVersion} is installed but not loaded in this process " +
                    "(the game was not started through RAGE Plugin Hook). Detection only.");
            }

            foreach (string plugin in environment.LspdfrPlugins)
            {
                _log.Info(LogCategory.Mod, "LSPDFR plugin detected: " + Path.GetFileName(plugin));
            }

            // Custom state keys, carried on an entity's CustomData. A callout script
            // that wants to mark the ped it spawned as its suspect writes these and
            // they replicate with the entity like any other field.
            sdk.RegisterState("lspdfr.callout", "Identifier of the callout an entity belongs to.");
            sdk.RegisterState("lspdfr.role", "Role in the callout: suspect, victim, witness, officer.");
            sdk.RegisterState("lspdfr.pursuit", "Identifier of the pursuit an entity is part of.");
            sdk.RegisterState("lspdfr.arrested", "Set when a suspect has been arrested.");

            sdk.RegisterNetworkEvent(StateEvent, HandlePeerState);

            _link = BridgeLink.Shared;
            _link.Subscribe(InteropTopics.LspdfrEvent, HandleBridgeState);
            _link.Subscribe(InteropTopics.Hello, OnBridgeHello);

            // Asking again is harmless — the bridge answers Describe with everything
            // it can see — and it covers the case where the RPH adapter is not loaded
            // and so nobody else has asked.
            _link.Send(InteropTopics.Describe, Array.Empty<byte>());
        }

        public void Update(double now)
        {
            _link?.Pump();
        }

        public void Shutdown()
        {
            if (_link != null)
            {
                _link.Unsubscribe(InteropTopics.LspdfrEvent, HandleBridgeState);
                _link.Unsubscribe(InteropTopics.Hello, OnBridgeHello);
                _link = null;
            }

            _localState.Clear();
            _remoteState.Clear();
        }

        /// <summary>The local player's LSPDFR state, as last reported by the bridge.</summary>
        public IReadOnlyDictionary<string, string> LocalState => _localState;

        /// <summary>Every other player's LSPDFR state, keyed by player id.</summary>
        public IReadOnlyDictionary<uint, Dictionary<string, string>> RemoteState => _remoteState;

        /// <summary>
        /// A change the bridge observed on this machine. The payload is
        /// <c>key=value;key=value</c> holding only what changed, so an unchanged
        /// pursuit does not cost a packet every poll.
        /// </summary>
        private void HandleBridgeState(byte[] payload)
        {
            string text = Encoding.UTF8.GetString(payload);
            if (!Merge(_localState, text, out string changed))
            {
                return;
            }

            _updatesFromBridge++;
            _bridgeSeen = true;
            _log?.Debug(LogCategory.Mod, "LSPDFR state changed: " + changed);

            // Forwarded verbatim rather than re-encoded: the server relays the bytes
            // without parsing them (ServerConfig.RelayedModEvents), and the receiving
            // adapter runs the same Merge over the same text.
            try
            {
                _sdk?.SendNetworkEvent(StateEvent, Encoding.UTF8.GetBytes(changed));
            }
            catch (InvalidOperationException)
            {
                // Not connected. The state is still tracked locally and the next
                // change after connecting carries it.
            }
        }

        private void OnBridgeHello(byte[] payload)
        {
            string text = Encoding.UTF8.GetString(payload);
            if (text.IndexOf("state=stopped", StringComparison.Ordinal) >= 0)
            {
                _bridgeSeen = false;
                _localState.Clear();
                _log?.Warning(
                    LogCategory.Mod,
                    "The RPH bridge stopped; the local LSPDFR state is no longer being observed.");
            }
        }

        private void HandlePeerState(uint senderPlayerId, byte[] payload)
        {
            if (!_remoteState.TryGetValue(senderPlayerId, out Dictionary<string, string> state))
            {
                state = new Dictionary<string, string>(StringComparer.Ordinal);
                _remoteState[senderPlayerId] = state;
            }

            if (!Merge(state, Encoding.UTF8.GetString(payload), out string changed))
            {
                return;
            }

            _updatesFromPeers++;
            _log?.Debug(LogCategory.Mod, $"LSPDFR state of player {senderPlayerId}: {changed}");
        }

        /// <summary>
        /// Applies a <c>key=value;key=value</c> payload. Returns false when nothing
        /// actually differed, so an echoed poll does not turn into a packet or a log
        /// line. <paramref name="changed"/> is the subset that did differ, in the same
        /// format, ready to forward.
        /// </summary>
        internal static bool Merge(Dictionary<string, string> into, string payload, out string changed)
        {
            changed = string.Empty;
            if (string.IsNullOrEmpty(payload))
            {
                return false;
            }

            var builder = new StringBuilder();

            foreach (string pair in payload.Split(';'))
            {
                int separator = pair.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = pair.Substring(0, separator);
                string value = pair.Substring(separator + 1);

                if (into.TryGetValue(key, out string existing) && existing == value)
                {
                    continue;
                }

                into[key] = value;

                if (builder.Length > 0)
                {
                    builder.Append(';');
                }

                builder.Append(key).Append('=').Append(value);
            }

            changed = builder.ToString();
            return changed.Length > 0;
        }

        public string DescribeStatus()
        {
            var builder = new StringBuilder();
            builder.Append(_probe != null ? $"loaded, version {_probe.Version}" : "installed, not loaded");
            builder.Append($"; {_pluginCount} callout plugin(s) detected");
            if (_probe != null && _probe.Misses.Count > 0)
            {
                builder.Append($"; {_probe.Misses.Count} reflection miss(es)");
            }

            builder.Append(_bridgeSeen ? "; bridge reporting" : "; no bridge state yet");
            builder.Append($"; {_localState.Count} local key(s), {_remoteState.Count} peer(s)");
            builder.Append($"; {_updatesFromBridge} local update(s), {_updatesFromPeers} peer update(s)");
            builder.Append("; callout logic not replicated by design");
            return builder.ToString();
        }
    }
}
