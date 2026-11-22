using Gtamp.Server.Players;
using Gtamp.Shared.Core;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The three world-environment fields that reached every client and stopped.
    /// <para>
    /// <c>WindSpeed</c>, <c>WindDirection</c> and <c>Blackout</c> were on
    /// <c>WorldEnvironment</c> from its first version, serialised into every
    /// snapshot, restored from persistence and printed by the admin console — and
    /// nothing in the game layer ever applied one. Every client ran whatever wind its
    /// own copy of the game had picked, so the same storm blew different ways for
    /// different players, and a city-wide blackout was visible only to the server.
    /// </para>
    /// </summary>
    public class WorldEnvironmentTests
    {
        private static TestClient Join(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            return client;
        }

        [Fact]
        public void TheWindReachesTheGame()
        {
            using var harness = new TestHarness();
            TestClient client = Join(harness, "alice");
            Assert.True(harness.AdvanceUntil(() => client.Client.IsConnected));

            harness.Server.World.State.Environment.WindSpeed = 7.5f;
            harness.Server.World.State.Environment.WindDirection = 210f;

            // The direction is an angle on the wire, quantised to 16 bits, so it comes
            // back within 0.0055 degrees rather than exactly. The speed is a float and
            // does arrive exactly.
            Assert.True(harness.AdvanceUntil(() =>
                client.Bridge.WindSpeed == 7.5f
                && System.Math.Abs(client.Bridge.WindDirection - 210f) < 0.01f));
        }

        [Fact]
        public void ABlackoutReachesTheGame()
        {
            using var harness = new TestHarness();
            TestClient client = Join(harness, "alice");
            Assert.True(harness.AdvanceUntil(() => client.Client.IsConnected));

            harness.Server.World.State.Environment.Blackout = true;

            Assert.True(harness.AdvanceUntil(() => client.Bridge.Blackout));

            harness.Server.World.State.Environment.Blackout = false;

            // And back: a world state that can be entered and not left is half a
            // feature, and the one half nobody notices is the half that is missing.
            Assert.True(harness.AdvanceUntil(() => !client.Bridge.Blackout));
        }

        [Fact]
        public void EveryClientGetsTheSameWeather()
        {
            using var harness = new TestHarness();
            TestClient alice = Join(harness, "alice");
            TestClient bob = Join(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected && bob.Client.IsConnected));

            harness.Server.World.State.Environment.WeatherHash = GameHash.Joaat("THUNDER");
            harness.Server.World.State.Environment.WindSpeed = 12f;

            Assert.True(harness.AdvanceUntil(() =>
                alice.Bridge.WindSpeed == 12f && bob.Bridge.WindSpeed == 12f));

            Assert.Equal(alice.Bridge.WeatherHash, bob.Bridge.WeatherHash);
        }

        [Fact]
        public void TheEnvironmentSurvivesAReconnect()
        {
            // The environment rides in the snapshot header rather than as an entity,
            // so it is worth asserting that a fresh client gets it rather than
            // inheriting whatever its own game had.
            using var harness = new TestHarness();
            TestClient first = Join(harness, "first");
            Assert.True(harness.AdvanceUntil(() => first.Client.IsConnected));

            harness.Server.World.State.Environment.WindSpeed = 3.25f;
            harness.Server.World.State.Environment.Blackout = true;
            harness.Advance(0.5d);

            TestClient second = Join(harness, "second");
            Assert.True(harness.AdvanceUntil(() => second.Client.IsConnected));

            Assert.True(harness.AdvanceUntil(() => second.Bridge.WindSpeed == 3.25f && second.Bridge.Blackout));
        }
    }
}
