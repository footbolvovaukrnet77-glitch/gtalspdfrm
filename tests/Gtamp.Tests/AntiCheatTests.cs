using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Security;
using Xunit;

namespace Gtamp.Tests
{
    public class AntiCheatTests
    {
        private static PlayerEntity Player() => new PlayerEntity(new EntityId(1))
        {
            Position = new NetVector3(0f, 0f, 30f),
            Health = 150,
            MaxHealth = 200,
            Armor = 0,
        };

        private static PlayerStateProposal Proposal(NetVector3 position, int health = 150, int armor = 0) =>
            new PlayerStateProposal { Position = position, Health = health, Armor = armor };

        /// <summary>The same, reported from a vehicle, where the speed limit is the higher one.</summary>
        private static PlayerStateProposal InVehicle(NetVector3 position) =>
            new PlayerStateProposal { Position = position, Health = 150, Armor = 0, InVehicle = true };

        [Fact]
        public void NormalMovementIsAccepted()
        {
            var engine = new AntiCheatEngine();
            PlayerEntity player = Player();
            var state = new PlayerValidationState();

            double now = 0;
            for (int i = 0; i < 100; i++)
            {
                now += 1d / 30d;
                var next = new NetVector3(player.Position.X + 0.2f, 0f, 30f);
                ValidationOutcome outcome = engine.ValidatePlayerState(player, Proposal(next), state, now);

                Assert.True(outcome.Accepted, outcome.Violations.Count > 0 ? outcome.Violations[0].ToString() : "rejected");
                player.Position = next;
            }
        }

        [Fact]
        public void BunchedUpdatesAfterJitterAreNotMistakenForSpeedHacking()
        {
            // Two updates arriving in the same millisecond is normal on a jittery
            // link. The movement budget must absorb it.
            var engine = new AntiCheatEngine();
            PlayerEntity player = Player();
            var state = new PlayerValidationState();

            engine.ValidatePlayerState(player, Proposal(player.Position), state, 1.0);

            // A quiet second banks budget, then four updates land at once.
            double now = 2.0;
            for (int i = 0; i < 4; i++)
            {
                var next = new NetVector3(player.Position.X + 0.4f, 0f, 30f);
                ValidationOutcome outcome = engine.ValidatePlayerState(player, Proposal(next), state, now);
                Assert.True(outcome.Accepted);
                player.Position = next;
            }
        }

        [Fact]
        public void SustainedImpossibleSpeedIsRejected()
        {
            var engine = new AntiCheatEngine();
            PlayerEntity player = Player();
            var state = new PlayerValidationState();

            bool rejected = false;
            double now = 0;
            for (int i = 0; i < 60; i++)
            {
                now += 1d / 30d;

                // 60 m/s on foot: four times the sustained limit.
                var next = new NetVector3(player.Position.X + 2f, 0f, 30f);
                ValidationOutcome outcome = engine.ValidatePlayerState(player, Proposal(next), state, now);
                if (!outcome.Accepted)
                {
                    rejected = true;
                    Assert.Equal(ViolationKind.SpeedHack, outcome.Violations[0].Kind);
                    break;
                }

                player.Position = next;
            }

            Assert.True(rejected, "sustained 60 m/s on foot should have been rejected");
        }

        [Fact]
        public void TeleportIsRejected()
        {
            var engine = new AntiCheatEngine();
            var state = new PlayerValidationState();
            ValidationOutcome outcome = engine.ValidatePlayerState(
                Player(), Proposal(new NetVector3(5000f, 5000f, 30f)), state, 1.0);

            Assert.False(outcome.Accepted);
            Assert.Equal(ViolationKind.Teleport, outcome.Violations[0].Kind);
        }

