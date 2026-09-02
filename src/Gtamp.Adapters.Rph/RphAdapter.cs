using System;
using System.Collections.Generic;
using System.Text;
using Gtamp.Client.Mods;
using Gtamp.Client.Sdk;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Interop;
using Gtamp.Shared.Mods;

namespace Gtamp.Adapters.Rph
{
    /// <summary>
    /// RAGE Plugin Hook integration (master prompt section 19), core side.
    /// <para>
    /// It does not touch a single RPH type. RPH exposes its API to assemblies it
    /// loads itself, and its objects assume they are used from a <c>GameFiber</c>;
    /// reaching into them from the ScriptHookVDotNet script thread is unsafe even
    /// when the assembly happens to resolve. So this adapter talks to
    /// <c>Gtamp.RphBridge.dll</c> — a separate assembly RPH loads — across the
    /// in-process channel, and each side stays on its own scheduler.
    /// </para>
    /// <para>
    /// <b>Working today:</b> detection, handshake with the bridge, RPH and LSPDFR
    /// versions read from the process rather than from files on disk, and the list of
    /// loaded RPH plugins published into the mod manifest so the server and other
    /// players can see it.
    /// </para>
    /// <para>
    /// <b>Not working:</b> replicating the internal state of arbitrary RPH plugins.
    /// RPH gives a plugin no way to enumerate or drive another plugin's objects, so
    /// there is nothing to read generically — a plugin that wants its state
    /// replicated has to send it, which is what the <c>rph.event</c> route below is
    /// for. What the bridge does read is the plugin list and, through
    /// <c>LspdfrObserver</c>, LSPDFR's own public API; that goes to the LSPDFR
    /// adapter.
    /// </para>
    /// </summary>
    public sealed class RphAdapter : IModAdapter
    {
        /// <summary>How long to wait for the bridge before reporting it absent.</summary>
        public const double BridgeHandshakeTimeout = 10.0;

        private readonly List<string> _rphPlugins = new List<string>();

        private BridgeLink? _link;
        private LogBus? _log;
        private ModEnvironment? _environment;
        private IModSdk? _sdk;

        private double _openedAt;
        private bool _clockStarted;
        private bool _bridgeSeen;
        private bool _bridgeReportedMissing;
        private string _bridgeVersion = string.Empty;
        private string _rphVersion = string.Empty;
        private string _lspdfrVersion = string.Empty;

        public string Id => "rph";

        public string DisplayName => "RAGE Plugin Hook";

        /// <summary>
        /// Reads only the environment record, so this adapter can ship in a client with
        /// no RPH installed (see <see cref="IModAdapter"/>).
        /// </summary>
        public bool IsAvailable(ModEnvironment environment) => environment.RagePluginHook;

        public void Initialize(IModSdk sdk, ModEnvironment environment)
        {
            _log = sdk.Log;
            _environment = environment;
            _sdk = sdk;

            sdk.RegisterMod(new ModDescriptor
            {
                Id = "rph.adapter",
                Name = "GTAMP RAGE Plugin Hook adapter",
                Version = typeof(RphAdapter).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                Requirement = ModNetworkRequirement.Optional,
            });

            sdk.RegisterState("rph.plugin", "Identifier of the RPH plugin that owns this entity.");

            sdk.RegisterNetworkEvent("rph.event", (senderPlayerId, payload) =>
            {
                // A payload from another player's RPH plugin. Forwarded to the bridge
                // so the plugin that understands it can act on it.
                _link?.Send(InteropTopics.ModPayload, payload);
                _log?.Debug(LogCategory.Mod, $"rph.event from player {senderPlayerId}, {payload.Length} byte(s).");
            });

            _link = BridgeLink.Shared;
            _link.Subscribe(InteropTopics.Hello, payload => HandleHello(Encoding.UTF8.GetString(payload)));
            _link.Subscribe(InteropTopics.PluginList, payload => HandlePluginList(Encoding.UTF8.GetString(payload)));
            _link.Send(InteropTopics.Describe, Array.Empty<byte>());

            _log.Info(
                LogCategory.Mod,
                $"RPH adapter ready; waiting for Gtamp.RphBridge.dll to answer on the in-process channel.");
        }

