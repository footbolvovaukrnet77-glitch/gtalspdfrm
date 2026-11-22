using Gtamp.Client.Entities;
using Gtamp.Client.Players;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Interpolating an orientation as one rotation rather than three angles.
    /// <para>
    /// Pitch, roll and yaw were blended independently. That is right for a car on a
    /// hill and wrong for one rolling onto its roof, because the three are not
    /// independent axes: blending each on its own passes through orientations on no
    /// path between the two ends, and an entity pitched near vertical loses an axis
    /// entirely — gimbal lock arriving in the interpolator rather than in the wire
    /// format, which round-trips exactly.
    /// </para>
    /// </summary>
    public class OrientationTests
    {
        private static void AssertAngle(float expected, float actual, float tolerance = 0.01f)
        {
            float difference = ((actual - expected) % 360f + 540f) % 360f - 180f;
            Assert.True(
                System.Math.Abs(difference) <= tolerance,
                $"expected {expected}, got {actual} ({difference} apart)");
        }

        [Fact]
        public void EulerSurvivesTheRoundTrip()
        {
            // The property everything else rests on: whatever axis order this uses,
            // converting out and back must return what went in, or every endpoint is
            // wrong rather than only the frames between them.
            foreach ((float pitch, float roll, float yaw) in new[]
            {
                (0f, 0f, 0f), (12f, -7f, 190f), (-45f, 30f, 359f), (89f, 0f, 45f), (-89f, 12f, 271f),
            })
            {
                NetQuaternion.FromEuler(pitch, roll, yaw).ToEuler(out float p, out float r, out float y);
                AssertAngle(pitch, p);
                AssertAngle(roll, r);
                AssertAngle(yaw, y);
            }
        }

        [Fact]
        public void TheEndpointsAreExact()
        {
            NetQuaternion.LerpEuler(10f, 20f, 30f, 40f, 50f, 60f, 0f,
                out float p0, out float r0, out float y0);
            Assert.Equal(10f, p0);
            Assert.Equal(20f, r0);
            Assert.Equal(30f, y0);

            NetQuaternion.LerpEuler(10f, 20f, 30f, 40f, 50f, 60f, 1f,
                out float p1, out float r1, out float y1);
            Assert.Equal(40f, p1);
            Assert.Equal(50f, r1);
            Assert.Equal(60f, y1);
        }

        [Fact]
        public void AYawOnlyTurnBlendsHalfway()
        {
            // The ordinary case, which the old per-axis blend also got right — kept so
            // a regression in the new path shows up on the common case, not only on
            // the exotic one.
            NetQuaternion.LerpEuler(0f, 0f, 0f, 0f, 0f, 90f, 0.5f,
                out float pitch, out float roll, out float yaw);

            AssertAngle(0f, pitch);
            AssertAngle(0f, roll);
            AssertAngle(45f, yaw);
        }

        [Fact]
        public void ATurnAcrossNorthTakesTheShortWay()
        {
            // 350 to 10 degrees is twenty degrees of turning, not three hundred and
            // forty. A quaternion pair that is not sign-corrected takes the long way.
            NetQuaternion.LerpEuler(0f, 0f, 350f, 0f, 0f, 10f, 0.5f,
                out _, out _, out float yaw);

            AssertAngle(0f, yaw);
        }

        [Fact]
        public void ARollOntoTheRoofDoesNotSwingTheNoseAround()
        {
            // The defect, made visible. Rolling 170 degrees while the yaw reads 0 at
            // one end and 180 at the other describes one continuous roll; blending the
            // axes separately spins the car about its vertical axis on the way.
            NetQuaternion.LerpEuler(0f, 170f, 0f, 0f, 190f, 0f, 0.5f,
                out float pitch, out float roll, out float yaw);

            // Halfway through a roll past vertical the game's own Euler triple
            // re-expresses itself, so the assertion is on the rotation, not on the
            // numbers: whatever it reports must convert back to the same orientation.
            NetQuaternion middle = NetQuaternion.FromEuler(pitch, roll, yaw);
            NetQuaternion expected = NetQuaternion.Slerp(
                NetQuaternion.FromEuler(0f, 170f, 0f), NetQuaternion.FromEuler(0f, 190f, 0f), 0.5f);

            float dot = (middle.X * expected.X) + (middle.Y * expected.Y)
                + (middle.Z * expected.Z) + (middle.W * expected.W);

            // A quaternion and its negation are the same rotation, hence the modulus.
            Assert.True(System.Math.Abs(dot) > 0.999f, $"orientation drifted: dot {dot}");
        }

        [Fact]
        public void SlerpNormalisesWhateverItIsGiven()
        {
            var unnormalised = new NetQuaternion(0f, 0f, 2f, 2f);

            NetQuaternion result = NetQuaternion.Slerp(NetQuaternion.Identity, unnormalised, 0.5f);

            Assert.Equal(1f, result.Length, 4);
        }

        [Fact]
        public void AVerticalPitchDoesNotProduceNaN()
        {
            // Asin of a value a hair past 1 is NaN, and an entity handed NaN for its
            // rotation is at an orientation the game refuses and never recovers from.
            NetQuaternion.LerpEuler(90f, 0f, 0f, -90f, 0f, 180f, 0.5f,
                out float pitch, out float roll, out float yaw);

            Assert.False(float.IsNaN(pitch));
            Assert.False(float.IsNaN(roll));
            Assert.False(float.IsNaN(yaw));
        }

        [Fact]
        public void AReplicatedVehicleUsesIt()
        {
            var vehicle = new RemoteVehicle(new EntityId(3));

            vehicle.Push(1d, new VehicleEntity(new EntityId(3))
            {
                Heading = 350f, Pitch = 0f, Roll = 0f, EngineHealth = 1000f,
            });
            vehicle.Push(2d, new VehicleEntity(new EntityId(3))
            {
                Heading = 10f, Pitch = 0f, Roll = 0f, EngineHealth = 1000f,
            });

            Assert.True(vehicle.TrySample(1.5d, out RemoteVehicleFrame frame));

            AssertAngle(0f, frame.Heading, 0.5f);
        }

        [Fact]
        public void APedStillBlendsItsHeadingOnItsOwn()
        {
            // A ped has one axis that matters and no pitch or roll to couple it to, so
            // the cheaper angle blend stays where it is. Stated as a test so nobody
            // "fixes" it later to match the vehicle path.
            Assert.Equal(0f, RemotePlayer.LerpAngle(350f, 10f, 0.5f), 3);
        }
    }
}