        /// <summary>
        /// A frame the game spends streaming is not a teleport.
        /// <para>
        /// The gate was a flat 75 m regardless of how long it had been. At the vehicle
        /// limit a player covers about 71 m per second, so a hitch of a second and a bit
        /// — which GTA V does routinely — was called a teleport and the update rejected.
        /// The server's position then stopped advancing, so the next report was further
        /// still and was rejected too: once a player got ahead they could never report
        /// again. A real session shows it as a steady 141 m disagreement while driving.
        /// </para>
        /// </summary>
        [Fact]
        public void AHitchWhileDrivingIsNotATeleport()
        {
            var engine = new AntiCheatEngine();
            PlayerEntity player = Player();
            var state = new PlayerValidationState();

            // Settle in, in a vehicle, so the movement budget is the vehicle one.
            double now = 0d;
            for (int i = 0; i < 10; i++)
            {
                now += 1d / 30d;
                var step = new NetVector3(player.Position.X + 2f, 0f, 30f);
                Assert.True(engine.ValidatePlayerState(
                    player, InVehicle(step), state, now).Accepted);
                player.Position = step;
            }

            // A second and a half of silence, then a report from where honest driving
            // would have put them.
            now += 1.5d;
            var afterHitch = new NetVector3(player.Position.X + 100f, 0f, 30f);
            ValidationOutcome outcome = engine.ValidatePlayerState(
                player, InVehicle(afterHitch), state, now);

            Assert.True(
                outcome.Accepted,
                outcome.Violations.Count > 0 ? outcome.Violations[0].ToString() : "rejected");
        }

        /// <summary>
        /// And a real teleport is still a teleport, however long the client waits: the
        /// movement budget is capped, so patience does not buy distance.
        /// </summary>
        [Fact]
        public void WaitingDoesNotBuyATeleport()
        {
            var engine = new AntiCheatEngine();
            var state = new PlayerValidationState();
            PlayerEntity player = Player();

            double now = 1d;
            Assert.True(engine.ValidatePlayerState(player, InVehicle(new NetVector3(1f, 0f, 30f)), state, now).Accepted);

            // A full minute of waiting, then a jump across the map.
            now += 60d;
            ValidationOutcome outcome = engine.ValidatePlayerState(
                player, InVehicle(new NetVector3(5000f, 5000f, 30f)), state, now);

            Assert.False(outcome.Accepted);
            Assert.Equal(ViolationKind.Teleport, outcome.Violations[0].Kind);
        }

        [Fact]
        public void HealthAboveTheMaximumIsRejected()
        {
            var engine = new AntiCheatEngine();
            var state = new PlayerValidationState();
            ValidationOutcome outcome = engine.ValidatePlayerState(
                Player(), Proposal(new NetVector3(0f, 0f, 30f), health: 5000), state, 1.0);

            Assert.False(outcome.Accepted);
            Assert.Equal(ViolationKind.HealthHack, outcome.Violations[0].Kind);
        }

        [Fact]
        public void ImplausibleHealthRegenerationIsRejectedAtStandardAndAbove()
        {
            var engine = new AntiCheatEngine(new AntiCheatSettings { Level = AntiCheatLevel.Standard });
            PlayerEntity player = Player();
            var state = new PlayerValidationState();

            engine.ValidatePlayerState(player, Proposal(player.Position), state, 1.0);
            ValidationOutcome outcome = engine.ValidatePlayerState(
                player, Proposal(player.Position, health: 200), state, 1.02);

            Assert.False(outcome.Accepted);
            Assert.Equal(ViolationKind.HealthHack, outcome.Violations[0].Kind);
        }

        [Fact]
        public void ArmorAboveTheCapIsRejected()
        {
            var engine = new AntiCheatEngine();
            var state = new PlayerValidationState();
            ValidationOutcome outcome = engine.ValidatePlayerState(
                Player(), Proposal(new NetVector3(0f, 0f, 30f), armor: 500), state, 1.0);

            Assert.False(outcome.Accepted);
            Assert.Equal(ViolationKind.ArmorHack, outcome.Violations[0].Kind);
        }

        [Fact]
        public void GodModeIsOnlyFlaggedAtStrict()
        {
            var proposal = new PlayerStateProposal
            {
                Position = new NetVector3(0f, 0f, 30f),
                Health = 150,
                Invincible = true,
            };

            var standard = new AntiCheatEngine(new AntiCheatSettings { Level = AntiCheatLevel.Standard });
            Assert.True(standard.ValidatePlayerState(Player(), proposal, new PlayerValidationState(), 1.0).Accepted);

            var strict = new AntiCheatEngine(new AntiCheatSettings { Level = AntiCheatLevel.Strict });
            ValidationOutcome outcome = strict.ValidatePlayerState(Player(), proposal, new PlayerValidationState(), 1.0);
            Assert.False(outcome.Accepted);
            Assert.Equal(ViolationKind.GodMode, outcome.Violations[0].Kind);
        }

