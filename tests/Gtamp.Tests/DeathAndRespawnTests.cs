using System;
using Gtamp.Server.Core;
using Gtamp.Server.World;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    public class DeathAndRespawnTests
    {
        private static ServerConfig Config(double respawnDelay = 1.0) => new ServerConfig
        {
            PersistenceEnabled = false,
            SaveIntervalSeconds = 0,
            RespawnDelaySeconds = respawnDelay,
        };

        private static TestClient Connected(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.PlayerCount >= 1), $"{name} never connected");
            return client;
        }

        [Fact]
        public void AClientReportingZeroHealthIsMarkedDeadByTheServer()
        {
            using var harness = new TestHarness(Config(respawnDelay: 60));
            TestClient alice = Connected(harness, "alice");

            alice.Bridge.Sample.Health = 0;
            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Sessions[0].IsDead));

            PlayerEntity onServer = harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!;
            Assert.Equal(0, onServer.Health);
            Assert.True(onServer.HasFlag(PlayerFlags.Dead));
        }

        [Fact]
        public void ADeadPlayerCannotHealThemselvesBackToLife()
        {
            using var harness = new TestHarness(Config(respawnDelay: 60));
            TestClient alice = Connected(harness, "alice");

            alice.Bridge.Sample.Health = 0;
            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Sessions[0].IsDead));

            // A trainer's heal key, or a modified client, claiming full health again.
            alice.Bridge.Sample.Health = 200;
            harness.Advance(2.0);

            PlayerEntity onServer = harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!;
            Assert.Equal(0, onServer.Health);
            Assert.True(harness.Server.Players.Sessions[0].IsDead);
        }

        [Fact]
        public void TheServerRespawnsAPlayerAfterTheConfiguredDelay()
        {
            using var harness = new TestHarness(Config(respawnDelay: 1.0));
            TestClient alice = Connected(harness, "alice");

            alice.Bridge.Sample.Health = 0;
            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Sessions[0].IsDead));

            double diedAt = harness.Now;
            Assert.True(
                harness.AdvanceUntil(() => !harness.Server.Players.Sessions[0].IsDead, timeoutSeconds: 10),
                "the player was never respawned");

            Assert.True(harness.Now - diedAt >= 1.0, "the respawn happened before the delay elapsed");

            PlayerEntity onServer = harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!;
            Assert.Equal(onServer.MaxHealth, onServer.Health);
            Assert.False(onServer.HasFlag(PlayerFlags.Dead));
        }

        [Fact]
        public void RespawnPlacesThePlayerAtTheNearestHospital()
        {
            using var harness = new TestHarness(Config(respawnDelay: 0.5));
            TestClient alice = Connected(harness, "alice");

            // Move the player next to Paleto Bay before killing them. A server-side
            // teleport, so no anti-cheat budget is involved and the client cannot drag
            // itself back.
            Assert.True(harness.Server.TeleportPlayer(
                harness.Server.Players.Sessions[0], new NetVector3(-300f, 6300f, 32f), 0f));

            Assert.True(harness.AdvanceUntil(
                () => alice.Bridge.Sample.Position.Y > 6000f, timeoutSeconds: 5),
                "the client never accepted the teleport");

            PlayerEntity entity = harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!;
            alice.Bridge.Sample.Health = 0;
            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Sessions[0].IsDead));
            Assert.True(harness.AdvanceUntil(() => !harness.Server.Players.Sessions[0].IsDead, timeoutSeconds: 10));

            RespawnPoint expected = RespawnPoints.Nearest(new NetVector3(-300f, 6300f, 32f));
            Assert.Equal("Paleto Bay Care Center", expected.Name);
            Assert.Equal(expected.Position.X, entity.Position.X, 1);
            Assert.Equal(expected.Position.Y, entity.Position.Y, 1);
        }

        [Fact]
        public void TheRespawnReachesTheClientAsACorrection()
        {
            using var harness = new TestHarness(Config(respawnDelay: 0.5));
            TestClient alice = Connected(harness, "alice");

            alice.Bridge.Sample.Health = 0;
            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Sessions[0].IsDead));
            Assert.True(harness.AdvanceUntil(() => !harness.Server.Players.Sessions[0].IsDead, timeoutSeconds: 10));

            // The client must end up where the server put it, with its health back.
            Assert.True(
                harness.AdvanceUntil(() => alice.Bridge.Sample.Health == 200, timeoutSeconds: 5),
                "the client never received the respawn");

            PlayerEntity onServer = harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!;
            Assert.Equal(onServer.Position.X, alice.Bridge.Sample.Position.X, 1);
            Assert.Equal(onServer.Position.Y, alice.Bridge.Sample.Position.Y, 1);
            Assert.True(alice.Client.CorrectionsApplied > 0);
        }

        [Fact]
        public void OtherPlayersAreToldAboutADeathAndARespawn()
        {
            using var harness = new TestHarness(Config(respawnDelay: 0.5));
            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 2));

            alice.Bridge.Sample.Health = 0;

            Assert.True(
                harness.AdvanceUntil(
                    () => bob.Bridge.Notifications.Exists(n => n.Contains("alice died")),
                    timeoutSeconds: 10),
                "bob was never told alice died");

            Assert.True(
                harness.AdvanceUntil(
                    () => bob.Bridge.Notifications.Exists(n => n.Contains("respawned")),
                    timeoutSeconds: 10),
                "bob was never told alice respawned");
        }

        [Fact]
        public void ADeadPlayerIsReplicatedAsDeadToOthers()
        {
            using var harness = new TestHarness(Config(respawnDelay: 60));
            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 2));

            alice.Bridge.Sample.Health = 0;
            Assert.True(harness.AdvanceUntil(() => bob.FindPlayer("alice")?.Health == 0, timeoutSeconds: 10));

            PlayerEntity aliceOnBob = bob.FindPlayer("alice")!;
            Assert.True(aliceOnBob.HasFlag(PlayerFlags.Dead));

            // And bob's ped for alice is driven as a corpse, not walked around. The
            // interpolation buffer renders behind the newest snapshot, so this takes
            // an extra moment to show up.
            Assert.True(
                harness.AdvanceUntil(
                    () =>
                    {
                        if (bob.Bridge.Peds.Count != 1)
                        {
                            return false;
                        }

                        foreach (var command in bob.Bridge.Peds.Values)
                        {
                            if (command.Action != Gtamp.Client.Players.RemotePedAction.Dead)
                            {
                                return false;
                            }
                        }

                        return true;
                    },
                    timeoutSeconds: 5),
                "alice's ped was never driven as dead on bob's client");
        }

        [Fact]
        public void RespawnDoesNotTripTheAntiCheat()
        {
            using var harness = new TestHarness(Config(respawnDelay: 0.5));
            TestClient alice = Connected(harness, "alice");

            // Put the player far from any hospital so the respawn is a long teleport.
            Assert.True(harness.Server.TeleportPlayer(
                harness.Server.Players.Sessions[0], new NetVector3(1000f, 2000f, 40f), 0f));
            Assert.True(harness.AdvanceUntil(
                () => alice.Bridge.Sample.Position.Y > 1500f, timeoutSeconds: 5));

            alice.Bridge.Sample.Health = 0;
            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Sessions[0].IsDead));
            Assert.True(harness.AdvanceUntil(() => !harness.Server.Players.Sessions[0].IsDead, timeoutSeconds: 10));

            harness.Advance(3.0);

            // The server teleported the player across the map and refilled their
            // health. Neither is a violation when the server did it.
            Assert.Equal(0, harness.Server.Players.Sessions[0].Validation.TotalViolations);
            Assert.True(alice.Client.IsConnected);
        }

        [Fact]
        public void NearestHospitalIsChosenPerRegion()
        {
            Assert.Equal(
                "Sandy Shores Medical Center",
                RespawnPoints.Nearest(new NetVector3(1800f, 3600f, 34f)).Name);

            Assert.Equal(
                "Mount Zonah Medical Center",
                RespawnPoints.Nearest(new NetVector3(-450f, -300f, 34f)).Name);
        }
    }
}
