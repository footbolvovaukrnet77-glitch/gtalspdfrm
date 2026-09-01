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

        /// <summary>
        /// A wanted level the server sets reaches the game.
        /// <para>
        /// It was persisted, restored on connect, printed by the admin console — and
        /// applied to nothing. The client's very first update, from a fresh session at
        /// zero stars, overwrote the restored value before the snapshot carrying it had
        /// been written. The save file recorded three stars and handed back none.
        /// </para>
        /// </summary>
        [Fact]
        public void AWantedLevelTheServerSetsReachesTheGame()
        {
            using var harness = new TestHarness();
            TestClient player = Join(harness, "suspect");
            Assert.True(harness.AdvanceUntil(() => player.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));

            Assert.True(harness.Server.Players.TryGetByPlayerId(
                player.Client.LocalPlayerId, out PlayerSession session));
            Assert.True(harness.Server.SetWantedLevel(session, 3));

            Assert.True(harness.AdvanceUntil(() => player.Bridge.LocalWantedLevel == 3));
            Assert.True(player.Client.WantedLevelCorrectionsApplied > 0);
        }

        /// <summary>
        /// And it stays set. The client reports twenty times a second from a game that
        /// still says zero; without the hold, the server's own value survives for less
        /// than one update.
        /// </summary>
        [Fact]
        public void TheServersWantedLevelIsNotOverwrittenByTheClientsNextReport()
        {
            using var harness = new TestHarness();
            TestClient player = Join(harness, "suspect");
            Assert.True(harness.AdvanceUntil(() => player.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));

            Assert.True(harness.Server.Players.TryGetByPlayerId(
                player.Client.LocalPlayerId, out PlayerSession session));
            Assert.True(harness.Server.SetWantedLevel(session, 4));

            Assert.True(harness.AdvanceUntil(() => player.Bridge.LocalWantedLevel == 4));

            harness.Advance(1d);

            PlayerEntity? entity = harness.Server.World.GetPlayer(player.Client.LocalEntityId);
            Assert.NotNull(entity);
            Assert.Equal(4, entity!.WantedLevel);
        }

        /// <summary>
        /// An ordinary session never triggers it. The wanted level the local game owns
        /// is reported upward and echoed back, and echoing is not a disagreement: a
        /// client that re-applied it would fight its own game every snapshot.
        /// </summary>
        [Fact]
        public void AWantedLevelTheClientReportedIsNotAppliedBackToIt()
        {
            using var harness = new TestHarness();
            TestClient player = Join(harness, "citizen");
            Assert.True(harness.AdvanceUntil(() => player.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));

            // Let the session settle first: until a report of ours has been answered,
            // "the server changed it" and "the server has not heard us" look the same.
            harness.Advance(1d);
            int applicationsBefore = player.Bridge.LocalWantedLevelApplications;

            player.Bridge.Sample.WantedLevel = 2;
            harness.Advance(1d);

            PlayerEntity? entity = harness.Server.World.GetPlayer(player.Client.LocalEntityId);
            Assert.NotNull(entity);
            Assert.Equal(2, entity!.WantedLevel);

            // The client's own level was never written back to it, and the sample it
            // reports still says what the game says.
            Assert.Equal(applicationsBefore, player.Bridge.LocalWantedLevelApplications);
            Assert.Equal(2, player.Bridge.Sample.WantedLevel);
        }

        /// <summary>
        /// The other side of the same rule: the stars you were carrying in single
        /// player do not come with you.
        /// <para>
        /// At the moment of connecting the server has heard nothing from this client,
        /// so its own value — a restored save, or a clean zero — is the authoritative
        /// one, exactly as it is for position and health. Importing a wanted level from
        /// the client's own session would let anyone arrive on a server already at five
        /// stars, and would make a restored save lose to whatever the game happened to
        /// have on screen.
        /// </para>
        /// </summary>
        [Fact]
        public void TheStarsYouArriveWithAreClearedByTheServer()
        {
            using var harness = new TestHarness();
            TestClient player = Join(harness, "arrival");
            player.Bridge.Sample.WantedLevel = 3;

            Assert.True(harness.AdvanceUntil(() => player.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));
            Assert.True(harness.AdvanceUntil(() => player.Bridge.LocalWantedLevelApplications > 0));

            Assert.Equal(0, player.Bridge.LocalWantedLevel);

            harness.Advance(1d);
            PlayerEntity? entity = harness.Server.World.GetPlayer(player.Client.LocalEntityId);
            Assert.NotNull(entity);
            Assert.Equal(0, entity!.WantedLevel);
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
