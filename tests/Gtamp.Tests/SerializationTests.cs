using System;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Xunit;

namespace Gtamp.Tests
{
    public class SerializationTests
    {
        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(127u)]
        [InlineData(128u)]
        [InlineData(16383u)]
        [InlineData(16384u)]
        [InlineData(uint.MaxValue)]
        public void VarUIntRoundTrips(uint value)
        {
            var writer = new NetWriter();
            writer.WriteVarUInt(value);
            Assert.Equal(value, new NetReader(writer.ToArray()).ReadVarUInt());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(1)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        [InlineData(-123456)]
        public void VarIntRoundTrips(int value)
        {
            var writer = new NetWriter();
            writer.WriteVarInt(value);
            Assert.Equal(value, new NetReader(writer.ToArray()).ReadVarInt());
        }

        [Fact]
        public void SmallVarIntsUseOneByte()
        {
            var writer = new NetWriter();
            writer.WriteVarInt(-1);
            Assert.Equal(1, writer.Length);
        }

        [Fact]
        public void VarUInt64RoundTrips()
        {
            var writer = new NetWriter();
            writer.WriteVarUInt64(ulong.MaxValue);
            writer.WriteVarUInt64(0);
            writer.WriteVarUInt64(1UL << 40);

            var reader = new NetReader(writer.ToArray());
            Assert.Equal(ulong.MaxValue, reader.ReadVarUInt64());
            Assert.Equal(0UL, reader.ReadVarUInt64());
            Assert.Equal(1UL << 40, reader.ReadVarUInt64());
        }

        [Fact]
        public void PrimitivesRoundTripInOrder()
        {
            var writer = new NetWriter();
            writer.WriteByte(0xAB);
            writer.WriteBool(true);
            writer.WriteUInt16(65535);
            writer.WriteInt16(-32768);
            writer.WriteUInt32(4000000000);
            writer.WriteInt32(-2000000000);
            writer.WriteUInt64(ulong.MaxValue);
            writer.WriteSingle(1.25f);
            writer.WriteDouble(-9.5);
            writer.WriteString("hello wörld");
            writer.WriteByteArray(new byte[] { 1, 2, 3 });

            var reader = new NetReader(writer.ToArray());
            Assert.Equal(0xAB, reader.ReadByte());
            Assert.True(reader.ReadBool());
            Assert.Equal(65535, reader.ReadUInt16());
            Assert.Equal(-32768, reader.ReadInt16());
            Assert.Equal(4000000000u, reader.ReadUInt32());
            Assert.Equal(-2000000000, reader.ReadInt32());
            Assert.Equal(ulong.MaxValue, reader.ReadUInt64());
            Assert.Equal(1.25f, reader.ReadSingle());
            Assert.Equal(-9.5, reader.ReadDouble());
            Assert.Equal("hello wörld", reader.ReadString());
            Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadByteArray());
            Assert.True(reader.EndOfData);
        }

        [Fact]
        public void TruncatedBufferThrowsInsteadOfCorrupting()
        {
            var writer = new NetWriter();
            writer.WriteUInt32(12345);
            byte[] truncated = new byte[2];
            Array.Copy(writer.ToArray(), truncated, 2);

            Assert.Throws<NetSerializationException>(() => new NetReader(truncated).ReadUInt32());
        }

        [Fact]
        public void OversizedStringLengthIsRejected()
        {
            var writer = new NetWriter();
            writer.WriteVarUInt(1_000_000);

            Assert.Throws<NetSerializationException>(() => new NetReader(writer.ToArray()).ReadString(64));
        }

        [Fact]
        public void QuantizedPositionStaysWithinTheDocumentedErrorBound()
        {
            // docs/NETWORK_PROTOCOL.md promises better than 1 mm on each axis.
            const float bound = 0.001f;
            var random = new Random(42);

            for (int i = 0; i < 5000; i++)
            {
                var value = new NetVector3(
                    (float)((random.NextDouble() - 0.5) * 2 * Quantize.WorldExtentXY),
                    (float)((random.NextDouble() - 0.5) * 2 * Quantize.WorldExtentXY),
                    (float)((random.NextDouble() - 0.5) * 2 * Quantize.WorldExtentZ));

                var writer = new NetWriter();
                writer.WriteQuantizedPosition(value);
                NetVector3 decoded = new NetReader(writer.ToArray()).ReadQuantizedPosition();

                Assert.True(Math.Abs(value.X - decoded.X) <= bound, $"X error {Math.Abs(value.X - decoded.X)}");
                Assert.True(Math.Abs(value.Y - decoded.Y) <= bound, $"Y error {Math.Abs(value.Y - decoded.Y)}");
                Assert.True(Math.Abs(value.Z - decoded.Z) <= bound, $"Z error {Math.Abs(value.Z - decoded.Z)}");
            }
        }

