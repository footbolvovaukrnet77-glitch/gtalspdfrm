using System;
using System.Reflection;
using System.Text;
using Gtamp.Client.Mods;
using Gtamp.Client.Sdk;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Mods;

namespace Gtamp.Adapters.Rph
{
    /// <summary>
    /// RAGE Plugin Hook integration (master prompt section 19).
    /// <para>
    /// <b>What works today (Phase 1):</b> detection, version reporting, publication
    /// of RPH's presence into the mod manifest so the server and other clients can
    /// see it, and the registration point mod authors bind to.
    /// </para>
    /// <para>
    /// <b>What does not, and why.</b> RPH and ScriptHookVDotNet are two separate
    /// hosts loading .NET code into the same GTA V process. There is no supported
    /// cross-host call: an SHVDN script cannot enumerate RPH plugins or subscribe to
    /// RPH events through a public API, because RPH exposes its API to assemblies it
    /// loads itself, on its own <c>GameFiber</c> scheduler. Touching Rage types from
    /// the SHVDN script thread is unsafe even when the assembly resolves.
    /// </para>
    /// <para>
    /// The chosen route for Phase 7 is a second, RPH-loaded plugin assembly that
    /// talks to this one over an in-process shared channel, so each side stays on
    /// its own scheduler. That plugin is not written yet. Until it is, this adapter
    /// reports honestly rather than pretending RPH state is synchronised — a
    /// deliberate choice over a silent no-op that would look like it worked.
    /// </para>
    /// </summary>
    public sealed class RphAdapter : IModAdapter
    {
        private ReflectionProbe? _probe;
        private LogBus? _log;

        public string Id => "rph";

        public string DisplayName => "RAGE Plugin Hook";

        /// <summary>
        /// Touches nothing but the environment record, so this adapter can ship in a
        /// client that has no RPH installed (see <see cref="IModAdapter"/>).
        /// </summary>
        public bool IsAvailable(ModEnvironment environment) => environment.RagePluginHook;

        public void Initialize(IModSdk sdk, ModEnvironment environment)
        {
            _log = sdk.Log;

            sdk.RegisterMod(new ModDescriptor
            {
                Id = "rph.adapter",
                Name = "GTAMP RAGE Plugin Hook adapter",
                Version = typeof(RphAdapter).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                Requirement = ModNetworkRequirement.Optional,
            });

            // RPH's own assembly is named "RAGEPluginHook" when it is the host.
            Assembly? rph = ReflectionProbe.FindLoadedAssembly("RAGEPluginHook");
            if (rph != null)
            {
                _probe = new ReflectionProbe(rph);
                _log.Success(LogCategory.Mod, $"RAGE Plugin Hook {_probe.Version} is hosting this process.");
            }
            else
            {
                _log.Info(
                    LogCategory.Mod,
                    $"RAGE Plugin Hook {environment.RagePluginHookVersion} is installed but is not hosting this " +
                    "process; the game was started without it. Detection only.");
            }

            sdk.RegisterState("rph.plugin", "Identifier of the RPH plugin that owns this entity.");

            // The registration point mod authors bind to. It carries opaque payloads
            // so an RPH plugin can already move its own state between clients.
            sdk.RegisterNetworkEvent("rph.event", (senderPlayerId, payload) =>
                _log?.Debug(LogCategory.Mod, $"rph.event from player {senderPlayerId}, {payload.Length} byte(s)."));

            _log.Warning(
                LogCategory.Mod,
                "RPH state replication is not implemented (Phase 7). See docs/RPH_INTEGRATION.md for the reason " +
                "and the planned cross-host channel.");
        }

        public void Update(double now)
        {
            // Nothing to poll until the cross-host channel exists.
        }

        public void Shutdown()
        {
        }

        public string DescribeStatus()
        {
            var builder = new StringBuilder();
            builder.Append(_probe != null ? $"hosting, version {_probe.Version}" : "installed, not hosting");
            if (_probe != null && _probe.Misses.Count > 0)
            {
                builder.Append($"; {_probe.Misses.Count} reflection miss(es)");
            }

            builder.Append("; state replication pending Phase 7");
            return builder.ToString();
        }
    }
}
