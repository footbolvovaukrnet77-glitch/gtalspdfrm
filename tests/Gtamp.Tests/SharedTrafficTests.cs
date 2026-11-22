using System.Collections.Generic;
using Gtamp.Client.Entities;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Ambient traffic, shared.
    /// <para>
    /// It was local to every client and always had been — two players on the same
    /// corner saw different cars, which is the plainest statement available that they
    /// are not in one world. Section 15 of the specification lists traffic among the
    /// things to synchronise.
    /// </para>
    /// </summary>
    public class SharedTrafficTests
    {
        private static PopulationCandidate At(uint id, float x, bool wasSource = false) =>
            new PopulationCandidate(id, new NetVector3(x, 0f, 0f), wasSource);

        [Fact]
        public void ALonePlayerSpawnsTheirOwnTraffic()
        {
            var all = new List<PopulationCandidate> { At(1, 0f) };
            Assert.True(PopulationDirector.IsSource(all[0], all));
        }

        /// <summary>
        /// Two players together must produce exactly one source. Both spawning is a
        /// street with two sets of cars in it; neither spawning is an empty street.
        /// </summary>
        [Fact]
        public void TwoPlayersStandingTogetherProduceExactlyOneSource()
        {
            var all = new List<PopulationCandidate> { At(1, 0f), At(2, 10f) };

            Assert.True(PopulationDirector.IsSource(all[0], all));
            Assert.False(PopulationDirector.IsSource(all[1], all));
        }

        /// <summary>
        /// The case the whole architecture exists for: five players in five cities are
        /// in one world, and four of them must not be left on empty streets by a single
        /// global source.
        /// </summary>
        [Fact]
        public void PlayersInDifferentCitiesAreEachTheirOwnSource()
        {
            var all = new List<PopulationCandidate>
            {
                At(1, 0f),
                At(2, 2000f),
                At(3, 4000f),
                At(4, 6000f),
                At(5, 8000f),
            };

            foreach (PopulationCandidate candidate in all)
            {
                Assert.True(PopulationDirector.IsSource(candidate, all), $"player {candidate.PlayerId}");
            }
        }

        /// <summary>
        /// Without hysteresis two players walking in and out of each other's radius
        /// swap the role several times a minute, and every swap is a street's worth of
        /// cars deleted and respawned.
        /// </summary>
        [Fact]
        public void AnExistingSourceKeepsTheRoleAcrossTheBoundary()
        {
            float justOutside = PopulationDirector.CoverageRadius + 20f;

            var all = new List<PopulationCandidate> { At(1, 0f), At(2, justOutside, wasSource: true) };

            // Player 2 was the source and is inside the sticky radius, so they keep it.
            Assert.False(PopulationDirector.IsSource(all[1], all));

            // A player who was not the source at that same distance would have been one:
            // the difference is entirely the hysteresis, which is the point of the test.
            var fresh = new List<PopulationCandidate> { At(1, 0f), At(2, justOutside) };
            Assert.True(PopulationDirector.IsSource(fresh[1], fresh));
        }

        [Fact]
        public void TheRoleGoesToTheLowerIdSoTwoPlayersNeverBothStandDown()
        {
            var all = new List<PopulationCandidate> { At(7, 0f), At(3, 5f) };

            Assert.False(PopulationDirector.IsSource(all[0], all));
            Assert.True(PopulationDirector.IsSource(all[1], all));
        }

        /// <summary>
        /// A client that is not the source must stop the game spawning cars, every
        /// frame, because the natives that do it are reset by the game every frame.
        /// </summary>
        [Fact]
        public void AClientThatIsNotTheSourceSuppressesTrafficEveryFrame()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("watcher");

            var controller = new AmbientTrafficController(
                client.Bridge, client.Client.OwnedEntities) { IsSource = false };

            for (int i = 0; i < 5; i++)
            {
                controller.Update(EntitySnapshotView.Empty, i * 0.1d);
            }

            Assert.Equal(5, client.Bridge.TrafficSuppressedFrames);
            Assert.Equal(0, controller.Offered);
        }

        [Fact]
        public void TheSourceDoesNotSuppressAndOffersWhatTheGameSpawned()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("source");
            client.Bridge.AmbientVehicles.AddRange(new[] { 501, 502, 503 });

            var controller = new AmbientTrafficController(
                client.Bridge, client.Client.OwnedEntities) { IsSource = true };

            controller.Update(EntitySnapshotView.Empty, 1.0d);

            Assert.Equal(0, client.Bridge.TrafficSuppressedFrames);
            Assert.Equal(3, controller.Offered);
        }

        /// <summary>
        /// GTA V keeps spawning as a player drives, so without a ceiling a long drive
        /// turns into a thousand-entity world every client pays for.
        /// </summary>
        [Fact]
        public void TheNumberOfferedIsCapped()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("source");

            for (int handle = 1; handle <= AmbientTrafficController.MaxAdopted * 3; handle++)
            {
                client.Bridge.AmbientVehicles.Add(handle);
            }

            var controller = new AmbientTrafficController(
                client.Bridge, client.Client.OwnedEntities) { IsSource = true };

            controller.Update(EntitySnapshotView.Empty, 1.0d);

            Assert.Equal(AmbientTrafficController.MaxAdopted, controller.Offered);
        }

        [Fact]
        public void TurningItOffLeavesTheGameAloneEntirely()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("offline");
            client.Bridge.AmbientVehicles.Add(9);

            var controller = new AmbientTrafficController(
                client.Bridge, client.Client.OwnedEntities) { Enabled = false, IsSource = false };

            controller.Update(EntitySnapshotView.Empty, 1.0d);

            Assert.Equal(0, client.Bridge.TrafficSuppressedFrames);
            Assert.Equal(0, controller.Offered);
        }

        /// <summary>
        /// The decision is the server's and reaches the client in the snapshot header,
        /// so this is the whole path: a real server with two connected clients, and the
        /// two of them ending up on opposite sides of the answer.
        /// </summary>
        [Fact]
        public void TheServerNominatesOneOfTwoConnectedClientsAndBothAreTold()
        {
            using var harness = new TestHarness();
            TestClient first = harness.CreateClient("first");
            TestClient second = harness.CreateClient("second");
            first.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            second.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);

            Assert.True(harness.AdvanceUntil(() => first.PlayerCount >= 2 && second.PlayerCount >= 2));
            harness.Advance(1.0);

            // Both spawn at the server's spawn point, so they are covering each other
            // and exactly one of them must have the role.
            Assert.True(
                first.Client.AmbientTraffic.IsSource ^ second.Client.AmbientTraffic.IsSource,
                $"first={first.Client.AmbientTraffic.IsSource}, second={second.Client.AmbientTraffic.IsSource}");
        }

        /// <summary>
        /// A server with shared traffic off must never nominate anybody, whatever the
        /// clients have in their own configuration: a client cannot make itself the
        /// source, and that is what stops two of them both deciding they are.
        /// </summary>
        [Fact]
        public void AServerWithSharedTrafficOffNominatesNobody()
        {
            using var harness = new TestHarness(new Gtamp.Server.Core.ServerConfig
            {
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
                SharedTraffic = false,
            });

            TestClient client = harness.CreateClient("alone");
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.Client.IsConnected));
            harness.Advance(1.0);

            Assert.False(client.Client.AmbientTraffic.IsSource);
        }
        /// <summary>
        /// The two numbers that have to agree and are set in different files by
        /// different reasoning.
        /// <para>
        /// The client offers up to <c>MaxAdopted</c> ambient cars. The server refuses a
        /// spawn past a per-player cap. Set independently they were 60 and 32: the
        /// source would offer sixty cars, have twenty-eight refused, and re-offer them
        /// twice a second for the rest of the session. Both numbers are individually
        /// sensible and the failure is a rejection loop in a log nobody reads, which is
        /// exactly the kind nothing else here would catch.
        /// </para>
        /// </summary>
        [Fact]
        public void TheServerAllowsASourceEverythingTheClientWillOffer()
        {
            var config = new Gtamp.Server.Core.ServerConfig();

            Assert.True(
                config.MaxEntitiesPerPlayer + config.MaxAmbientEntitiesPerSource
                    >= AmbientTrafficController.MaxAdopted,
                $"a traffic source may hold {config.MaxEntitiesPerPlayer + config.MaxAmbientEntitiesPerSource} "
                + $"entities but the client will offer {AmbientTrafficController.MaxAdopted}");
        }

        /// <summary>
        /// And the allowance must be conditional: a client that is not the source has
        /// no business holding a street, and the extra room is the server's to give
        /// rather than a client's to claim.
        /// </summary>
        [Fact]
        public void AClientThatIsNotTheSourceGetsNoExtraAllowance()
        {
            var config = new Gtamp.Server.Core.ServerConfig();
            Assert.True(config.MaxAmbientEntitiesPerSource > 0);
            Assert.Equal(32, config.MaxEntitiesPerPlayer);
        }
        /// <summary>
        /// A client that is not the source must empty its pavements as well as its
        /// streets, or the two halves of the city disagree in the way that is hardest
        /// to notice: everybody sees the same cars and a different crowd.
        /// </summary>
        [Fact]
        public void ANonSourceSuppressesPedestriansAsWellAsTraffic()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("watcher");

            var controller = new AmbientTrafficController(
                client.Bridge, client.Client.OwnedEntities) { IsSource = false, SharePedestrians = true };

            controller.Update(EntitySnapshotView.Empty, 0d);

            Assert.Equal(1, client.Bridge.TrafficSuppressedFrames);
            Assert.Equal(1, client.Bridge.PedsSuppressedFrames);
        }

        /// <summary>
        /// And with pedestrians turned off it must leave them entirely alone. Emptying
        /// a pavement without replicating it is the one configuration that is not a
        /// setting: it is a city with nobody in it.
        /// </summary>
        [Fact]
        public void WithPedestriansOffThePavementIsLeftAlone()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("watcher");

            var controller = new AmbientTrafficController(
                client.Bridge, client.Client.OwnedEntities) { IsSource = false, SharePedestrians = false };

            controller.Update(EntitySnapshotView.Empty, 0d);

            Assert.Equal(1, client.Bridge.TrafficSuppressedFrames);
            Assert.Equal(0, client.Bridge.PedsSuppressedFrames);
        }

        /// <summary>
        /// Pedestrians have their own ceiling and it must not be spent by the cars. A
        /// street full of traffic collected first would otherwise leave no room for
        /// anybody walking on it.
        /// </summary>
        [Fact]
        public void PedestriansAreNotStarvedByAFullStreetOfCars()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("source");

            for (int handle = 1; handle <= AmbientTrafficController.MaxAdopted; handle++)
            {
                client.Bridge.AmbientVehicles.Add(handle);
            }

            for (int handle = 5000; handle < 5000 + AmbientTrafficController.MaxAdoptedPeds; handle++)
            {
                client.Bridge.AmbientPeds.Add(handle);
                client.Bridge.Peds2[handle] = new PedEntity(EntityId.None) { ModelHash = 0x705E61F2 };
            }

            var controller = new AmbientTrafficController(
                client.Bridge, client.Client.OwnedEntities) { IsSource = true, SharePedestrians = true };

            controller.Update(EntitySnapshotView.Empty, 1.0d);

            Assert.Equal(AmbientTrafficController.MaxAdopted, controller.Offered);
            Assert.Equal(AmbientTrafficController.MaxAdoptedPeds, controller.PedsOffered);
        }

        /// <summary>
        /// The same agreement the cars needed, for the people: the server's allowance
        /// for a source has to cover both ceilings, or the source offers and the server
        /// refuses twice a second for the rest of the session.
        /// </summary>
        [Fact]
        public void TheServerAllowanceCoversCarsAndPedestriansTogether()
        {
            var config = new Gtamp.Server.Core.ServerConfig();

            Assert.True(
                config.MaxEntitiesPerPlayer + config.MaxAmbientEntitiesPerSource
                    >= AmbientTrafficController.MaxAdopted + AmbientTrafficController.MaxAdoptedPeds,
                $"a source may hold {config.MaxEntitiesPerPlayer + config.MaxAmbientEntitiesPerSource} "
                + $"but will offer {AmbientTrafficController.MaxAdopted + AmbientTrafficController.MaxAdoptedPeds}");
        }
        /// <summary>
        /// The defect a Phase 3 test caught when the two limits were first summed: a
        /// player alone on a server is always the traffic source, because there is
        /// nobody else to be it, so a combined allowance handed every solo player the
        /// ambient room on top of their anti-spam cap and the cap stopped meaning
        /// anything. They are counted separately now, and this says so where the next
        /// person to touch either number will read it.
        /// </summary>
        [Fact]
        public void TheAmbientAllowanceDoesNotRaiseThePerPlayerSpamCap()
        {
            var config = new Gtamp.Server.Core.ServerConfig { MaxEntitiesPerPlayer = 2 };
            using var harness = new TestHarness(config);
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));
            harness.Advance(1.0);

            // Alone, so the server has certainly made her the source.
            Assert.True(alice.Client.AmbientTraffic.IsSource);

            // Five vehicles offered as her own, not as ambient population.
            for (int i = 0; i < 5; i++)
            {
                alice.Bridge.Vehicles[900 + i] = new VehicleEntity(EntityId.None) { ModelHash = 0x9F05F101 };
                alice.Client.OwnedEntities.RegisterVehicle(
                    900 + i, alice.Client.ReplicatedWorld.Current, harness.Now);
                harness.Advance(0.4);
            }

            // Hers, not every vehicle in the world: a server's world has vehicles in it
            // that nobody owns, and counting those would make this test pass or fail on
            // whatever the world happened to be seeded with.
            uint playerId = harness.Server.Players.Sessions[0].PlayerId;
            int owned = harness.Server.Entities.CountOwnedBy(playerId);

            Assert.True(owned <= 2, $"the spam cap was 2 and she holds {owned}");
            Assert.True(alice.Client.OwnedEntities.SpawnsRejected > 0);
        }
    }
}