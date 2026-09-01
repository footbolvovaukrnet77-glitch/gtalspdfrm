using Gtamp.Client.Core;
using Gtamp.Server.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Two more fields that were carried by everything and filled in by nobody.
    /// <para>
    /// `WantedLevel` was serialised, persisted, restored on reconnect and printed by
    /// the admin console — and never read from the game, so it was zero for every
    /// player in every session. `Occupants` was validated against a spam limit,
    /// compared, cloned and persisted, and never populated: nothing anywhere put a
    /// player in it.
    /// </para>
    /// </summary>
    public class WantedAndOccupantTests
    {
        private static TestClient Join(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            return client;
        }

        [Fact]
        public void AWantedLevelReachesEveryoneElse()
        {
            using var harness = new TestHarness();
            TestClient hunted = Join(harness, "hunted");
            TestClient watcher = Join(harness, "watcher");
            Assert.True(harness.AdvanceUntil(() => watcher.PlayerCount >= 2 && hunted.PlayerCount >= 2));

            hunted.Bridge.Sample.WantedLevel = 3;

            Assert.True(harness.AdvanceUntil(() => watcher.FindPlayer("hunted")?.WantedLevel == 3));
        }

        [Fact]
        public void AnImpossibleWantedLevelIsClamped()
        {
            // The wire carries a byte and GTA V has six levels, so an unclamped claim
            // is replicated and printed as whatever the client said.
            using var harness = new TestHarness();
            TestClient liar = Join(harness, "liar");
            Assert.True(harness.AdvanceUntil(() => liar.Client.IsConnected));

            liar.Bridge.Sample.WantedLevel = 200;

            Assert.True(harness.AdvanceUntil(() =>
                harness.Server.World.GetPlayer(liar.Client.LocalEntityId)?.WantedLevel == 5));
        }

        [Fact]
        public void TheWantedLevelSurvivesTheClientUpdate()
        {
            var sent = new ClientStateUpdateMessage { WantedLevel = 4, InteriorId = 77 };

            ClientStateUpdateMessage received = ClientStateUpdateMessage.Deserialize(sent.Serialize());

            Assert.Equal(4, received.WantedLevel);

            // The field sits between two others; a misordered read shifts everything
            // after it, so the neighbour is asserted too.
            Assert.Equal(77, received.InteriorId);
        }

        [Fact]
        public void ARiderAppearsInTheirVehiclesOccupantList()
        {
            using var harness = new TestHarness();
            TestClient rider = Join(harness, "rider");
            Assert.True(harness.AdvanceUntil(() => rider.Client.IsConnected));

            var car = new VehicleEntity(harness.Server.World.AllocateEntityId())
            {
                Position = new NetVector3(220f, -800f, 30f),
                ModelHash = 0x0BBA91E1,
                EngineHealth = 1000f,
            };
            harness.Server.World.Spawn(car);

            PlayerEntity? player = harness.Server.World.GetPlayer(rider.Client.LocalEntityId);
            Assert.NotNull(player);
            player!.VehicleId = car.Id;
            player.VehicleSeat = -1;
            harness.Server.World.Touch(player);

            Assert.True(harness.AdvanceUntil(() => car.Occupants.Count == 1));
            Assert.Equal(-1, car.Occupants[0].Seat);
            Assert.Equal(player.Id, car.Occupants[0].Occupant);
        }

        [Fact]
        public void LeavingTheVehicleEmptiesTheList()
        {
            using var harness = new TestHarness();
            TestClient rider = Join(harness, "rider");
            Assert.True(harness.AdvanceUntil(() => rider.Client.IsConnected));

            var car = new VehicleEntity(harness.Server.World.AllocateEntityId())
            {
                Position = new NetVector3(220f, -800f, 30f),
                ModelHash = 0x0BBA91E1,
                EngineHealth = 1000f,
            };
            harness.Server.World.Spawn(car);

            PlayerEntity? player = harness.Server.World.GetPlayer(rider.Client.LocalEntityId);
            player!.VehicleId = car.Id;
            player.VehicleSeat = 0;
            harness.Server.World.Touch(player);
            Assert.True(harness.AdvanceUntil(() => car.Occupants.Count == 1));

            player.VehicleId = EntityId.None;
            player.VehicleSeat = -2;
            harness.Server.World.Touch(player);

            // Rebuilt rather than patched, so a rider who leaves without saying so —
            // a disconnect, a kill, a seat the server never saw them take — cannot
            // leave a ghost in the list.
            Assert.True(harness.AdvanceUntil(() => car.Occupants.Count == 0));
        }

        [Fact]
        public void ASeatIsClaimedByOneOccupantAtATime()
        {
            var car = new VehicleEntity(new EntityId(1));

            car.SetOccupant(-1, new EntityId(10));
            car.SetOccupant(-1, new EntityId(11));

            Assert.Single(car.Occupants);
            Assert.Equal(new EntityId(11), car.Occupants[0].Occupant);
        }
    }
}
