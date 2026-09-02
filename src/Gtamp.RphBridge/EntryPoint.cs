using System;
using System.IO;
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
    /// The two methods RAGE Plugin Hook calls, and nothing else.
    /// <para>
    /// <b>Why this class is empty.</b> It used to be <see cref="BridgeHost"/> itself,
    /// with the work inside a <c>try</c>. That <c>try</c> never ran. The JIT resolves a
    /// method's type references when it compiles the method, before the first
    /// instruction executes — so a missing <c>Gtamp.Shared.dll</c> threw
    /// <see cref="FileNotFoundException"/> on the way <em>into</em> <c>Main</c>, where no
    /// handler of its own can catch it. RAGE Plugin Hook treats an unhandled exception
    /// on a game fiber as fatal, so the result was a crash report and a dead game rather
    /// than a line in a log.
    /// </para>
    /// <para>
    /// Nothing here names a type from any other assembly, so this method always compiles
    /// and the <c>try</c> always runs. The work is one call away, in a class the JIT does
    /// not touch until that call is made — by which time the handler is in place.
    /// </para>
    /// </summary>
    public static class EntryPoint
    {
        /// <summary>RPH's entry point. Runs on a GameFiber; returning ends the plugin.</summary>
        public static void Main()
        {
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
        }

        /// <summary>
        /// Says what happened in RagePluginHook.log, and for the one failure a player can
        /// actually fix, says what to do about it.
        /// </summary>
        private static void Report(Exception exception)
        {
            if (exception is FileNotFoundException || exception is TypeLoadException
                || exception is BadImageFormatException)
            {
                Game.LogTrivial(
                    "[GTAMP] The RPH bridge could not start because an assembly it needs is missing "
                    + "from the Plugins folder. Gtamp.RphBridge.dll and Gtamp.Shared.dll must BOTH be "
                    + "in '<GTA V>\\Plugins\\' — RAGE Plugin Hook resolves a plugin's dependencies from "
                    + "its own folder and never from '<GTA V>\\scripts\\'. Copy the whole contents of "
                    + "dist/client/RagePluginHook-plugins/, not just the one file. Multiplayer itself "
                    + "is unaffected; only RPH and LSPDFR state stay local.");
            }

            Game.LogTrivial("[GTAMP] RPH bridge failed: " + exception);
        }
    }
}
