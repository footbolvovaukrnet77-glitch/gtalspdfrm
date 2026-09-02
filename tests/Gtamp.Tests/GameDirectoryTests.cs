using System;
using System.IO;
using Gtamp.Client.Mods;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Where the client thinks GTA V is.
    /// <para>
    /// For the life of the project this was <c>AppDomain.CurrentDomain.BaseDirectory</c>,
    /// and the first run in a real game showed that under ScriptHookVDotNet the app
    /// domain is rooted at the <c>scripts</c> folder rather than the game. The client's
    /// own log said it plainly and nobody had ever read one before:
    /// <c>Detected 0 mod file(s): ScriptHookV=no, SHVDN=no</c> — reported from inside
    /// ScriptHookVDotNet, with ScriptHookV loaded, because the scan was looking in
    /// <c>scripts\</c>.
    /// </para>
    /// </summary>
    public class GameDirectoryTests
    {
        [Fact]
        public void TheRunningExecutableDecides()
        {
            // The only answer that does not depend on where the host rooted anything.
            string resolved = GameDirectory.Resolve(
                @"E:\SteamLibrary\steamapps\common\Grand Theft Auto V\scripts\",
                @"E:\SteamLibrary\steamapps\common\Grand Theft Auto V\GTA5.exe");

            Assert.Equal(@"E:\SteamLibrary\steamapps\common\Grand Theft Auto V", resolved);
        }

        /// <summary>
        /// The exact directory pair from the first real session, which is the case this
        /// whole file exists for.
        /// </summary>
        [Fact]
        public void AScriptsFolderIsClimbedOutOfWhenTheProcessCannotBeAsked()
        {
            string resolved = GameDirectory.Resolve(
                @"E:\SteamLibrary\steamapps\common\Grand Theft Auto V\scripts\",
                processExecutablePath: null);

            Assert.Equal(@"E:\SteamLibrary\steamapps\common\Grand Theft Auto V", resolved);
        }

        [Fact]
        public void ScriptsIsMatchedWhateverItsCase()
        {
            Assert.Equal(
                @"D:\Games\GTAV",
                GameDirectory.Resolve(@"D:\Games\GTAV\Scripts", null));
            Assert.Equal(
                @"D:\Games\GTAV",
                GameDirectory.Resolve(@"D:\Games\GTAV\SCRIPTS\", null));
        }

        [Fact]
        public void ADirectoryThatIsAlreadyTheGameIsLeftAlone()
        {
            Assert.Equal(
                @"D:\Games\GTAV",
                GameDirectory.Resolve(@"D:\Games\GTAV\", null));
        }

        /// <summary>
        /// A folder that merely contains the word is not the scripts folder. Climbing out
        /// of it would put the client one directory above the game.
        /// </summary>
        [Fact]
        public void AFolderNamedLikeScriptsIsNotTheScriptsFolder()
        {
            Assert.Equal(
                @"D:\Games\GTAV\myscripts",
                GameDirectory.Resolve(@"D:\Games\GTAV\myscripts", null));
        }

        [Fact]
        public void NothingKnowableGivesNothingRatherThanACrash()
        {
            Assert.Equal(string.Empty, GameDirectory.Resolve(null, null));
            Assert.Equal(string.Empty, GameDirectory.Resolve("   ", null));
        }

        /// <summary>
        /// The old location is reported only when it differs, so a correct install says
        /// nothing rather than warning about a folder that is where it should be.
        /// </summary>
        [Fact]
        public void TheOldLocationIsNamedOnlyWhenItIsAnOldLocation()
        {
            Assert.Equal(
                @"E:\GTAV\scripts",
                GameDirectory.LegacyRoot(@"E:\GTAV\scripts\", @"E:\GTAV"));

            Assert.Null(GameDirectory.LegacyRoot(@"E:\GTAV", @"E:\GTAV"));
            Assert.Null(GameDirectory.LegacyRoot(@"E:\GTAV\", @"E:\GTAV"));
            Assert.Null(GameDirectory.LegacyRoot(null, @"E:\GTAV"));
        }
        /// <summary>
        /// The join, which is where the defect actually lived.
        /// <para>
        /// Neither component was wrong. <c>ModEnvironment.Detect</c> finds what is in the
        /// directory it is given, and <c>InteropTests</c> has always proved that. The
        /// host chose the directory, and chose the wrong one — so the scan reported
        /// <c>ScriptHookV=no</c> from a machine with ScriptHookV loaded and running.
        /// A test of either half alone passes; only the two together catch it.
        /// </para>
        /// </summary>
        [Fact]
        public void ResolvingFromTheScriptsFolderFindsTheModsThatAreInstalled()
        {
            string game = Path.Combine(Path.GetTempPath(), "gtamp-dir-" + Guid.NewGuid().ToString("N"));
            string scripts = Path.Combine(game, "scripts");
            Directory.CreateDirectory(scripts);

            try
            {
                // A real install: the loaders sit beside GTA5.exe, our client sits in
                // scripts, and the host is rooted in scripts because that is where
                // ScriptHookVDotNet loaded it from.
                File.WriteAllText(Path.Combine(game, "ScriptHookV.dll"), "stub");
                File.WriteAllText(Path.Combine(game, "ScriptHookVDotNet3.dll"), "stub");
                File.WriteAllText(Path.Combine(scripts, "Gtamp.Client.Shv.dll"), "stub");

                // What the host used to do.
                ModEnvironment naive = ModEnvironment.Detect(scripts);
                Assert.False(naive.ScriptHookV);
                Assert.False(naive.ScriptHookVDotNet);

                // What it does now.
                string resolved = GameDirectory.Resolve(scripts, processExecutablePath: null);
                ModEnvironment detected = ModEnvironment.Detect(resolved);

                Assert.True(detected.ScriptHookV);
                Assert.True(detected.ScriptHookVDotNet);
            }
            finally
            {
                try
                {
                    Directory.Delete(game, recursive: true);
                }
                catch (IOException)
                {
                    // A leftover temp directory is not worth failing a test over.
                }
            }
        }

    }
}
