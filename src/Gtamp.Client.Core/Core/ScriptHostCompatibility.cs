namespace Gtamp.Client.Core
{
    /// <summary>
    /// Whether ScriptHookVDotNet's managed game API can be used on this installation,
    /// and what to tell the player when it cannot.
    /// <para>
    /// ScriptHookVDotNet does not ask the game where its data is; it scans the game's
    /// code for byte patterns and works the addresses out. On a game build whose code
    /// no longer matches those patterns the scan produces nothing, the class that holds
    /// the results fails to initialise, and from then on <b>every</b> member built on it
    /// throws — spawning a ped, reading a vehicle, asking whether an entity still
    /// exists. Thirty-eight of the members this client calls are in that group.
    /// </para>
    /// <para>
    /// Nothing in this project can fix that: the addresses are ScriptHookVDotNet's to
    /// find. What this project can do is find out at startup instead of at a random
    /// later frame, say so in words a player can act on, and refuse to connect rather
    /// than join a session in which nothing it does reaches the game.
    /// </para>
    /// </summary>
    public static class ScriptHostCompatibility
    {
        /// <summary>
        /// Where a build that supports a newer game version comes from. The stable
        /// releases lag the game by months; the nightly mirror is where support for a
        /// new build lands first, and it needs no GitHub account to download.
        /// </summary>
        public const string NightlyDownloadPage =
            "https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases";

        /// <summary>
        /// One line, for the notification a player sees before they open anything. The
        /// detail goes in the log; this only has to make them look.
        /// </summary>
        public const string ShortNotification =
            "~r~GTAMP cannot use the game API~s~. ScriptHookVDotNet does not support this "
            + "game build. See Gtamp/logs.";

        /// <summary>
        /// Returns null when the managed API answered, or a player-facing explanation
        /// when it did not. Both version strings are optional: an installation that will
        /// not say which build it is still deserves the rest of the sentence.
        /// </summary>
        public static string? Describe(bool managedApiWorks, string? gameVersion, string? hostVersion)
        {
            if (managedApiWorks)
            {
                return null;
            }

            string host = string.IsNullOrWhiteSpace(hostVersion)
                ? "The installed ScriptHookVDotNet"
                : "ScriptHookVDotNet " + hostVersion!.Trim();

            string game = string.IsNullOrWhiteSpace(gameVersion)
                ? "this game build"
                : "game build " + gameVersion!.Trim();

            return host + " cannot read " + game + ". It locates the game's data by scanning "
                + "for byte patterns, and on a build it does not know that scan fails, so every "
                + "call that reads or changes the world — spawning a ped, reading a vehicle, "
                + "checking that an entity still exists — throws instead of answering. "
                + "Multiplayer is refused rather than half-working. Install a ScriptHookVDotNet "
                + "nightly that supports this build from " + NightlyDownloadPage + ", replace "
                + "ScriptHookVDotNet.asi, ScriptHookVDotNet2.dll and ScriptHookVDotNet3.dll in "
                + "the game directory with the ones from it, and restart the game. The console "
                + "and the logs work either way; nothing that touches the game does.";
        }
    }
}