        [Fact]
        public void ProtocolGuardsStayOnEvenWithAntiCheatOff()
        {
            var engine = new AntiCheatEngine(new AntiCheatSettings { Level = AntiCheatLevel.Off });
            var state = new PlayerValidationState();

            ValidationOutcome nan = engine.ValidatePlayerState(
                Player(), Proposal(new NetVector3(float.NaN, 0f, 0f)), state, 1.0);
            Assert.False(nan.Accepted);
            Assert.Equal(ViolationKind.InvalidPosition, nan.Violations[0].Kind);

            ValidationOutcome outOfBounds = engine.ValidatePlayerState(
                Player(), Proposal(new NetVector3(1e9f, 0f, 0f)), state, 1.0);
            Assert.False(outOfBounds.Accepted);
            Assert.Equal(ViolationKind.InvalidPosition, outOfBounds.Violations[0].Kind);
        }

        [Fact]
        public void TeleportingIsAllowedWithAntiCheatOff()
        {
            var engine = new AntiCheatEngine(new AntiCheatSettings { Level = AntiCheatLevel.Off });
            ValidationOutcome outcome = engine.ValidatePlayerState(
                Player(), Proposal(new NetVector3(3000f, 3000f, 30f)), new PlayerValidationState(), 1.0);

            Assert.True(outcome.Accepted);
        }

        [Fact]
        public void PacketFloodingIsThrottledAtEveryLevel()
        {
            var engine = new AntiCheatEngine(new AntiCheatSettings { Level = AntiCheatLevel.Off });
            var state = new PlayerValidationState();
            PlayerEntity player = Player();

            bool throttled = false;
            for (int i = 0; i < 500; i++)
            {
                ValidationOutcome outcome = engine.ValidatePlayerState(player, Proposal(player.Position), state, 1.0);
                if (!outcome.Accepted && outcome.Violations[0].Kind == ViolationKind.PacketRate)
                {
                    throttled = true;
                    break;
                }
            }

            Assert.True(throttled, "500 updates in the same second should have been throttled");
        }

        [Fact]
        public void ActionsEscalateWithTheConfiguredLevel()
        {
            Assert.Equal(ViolationAction.Ignore, new AntiCheatSettings { Level = AntiCheatLevel.Off }.ActionFor(ViolationKind.SpeedHack));
            Assert.Equal(ViolationAction.Log, new AntiCheatSettings { Level = AntiCheatLevel.Basic }.ActionFor(ViolationKind.SpeedHack));
            Assert.Equal(ViolationAction.Warn, new AntiCheatSettings { Level = AntiCheatLevel.Standard }.ActionFor(ViolationKind.SpeedHack));
            Assert.Equal(ViolationAction.Kick, new AntiCheatSettings { Level = AntiCheatLevel.Strict }.ActionFor(ViolationKind.SpeedHack));

            var custom = new AntiCheatSettings { Level = AntiCheatLevel.Custom };
            custom.Actions[ViolationKind.SpeedHack] = ViolationAction.Ban;
            Assert.Equal(ViolationAction.Ban, custom.ActionFor(ViolationKind.SpeedHack));
        }

        [Fact]
        public void EscalationTriggersOnlyAfterTheViolationBudgetIsSpent()
        {
            var engine = new AntiCheatEngine(new AntiCheatSettings { Level = AntiCheatLevel.Strict, ViolationsBeforeEscalation = 3 });
            var state = new PlayerValidationState();

            Assert.False(engine.ShouldEscalate(state));
            for (int i = 0; i < 3; i++)
            {
                state.Count(ViolationKind.SpeedHack);
            }

            Assert.True(engine.ShouldEscalate(state));
        }
    }
}
