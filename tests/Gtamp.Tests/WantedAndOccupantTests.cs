using Gtamp.Client.Core;
using Gtamp.Server.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Protocol;
using Gtamp.Shared.Security;
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

        /// <summary>
        /// A model the server sets reaches the player's own game.
        /// <para>
        /// The same hole as the wanted level, one field over: the client reported its
        /// model, the server took it, persisted it and replicated it, and a model the
        /// server set itself — a restored save, a mod handing out a skin — was
        /// overwritten by the client's next update without ever reaching the game. Other
        /// players saw the skin; the player wearing it did not.
        /// </para>
        /// </summary>
        [Fact]
        public void AModelTheServerSetsReachesThePlayersOwnGame()
        {
            using var harness = new TestHarness();
            TestClient player = Join(harness, "skin");
            Assert.True(harness.AdvanceUntil(() => player.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));
            harness.Advance(1d);

            const uint Skin = 0x9C9EFFD8u;
            Assert.True(harness.Server.Players.TryGetByPlayerId(
                player.Client.LocalPlayerId, out PlayerSession session));
            Assert.True(harness.Server.SetPlayerModel(session, Skin));

            Assert.True(harness.AdvanceUntil(() => player.Bridge.LocalModelHash == Skin));
            Assert.Equal(1, player.Client.ModelChangesApplied);

            // And it stays: the client now reports the model it was given, and the
            // server's own value is not undone by the reports that were already in
            // flight when it was set.
            harness.Advance(1d);
            Assert.Equal(Skin, harness.Server.World.GetPlayer(player.Client.LocalEntityId)!.ModelHash);
        }

        /// <summary>
        /// A game that says "not now" is retried, not obeyed once and forgotten. The
        /// real one refuses while the player is in a vehicle or dead, and while the
        /// model is still streaming in.
        /// </summary>
        [Fact]
        public void AModelChangeTheGameRefusesIsRetried()
        {
            using var harness = new TestHarness();
            TestClient player = Join(harness, "skin");
            Assert.True(harness.AdvanceUntil(() => player.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));
            harness.Advance(1d);

            const uint Skin = 0x9C9EFFD8u;
            player.Bridge.ModelChangeRefusals = 3;

            Assert.True(harness.Server.Players.TryGetByPlayerId(
                player.Client.LocalPlayerId, out PlayerSession session));
            Assert.True(harness.Server.SetPlayerModel(session, Skin));

            Assert.True(harness.AdvanceUntil(() => player.Bridge.LocalModelHash == Skin, timeoutSeconds: 5d));
            Assert.True(player.Bridge.ModelChangeAttempts >= 4);
            Assert.Equal(0, player.Client.ModelChangesRefused);
        }

        /// <summary>
        /// And a model that never applies is given up on out loud rather than retried
        /// for the rest of the session. A skin this client does not have is a missing
        /// mod, and the player is the last person who should have to guess.
        /// </summary>
        [Fact]
        public void AModelThatNeverAppliesIsGivenUpOnAndReported()
        {
            using var harness = new TestHarness();
            TestClient player = Join(harness, "skin");
            Assert.True(harness.AdvanceUntil(() => player.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));
            harness.Advance(1d);

            player.Bridge.ModelChangeRefusals = int.MaxValue;

            Assert.True(harness.Server.Players.TryGetByPlayerId(
                player.Client.LocalPlayerId, out PlayerSession session));
            Assert.True(harness.Server.SetPlayerModel(session, 0x9C9EFFD8u));

            Assert.True(harness.AdvanceUntil(() => player.Client.ModelChangesRefused > 0, timeoutSeconds: 20d));
            Assert.Equal(0u, player.Bridge.LocalModelHash);

            int attempts = player.Bridge.ModelChangeAttempts;
            harness.Advance(5d);

            // Given up on means given up on: no more attempts after the warning.
            Assert.Equal(attempts, player.Bridge.ModelChangeAttempts);
        }

        /// <summary>
        /// A game whose maximum health disagrees with the server's is brought into
        /// line, not banned for it.
        /// <para>
        /// `MaxHealth` was on the character entity from the start, replicated in every
        /// snapshot, restored from every save, and applied to the game by nothing —
        /// while the anti-cheat measured every reported health against it. A player
        /// whose game says 300, which a mod raising maximum health makes ordinary in an
        /// LSPDFR install, reported 300 against a ceiling of 200 and tripped
        /// <c>HealthHack</c> on every update once the join grace ran out: at `Strict`,
        /// a kick for having a mod installed.
        /// </para>
        /// </summary>
        [Fact]
        public void AGameWithADifferentMaximumHealthIsBroughtIntoLine()
        {
            using var harness = new TestHarness();
            TestClient player = Join(harness, "modded");

            // Their game has a mod that raises maximum health, and they are at it.
            player.Bridge.Sample.MaxHealth = 300;
            player.Bridge.Sample.Health = 300;

            Assert.True(harness.AdvanceUntil(() => player.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));

            // The server's ceiling reaches the game, and the health above it comes down
            // with it — otherwise the next report is still over a maximum it no longer
            // has.
            Assert.True(harness.AdvanceUntil(() => player.Bridge.LocalMaxHealth == 200));
            Assert.Equal(200, player.Bridge.Sample.MaxHealth);
            Assert.True(player.Bridge.Sample.Health <= 200);

            harness.Advance(2d);

            // And no violation was recorded for it: the ceiling arrived inside the
            // join grace, which is exactly what that grace is for.
            Assert.True(harness.Server.Players.TryGetByPlayerId(
                player.Client.LocalPlayerId, out PlayerSession session));
            Assert.False(session.PendingRemoval);
            Assert.Equal(200, harness.Server.World.GetPlayer(player.Client.LocalEntityId)!.MaxHealth);
        }

        /// <summary>
        /// The failure this prevents, shown directly rather than described: the
        /// anti-cheat measures reported health against the server's maximum, so health
        /// the client's own game considers normal is a `HealthHack` violation the moment
        /// the two disagree.
        /// </summary>
        [Fact]
        public void HealthAboveTheServersMaximumIsAViolation()
        {
            var engine = new AntiCheatEngine(new AntiCheatSettings { Level = AntiCheatLevel.Standard });
            var entity = new PlayerEntity(new EntityId(1))
            {
                Position = new NetVector3(220f, -800f, 30f),
                Health = 200,
                MaxHealth = 200,
            };

            var state = new PlayerValidationState();
            var proposal = new PlayerStateProposal
            {
                Position = entity.Position,
                Velocity = NetVector3.Zero,
                AimPosition = entity.Position,
                Heading = 0f,
                Health = 300,
                Armor = 0,
            };

            ValidationOutcome outcome = engine.ValidatePlayerState(entity, proposal, state, now: 100d);

            Assert.False(outcome.Accepted);
            Assert.Contains(outcome.Violations, v => v.Kind == ViolationKind.HealthHack);
        }

        /// <summary>
        /// And it is applied once, not every frame: it is a ceiling, not a per-frame
        /// command, and a native call twenty times a second for a number that does not
        /// move is how a client's frame budget disappears.
        /// </summary>
        [Fact]
        public void TheMaximumIsAppliedOnChangeOnly()
        {
            using var harness = new TestHarness();
            TestClient player = Join(harness, "modded");
            player.Bridge.Sample.MaxHealth = 300;

            Assert.True(harness.AdvanceUntil(() => player.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Bridge.LocalMaxHealth == 200));

            int applications = player.Bridge.MaxHealthApplications;
            harness.Advance(2d);

            Assert.Equal(applications, player.Bridge.MaxHealthApplications);
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
