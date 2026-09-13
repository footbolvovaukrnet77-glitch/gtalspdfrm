using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Rage;
using Rage.Attributes;

[assembly: Plugin(
    "GTAMP RPH Bridge",
    Description = "Connects RAGE Plugin Hook to the GTAMP multiplayer core running under ScriptHookVDotNet.",
    Author = "GTAMP",
    PrefersSingleInstance = true,
    ShouldTickInPauseMenu = false)]

namespace Gtamp.RphBridge
{
    /// <summary>
    /// The two methods RAGE Plugin Hook calls, and the assembly resolver that has to be
    /// in place before either of them can do anything.
    /// <para>
    /// <b>Why this class names no other assembly.</b> The JIT resolves a method's type
    /// references when it compiles the method, before the first instruction runs. So a
    /// missing dependency used to throw on the way <em>into</em> <c>Main</c>, past its own
    /// <c>try</c> — and RAGE Plugin Hook treats an unhandled exception on a game fiber as
    /// fatal, which closed the game. Everything real lives in <see cref="BridgeHost"/>,
    /// one call away, so the handler is always in place by the time anything can fail.
    /// </para>
    /// <para>
    /// <b>Why the resolver exists.</b> RPH loads each plugin into its own
    /// <see cref="AppDomain"/>, and that domain does not probe the plugins folder for a
    /// plugin's dependencies: <c>Gtamp.Shared.dll</c> sitting in <c>Plugins\</c> beside
    /// the bridge was not found, and the bridge failed with
    /// <see cref="FileNotFoundException"/> on a machine where the file was demonstrably
    /// there — copied five minutes before the run that could not load it. Telling players
    /// to copy the file into a second place would be asking them to work around this
    /// project's own packaging. The bridge finds its own dependencies instead, beside
    /// itself, which is where they are.
    /// </para>
    /// <para>
    /// None of this is reachable from the test suite: it runs before <c>Gtamp.Shared</c>
    /// can be loaded at all, in an AppDomain only RPH creates. It was verified from the
    /// player's own <c>RagePluginHook.log</c> and is stated here rather than claimed to
    /// be covered.
    /// </para>
    /// </summary>
    public static class EntryPoint
    {
        private static bool _resolverInstalled;

        /// <summary>RPH's entry point. Runs on a GameFiber; returning ends the plugin.</summary>
        public static void Main()
        {
            InstallAssemblyResolver();

            try
            {
                BridgeHost.Run();
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }

        /// <summary>RPH's exit point, named by the plugin attribute's default convention.</summary>
        public static void Finally()
        {
            try
            {
                BridgeHost.Stop();
            }
            catch (Exception exception)
            {
                Report(exception);
            }
            finally
            {
                if (_resolverInstalled)
                {
                    _resolverInstalled = false;
                    AppDomain.CurrentDomain.AssemblyResolve -= ResolveBesideThisPlugin;
                }
            }
        }

        private static void InstallAssemblyResolver()
        {
            if (_resolverInstalled)
            {
                return;
            }

            _resolverInstalled = true;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveBesideThisPlugin;
        }

        /// <summary>
        /// Loads a dependency from the folder this plugin was loaded from. Returns null
        /// for anything it cannot find, which leaves the runtime to fail exactly as it
        /// would have — this resolves more, never less.
        /// </summary>
        private static Assembly? ResolveBesideThisPlugin(object sender, ResolveEventArgs args)
        {
            try
            {
                // AssemblyName parses the display name without loading anything, so this
                // cannot re-enter the handler it is running inside.
                string simpleName = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(simpleName))
                {
                    return null;
                }

                string[] folders = SearchFolders();
                if (folders.Length == 0)
                {
                    Game.LogTrivial($"[GTAMP] Could not resolve {simpleName}: no folder to look in.");
                    return null;
                }

                foreach (string folder in folders)
                {
                    string candidate = Path.Combine(folder, simpleName + ".dll");
                    if (File.Exists(candidate))
                    {
                        Game.LogTrivial($"[GTAMP] Resolved {simpleName} from {folder}.");
                        return Assembly.LoadFrom(candidate);
                    }
                }

                // Saying nothing here is what made the first attempt at this useless:
                // the bridge failed exactly as before and the log could not tell whether
                // the handler had never run, or had run and looked in the wrong place.
                Game.LogTrivial(
                    $"[GTAMP] Could not resolve {simpleName}. Looked in: {string.Join(" ; ", folders)}");
                return null;
            }
            catch (Exception)
            {
                // A resolver that throws turns a missing dependency into something worse.
                return null;
            }
        }

        /// <summary>
        /// Every folder worth looking in, most specific first, without duplicates.
        /// <para>
        /// One folder was not enough. <c>Location</c> is where the assembly was loaded
        /// from — but a host that shadow-copies its plugins, or loads them from bytes,
        /// makes that a temporary directory or nothing at all, and the dependency is
        /// still beside the original. So the plugin's own folder, the AppDomain's base,
        /// and a <c>Plugins</c> folder under that base are all tried, and whichever one
        /// answers is named in the log.
        /// </para>
        /// </summary>
        private static string[] SearchFolders()
        {
            var folders = new List<string>(4);
            Assembly self = typeof(EntryPoint).Assembly;

            Add(folders, SafeDirectory(self.Location));

            try
            {
                string codeBase = self.CodeBase ?? string.Empty;
                if (codeBase.Length > 0)
                {
                    Add(folders, SafeDirectory(new Uri(codeBase).LocalPath));
                }
            }
            catch (Exception)
            {
                // A CodeBase that is not a file URI tells us nothing; the others remain.
            }

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory ?? string.Empty;
            if (baseDirectory.Length > 0)
            {
                Add(folders, baseDirectory);
                Add(folders, Path.Combine(baseDirectory, "Plugins"));
            }

            return folders.ToArray();
        }

        private static string? SafeDirectory(string? path)
        {
            try
            {
                return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Add(List<string> folders, string? folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            for (int i = 0; i < folders.Count; i++)
            {
                if (string.Equals(folders[i], folder, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            folders.Add(folder!);
        }

        /// <summary>
        /// Says what happened in RagePluginHook.log, and for the failure a player can act
        /// on, says exactly what to check.
        /// </summary>
        private static void Report(Exception exception)
        {
            if (exception is FileNotFoundException || exception is TypeLoadException
                || exception is BadImageFormatException)
            {
                Game.LogTrivial(
                    "[GTAMP] The RPH bridge could not start because an assembly it needs is missing. "
                    + "Gtamp.RphBridge.dll and Gtamp.Shared.dll must BOTH be in '<GTA V>\\Plugins\\' — "
                    + "copy the whole contents of dist/client/RagePluginHook-plugins/, not just the one "
                    + "file. If both files are there and you are still reading this, the copy is from a "
                    + "different build than the bridge: rebuild and copy both again. Multiplayer itself "
                    + "is unaffected; only RPH and LSPDFR state stay local.");
            }

            Game.LogTrivial("[GTAMP] RPH bridge failed: " + exception);
        }
    }
}
