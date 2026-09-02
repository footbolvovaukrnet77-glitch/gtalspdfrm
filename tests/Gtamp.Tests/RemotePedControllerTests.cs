using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    public class RemotePedControllerTests
    {
        private static RemotePedFrame Frame(
            NetVector3 position,
            MovementState movement = MovementState.Idle,
            PlayerFlags flags = PlayerFlags.None,
            int health = 200,
            NetVector3 velocity = default) => new RemotePedFrame
        {
            Position = position,
            Velocity = velocity,
            Heading = 90f,
            Health = health,
            Armor = 0,
            Flags = flags,
            Movement = movement,
            AimPosition = position,
        };

        [Fact]
        public void AStationaryPlayerAtItsTargetIsIdle()
        {
            RemotePedFrame frame = Frame(new NetVector3(10f, 10f, 30f));
            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.Idle, command.Action);
            Assert.Equal(0f, command.MoveBlendRatio);
            Assert.False(command.IsMoving);
        }

        [Theory]
        [InlineData(MovementState.Walk, RemotePedAction.Walk, 1f)]
        [InlineData(MovementState.Run, RemotePedAction.Run, 2f)]
        [InlineData(MovementState.Sprint, RemotePedAction.Sprint, 3f)]
        public void TheGaitMapsToTheGameMoveBlendRatio(MovementState movement, RemotePedAction action, float blend)
        {
            RemotePedFrame frame = Frame(
                new NetVector3(15f, 10f, 30f), movement, velocity: new NetVector3(5f, 0f, 0f));

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.Equal(action, command.Action);
            Assert.Equal(blend, command.MoveBlendRatio);
            Assert.False(command.HardCorrect);
        }

        [Fact]
        public void ASmallErrorIsWalkedOffRatherThanTeleported()
        {
            RemotePedFrame frame = Frame(
                new NetVector3(12f, 10f, 30f), MovementState.Walk, velocity: new NetVector3(2f, 0f, 0f));

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.False(command.HardCorrect);
            Assert.Equal(RemotePedAction.Walk, command.Action);
        }

        [Fact]
        public void ALargeErrorIsCorrectedOutright()
        {
            RemotePedFrame frame = Frame(
                new NetVector3(60f, 10f, 30f), MovementState.Run, velocity: new NetVector3(6f, 0f, 0f));

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.True(command.HardCorrect);

            // Still tasked to run afterwards, so it does not arrive and freeze.
            Assert.Equal(RemotePedAction.Run, command.Action);
        }

        [Fact]
        public void AStaleIdleFlagStillWalksWhenThePedIsClearlyBehind()
        {
            // The gait says idle but the ped is three metres from where the player is.
            // Trusting the flag would leave it standing still until the next snapshot.
            RemotePedFrame frame = Frame(new NetVector3(13f, 10f, 30f), MovementState.Idle);
            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.Walk, command.Action);
        }

        [Fact]
        public void AStaleSprintFlagWithNoSpeedAndNoDistanceGoesIdle()
        {
            // The player stopped; the flag has not caught up. Sprinting on the spot
            // is the visible artefact this avoids.
            RemotePedFrame frame = Frame(
                new NetVector3(10.2f, 10f, 30f), MovementState.Sprint, velocity: NetVector3.Zero);

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.Idle, command.Action);
        }

        [Fact]
        public void ARagdollingPlayerIsHandedToPhysicsAndNotCorrected()
        {
            RemotePedFrame frame = Frame(
                new NetVector3(11f, 10f, 30f), MovementState.Idle, PlayerFlags.Ragdoll);

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.Ragdoll, command.Action);

            // Placing a ragdoll fights the physics solver, which is exactly the
            // twitching the ragdoll flag exists to avoid. A metre of drift is what
            // RagdollDriver's impulses are for.
            Assert.False(command.HardCorrect);
            Assert.Equal(0f, command.MoveBlendRatio);
        }

        [Fact]
        public void ARagdollThatHasDivergedEntirelyIsStillPlaced()
        {
            // Thirty metres apart, the two machines are no longer describing the same
            // fall. This case used to be left to physics on the grounds that ragdolls
            // are never corrected, which meant a body could stay in the wrong street
            // until it stood up again.
            RemotePedFrame frame = Frame(
                new NetVector3(40f, 10f, 30f), MovementState.Idle, PlayerFlags.Ragdoll);

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.Ragdoll, command.Action);
            Assert.True(command.HardCorrect);
        }

        [Fact]
        public void ADeadPlayerIsPlacedAndNotTasked()
        {
            RemotePedFrame frame = Frame(
                new NetVector3(10f, 10f, 30f), MovementState.Idle, PlayerFlags.Dead, health: 0);

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.Dead, command.Action);
            Assert.True(command.HardCorrect);
            Assert.Equal(0, command.Health);
        }

        [Fact]
        public void ZeroHealthCountsAsDeadEvenWithoutTheFlag()
        {
            RemotePedFrame frame = Frame(new NetVector3(10f, 10f, 30f), health: 0);
            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.Dead, command.Action);
        }

        [Fact]
        public void APlayerInAVehicleIsHeldRatherThanWalked()
        {
            RemotePedFrame frame = Frame(
                new NetVector3(200f, 10f, 30f), MovementState.Idle, PlayerFlags.InVehicle);

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.Equal(RemotePedAction.InVehicle, command.Action);
            Assert.True(command.HardCorrect);
            Assert.Equal(0f, command.MoveBlendRatio);
        }

        [Fact]
        public void AimingIsPassedThroughWhileMoving()
        {
            RemotePedFrame frame = Frame(
                new NetVector3(15f, 10f, 30f),
                MovementState.Walk,
                PlayerFlags.Aiming,
                velocity: new NetVector3(2f, 0f, 0f));

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));

            Assert.True(command.Aiming);
            Assert.Equal(RemotePedAction.Walk, command.Action);
        }

        [Fact]
        public void DeathTakesPrecedenceOverRagdoll()
        {
            RemotePedFrame frame = Frame(
                new NetVector3(10f, 10f, 30f),
                MovementState.Idle,
                PlayerFlags.Dead | PlayerFlags.Ragdoll,
                health: 0);

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(10f, 10f, 30f));
            Assert.Equal(RemotePedAction.Dead, command.Action);
        }
    }
}
