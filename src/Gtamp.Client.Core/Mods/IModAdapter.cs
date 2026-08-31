using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Gtamp.Client.Sdk;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Client.Mods
{
    /// <summary>
    /// Bridge between one third-party mod and the multiplayer core.
    /// <para>
    /// <b>Implementation rule:</b> the constructor and <see cref="IsAvailable"/> must
    /// not touch any type from the mod being adapted. The adapter assembly is loaded
    /// before the mod's presence is confirmed, and on .NET Framework a missing
    /// referenced assembly only fails when a method that uses it is JIT-compiled.
    /// Keeping those two members clean is what lets an RPH adapter ship in a client
    /// that has no RPH installed.
    /// </para>
    /// </summary>
    public interface IModAdapter
    {
        string Id { get; }

        string DisplayName { get; }

        /// <summary>Checked before anything else runs. Must inspect only <paramref name="environment"/>.</summary>
        bool IsAvailable(ModEnvironment environment);

        void Initialize(IModSdk sdk, ModEnvironment environment);

        /// <summary>Called once per client update while the adapter is active.</summary>
        void Update(double now);

        void Shutdown();

        /// <summary>One line for /diagnostics.</summary>
        string DescribeStatus();
    }

    /// <summary>
    /// Discovers and drives adapters.
    /// <para>
    /// Adapters live in <c>GTA V/Gtamp/Adapters/Gtamp.Adapters.*.dll</c> and are
    /// loaded reflectively, so the shipped client has no compile-time reference to
    /// RAGE Plugin Hook, LSPDFR or anything else optional.
    /// </para>
    /// </summary>
    public sealed class AdapterHost
    {
        private readonly LogBus _log;
        private readonly List<IModAdapter> _active = new List<IModAdapter>();
        private readonly List<string> _skipped = new List<string>();
        private readonly List<string> _failed = new List<string>();

        public AdapterHost(LogBus log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public IReadOnlyList<IModAdapter> Active => _active;

        public IReadOnlyList<string> Skipped => _skipped;

        public IReadOnlyList<string> Failed => _failed;

        public void LoadFrom(string adapterDirectory, IModSdk sdk, ModEnvironment environment)
        {
            if (!Directory.Exists(adapterDirectory))
            {
                _log.Info(LogCategory.Mod, $"No adapter directory at '{adapterDirectory}'; running with built-in support only.");
                return;
            }

            foreach (string path in Directory.GetFiles(adapterDirectory, "Gtamp.Adapters.*.dll", SearchOption.TopDirectoryOnly))
            {
                TryLoadAssembly(path, sdk, environment);
            }
        }

        /// <summary>Registers an adapter directly. Used by tests and by adapters compiled into the host.</summary>
        public void Add(IModAdapter adapter, IModSdk sdk, ModEnvironment environment)
        {
            if (!adapter.IsAvailable(environment))
            {
                _skipped.Add(adapter.Id);
                _log.Info(LogCategory.Mod, $"Adapter '{adapter.Id}' is inactive: {adapter.DisplayName} is not installed.");
                return;
            }

            try
            {
                adapter.Initialize(sdk, environment);
                _active.Add(adapter);
                _log.Success(LogCategory.Mod, $"Adapter '{adapter.Id}' initialised — {adapter.DescribeStatus()}");
            }
            catch (Exception exception)
            {
                _failed.Add(adapter.Id);
                _log.Error(LogCategory.Mod, $"Adapter '{adapter.Id}' failed to initialise; continuing without it.", exception);
            }
        }

        public void Update(double now)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                IModAdapter adapter = _active[i];
                try
                {
                    adapter.Update(now);
                }
                catch (Exception exception)
                {
                    // One misbehaving adapter must not stop the others or the core.
                    _log.Error(LogCategory.Mod, $"Adapter '{adapter.Id}' threw during update; disabling it.", exception);
                    _active.RemoveAt(i);
                    _failed.Add(adapter.Id);
                    SafeShutdown(adapter);
                }
            }
        }

        public void Shutdown()
        {
            foreach (IModAdapter adapter in _active)
            {
                SafeShutdown(adapter);
            }

            _active.Clear();
        }

        private void TryLoadAssembly(string path, IModSdk sdk, ModEnvironment environment)
        {
            try
            {
                Assembly assembly = Assembly.LoadFrom(path);
                foreach (Type type in assembly.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface || !typeof(IModAdapter).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (Activator.CreateInstance(type) is IModAdapter adapter)
                    {
                        Add(adapter, sdk, environment);
                    }
                }
            }
            catch (Exception exception)
            {
                _failed.Add(Path.GetFileName(path));
                _log.Error(
                    LogCategory.Mod,
                    $"Could not load adapter '{Path.GetFileName(path)}'; continuing without it.",
                    exception);
            }
        }

        private void SafeShutdown(IModAdapter adapter)
        {
            try
            {
                adapter.Shutdown();
            }
            catch (Exception exception)
            {
                _log.Warning(LogCategory.Mod, $"Adapter '{adapter.Id}' threw during shutdown: {exception.Message}");
            }
        }
    }
}
