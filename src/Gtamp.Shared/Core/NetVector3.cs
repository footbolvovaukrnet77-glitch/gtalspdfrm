using System;
using System.Globalization;

namespace Gtamp.Shared.Core
{
    /// <summary>
    /// Engine-agnostic 3D vector. The framework never references a GTA V math type
    /// outside the game bridge, so every layer below the bridge is unit-testable
    /// without the game process.
    /// </summary>
    public readonly struct NetVector3 : IEquatable<NetVector3>
    {
        public static readonly NetVector3 Zero = new NetVector3(0f, 0f, 0f);

        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public NetVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float LengthSquared => (X * X) + (Y * Y) + (Z * Z);

        public float Length => (float)Math.Sqrt(LengthSquared);

        public static NetVector3 operator +(NetVector3 a, NetVector3 b) => new NetVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static NetVector3 operator -(NetVector3 a, NetVector3 b) => new NetVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static NetVector3 operator *(NetVector3 a, float s) => new NetVector3(a.X * s, a.Y * s, a.Z * s);

        public static float Distance(NetVector3 a, NetVector3 b) => (a - b).Length;

        public static float DistanceSquared(NetVector3 a, NetVector3 b) => (a - b).LengthSquared;

        public static NetVector3 Lerp(NetVector3 a, NetVector3 b, float t) => a + ((b - a) * t);

        public bool Equals(NetVector3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object? obj) => obj is NetVector3 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return hash;
            }
        }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }
}
