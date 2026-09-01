using Gtamp.Client.Core;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Putting a riding player in the seat they are replicated as occupying.
    /// <para>
    /// <c>VehicleId</c> and <c>VehicleSeat</c> were on every character from the start,
    /// and <c>SeatRemotePedInVehicle</c> was declared on the bridge interface and
    /// implemented against the game — and called by nothing at all. A passing car was
    /// drawn empty while its driver stood at the car's coordinates, sliding along the
    /// road beside it.
    /// </para>
    /// </summary>
    public class VehicleSeatingTests
    {
        private static RemotePedFrame Riding(EntityId vehicle, sbyte seat) => new RemotePedFrame
        {
            Position = new NetVector3(50f, 50f, 30f),
            Heading = 180f,
            Health = 200,
            Flags = PlayerFlags.InVehicle,
            VehicleId = vehicle,
            VehicleSeat = seat,
        };

        [Fact]
        public void ARiderIsSeatedRatherThanPlaced()
        {
            RemotePedFrame frame = Riding(new EntityId(4), -1);

            RemotePedCommand command = RemotePedController.Decide(in frame, NetVector3.Zero, vehicleHandle: 77);

            Assert.Equal(RemotePedAction.InVehicle, command.Action);
            Assert.Equal(77, command.VehicleHandle);
            Assert.Equal(-1, command.VehicleSeat);

            // Placing a ped that is sitting in a car moves it out of the seat, so a
            // seated ped must not also be corrected.
            Assert.False(command.HardCorrect);
        }

        [Fact]
        public void ARiderWithNoLocalCarIsStillPutSomewhereSensible()
        {
            // The car has not been created on this client yet, or its model is
            // missing. Holding the ped at the reported position looks wrong; leaving
            // it where it last was looks broken.
            RemotePedFrame frame = Riding(new EntityId(4), -1);

            RemotePedCommand command = RemotePedController.Decide(in frame, NetVector3.Zero, vehicleHandle: 0);

            Assert.Equal(RemotePedAction.InVehicle, command.Action);
            Assert.Equal(0, command.VehicleHandle);
            Assert.True(command.HardCorrect);
        }

        [Fact]
        public void AnUnseatedRiderIsNotSeatedByAccident()
        {
            // The in-vehicle flag with no seat index is a contradiction: -2 is the
            // value for "not in a vehicle", and seating into it would ask the game for
            // seat minus two.
            RemotePedFrame frame = Riding(new EntityId(4), -2);

            RemotePedCommand command = RemotePedController.Decide(in frame, NetVector3.Zero, vehicleHandle: 77);

            Assert.Equal(0, command.VehicleHandle);
            Assert.True(command.HardCorrect);
        }

        [Fact]
        public void APassengerSeatTravelsAsGivenAndIsNotNormalised()
        {
            RemotePedFrame frame = Riding(new EntityId(4), 2);

            RemotePedCommand command = RemotePedController.Decide(in frame, NetVector3.Zero, vehicleHandle: 77);

            Assert.Equal(2, command.VehicleSeat);
        }

        [Fact]
        public void AnOnFootPlayerCarriesNoSeat()
        {
            var frame = new RemotePedFrame
            {
                Position = new NetVector3(50f, 50f, 30f),
                Health = 200,
                Movement = MovementState.Walk,
                Velocity = new NetVector3(1.5f, 0f, 0f),
            };

            RemotePedCommand command = RemotePedController.Decide(in frame, new NetVector3(45f, 50f, 30f));

            Assert.NotEqual(RemotePedAction.InVehicle, command.Action);
            Assert.Equal(0, command.VehicleHandle);
        }

        [Fact]
        public void TheSeatSurvivesInterpolation()
        {
            // Discrete state comes from the newer sample; a seat index blended halfway
            // between the driver's seat and the back left is a seat nobody sits in.
            var player = new RemotePlayer(new EntityId(2), 2, "rider");

            player.Push(1d, new PlayerEntity(new EntityId(2))
            {
                Health = 200, VehicleId = new EntityId(9), VehicleSeat = -1, Flags = PlayerFlags.InVehicle,
            });
            player.Push(2d, new PlayerEntity(new EntityId(2))
            {
                Health = 200, VehicleId = new EntityId(9), VehicleSeat = 1, Flags = PlayerFlags.InVehicle,
            });

            Assert.True(player.TrySample(1.5d, out RemotePedFrame frame));
            Assert.Equal(new EntityId(9), frame.VehicleId);
            Assert.Equal(1, frame.VehicleSeat);
        }

        [Fact]
        public void ARiderInTheCarThisClientIsDrivingResolvesToItsHandle()
        {
            // The ordinary case, and the one a lookup restricted to replicated
            // vehicles would miss entirely: a passenger in your own car.
            using var harness = new TestHarness();
            TestClient driver = harness.CreateClient("driver");
            driver.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => driver.Client.IsConnected));

            Assert.NotNull(driver.Client.RemotePlayers.ResolveVehicleHandle);

            // Nothing owns entity 12345, so the resolver has nothing to offer — which
            // is the answer, not a crash.
            Assert.Equal(0, driver.Client.RemotePlayers.ResolveVehicleHandle!(new EntityId(12345)));
        }
    }
}
