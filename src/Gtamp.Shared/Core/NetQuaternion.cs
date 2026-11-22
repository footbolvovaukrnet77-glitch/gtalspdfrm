using System;

namespace Gtamp.Shared.Core
{
    /// <summary>
    /// A rotation, used to interpolate between two replicated orientations.
    /// <para>
    /// <b>Why this exists, and why it is not on the wire.</b> Orientation travels as
    /// three angles because that is what the game hands over and what it takes back,
    /// and the round trip is exact. Interpolating those three angles
    /// <em>independently</em> is where it goes wrong: pitch, roll and yaw are not
    /// independent axes, so blending each one on its own passes through
    /// orientations on no path between the two ends. A car rolling onto its roof
    /// swings its nose through the turn on the way; an aircraft pitched near
    /// vertical loses a degree of freedom entirely, which is gimbal lock arriving in
    /// the interpolator rather than in the format.
    /// </para>
    /// <para>
    /// Converting both ends to quaternions, spherically interpolating and converting
    /// back takes the short way round the actual sphere of rotations. The endpoints
    /// come back bit-for-bit unchanged whatever convention this file uses, because
    /// the conversion is its own inverse — so a wrong guess about the game's axis
    /// order would cost accuracy only strictly between two samples, never at one.
    /// </para>
    /// </summary>
    public readonly struct NetQuaternion : IEquatable<NetQuaternion>
    {
        public static readonly NetQuaternion Identity = new NetQuaternion(0f, 0f, 0f, 1f);

        public NetQuaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public float X { get; }

        public float Y { get; }

        public float Z { get; }

        public float W { get; }

        public float Length => (float)Math.Sqrt((X * X) + (Y * Y) + (Z * Z) + (W * W));

        public NetQuaternion Normalized()
        {
            float length = Length;
            if (length < 1e-6f)
            {
                return Identity;
            }

            float inverse = 1f / length;
            return new NetQuaternion(X * inverse, Y * inverse, Z * inverse, W * inverse);
        }

        /// <summary>
        /// Builds a rotation from GTA V's Euler angles in degrees: X pitch, Y roll,
        /// Z yaw, composed in the order the game's rotation-order 2 uses.
        /// </summary>
        public static NetQuaternion FromEuler(float pitchDegrees, float rollDegrees, float yawDegrees)
        {
            double halfPitch = ToRadians(pitchDegrees) * 0.5d;
            double halfRoll = ToRadians(rollDegrees) * 0.5d;
            double halfYaw = ToRadians(yawDegrees) * 0.5d;

            double sinPitch = Math.Sin(halfPitch), cosPitch = Math.Cos(halfPitch);
            double sinRoll = Math.Sin(halfRoll), cosRoll = Math.Cos(halfRoll);
            double sinYaw = Math.Sin(halfYaw), cosYaw = Math.Cos(halfYaw);

            // Composed so that ToEuler is its exact inverse. That property is the one
            // that matters here: it makes every sample endpoint bit-identical to what
            // the game reported, and confines any disagreement with the engine's own
            // axis order to the frames strictly between two samples.
            return new NetQuaternion(
                (float)((cosYaw * cosRoll * sinPitch) - (sinYaw * sinRoll * cosPitch)),
                (float)((sinYaw * cosRoll * sinPitch) + (cosYaw * sinRoll * cosPitch)),
                (float)((sinYaw * cosRoll * cosPitch) - (cosYaw * sinRoll * sinPitch)),
                (float)((cosYaw * cosRoll * cosPitch) + (sinYaw * sinRoll * sinPitch)));
        }

        /// <summary>
        /// The inverse of <see cref="FromEuler"/>: pitch, roll and yaw in degrees,
        /// with yaw wrapped to 0..360 the way the game reports headings.
        /// </summary>
        public void ToEuler(out float pitchDegrees, out float rollDegrees, out float yawDegrees)
        {
            NetQuaternion q = Normalized();

            double sinPitch = 2d * ((q.W * q.X) + (q.Y * q.Z));
            double cosPitch = 1d - (2d * ((q.X * q.X) + (q.Y * q.Y)));
            double pitch = Math.Atan2(sinPitch, cosPitch);

            double sinRoll = 2d * ((q.W * q.Y) - (q.Z * q.X));

            // Clamped rather than trusted: accumulated float error can push this a
            // hair past 1, and Asin of 1.0000001 is NaN, which would put an entity
            // at an orientation the game refuses and never recovers from.
            sinRoll = sinRoll > 1d ? 1d : (sinRoll < -1d ? -1d : sinRoll);
            double roll = Math.Asin(sinRoll);

            double sinYaw = 2d * ((q.W * q.Z) + (q.X * q.Y));
            double cosYaw = 1d - (2d * ((q.Y * q.Y) + (q.Z * q.Z)));
            double yaw = Math.Atan2(sinYaw, cosYaw);

            pitchDegrees = (float)ToDegrees(pitch);
            rollDegrees = (float)ToDegrees(roll);
            yawDegrees = Wrap360((float)ToDegrees(yaw));
        }

        /// <summary>
        /// Spherical interpolation, taking the short way round.
        /// <para>
        /// A quaternion and its negation describe the same rotation, so the sign is
        /// flipped when the two ends point away from each other — without that, half
        /// of all interpolations take the long way and a car turning ten degrees
        /// spins three hundred and fifty the other way.
        /// </para>
        /// </summary>
        public static NetQuaternion Slerp(NetQuaternion from, NetQuaternion to, float t)
        {
            if (t <= 0f)
            {
                return from;
            }

            if (t >= 1f)
            {
                return to;
            }

            NetQuaternion a = from.Normalized();
            NetQuaternion b = to.Normalized();

            float dot = (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W);
            if (dot < 0f)
            {
                b = new NetQuaternion(-b.X, -b.Y, -b.Z, -b.W);
                dot = -dot;
            }

            // Nearly identical rotations: the angle between them is too small for the
            // trigonometry to be stable, and a straight blend is indistinguishable.
            if (dot > 0.9995f)
            {
                return new NetQuaternion(
                    a.X + ((b.X - a.X) * t),
                    a.Y + ((b.Y - a.Y) * t),
                    a.Z + ((b.Z - a.Z) * t),
                    a.W + ((b.W - a.W) * t)).Normalized();
            }

            double theta = Math.Acos(dot);
            double sinTheta = Math.Sin(theta);
            float weightFrom = (float)(Math.Sin((1d - t) * theta) / sinTheta);
            float weightTo = (float)(Math.Sin(t * theta) / sinTheta);

            return new NetQuaternion(
                (a.X * weightFrom) + (b.X * weightTo),
                (a.Y * weightFrom) + (b.Y * weightTo),
                (a.Z * weightFrom) + (b.Z * weightTo),
                (a.W * weightFrom) + (b.W * weightTo)).Normalized();
        }

        /// <summary>
        /// Blends two GTA V Euler orientations the short way round the sphere of
        /// rotations, rather than one axis at a time. The endpoints are exact.
        /// </summary>
        public static void LerpEuler(
            float fromPitch, float fromRoll, float fromYaw,
            float toPitch, float toRoll, float toYaw,
            float t,
            out float pitch, out float roll, out float yaw)
        {
            if (t <= 0f)
            {
                pitch = fromPitch;
                roll = fromRoll;
                yaw = fromYaw;
                return;
            }

            if (t >= 1f)
            {
                pitch = toPitch;
                roll = toRoll;
                yaw = toYaw;
                return;
            }

            Slerp(
                FromEuler(fromPitch, fromRoll, fromYaw),
                FromEuler(toPitch, toRoll, toYaw),
                t).ToEuler(out pitch, out roll, out yaw);
        }

        public bool Equals(NetQuaternion other) =>
            X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && W.Equals(other.W);

        public override bool Equals(object? obj) => obj is NetQuaternion other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return (hash * 397) ^ W.GetHashCode();
            }
        }

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###}, {W:0.###})";

        private static double ToRadians(float degrees) => degrees * (Math.PI / 180d);

        private static double ToDegrees(double radians) => radians * (180d / Math.PI);

        private static float Wrap360(float degrees)
        {
            float wrapped = degrees % 360f;
            return wrapped < 0f ? wrapped + 360f : wrapped;
        }
    }
}
