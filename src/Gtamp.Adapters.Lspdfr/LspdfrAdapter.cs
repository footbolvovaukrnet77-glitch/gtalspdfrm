using System;
using System.IO;
using System.Reflection;
using System.Text;
using Gtamp.Client.Mods;
using Gtamp.Client.Sdk;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Mods;

namespace Gtamp.Adapters.Lspdfr
{
    /// <summary>
    /// LSPDFR integration (master prompt section 18).
    /// <para>
    /// <b>Why reflection.</b> LSPDFR ships no redistributable SDK package.
    /// <c>LSPD First Response.dll</c> is installed with the mod and its licence does
    /// not permit redistributing it, so this adapter cannot reference it at compile
    /// time. Everything it reads is bound by name at runtime, and every lookup that
    /// misses is recorded and reported through /diagnostics instead of throwing.
    /// </para>
    /// <para>
    /// <b>What works today (Phase 1):</b> detection, version reporting, enumeration
    /// of the installed callout plugins, and the registration points (state keys and
    /// the network event) that Phase 8 will drive.
    /// </para>
    /// <para>
    /// <b>What does not.</b> Callouts, pursuits, suspects and police AI are not
    /// replicated. LSPDFR is an RPH plugin, so reaching its live state has the same
    /// cross-host problem described in the RPH adapter, plus a second one: LSPDFR's
    /// public API surface is not versioned, and binding to internals by name would
    /// break on every LSPDFR update. Phase 8 goes through the LSPDFR-side plugin
    /// path (an <c>API.Functions</c> consumer loaded by LSPDFR itself), not through
    /// reflection into its internals.
    /// </para>
    /// </summary>
    public sealed class LspdfrAdapter : IModAdapter
    {
        private ReflectionProbe? _probe;
        private LogBus? _log;
        private int _pluginCount;

        public string Id => "lspdfr";

        public string DisplayName => "LSPD First Response";

        public bool IsAvailable(ModEnvironment environment) => environment.Lspdfr;

        public void Initialize(IModSdk sdk, ModEnvironment environment)
        {
            _log = sdk.Log;
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

            // Registration points Phase 8 fills in. Declared now so mod authors can
            // already read and write these keys, and so /entity can describe them.
            sdk.RegisterState("lspdfr.callout", "Identifier of the callout an entity belongs to.");
            sdk.RegisterState("lspdfr.role", "Role in the callout: suspect, victim, witness, officer.");
            sdk.RegisterState("lspdfr.pursuit", "Identifier of the pursuit an entity is part of.");
            sdk.RegisterState("lspdfr.arrested", "Set when a suspect has been arrested.");

            sdk.RegisterNetworkEvent("lspdfr.event", (senderPlayerId, payload) =>
                _log?.Debug(LogCategory.Mod, $"lspdfr.event from player {senderPlayerId}, {payload.Length} byte(s)."));

            _log.Warning(
                LogCategory.Mod,
                "LSPDFR callout, pursuit and police-AI replication is not implemented (Phase 8). " +
                "See docs/LSPDFR_INTEGRATION.md.");
        }

        public void Update(double now)
        {
            // Nothing to poll until the LSPDFR-side plugin exists.
        }

        public void Shutdown()
        {
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

            builder.Append("; replication pending Phase 8");
            return builder.ToString();
        }
    }
}
