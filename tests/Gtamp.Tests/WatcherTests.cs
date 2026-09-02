using System;
using System.Collections.Generic;
using System.IO;
using Gtamp.Watcher;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The watcher reads the logs GTA V and GTAMP write and records what broke.
    /// Two things decide whether it is any use: whether it stays quiet on a
    /// healthy session, and whether what it writes is safe to hand to somebody.
    /// </summary>
    public class WatcherRuleTests
    {
        /// <summary>
        /// The first version matched the bare word <c>NativeMemory</c> and called
        /// it "the session ended". That word is in the first ten lines of every
        /// healthy ScriptHookVDotNet log — measured across ten of the user's real
        /// logs, it hit all ten while the session was fine. A rule that fires on a
        /// healthy session teaches the reader to ignore the file, which is worse
        /// than having no file.
        /// </summary>
        [Theory]
        [InlineData("[19:40:59] [DEBUG] Initializing NativeMemory members...")]
        [InlineData("[19:41:00] [DEBUG] Loading scripts from E:\\...\\scripts ...")]
        [InlineData("[19:41:00] [INFO] Started script Gtamp.Client.Shv.GtampScript.")]
        [InlineData("[16:18:32.159] [DEBUG] [NETWORK] Applied full snapshot 1: 11 entities.")]
        [InlineData("[16:18:32.139] [DEBUG] [ENTITY] Vehicle #2 appeared.")]
        [InlineData("[9/2/2026 7:39:58 PM.048] GTAMP RPH Bridge: [GTAMP] RPH bridge started (channel v1).")]
        [InlineData("[17:33:12.657] [SUCCESS] [SERVER] 'GTAMP Server' listening on 0.0.0.0:27015")]
        public void AHealthySessionProducesNoIncidents(string line)
        {
            Assert.Null(IncidentRules.Match("test.log", line));
        }

        [Theory]
        [InlineData("[17:08:44.634] [ERROR] [NETWORK] Connection timed out.", "timeout")]
        [InlineData("[17:08:30.133] [WARNING] [NETWORK] Requesting a resync: baseline snapshot 12677 is no longer in history", "resync")]
        [InlineData("System.TypeInitializationException: SHVDN.NativeMemory..cctor()", "shvdn-unusable")]
        [InlineData("Aborted script Gtamp.Client.Shv.GtampScript", "script-aborted")]
        [InlineData("Model 0x00000000 is not installed on this client.", "missing-content")]
        public void TheThingsThatHaveActuallyGoneWrongAreCaught(string line, string kind)
        {
            Incident? incident = IncidentRules.Match("test.log", line);

            Assert.NotNull(incident);
            Assert.Equal(kind, incident!.Kind);
            Assert.False(string.IsNullOrWhiteSpace(incident.Why), "an incident has to say what it means");
        }

        [Fact]
        public void ATimeoutIsFatalAndAResyncIsNot()
        {
            Assert.Equal(
                IncidentSeverity.Fatal,
                IncidentRules.Match("t", "[17:08:44.634] [ERROR] [NETWORK] Connection timed out.")!.Severity);

            Assert.Equal(
                IncidentSeverity.Problem,
                IncidentRules.Match("t", "[17:08:30.133] [WARNING] [NETWORK] Requesting a resync: x")!.Severity);
        }
    }

    /// <summary>
    /// Whatever the watcher collects may be handed to somebody else, so the
    /// redactor runs on every line of every file — not only on the publishing
    /// path, because a redactor that runs only there is one that will one day be
    /// skipped.
    /// </summary>
    public class WatcherRedactionTests
    {
        [Fact]
        public void TheIdentityKeypairNeverSurvives()
        {
            const string config = """
                [client]
                PlayerName=footbolvova
                IdentityToken=I1NcP2A0i49262TQETFuKu0q9zTVSW46A5/Je4ebxYhCmdWI6Vpo5BKDNFNu9k1
                IdentitySecret=MC4CAQAwBQYDK2VwBCIEIHqvSecretMaterialThatMustNeverLeaveTheBox
                ServerPassword=hunter2
                """;

            string scrubbed = Redactor.Scrub(config);

            Assert.DoesNotContain("I1NcP2A0", scrubbed);
            Assert.DoesNotContain("SecretMaterial", scrubbed);
            Assert.DoesNotContain("hunter2", scrubbed);
            Assert.Contains("(redacted)", scrubbed);

            // The player's chosen name is not a secret and is what makes a report
            // possible to match to a session, so it stays.
            Assert.Contains("footbolvova", scrubbed);
        }

        [Fact]
        public void TheWindowsAccountNameIsTakenOutOfPaths()
        {
            string scrubbed = Redactor.Scrub(@"Config: C:\Users\Volodymyr\Documents\Gtamp\client.ini");

            Assert.DoesNotContain("Volodymyr", scrubbed);
            Assert.Contains(@"C:\Users\(user)", scrubbed);
        }

        /// <summary>
        /// Loopback and LAN addresses say nothing about anybody and are the whole
        /// content of a local test; a routable address is somebody's server.
        /// </summary>
        [Fact]
        public void PublicAddressesGoAndLocalOnesStay()
        {
            Assert.Contains("127.0.0.1:27015", Redactor.Scrub("Connecting to 127.0.0.1:27015"));
            Assert.Contains("192.168.1.5", Redactor.Scrub("Connecting to 192.168.1.5:27015"));
            Assert.Contains("0.0.0.0:27015", Redactor.Scrub("listening on 0.0.0.0:27015"));

            string scrubbed = Redactor.Scrub("Connecting to 203.0.113.44:27015");
            Assert.DoesNotContain("203.0.113.44", scrubbed);
            Assert.Contains("(address)", scrubbed);
        }

        /// <summary>
        /// The real client.ini is never copied at all, redacted or not. Trusting
        /// Scrub to blank the secret works today and breaks the day a setting is
        /// renamed; refusing the file does not.
        /// </summary>
        [Fact]
        public void TheFilesThatHoldKeysAreNeverCopied()
        {
            Assert.True(Redactor.IsForbidden(@"E:\GTA V\Gtamp\client.ini"));
            Assert.True(Redactor.IsForbidden("server.json"));
            Assert.True(Redactor.IsForbidden(@"C:\gtalspdfrm\data\world.db"));

            // The client writes this one itself, with the secret already gone.
            Assert.False(Redactor.IsForbidden("client.ini.redacted"));
            Assert.False(Redactor.IsForbidden("client-2026-09-02.log"));
        }

        [Fact]
        public void ScrubbingSurvivesNothingAndEmptyText()
        {
            Assert.Equal(string.Empty, Redactor.Scrub(string.Empty));
            Assert.Equal(string.Empty, Redactor.Scrub(null!));
        }
    }

    /// <summary>
    /// Publishing is the only automatic route off the machine, so the decision to
    /// take it is the user's and the tool asks before assuming it.
    /// </summary>
    public class WatcherOptionsTests
    {
        [Fact]
        public void PublishingToAGitHubRemoteIsRefusedUntilItIsAcknowledged()
        {
            string repo = NewRepositoryWithOrigin("https://github.com/someone/public-thing.git");
            try
            {
                WatcherOptions.Parse(new[] { "--game", repo, "--repo", repo, "--publish" }, out string? refused);
                Assert.NotNull(refused);
                Assert.Contains("--public-ok", refused);

                WatcherOptions.Parse(
                    new[] { "--game", repo, "--repo", repo, "--publish", "--public-ok" }, out string? allowed);
                Assert.Null(allowed);
            }
            finally
            {
                Directory.Delete(repo, recursive: true);
            }
        }

        [Fact]
        public void WithoutPublishNothingIsSentAnywhere()
        {
            string repo = NewRepositoryWithOrigin("https://github.com/someone/public-thing.git");
            try
            {
                WatcherOptions options = WatcherOptions.Parse(
                    new[] { "--game", repo, "--repo", repo }, out string? error);

                Assert.Null(error);
                Assert.False(options.Publish);
                Assert.False(options.Screenshots);
            }
            finally
            {
                Directory.Delete(repo, recursive: true);
            }
        }

        [Fact]
        public void AMistypedKeyIsRefusedWithAReason()
        {
            WatcherOptions.Parse(new[] { "--sceenshot" }, out string? error);
            Assert.Contains("--sceenshot", error);
        }

        private static string NewRepositoryWithOrigin(string url)
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "gtamp-watch-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "GTA5.exe"), "not really");

            Run(directory, "init");
            Run(directory, "remote", "add", "origin", url);
            return directory;
        }

        private static void Run(string workingDirectory, params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);
            process?.WaitForExit(20_000);
        }
    }

    /// <summary>The PNG writer exists because System.Drawing is Windows-only and CI is Linux.</summary>
    public class PngTests
    {
        [Fact]
        public void ThePngIsSomethingADecoderWouldRecognise()
        {
            var pixels = new byte[4 * 4 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0x20;
                pixels[i + 1] = 0x40;
                pixels[i + 2] = 0x60;
                pixels[i + 3] = 0xFF;
            }

            byte[] png = Png.Encode(pixels, 4, 4);

            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
            Assert.Contains("IHDR", System.Text.Encoding.ASCII.GetString(png));
            Assert.Contains("IDAT", System.Text.Encoding.ASCII.GetString(png));
            Assert.EndsWith("IEND", System.Text.Encoding.ASCII.GetString(png[^8..^4]));
        }

        [Fact]
        public void APixelBufferSmallerThanTheStatedImageIsRefused()
        {
            Assert.Throws<ArgumentException>(() => Png.Encode(new byte[8], 100, 100));
        }
    }
}
