using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Gtamp.Shared.Integration;
using Gtamp.Shared.Interop;
using Rage;
using Rage.Attributes;


namespace Gtamp.RphBridge
{
    /// <summary>
    /// The RAGE Plugin Hook half of the bridge.
    /// <para>
    /// It exists because there is no supported way for a ScriptHookVDotNet script to
    /// reach RPH state: RPH exposes its API to assemblies it loads itself, and its
    /// objects assume they are touched from a <c>GameFiber</c>. So the multiplayer
    /// core does not try. This assembly is loaded by RPH, stays on RPH's scheduler,
    /// and passes bytes across the in-process channel.
    /// </para>
    /// <para>
    /// <b>It never blocks.</b> A blocking call between the two schedulers deadlocks
    /// the game, so every exchange is a queue push or a queue poll.
    /// </para>
    /// </summary>
    internal static class BridgeHost
    {
        /// <summary>How often the bridge polls the channel, in milliseconds.</summary>
        private const int PollIntervalMilliseconds = 50;

        /// <summary>How often the plugin list is re-scanned. Plugins are loaded rarely.</summary>
        private const int PluginScanIntervalMilliseconds = 5000;

        private static InProcessEndpoint? _channel;
        private static readonly LspdfrObserver Lspdfr = new LspdfrObserver();
        private static bool _running;
        private static uint _lastPluginScan;

        /// <summary>The real body, called by <see cref="EntryPoint.Main"/>.</summary>
        public static void Run()
        {
            try
            {
                _channel = InProcessChannel.OpenPluginSide();
                _running = true;

                Game.LogTrivial($"[GTAMP] RPH bridge started (channel v{InProcessChannel.Version}).");

                Lspdfr.Bind();
                if (Lspdfr.IsAvailable)
                {
                    Game.LogTrivial(
                        $"[GTAMP] LSPDFR {Lspdfr.Version} detected; bound {Lspdfr.BoundProbeCount} probe(s), " +
                        $"{Lspdfr.MissingProbes.Count} missing.");

                    foreach (string missing in Lspdfr.MissingProbes)
                    {
                        Game.LogTrivial($"[GTAMP] LSPDFR probe not found: {missing}");
                    }
                }

                Announce();

                while (_running)
                {
                    GameFiber.Wait(PollIntervalMilliseconds);
                    Pump();
                    PollLspdfr();
                    MaybeScanPlugins();
                }
            }
            catch (Exception exception)
            {
                // RPH kills the plugin on an unhandled exception. Logging first means
                // the reason is visible in RagePluginHook.log rather than lost.
                Game.LogTrivial("[GTAMP] RPH bridge failed: " + exception);
            }
        }

        /// <summary>The real teardown, called by <see cref="EntryPoint.Finally"/>.</summary>
        public static void Stop()
        {
            _running = false;

            if (_channel != null)
            {
                Send(InteropTopics.Hello, "state=stopped");
                _channel = null;
            }

            // Detach from LSPDFR before this assembly goes away. The observer
            // subscribes to LSPDFR's on-duty event when it can, and RPH reloads
            // plugins -- a handler left attached would keep calling into an assembly
            // that has stopped.
            Lspdfr.Dispose();

            Game.LogTrivial("[GTAMP] RPH bridge stopped.");
        }

        private static void Pump()
        {
            if (_channel == null)
            {
                return;
            }

            while (_channel.TryReceive(out string topic, out byte[] payload))
            {
                switch (topic)
                {
                    case InteropTopics.Describe:
                        Announce();
                        ScanPlugins();

                        // A client that has just connected needs the whole picture, not
                        // the changes since a poll it was not there for.
                        Send(InteropTopics.LspdfrEvent, Lspdfr.Describe());
                        break;

                    case InteropTopics.ModPayload:
                        // Nothing consumes mod payloads on this side yet. They are
                        // logged rather than dropped silently so a mod author can see
                        // their message arrived.
                        Game.LogTrivialDebug($"[GTAMP] Mod payload received ({payload.Length} bytes).");
                        break;
                }
            }
        }

        private static void PollLspdfr()
        {
            if (!Lspdfr.IsAvailable)
            {
                return;
            }

            string changes = Lspdfr.PollChanges();
            if (changes.Length > 0)
            {
                Send(InteropTopics.LspdfrEvent, changes);
            }
        }

        private static void Announce() =>
            Send(
                InteropTopics.Hello,
                $"state=running;bridge={BridgeVersion()};rph={RphVersion()};lspdfr={Lspdfr.Version}");

        private static void MaybeScanPlugins()
        {
            uint now = Game.GameTime;
            if (_lastPluginScan != 0 && now - _lastPluginScan < PluginScanIntervalMilliseconds)
            {
                return;
            }

            _lastPluginScan = now;
            ScanPlugins();
        }

        /// <summary>
        /// Reports the RPH plugins loaded in this process.
        /// <para>
        /// Read from the loaded assemblies rather than from RPH's own plugin registry,
        /// which is not public. An assembly carrying RPH's plugin attribute is an RPH
        /// plugin, and that attribute <em>is</em> public — so this is a supported
        /// surface rather than a reach into internals that would break on the next RPH
        /// release.
        /// </para>
        /// </summary>
        private static void ScanPlugins()
        {
            var names = new List<string>();

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    object[] attributes = assembly.GetCustomAttributes(typeof(PluginAttribute), false);
                    if (attributes.Length == 0)
                    {
                        continue;
                    }

                    var plugin = (PluginAttribute)attributes[0];
                    string version = assembly.GetName().Version?.ToString(3) ?? "unknown";
                    names.Add($"{plugin.Name}|{version}");
                }
                catch (Exception)
                {
                    // A dynamic or unloadable assembly. Skipping it is right; failing
                    // the scan because of one would lose the rest.
                }
            }

            Send(InteropTopics.PluginList, string.Join(";", names.ToArray()));
        }

        private static string BridgeVersion() =>
            typeof(EntryPoint).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

        private static string RphVersion()
        {
            Assembly? rph = FindAssembly("RAGEPluginHook");
            return rph?.GetName().Version?.ToString() ?? "unknown";
        }

        private static Assembly? FindAssembly(string simpleName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;
                }
            }

            return null;
        }

        private static void Send(string topic, string payload)
        {
            try
            {
                _channel?.Send(topic, Encoding.UTF8.GetBytes(payload));
            }
            catch (Exception exception)
            {
                Game.LogTrivial($"[GTAMP] Could not send '{topic}': {exception.Message}");
            }
        }
    }
}