        public void Update(double now)
        {
            if (_link == null)
            {
                return;
            }

            if (!_clockStarted)
            {
                // An explicit flag, not "_openedAt <= 0": the client clock legitimately
                // starts at zero, and treating that as "not started yet" would restart
                // the handshake timeout on every update and never report a missing bridge.
                _clockStarted = true;
                _openedAt = now;
            }

            _link.Pump();

            if (!_bridgeSeen && !_bridgeReportedMissing && now - _openedAt > BridgeHandshakeTimeout)
            {
                _bridgeReportedMissing = true;
                _log?.Warning(
                    LogCategory.Mod,
                    "RAGE Plugin Hook is installed but Gtamp.RphBridge.dll never answered. " +
                    "One of three things: the game was not started through RPH; Gtamp.RphBridge.dll and " +
                    "Gtamp.Shared.dll are not BOTH in '<GTA V>\\Plugins\\' (the copies in 'scripts\\' do not " +
                    "count); or the two are from different builds. RagePluginHook.log has a GTAMP line " +
                    "naming which. " +
                    "RPH state will not be replicated. See docs/RPH_INTEGRATION.md.");
            }
        }

        private void HandleHello(string payload)
        {
            foreach (string pair in payload.Split(';'))
            {
                int separator = pair.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = pair.Substring(0, separator);
                string value = pair.Substring(separator + 1);

                switch (key)
                {
                    case "bridge":
                        _bridgeVersion = value;
                        break;
                    case "rph":
                        _rphVersion = value;
                        break;
                    case "lspdfr":
                        _lspdfrVersion = value;
                        break;
                    case "state" when value == "stopped":
                        _bridgeSeen = false;
                        _log?.Warning(LogCategory.Mod, "The RPH bridge stopped; RPH state is no longer available.");
                        return;
                }
            }

            if (_bridgeSeen)
            {
                return;
            }

            _bridgeSeen = true;
            _log?.Success(
                LogCategory.Mod,
                $"RPH bridge {_bridgeVersion} connected. RPH {_rphVersion}, LSPDFR {_lspdfrVersion}.");

            _log?.Info(
                LogCategory.Mod,
                "RPH plugin state is only replicated for plugins that send it over 'rph.event'. " +
                "RPH exposes no way to read another plugin's state. See docs/RPH_INTEGRATION.md.");
        }

        private void HandlePluginList(string payload)
        {
            _rphPlugins.Clear();

            foreach (string entry in payload.Split(';'))
            {
                if (entry.Length == 0)
                {
                    continue;
                }

                _rphPlugins.Add(entry.Replace('|', ' '));

                string[] parts = entry.Split('|');
                string name = parts[0];
                string version = parts.Length > 1 ? parts[1] : "unknown";

                // Published into the manifest so the server and other players can see
                // exactly which RPH plugins this client is running.
                _environment?.AddDetectedMod(new ModDescriptor
                {
                    Id = "rph.plugin." + name.ToLowerInvariant().Replace(' ', '-'),
                    Name = name,
                    Version = version,
                    Requirement = ModNetworkRequirement.Optional,
                });
            }

            _log?.Info(LogCategory.Mod, $"RPH reports {_rphPlugins.Count} loaded plugin(s).");
        }

        public void Shutdown()
        {
            _link = null;
        }

        public string DescribeStatus()
        {
            if (_link == null)
            {
                return "not initialised";
            }

            if (!_bridgeSeen)
            {
                return _bridgeReportedMissing
                    ? "installed, but the RPH bridge never answered"
                    : "waiting for the RPH bridge";
            }

            return $"bridge {_bridgeVersion}, RPH {_rphVersion}, {_rphPlugins.Count} plugin(s); " +
                   "plugin state replicated only via 'rph.event'";
        }
    }
}
