using Gtamp.Client.Core;
using Gtamp.Client.Network;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The first real playtest died because ScriptHookVDotNet could not read the game
    /// build, and the only sign of it was a .NET stack trace in a log the player had not
    /// been told about. These cover the two halves of the answer to that: the words, and
    /// the refusal to connect.
    /// </summary>
    public class ScriptHostCompatibilityTests
    {
        [Fact]
        public void AWorkingApiHasNothingToSay()
        {
            Assert.Null(ScriptHostCompatibility.Describe(true, "1.0.3889.0", "3.6.0.0"));
        }

        [Fact]
        public void TheMessageNamesBothVersions()
        {
            string? message = ScriptHostCompatibility.Describe(false, "1.0.3889.0", "3.6.0.0");

            Assert.NotNull(message);
            Assert.Contains("1.0.3889.0", message!);
            Assert.Contains("3.6.0.0", message!);
        }

        /// <summary>
        /// Section 50 of the specification: no "install the dependencies". The message
        /// has to say where from and which files to replace.
        /// </summary>
        [Fact]
        public void TheMessageSaysWhereToGetAWorkingBuild()
        {
            string message = ScriptHostCompatibility.Describe(false, "1.0.3889.0", "3.6.0.0")!;

            Assert.Contains(ScriptHostCompatibility.NightlyDownloadPage, message);
            Assert.Contains("ScriptHookVDotNet3.dll", message);
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData("   ", null)]
        [InlineData(null, "3.6.0.0")]
        [InlineData("1.0.3889.0", null)]
        public void AnInstallationThatWillNotSayItsVersionStillGetsTheAdvice(string? game, string? host)
        {
            string message = ScriptHostCompatibility.Describe(false, game, host)!;

            Assert.Contains(ScriptHostCompatibility.NightlyDownloadPage, message);
            Assert.DoesNotContain("  ", message);
        }

        [Fact]
        public void ABlockedClientDoesNotConnect()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("Blocked");
            client.Client.BlockReason = "ScriptHookVDotNet cannot read this game build.";

            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            harness.Advance(2d);

            Assert.Equal(ClientConnectionState.Disconnected, client.Client.Connection.State);
            Assert.Equal(0, harness.Server.Players.Count);
        }

        /// <summary>
        /// Refusing silently would be the same defect wearing a different hat: the player
        /// types <c>connect</c>, nothing happens, and nothing says why.
        /// </summary>
        [Fact]
        public void ABlockedClientSaysWhyInTheConsole()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("Blocked");
            client.Client.BlockReason = "ScriptHookVDotNet cannot read this game build.";

            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.Contains(
                client.Console.VisibleLines(),
                line => line.Text.Contains("cannot read this game build"));
        }
    }
}
