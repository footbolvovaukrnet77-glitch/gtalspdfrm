using System;

namespace Gtamp.Shared.Core
{
    /// <summary>
    /// Lossy float packing used by the replication layer.
    /// <para>
    /// Every quantiser here is paired with a documented worst-case error so the
    /// protocol document can state a hard bound rather than a vague "compressed".
    /// See docs/NETWORK_PROTOCOL.md, section "Quantisation".
    /// </para>
    /// </summary>
    public static class Quantize
    {
        /// <summary>GTA V's playable world fits comfortably inside +/- 16 km on X/Y and +/- 2 km on Z.</summary>
        public const float WorldExtentXY = 16384f;
        public const float WorldExtentZ = 2048f;

        /// <summary>Position resolution: 1/512 m ~= 2 mm. Worst-case error 0.98 mm.</summary>
        public const float PositionScale = 512f;

        /// <summary>Velocity resolution: 1/128 m/s ~= 7.8 mm/s over a +/- 256 m/s range.</summary>
        public const float VelocityScale = 128f;
        public const float VelocityExtent = 256f;

        public static int EncodePositionAxis(float value, float extent)
        {
            float clamped = Clamp(value, -extent, extent);
            return (int)Math.Round(clamped * PositionScale);
        }

        public static float DecodePositionAxis(int encoded) => encoded / PositionScale;

        /// <summary>
        /// Bone offsets are relative to their character's root, so they need a metre
        /// of range and none of the world's. Resolution 1/128 m ~= 7.8 mm over a
        /// +/- 8 m range, which fits any limb of any ped and keeps each axis inside
        /// two varint bytes. Worst-case error 3.9 mm — far below the deadzone the
        /// ragdoll driver uses before it corrects at all.
        /// </summary>
        public const float BoneOffsetScale = 128f;
        public const float BoneOffsetExtent = 8f;

        public static int EncodeBoneOffsetAxis(float value)
        {
            float clamped = Clamp(value, -BoneOffsetExtent, BoneOffsetExtent);
            return (int)Math.Round(clamped * BoneOffsetScale);
        }

        public static float DecodeBoneOffsetAxis(int encoded) => encoded / BoneOffsetScale;

        public static int EncodeVelocityAxis(float value)
        {
            float clamped = Clamp(value, -VelocityExtent, VelocityExtent);
            return (int)Math.Round(clamped * VelocityScale);
        }

        public static float DecodeVelocityAxis(int encoded) => encoded / VelocityScale;

        /// <summary>Heading in degrees packed to 16 bits: 360/65536 ~= 0.0055 deg.</summary>
        public static ushort EncodeAngleDegrees(float degrees)
        {
            float wrapped = degrees % 360f;
            if (wrapped < 0f)
            {
                wrapped += 360f;
            }

            int q = (int)Math.Round(wrapped * (65536f / 360f));
            return (ushort)(q & 0xFFFF);
        }

        public static float DecodeAngleDegrees(ushort encoded) => encoded * (360f / 65536f);

        /// <summary>Normalised 0..1 quantities (health fraction, throttle, ...) in 8 bits.</summary>
        public static byte EncodeUnit(float value) => (byte)Math.Round(Clamp(value, 0f, 1f) * 255f);

        public static float DecodeUnit(byte encoded) => encoded / 255f;

        public static float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
