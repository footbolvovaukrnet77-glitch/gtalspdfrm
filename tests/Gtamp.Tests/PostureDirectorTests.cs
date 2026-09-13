using Gtamp.Client.Players;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Jump, climb and parachute travelled on the wire from the first commit and were
    /// applied by nothing. These are about the half that is decidable without a game:
    /// when a one-shot task should be issued, and when issuing one would be worse than
    /// not.
    /// </summary>
    public class PostureDirectorTests
    {
        private const PlayerFlags Nothing = PlayerFlags.None;

        [Fact]
        public void AJumpIsIssuedOnceOnTheFrameTheFlagArrives()
        {
            Assert.Equal(
                PostureTask.Jump,
                PostureDirector.Decide(Nothing, PlayerFlags.Jumping, settled: true));
        }

        /// <summary>
        /// The defect that would have made applying these worse than leaving them:
        /// a one-shot task re-issued every frame restarts, and a task that restarts
        /// sixty times a second never plays at all.
        /// </summary>
        [Fact]
        public void AJumpIsNotIssuedAgainWhileTheFlagIsStillSet()
        {
            Assert.Equal(
                PostureTask.None,
                PostureDirector.Decide(PlayerFlags.Jumping, PlayerFlags.Jumping, settled: true));
        }

        [Fact]
        public void AJumpIsIssuedAgainAfterTheFlagHasCleared()
        {
            Assert.Equal(PostureTask.None, PostureDirector.Decide(PlayerFlags.Jumping, Nothing, settled: true));
            Assert.Equal(PostureTask.Jump, PostureDirector.Decide(Nothing, PlayerFlags.Jumping, settled: true));
        }

        [Fact]
        public void AClimbIsIssuedOnItsOwnTransition()
        {
            Assert.Equal(
                PostureTask.Climb,
                PostureDirector.Decide(Nothing, PlayerFlags.Climbing, settled: true));
        }

        /// <summary>A climb wins, because a ped climbing a wall is also leaving the ground.</summary>
        [Fact]
        public void AClimbAndAJumpArrivingTogetherProduceTheClimb()
        {
            Assert.Equal(
                PostureTask.Climb,
                PostureDirector.Decide(Nothing, PlayerFlags.Climbing | PlayerFlags.Jumping, settled: true));
        }

        /// <summary>
        /// A hard correction teleports the ped. A jump started on that frame is a jump
        /// from the wrong place, and by the time the ped has settled the player has
        /// finished jumping — so the transition is dropped rather than queued.
        /// </summary>
        [Fact]
        public void NothingIsIssuedWhileThePedIsBeingTeleported()
        {
            Assert.Equal(
                PostureTask.None,
                PostureDirector.Decide(Nothing, PlayerFlags.Jumping, settled: false));

            Assert.Equal(
                PostureTask.None,
                PostureDirector.Decide(Nothing, PlayerFlags.Climbing, settled: false));
        }

        [Fact]
        public void AParachuteOpensOnItsTransition()
        {
            Assert.Equal(
                PostureTask.OpenParachute,
                PostureDirector.Decide(Nothing, PlayerFlags.Parachuting, settled: true));
        }

        [Fact]
        public void NeitherAJumpNorAClimbIsIssuedUnderAnOpenCanopy()
        {
            PlayerFlags under = PlayerFlags.Parachuting | PlayerFlags.Jumping | PlayerFlags.Climbing;

            Assert.Equal(
                PostureTask.None,
                PostureDirector.Decide(PlayerFlags.Parachuting, under, settled: true));
        }

        /// <summary>
        /// Closing the canopy is the one transition that is applied mid-correction. A
        /// ped left under a parachute it no longer has drifts down a street it should
        /// be standing in, and a correction cannot fix that — the parachute is a task,
        /// not a position.
        /// </summary>
        [Fact]
        public void TheCanopyIsClearedEvenWhileThePedIsBeingTeleported()
        {
            Assert.Equal(
                PostureTask.EndParachute,
                PostureDirector.Decide(PlayerFlags.Parachuting, Nothing, settled: false));
        }

        [Fact]
        public void FallingIsNeverAppliedBecauseGravityAlreadyIs()
        {
            Assert.Equal(
                PostureTask.None,
                PostureDirector.Decide(Nothing, PlayerFlags.Falling, settled: true));
        }

        [Fact]
        public void SwimmingAndDivingAreRecognisedAsWater()
        {
            Assert.True(PostureDirector.IsInWater(PlayerFlags.Swimming));
            Assert.True(PostureDirector.IsInWater(PlayerFlags.Diving));
            Assert.False(PostureDirector.IsInWater(PlayerFlags.Sprinting));
        }
    }
}
