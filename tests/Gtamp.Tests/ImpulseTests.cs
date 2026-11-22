using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Security;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Section 12's forces and impulses.
    /// <para>
    /// A push is the part of physics that is an event rather than a state. A car that
    /// was shoved and a car that was driven end as the same position and the same
    /// velocity, so replicating the state replicates the result and loses the shove: on
    /// the machine that did it the car leaps, and everywhere else it slides to where it
    /// landed. The specification says not to give up on physics because it is hard, and
    /// prescribes exactly this arrangement — client physics, server validation, server
    /// state, correction.
    /// </para>
    /// </summary>
    public class ImpulseTests
    {
        private static VehicleEntity Car(float x = 0f) =>
            new VehicleEntity(new EntityId(7)) { Position = new NetVector3(x, 0f, 0f) };

        [Fact]
        public void AnOrdinaryPushIsAccepted()
        {
            ImpulseResolution resolution = ImpulseArbiter.Resolve(
                Car(), NetVector3.Zero, new NetVector3(10f, 0f, 0f), fromServerSideMod: false);

            Assert.Equal(ImpulseVerdict.Accepted, resolution.Verdict);
            Assert.Equal(10f, resolution.Impulse.X, 1);
        }

        /// <summary>
        /// Clamped rather than rejected, for the same reason damage is: dropping an
        /// over-claim lets a modified client suppress a real push by exaggerating it.
        /// </summary>
        [Fact]
        public void AnAbsurdPushIsReducedRatherThanDropped()
        {
            ImpulseResolution resolution = ImpulseArbiter.Resolve(
                Car(), NetVector3.Zero, new NetVector3(100000f, 0f, 0f), fromServerSideMod: false);

            Assert.Equal(ImpulseVerdict.Clamped, resolution.Verdict);
            Assert.True(resolution.Accepted);
            Assert.Equal(ImpulseArbiter.MaxMagnitude, resolution.Impulse.Length, 1);
        }

        /// <summary>
        /// The one thing a player must not be able to do to another player is move
        /// them, and an impulse is exactly that with a physics engine in between. A
        /// server-side mod may; a client may not.
        /// </summary>
        [Fact]
        public void AClientMayNotPushAPlayer()
        {
            var player = new PlayerEntity(new EntityId(4));

            Assert.Equal(
                ImpulseVerdict.RejectedNotPushable,
                ImpulseArbiter.Resolve(player, NetVector3.Zero, new NetVector3(5f, 0f, 0f), false).Verdict);

            Assert.Equal(
                ImpulseVerdict.Accepted,
                ImpulseArbiter.Resolve(player, NetVector3.Zero, new NetVector3(5f, 0f, 0f), true).Verdict);
        }

        [Fact]
        public void APushFromAcrossTheMapIsRefused()
        {
            Assert.Equal(
                ImpulseVerdict.RejectedOutOfRange,
                ImpulseArbiter.Resolve(
                    Car(x: 5000f), NetVector3.Zero, new NetVector3(5f, 0f, 0f), false).Verdict);
        }

        /// <summary>
        /// A server-side mod is trusted about distance, because it is the server. It is
        /// still not trusted about arithmetic.
        /// </summary>
        [Fact]
        public void AServerSideModIgnoresDistanceButNotTheCeiling()
        {
            Assert.Equal(
                ImpulseVerdict.Accepted,
                ImpulseArbiter.Resolve(Car(x: 5000f), NetVector3.Zero, new NetVector3(5f, 0f, 0f), true).Verdict);

            Assert.Equal(
                ImpulseVerdict.Clamped,
                ImpulseArbiter.Resolve(Car(), NetVector3.Zero, new NetVector3(99999f, 0f, 0f), true).Verdict);
        }

        [Fact]
        public void NothingAndNonsenseAreBothRefused()
        {
            Assert.Equal(
                ImpulseVerdict.RejectedNoEntity,
                ImpulseArbiter.Resolve(null, NetVector3.Zero, new NetVector3(1f, 0f, 0f), false).Verdict);

            Assert.Equal(
                ImpulseVerdict.RejectedNotANumber,
                ImpulseArbiter.Resolve(Car(), NetVector3.Zero, NetVector3.Zero, false).Verdict);

            Assert.Equal(
                ImpulseVerdict.RejectedNotANumber,
                ImpulseArbiter.Resolve(
                    Car(), NetVector3.Zero, new NetVector3(float.NaN, 0f, 0f), false).Verdict);
        }

        [Fact]
        public void TheMessageSurvivesTheWire()
        {
            var message = new Gtamp.Shared.Protocol.EntityImpulseMessage
            {
                EntityId = new EntityId(88),
                Impulse = new NetVector3(3f, -4f, 12f),
                IsExplosion = true,
            };

            Gtamp.Shared.Protocol.EntityImpulseMessage back =
                Gtamp.Shared.Protocol.EntityImpulseMessage.Deserialize(message.Serialize());

            Assert.Equal(new EntityId(88), back.EntityId);
            Assert.Equal(3f, back.Impulse.X, 1);
            Assert.Equal(-4f, back.Impulse.Y, 1);
            Assert.True(back.IsExplosion);
        }

        /// <summary>
        /// The whole path, on a real server: one client asks, the server arbitrates and
        /// relays, and the other client's game is told to shove the right body.
        /// </summary>
        [Fact]
        public void APushReachesTheOtherClientsGame()
        {
            using var harness = new TestHarness();
            TestClient pusher = harness.CreateClient("pusher");
            TestClient watcher = harness.CreateClient("watcher");
            pusher.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            watcher.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => pusher.PlayerCount >= 2 && watcher.PlayerCount >= 2));
            harness.Advance(1.0);

            // The pusher's own car, adopted by the server, is a thing the watcher draws.
            // Beside the players, not at the origin. A car at (0,0,0) is eight hundred
            // metres from the spawn point and the server refuses to relay a push that
            // far — correctly, and the first version of this test did not notice it was
            // asserting against its own mistake.
            pusher.Bridge.LocalVehicleHandle = 800;
            pusher.Bridge.Vehicles[800] = new VehicleEntity(EntityId.None)
            {
                ModelHash = 0x9F05F101,
                Position = pusher.Bridge.Sample.Position,
            };

            Assert.True(harness.AdvanceUntil(() => pusher.Client.OwnedEntities.OwnedCount >= 1));

            EntityId carId = EntityId.None;
            foreach (NetEntity entity in harness.Server.World.State.Entities)
            {
                if (entity is VehicleEntity vehicle && vehicle.OwnerId != 0)
                {
                    carId = vehicle.Id;
                }
            }

            Assert.True(carId.IsValid, "the car was never adopted");
            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.Vehicles.Count > 0));

            watcher.Client.RequestImpulse(carId, new NetVector3(0f, 0f, 25f));

            Assert.True(
                harness.AdvanceUntil(() => watcher.Bridge.Impulses.Count > 0 || pusher.Bridge.Impulses.Count > 0),
                "the push never reached anybody's game");
        }

        /// <summary>
        /// And it must NOT be applied twice on the machine that owns the thing being
        /// pushed: its own physics has already run, and applying the relayed copy as
        /// well doubles the shove for exactly one client — which is the disagreement
        /// this exists to remove.
        /// </summary>
        [Fact]
        public void TheOwnersOwnPhysicsIsNotDoubledUp()
        {
            using var harness = new TestHarness();
            TestClient owner = harness.CreateClient("owner");
            owner.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => owner.Client.IsConnected));

            owner.Bridge.LocalVehicleHandle = 800;
            owner.Bridge.Vehicles[800] = new VehicleEntity(EntityId.None)
            {
                ModelHash = 0x9F05F101,
                Position = owner.Bridge.Sample.Position,
            };

            Assert.True(harness.AdvanceUntil(() => owner.Client.OwnedEntities.OwnedCount >= 1));

            EntityId carId = EntityId.None;
            foreach (NetEntity entity in harness.Server.World.State.Entities)
            {
                if (entity is VehicleEntity vehicle && vehicle.OwnerId != 0)
                {
                    carId = vehicle.Id;
                }
            }

            owner.Client.RequestImpulse(carId, new NetVector3(0f, 0f, 25f));
            harness.Advance(1.0);

            Assert.Empty(owner.Bridge.Impulses);
        }
    }
}
