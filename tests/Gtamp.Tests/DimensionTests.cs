using Gtamp.Server.Core;
using Gtamp.Server.Players;
using Gtamp.Server.Replication;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Dimensions: parallel copies of the world, used for instances, private
    /// sessions and interiors.
    /// <para>
    /// `Dimension` was on every entity from the first version. It was serialised,
    /// persisted, restored, printed by the admin console and by `entity`, settable
    /// through `TeleportPlayer` — and it filtered nothing. Two players in different
    /// dimensions saw each other, drove into each other and shot each other. The
    /// field described a separation that did not exist.
    /// </para>
    /// <para>
    /// This is a <em>replication</em> filter and only that. Every entity stays in the
    /// server world at every dimension, which the last test here asserts alongside
    /// the invisibility — the same pairing `StressTests` uses for distance.
    /// </para>
    /// </summary>
    public class DimensionTests
    {
        private static PlayerEntity Entity(uint dimension) =>
            new PlayerEntity(new EntityId(7)) { Dimension = dimension };

        [Fact]
        public void SharingADimensionIsStrictEquality()
        {
            // Deliberately no "dimension 0 sees everything" exception: a rule with a
            // special case has to be reasoned about at every call site.
            Assert.True(ReplicationPriority.SharesDimension(Entity(0), 0));
            Assert.True(ReplicationPriority.SharesDimension(Entity(5), 5));
            Assert.False(ReplicationPriority.SharesDimension(Entity(5), 0));
            Assert.False(ReplicationPriority.SharesDimension(Entity(0), 5));
        }

        [Fact]
        public void OrderingSkipsAnotherDimension()
        {
            var here = new PlayerEntity(new EntityId(1)) { Dimension = 3 };
            var elsewhere = new PlayerEntity(new EntityId(2)) { Dimension = 4 };

            var ordered = ReplicationPriority.Order(
                new NetEntity[] { here, elsewhere },
                NetVector3.Zero,
                currentTick: 10,
                new ClientReplicationState(),
                EntityId.None,
                viewerDimension: 3);

            Assert.Single(ordered);
            Assert.Equal(here.Id, ordered[0].Id);
        }

        [Fact]
        public void PlayersInDifferentDimensionsDoNotSeeEachOther()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 2 && bob.PlayerCount >= 2));

            Assert.True(harness.Server.Players.TryGetByPlayerId(bob.Client.LocalPlayerId, out PlayerSession bobSession));
            PlayerEntity? bobEntity = harness.Server.World.GetPlayer(bobSession.EntityId);
            Assert.NotNull(bobEntity);

            harness.Server.TeleportPlayer(bobSession, bobEntity!.Position, bobEntity.Heading, dimension: 9);

            // Not merely stale — gone. An entity dropped from the candidate list
            // without being reported removed keeps its baseline copy on the client for
            // ever, frozen at whatever it was doing when it left view.
            Assert.True(harness.AdvanceUntil(() => alice.FindPlayer("bob") == null));
            Assert.True(harness.AdvanceUntil(() => bob.FindPlayer("alice") == null));
        }

        [Fact]
        public void ComingBackBringsThemBack()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 2));

            Assert.True(harness.Server.Players.TryGetByPlayerId(bob.Client.LocalPlayerId, out PlayerSession bobSession));
            PlayerEntity? bobEntity = harness.Server.World.GetPlayer(bobSession.EntityId);

            harness.Server.TeleportPlayer(bobSession, bobEntity!.Position, bobEntity.Heading, dimension: 9);
            Assert.True(harness.AdvanceUntil(() => alice.FindPlayer("bob") == null));

            harness.Server.TeleportPlayer(bobSession, bobEntity.Position, bobEntity.Heading, dimension: 0);

            Assert.True(harness.AdvanceUntil(() => alice.FindPlayer("bob") != null));
        }

        [Fact]
        public void ADimensionNeverRemovesAnEntityFromTheServerWorld()
        {
            // The central constraint, applied to the second replication filter the
            // project has. SERVER WORLD STATE = FULL.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            for (uint dimension = 1; dimension <= 5; dimension++)
            {
                var far = new VehicleEntity(harness.Server.World.AllocateEntityId())
                {
                    Position = new NetVector3(100f * dimension, 0f, 30f),
                    ModelHash = 0x0BBA91E1,
                    Dimension = dimension,
                    EngineHealth = 1000f,
                };

                harness.Server.World.Spawn(far);
            }

            harness.Advance(2d);

            // Invisible to the only player, and every one of them still in the world.
            Assert.Equal(0, alice.Client.RemoteEntities.VehicleCount);
            Assert.Equal(6, harness.Server.World.EntityCount);
        }
    }
}