        [Fact]
        public void QuantizedPositionClampsRatherThanWrapping()
        {
            var writer = new NetWriter();
            writer.WriteQuantizedPosition(new NetVector3(1e9f, -1e9f, 1e9f));
            NetVector3 decoded = new NetReader(writer.ToArray()).ReadQuantizedPosition();

            Assert.Equal(Quantize.WorldExtentXY, decoded.X, 3);
            Assert.Equal(-Quantize.WorldExtentXY, decoded.Y, 3);
            Assert.Equal(Quantize.WorldExtentZ, decoded.Z, 3);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(90f)]
        [InlineData(359.9f)]
        [InlineData(-45f)]
        [InlineData(720f)]
        public void AngleRoundTripsWithinOneHundredthOfADegree(float degrees)
        {
            var writer = new NetWriter();
            writer.WriteAngleDegrees(degrees);
            float decoded = new NetReader(writer.ToArray()).ReadAngleDegrees();

            float expected = ((degrees % 360f) + 360f) % 360f;
            float error = Math.Abs(expected - decoded);
            if (error > 180f)
            {
                error = 360f - error;
            }

            Assert.True(error < 0.01f, $"angle error {error} for input {degrees}");
        }

        [Fact]
        public void PlayerFullStateRoundTrips()
        {
            var serializer = new PlayerEntitySerializer();
            var original = new PlayerEntity(new EntityId(7))
            {
                PlayerId = 3,
                OwnerId = 3,
                Name = "Trevor",
                ModelHash = 0xDEADBEEF,
                Position = new NetVector3(101.5f, -204.25f, 30.125f),
                Velocity = new NetVector3(1.5f, -2.25f, 0f),
                Heading = 271.5f,
                Health = 180,
                MaxHealth = 200,
                Armor = 45,
                Flags = PlayerFlags.Sprinting | PlayerFlags.Aiming,
                Movement = MovementState.Sprint,
                CurrentWeaponHash = 0x1B06D571,
                Ammo = 120,
                AimPosition = new NetVector3(110f, -200f, 31f),
                VehicleId = new EntityId(42),
                VehicleSeat = -1,
                WantedLevel = 3,
                AnimationHash = 0xABCDEF,
                Dimension = 2,
                InteriorId = 12345,
            };

            original.CustomData["lspdfr.callout"] = "traffic-stop";

            var writer = new NetWriter();
            serializer.WriteFull(writer, original);

            var restored = (PlayerEntity)serializer.Create(new EntityId(7));
            serializer.ReadFull(new NetReader(writer.ToArray()), restored);

            Assert.Equal(original.PlayerId, restored.PlayerId);
            Assert.Equal(original.Name, restored.Name);
            Assert.Equal(original.ModelHash, restored.ModelHash);
            Assert.Equal(original.Health, restored.Health);
            Assert.Equal(original.Armor, restored.Armor);
            Assert.Equal(original.Flags, restored.Flags);
            Assert.Equal(original.Movement, restored.Movement);
            Assert.Equal(original.CurrentWeaponHash, restored.CurrentWeaponHash);
            Assert.Equal(original.Ammo, restored.Ammo);
            Assert.Equal(original.VehicleId, restored.VehicleId);
            Assert.Equal(original.VehicleSeat, restored.VehicleSeat);
            Assert.Equal(original.WantedLevel, restored.WantedLevel);
            Assert.Equal(original.Dimension, restored.Dimension);
            Assert.Equal(original.InteriorId, restored.InteriorId);
            Assert.Equal("traffic-stop", restored.CustomData["lspdfr.callout"]);
            Assert.Equal(original.Position.X, restored.Position.X, 2);
            Assert.Equal(original.Position.Z, restored.Position.Z, 2);
        }

        [Fact]
        public void DeltaCarriesOnlyChangedFieldsAndIsSmallerThanFullState()
        {
            var serializer = new PlayerEntitySerializer();
            var baseline = new PlayerEntity(new EntityId(1))
            {
                Name = "A player with a reasonably long name",
                Position = new NetVector3(100f, 100f, 30f),
                Health = 200,
                ModelHash = 0x12345678,
            };

            var current = (PlayerEntity)baseline.Clone();
            current.Position = new NetVector3(100.5f, 100f, 30f);

            var fullWriter = new NetWriter();
            serializer.WriteFull(fullWriter, current);

            var deltaWriter = new NetWriter();
            serializer.WriteDelta(deltaWriter, baseline, current);

            Assert.True(
                deltaWriter.Length < fullWriter.Length,
                $"delta {deltaWriter.Length} B should be smaller than full {fullWriter.Length} B");

            var restored = (PlayerEntity)baseline.Clone();
            serializer.ReadDelta(new NetReader(deltaWriter.ToArray()), restored);

            Assert.Equal(100.5f, restored.Position.X, 2);
            Assert.Equal("A player with a reasonably long name", restored.Name);
            Assert.Equal(200, restored.Health);
        }

        [Fact]
        public void IdenticalEntitiesProduceAnEmptyDelta()
        {
            var serializer = new PlayerEntitySerializer();
            var entity = new PlayerEntity(new EntityId(1)) { Name = "same", Health = 100 };
            var clone = (PlayerEntity)entity.Clone();

            Assert.False(serializer.HasChanges(entity, clone));

            var writer = new NetWriter();
            serializer.WriteDelta(writer, entity, clone);

            // Just the zero mask.
            Assert.Equal(1, writer.Length);
        }
    }
}
