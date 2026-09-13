using System;

namespace Gtamp.Client.Mods
{
    /// <summary>
    /// Works out where GTA V actually lives.
    /// <para>
    /// This existed as <c>AppDomain.CurrentDomain.BaseDirectory</c> for the life of the
    /// project, and the first real game said otherwise: under ScriptHookVDotNet the app
    /// domain is rooted at the <c>scripts</c> folder, not at the game. Everything hung
    /// off that one value — the configuration file, the log directory, the adapter
    /// directory, and the scan that decides which mods are installed — so the client
    /// wrote its files into <c>scripts\Gtamp\</c>, looked for adapters somewhere the
    /// install guide never puts them, and reported <c>ScriptHookV=no, SHVDN=no</c>
    /// while running inside ScriptHookVDotNet.
    /// </para>
    /// <para>
    /// The executable's own folder is the answer, because it does not depend on where
    /// the host chose to root anything. The rest is a fallback for a host that cannot
    /// be asked.
    /// </para>
    /// </summary>
    public static class GameDirectory
    {
        /// <summary>The folder a script host roots its app domain in, when it is not the game's.</summary>
        public const string ScriptsFolderName = "scripts";

        private static readonly char[] Separators = { '\\', '/' };

        /// <summary>
        /// Resolves the game directory from the running executable, falling back to the
        /// app domain base — climbing out of <c>scripts</c> when that is where it points.
        /// </summary>
        /// <param name="baseDirectory">Usually <c>AppDomain.CurrentDomain.BaseDirectory</c>.</param>
        /// <param name="processExecutablePath">Full path of the running executable, or null when it cannot be read.</param>
        /// <remarks>
        /// The paths are split by hand on both separators rather than through
        /// <c>System.IO.Path</c>. Every path this sees is a Windows path, because GTA V
        /// is a Windows game — but the tests run on Linux in CI, where <c>Path</c> does
        /// not treat a backslash as a separator and would quietly agree with any wrong
        /// answer. Parsing the shape the code actually meets is the point.
        /// </remarks>
        public static string Resolve(string? baseDirectory, string? processExecutablePath)
        {
            if (!string.IsNullOrWhiteSpace(processExecutablePath))
            {
                string? fromProcess = ParentOf(processExecutablePath!);
                if (!string.IsNullOrEmpty(fromProcess))
                {
                    return fromProcess!;
                }
            }

            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return string.Empty;
            }

            string trimmed = Trim(baseDirectory!);

            // A host that rooted us in "scripts" is one directory below the game.
            if (string.Equals(LastSegment(trimmed), ScriptsFolderName, StringComparison.OrdinalIgnoreCase))
            {
                string? parent = ParentOf(trimmed);
                if (!string.IsNullOrEmpty(parent))
                {
                    return parent!;
                }
            }

            return trimmed;
        }

        /// <summary>
        /// Where an older build of this client would have kept its files, so the host can
        /// say so rather than leaving a player wondering where their identity key went.
        /// Null when the base directory was already the game directory.
        /// </summary>
        public static string? LegacyRoot(string? baseDirectory, string gameDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return null;
            }

            string trimmed = Trim(baseDirectory!);
            return string.Equals(trimmed, gameDirectory, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
        }

        private static string Trim(string path) => path.TrimEnd(Separators);

        private static string LastSegment(string path)
        {
            int cut = path.LastIndexOfAny(Separators);
            return cut < 0 ? path : path.Substring(cut + 1);
        }

        private static string? ParentOf(string path)
        {
            string trimmed = Trim(path);
            int cut = trimmed.LastIndexOfAny(Separators);
            return cut <= 0 ? null : trimmed.Substring(0, cut);
        }
    }
}
