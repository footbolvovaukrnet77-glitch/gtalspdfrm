using System;
using System.IO;
using System.Linq;
using Gtamp.Client.Core;
using Gtamp.Client.Diagnostics;
using Gtamp.Client.Mods;
using Gtamp.Shared.Core;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Security;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Phase 11: the tools that answer "why does it look wrong", and the rule that
    /// none of what they produce leaves the machine.
    /// </summary>
    public class DeveloperToolsTests : IDisposable
    {
        private readonly string _scratch = Path.Combine(
            Path.GetTempPath(), "gtamp-devtools-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_scratch))
                {
                    Directory.Delete(_scratch, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }

        // ------------------------------------------------------------------
        // The entity inspector
        // ------------------------------------------------------------------
        [Fact]
        public void TheLocalPlayerComparesCleanlyWhenNothingHasDrifted()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));
            harness.Advance(1.0);

            EntityComparison comparison = EntityInspector.Compare(alice.Client, alice.Client.LocalEntityId);

            Assert.Equal(0, comparison.DifferenceCount);
            Assert.Contains(comparison.Fields, f => f.Name == "position" && f.Outcome == ComparisonOutcome.Match);
            Assert.Contains(comparison.Fields, f => f.Name == "health" && f.Outcome == ComparisonOutcome.Match);
        }

        [Fact]
        public void ADisagreementIsShownAsOneAndNamedByField()
        {
            // The whole point of the tool: turn "it looks wrong" into "health differs".
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));
            harness.Advance(1.0);

            // The interesting case is drift the correction does not act on: below the
            // threshold the client never snaps, so the disagreement is invisible
            // everywhere except here. Raising the threshold reproduces that on demand.
            alice.Config.HealthCorrectionThreshold = 1000;

            // An admin kill: the server holds the player dead and ignores the health
            // their client keeps reporting, so the two genuinely disagree.
            Assert.True(harness.Server.KillPlayer(harness.Server.Players.Sessions[0]));
            harness.Advance(0.5);

            EntityComparison comparison = EntityInspector.Compare(alice.Client, alice.Client.LocalEntityId);
            FieldComparison health = comparison.Fields.Single(f => f.Name == "health");

            Assert.Equal(ComparisonOutcome.Differs, health.Outcome);
            Assert.Equal("0", health.Server);
            Assert.Equal("200", health.Local);
            Assert.Contains("≠", EntityInspector.Format(comparison));
        }

        [Fact]
        public void AFieldTheGameWillNotReadBackIsMarkedUnreadableNotMatching()
        {
            // A blank in a diff reads as agreement, which is the one answer that must
            // never be given for something nobody actually checked.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));
            harness.Advance(1.0);

            PlayerEntity bobOnAlice = alice.FindPlayer("bob")!;
            EntityComparison comparison = EntityInspector.Compare(alice.Client, bobOnAlice.Id);

            FieldComparison health = comparison.Fields.Single(f => f.Name == "health");
            Assert.Equal(ComparisonOutcome.NotReadable, health.Outcome);
            Assert.Contains("never read back", health.Note);

            Assert.True(comparison.UnreadableCount >= 2);
            Assert.Contains("will not read back", EntityInspector.Format(comparison));
        }

        [Fact]
        public void AnEntityTheServerHasNotSentIsSaidSoRatherThanShownEmpty()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            EntityComparison comparison = EntityInspector.Compare(alice.Client, new EntityId(9999));
            Assert.Contains("has not sent", comparison.Fields.Single().Note);
        }

        // ------------------------------------------------------------------
        // The network overlay
        // ------------------------------------------------------------------
        [Fact]
        public void TheOverlaySaysSoWhenThereIsNoSession()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");

            var lines = NetworkOverlay.Build(alice.Client);
            Assert.Single(lines);
            Assert.Equal(OverlaySeverity.Warning, lines[0].Severity);
            Assert.Contains("not connected", lines[0].Text);
        }

        [Fact]
        public void TheOverlayReportsAHealthySessionAsNormal()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));
            harness.Advance(1.0);

            var lines = NetworkOverlay.Build(alice.Client);
            Assert.Contains(lines, l => l.Text.StartsWith("ping ", StringComparison.Ordinal));
            Assert.All(lines, l => Assert.NotEqual(OverlaySeverity.Bad, l.Severity));
        }

        [Fact]
        public void MissingContentIsRaisedOnTheOverlayBecauseThatIsWhereThePlayerIsLooking()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            const uint ModdedBody = 0xFEEDFACE;
            alice.Bridge.Sample.ModelHash = ModdedBody;
            bob.Bridge.UnavailableModels.Add(ModdedBody);

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => !bob.Client.MissingContent.IsEmpty));

            var lines = NetworkOverlay.Build(bob.Client);
            Assert.Contains(lines, l => l.Severity == OverlaySeverity.Bad && l.Text.Contains("missing content"));
        }

        // ------------------------------------------------------------------
        // The diagnostic bundle
        // ------------------------------------------------------------------
        [Fact]
        public void TheBundleNeverContainsTheIdentitySecret()
        {
            // The one thing in this phase that would be a security bug if it were
            // wrong: client.ini now holds a private key, and a bundle is written to be
            // shared. Pasting it must not hand over the player's character.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            string secret = alice.Config.IdentitySecret;
            Assert.False(string.IsNullOrWhiteSpace(secret), "the test client has no secret to leak");
            alice.Config.ServerPassword = "hunter2";

            BundleResult result = DiagnosticBundle.Write(alice.Client, "a car went missing", _scratch);
            Assert.True(result.Success, result.Error);

            foreach (string file in Directory.GetFiles(result.Directory))
            {
                string content = File.ReadAllText(file);
                Assert.DoesNotContain(secret, content);
                Assert.DoesNotContain("hunter2", content);
            }

            // The public half is not a secret and is the most useful single
            // identifier in a report, so it stays.
            string redacted = File.ReadAllText(Path.Combine(result.Directory, "client.ini.redacted"));
            Assert.Contains(alice.Config.IdentityToken, redacted);
            Assert.Contains("redacted", redacted);
        }

        [Fact]
        public void TheBundleWritesTheFilesItPromisesAndSendsNothing()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            BundleResult result = DiagnosticBundle.Write(alice.Client, "steps to reproduce", _scratch);

            Assert.True(result.Success, result.Error);
            Assert.Contains("report.txt", result.Files);
            Assert.Contains("diagnostics.txt", result.Files);
            Assert.Contains("network.txt", result.Files);
            Assert.Contains("log.txt", result.Files);
            Assert.Contains("client.ini.redacted", result.Files);
            Assert.Contains("README.txt", result.Files);

            string readme = File.ReadAllText(Path.Combine(result.Directory, "README.txt"));
            Assert.Contains("sent anywhere", readme);

            Assert.Contains("steps to reproduce", File.ReadAllText(Path.Combine(result.Directory, "report.txt")));
        }

        [Fact]
        public void AnUnwritableDestinationIsReportedRatherThanThrown()
        {
            // The command is run when something has already gone wrong; it must not be
            // the thing that goes wrong next.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");

            BundleResult result = DiagnosticBundle.Write(alice.Client, "x", "\0invalid\0path");

            Assert.False(result.Success);
            Assert.NotEqual(string.Empty, result.Error);
        }

        [Theory]
        [InlineData("IdentitySecret=abc", true)]
        [InlineData("identitysecret = abc", true)]
        [InlineData("ServerPassword=abc", true)]
        [InlineData("IdentityToken=abc", false)]
        [InlineData("PlayerName=IdentitySecret", false)]
        [InlineData("", false)]
        [InlineData("no separator", false)]
        public void SecretLinesAreRecognisedByKeyNotBySubstring(string line, bool expected)
        {
            // Matching on a substring would redact a player called "IdentitySecret"
            // and, worse, miss nothing while looking like it worked.
            Assert.Equal(expected, DiagnosticBundle.IsSecretLine(line));
        }

        // ------------------------------------------------------------------
        // Reload
        // ------------------------------------------------------------------
        [Fact]
        public void ReloadingTheConfigAppliesWhatCanChangeAndNamesWhatCannot()
        {
            Directory.CreateDirectory(_scratch);
            string path = Path.Combine(_scratch, "client.ini");

            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.ConfigPath = path;

            var edited = new ClientConfig
            {
                PlayerName = alice.Config.PlayerName,
                ServerAddress = "203.0.113.9",
                ServerPort = 27020,
                IdentitySecret = alice.Config.IdentitySecret,
                InterpolationDelay = 0.2,
                ShowNetworkOverlay = true,
                ConsoleKey = 192,
            };

            edited.EnsureIdentity();
            edited.Save(path);

            ConfigReloadResult result = alice.Client.ReloadConfig();

            Assert.True(result.Success, result.Error);
            Assert.Contains(result.Applied, a => a.StartsWith("InterpolationDelay", StringComparison.Ordinal));
            Assert.Contains(result.Applied, a => a.StartsWith("ShowNetworkOverlay", StringComparison.Ordinal));
            Assert.Equal(0.2, alice.Config.InterpolationDelay);
            Assert.True(alice.Config.ShowNetworkOverlay);

            // The address changed on disk but the session is already open, so it is
            // reported rather than silently ignored.
            Assert.Contains(result.NeedsReconnect, r => r.Contains("ServerAddress"));
            Assert.Equal("127.0.0.1", alice.Config.ServerAddress);
        }

        [Fact]
        public void ReloadingAConfigThatIsNotThereFailsWithAReason()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");

            ConfigReloadResult result = alice.Client.ReloadConfig();
            Assert.False(result.Success);
            Assert.Contains("configuration file", result.Error);
        }

        [Fact]
        public void AnAdapterAlreadyLoadedIsNotRegisteredTwiceByARescan()
        {
            // A re-scan sees every file again. Registering an adapter twice would
            // double every event handler and entity type it declares — and .NET
            // Framework cannot unload the first copy to make room.
            var log = new LogBus();
            var host = new AdapterHost(log);
            var environment = new ModEnvironment();

            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");

            var adapter = new CountingAdapter();
            host.Add(adapter, alice.Client.Sdk, environment);
            host.Add(new CountingAdapter(), alice.Client.Sdk, environment);

            Assert.Single(host.Active);
            Assert.Equal(1, adapter.Initialisations);
            Assert.Contains("counting", host.Skipped);
        }

        private sealed class CountingAdapter : IModAdapter
        {
            public string Id => "counting";

            public string DisplayName => "Counting adapter";

            public int Initialisations { get; private set; }

            public bool IsAvailable(ModEnvironment environment) => true;

            public void Initialize(Gtamp.Client.Sdk.IModSdk sdk, ModEnvironment environment) => Initialisations++;

            public void Update(double now)
            {
            }

            public void Shutdown()
            {
            }

            public string DescribeStatus() => "ok";
        }
    }
}
