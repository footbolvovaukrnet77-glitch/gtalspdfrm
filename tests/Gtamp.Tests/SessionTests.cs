using System;
using Gtamp.Client.Network;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The Phase 1 acceptance criteria from the master prompt, section 57, driven
    /// end to end: server up, two players in one world, movement synchronised,
    /// disconnect, reconnect into the <em>current</em> world rather than a stale one.
    /// </summary>
    public class SessionTests
    {
        [Fact]
        public void AClientCanConnectAndReceiveItsOwnEntity()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected), "alice never connected");

            Assert.Equal(ClientConnectionState.Connected, alice.Client.Connection.State);
            Assert.True(alice.Client.LocalEntityId.IsValid);
            Assert.Equal(1, harness.Server.Players.Count);

            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1), "alice never received a snapshot");
            Assert.NotNull(alice.FindPlayer("alice"));
        }

        [Fact]
        public void TwoPlayersSeeEachOtherInOneWorld()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.True(
                harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2),
                "the two clients never converged on a two-player world");

            Assert.NotNull(alice.FindPlayer("bob"));
            Assert.NotNull(bob.FindPlayer("alice"));

            Assert.Equal(1, alice.Client.RemotePlayers.Count);
            Assert.Equal(1, bob.Client.RemotePlayers.Count);

            // Each client spawned exactly one ped for the other player, and none for itself.
            Assert.True(harness.AdvanceUntil(() => alice.Bridge.Peds.Count == 1 && bob.Bridge.Peds.Count == 1));
        }

        [Fact]
        public void MovementIsReplicatedFromOneClientToTheOther()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));

            alice.Bridge.Sample.Heading = 90f;
            harness.Walk(alice, metres: 40f);
            harness.Advance(0.5);

            PlayerEntity? aliceOnBob = bob.FindPlayer("alice");
            Assert.NotNull(aliceOnBob);
            Assert.InRange(aliceOnBob!.Position.X, 253f, 257f);
            Assert.Equal(90f, aliceOnBob.Heading, 1);
            Assert.Equal(0, alice.Client.CorrectionsApplied);

            // And bob's ped for alice followed, through the interpolation buffer.
            Assert.Single(bob.Bridge.Peds);
            foreach (var ped in bob.Bridge.Peds.Values)
            {
                Assert.InRange(ped.TargetPosition.X, 230f, 258f);
            }
        }

        [Fact]
        public void RemotePlayersAreInterpolatedAtFrameRateNotAtSnapshotRate()
        {
            // The server sends 20 snapshots a second. If the render timeline only
            // advanced when a snapshot landed, a remote ped would step 20 times a
            // second no matter how fast the game rendered, and interpolating would
            // buy nothing.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));

            // Fill alice's interpolation buffer on bob's side with continuous motion.
            harness.Walk(alice, metres: 12f);

            var renderedPositions = new System.Collections.Generic.HashSet<float>();
            for (int frame = 0; frame < 60; frame++)
            {
                alice.Bridge.Sample.Position = new NetVector3(
                    alice.Bridge.Sample.Position.X + 0.1f,
                    alice.Bridge.Sample.Position.Y,
                    alice.Bridge.Sample.Position.Z);

                harness.Advance(1d / 60d, 1d / 60d);

                foreach (var ped in bob.Bridge.Peds.Values)
                {
                    renderedPositions.Add(ped.TargetPosition.X);
                }
            }

            // One second at 60 fps against 20 snapshots: a snapshot-driven timeline
            // would produce at most ~21 distinct positions.
            Assert.True(
                renderedPositions.Count > 40,
                $"only {renderedPositions.Count} distinct rendered positions over 60 frames — " +
                "the render timeline is not advancing between snapshots");
        }

        [Fact]
        public void HealthAndFlagsAreReplicated()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));

            alice.Bridge.Sample.Health = 120;
            alice.Bridge.Sample.Armor = 50;
            alice.Bridge.Sample.Flags = PlayerFlags.Sprinting | PlayerFlags.Aiming;
            alice.Bridge.Sample.Movement = MovementState.Sprint;

            Assert.True(harness.AdvanceUntil(() => bob.FindPlayer("alice")?.Health == 120));

            PlayerEntity aliceOnBob = bob.FindPlayer("alice")!;
            Assert.Equal(50, aliceOnBob.Armor);
            Assert.Equal(MovementState.Sprint, aliceOnBob.Movement);
            Assert.True(aliceOnBob.HasFlag(PlayerFlags.Aiming));
        }

        [Fact]
        public void WhenOnePlayerLeavesTheOtherKeepsPlayingAndTheirPedIsRemoved()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));
            Assert.True(harness.AdvanceUntil(() => bob.Bridge.Peds.Count == 1));

            alice.Client.Disconnect("test");
            harness.RemoveClient(alice);

            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 1), "bob still sees alice after she left");
            Assert.True(bob.Client.IsConnected, "bob should be unaffected by alice leaving");
            Assert.Equal(0, bob.Client.RemotePlayers.Count);
            Assert.Empty(bob.Bridge.Peds);
            Assert.Equal(1, harness.Server.Players.Count);
        }

        [Fact]
        public void AReconnectingPlayerReceivesTheCurrentWorldNotAnOldOne()
        {
            using var harness = new TestHarness();
            string aliceIdentity = "alice-identity-token";

            TestClient alice = harness.CreateClient("alice", aliceIdentity);
            TestClient bob = harness.CreateClient("bob");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2));

            alice.Client.Disconnect("test");
            harness.RemoveClient(alice);
            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 1));

            // The world moves on while alice is away: bob walks a long way and the
            // server clock is changed.
            harness.Walk(bob, metres: 120f);
            harness.Server.World.State.Environment.SetTime(21, 45, 0);

            TestClient aliceAgain = harness.CreateClient("alice", aliceIdentity);
            aliceAgain.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => aliceAgain.PlayerCount == 2), "alice did not get the world back");

            PlayerEntity? bobOnAlice = aliceAgain.FindPlayer("bob");
            Assert.NotNull(bobOnAlice);

            // Bob's *current* position, not where he was when alice left.
            Assert.InRange(bobOnAlice!.Position.X, 330f, 340f);
            Assert.Equal(21, aliceAgain.Client.ReplicatedWorld.Environment.Hours);
            Assert.Equal(45, aliceAgain.Client.ReplicatedWorld.Environment.Minutes);
            Assert.Equal(2, harness.Server.Players.Count);
        }

        [Fact]
        public void TheSessionSurvivesLatencyAndPacketLoss()
        {
            using var harness = new TestHarness(seed: 20240607);
            harness.Latency = 0.08;
            harness.Network.Jitter = 0.04;
            harness.PacketLoss = 0.15;

            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.True(
                harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2, timeoutSeconds: 15),
                "the clients never converged over a lossy link");

            harness.Walk(alice, metres: 60f);
            harness.Advance(2.0);

            PlayerEntity? aliceOnBob = bob.FindPlayer("alice");
            Assert.NotNull(aliceOnBob);
            Assert.InRange(aliceOnBob!.Position.X, 273f, 277f);
            Assert.True(alice.Client.IsConnected);
            Assert.True(bob.Client.IsConnected);
        }

        [Fact]
        public void ChatIsBroadcastReliablyToEveryOtherPlayer()
        {
            using var harness = new TestHarness(seed: 777);
            harness.PacketLoss = 0.2;

            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount == 2 && bob.PlayerCount == 2, timeoutSeconds: 15));

            alice.Console.Submit("say hello from alice");

            Assert.True(
                harness.AdvanceUntil(
                    () => bob.Bridge.Notifications.Exists(n => n.Contains("hello from alice")),
                    timeoutSeconds: 15),
                "the chat message never reached bob despite being sent reliably");
        }

        [Fact]
        public void AWrongPasswordIsRefusedWithAReadableReason()
        {
            var config = new Gtamp.Server.Core.ServerConfig
            {
                Password = "correct-horse",
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
            };

            using var harness = new TestHarness(config);
            TestClient alice = harness.CreateClient("alice");
            alice.Config.ServerPassword = "wrong";

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.Connection.State == ClientConnectionState.Failed));

            Assert.Contains("password", alice.Client.Connection.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, harness.Server.Players.Count);
        }

        [Fact]
        public void AFullServerRefusesFurtherConnections()
        {
            var config = new Gtamp.Server.Core.ServerConfig
            {
                MaxPlayers = 1,
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
            };

            using var harness = new TestHarness(config);
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => bob.Client.Connection.State == ClientConnectionState.Failed));

            Assert.Contains("full", bob.Client.Connection.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.True(alice.Client.IsConnected);
        }

        [Fact]
        public void WorldTimeAndWeatherReachTheClient()
        {
            using var harness = new TestHarness();
            harness.Server.World.State.Environment.SetTime(3, 15, 0);
            harness.Server.World.State.Environment.WeatherHash = GameHash.Joaat("THUNDER");

            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

            Assert.Equal(3, alice.Client.ReplicatedWorld.Environment.Hours);
            Assert.Equal(GameHash.Joaat("THUNDER"), alice.Bridge.WeatherHash);
            Assert.Equal(3, alice.Bridge.ClockHours);
        }

        [Fact]
        public void ALostAcceptDoesNotStrandTheClient()
        {
            // The accept is the one packet with no reliability layer behind it: it is
            // sent before the session exists. On a lossy link it will be dropped, and
            // the client's retry must be answered rather than swallowed by the
            // half-open session the first request already created.
            using var harness = new TestHarness(seed: 31337);
            harness.PacketLoss = 0.6;

            var clients = new System.Collections.Generic.List<TestClient>();
            for (int i = 0; i < 6; i++)
            {
                TestClient client = harness.CreateClient("player" + i);
                clients.Add(client);
                client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            }

            Assert.True(
                harness.AdvanceUntil(() => clients.TrueForAll(c => c.Client.IsConnected), timeoutSeconds: 25),
                "a client was stranded after its accept was lost: " +
                string.Join(", ", clients.ConvertAll(c => $"{c.Config.PlayerName}={c.Client.Connection.State}")));

            Assert.Equal(6, harness.Server.Players.Count);
        }

        [Fact]
        public void ADisconnectedClientIsReapedAfterTheTimeout()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            // Simulate the process being killed: stop pumping the client entirely.
            harness.RemoveClient(alice);

            Assert.True(
                harness.AdvanceUntil(
                    () => harness.Server.Players.Count == 0,
                    timeoutSeconds: ProtocolConstants.ConnectionTimeout + 5),
                "the server never reaped the dead session");
        }
    }
}
