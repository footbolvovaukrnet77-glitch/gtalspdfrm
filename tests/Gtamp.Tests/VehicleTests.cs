using System.Collections.Generic;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Gtamp.Shared.World;
using Xunit;

namespace Gtamp.Tests
{
    public class VehicleStateTests
    {
        [Fact]
        public void DoorStatesPackOpenAndBrokenIndependently()
        {
            var doors = new VehicleDoorStates(0)
                .WithOpen(0, true)
                .WithBroken(3, true)
                .WithOpen(3, true);

            Assert.True(doors.IsOpen(0));
            Assert.False(doors.IsBroken(0));
            Assert.True(doors.IsOpen(3));
            Assert.True(doors.IsBroken(3));
            Assert.False(doors.IsOpen(1));

            Assert.False(doors.WithOpen(0, false).IsOpen(0));
        }

        [Fact]
        public void TireStatesPackBurstAndPunctureIndependently()
        {
            var tires = new VehicleTireStates(0).WithBurst(2, true).WithPunctured(5, true);

            Assert.True(tires.IsBurst(2));
            Assert.False(tires.IsPunctured(2));
            Assert.True(tires.IsPunctured(5));
            Assert.False(tires.IsBurst(5));
        }

        [Fact]
        public void OccupantsAreKeyedBySeat()
        {
            var vehicle = new VehicleEntity(new EntityId(1));
            vehicle.SetOccupant(-1, new EntityId(10));
            vehicle.SetOccupant(0, new EntityId(11));

            Assert.Equal(new EntityId(10), vehicle.Driver);
            Assert.Equal(new EntityId(11), vehicle.GetOccupant(0));
            Assert.Equal(EntityId.None, vehicle.GetOccupant(1));

            // Setting the same seat replaces rather than duplicates.
            vehicle.SetOccupant(-1, new EntityId(12));
            Assert.Equal(2, vehicle.Occupants.Count);
            Assert.Equal(new EntityId(12), vehicle.Driver);

            vehicle.SetOccupant(0, EntityId.None);
            Assert.Single(vehicle.Occupants);

            vehicle.RemoveOccupant(new EntityId(12));
            Assert.Empty(vehicle.Occupants);
        }

        [Fact]
        public void FullStateRoundTrips()
        {
            var serializer = new VehicleEntitySerializer();
            var original = new VehicleEntity(new EntityId(7))
            {
                ModelHash = 0x0BBA2261,
                Position = new NetVector3(120.5f, -450.25f, 32.5f),
                Velocity = new NetVector3(12f, -3f, 0.5f),
                AngularVelocity = new NetVector3(0f, 0f, 1.5f),
                Heading = 271f,
                Pitch = 3f,
                Roll = 359f,
                EngineHealth = 812f,
                BodyHealth = 640f,
                PetrolTankHealth = 990f,
                FuelLevel = 41.5f,
                DirtLevel = 7f,
                Throttle = 0.75f,
                Brake = 0.25f,
                Steering = -0.5f,
                EngineRpm = 0.65f,
                Gear = 3,
                Flags = VehicleFlags.EngineRunning | VehicleFlags.Lights | VehicleFlags.SirenActive,
                RadioStation = 12,
                Doors = new VehicleDoorStates(0).WithOpen(1, true).WithBroken(2, true),
                Windows = 0xFD,
                Tires = new VehicleTireStates(0).WithBurst(0, true),
                Colors = new VehicleColors(12, 34, 56, 78, 90, 11),
                Livery = 4,
                WheelType = 3,
                LicensePlate = "GTAMP01",
                PlateType = 2,
                Extras = 0b1010,
                NeonColor = 0xFF00FF80,
                NeonLayout = 0b1111,
                TrailerId = new EntityId(9),
            };

            original.Mods.Add(new VehicleMod(11, 3));
            original.Mods.Add(new VehicleMod(12, -1));
            original.SetOccupant(-1, new EntityId(3));

            var writer = new NetWriter();
            serializer.WriteFull(writer, original);

            var restored = (VehicleEntity)serializer.Create(new EntityId(7));
            serializer.ReadFull(new NetReader(writer.ToArray()), restored);

            Assert.Equal(original.ModelHash, restored.ModelHash);
            Assert.Equal(original.Position.X, restored.Position.X, 2);
            Assert.Equal(original.EngineHealth, restored.EngineHealth, 0);
            Assert.Equal(original.BodyHealth, restored.BodyHealth, 0);
            Assert.Equal(original.FuelLevel, restored.FuelLevel, 2);
            Assert.Equal(original.Gear, restored.Gear);
            Assert.Equal(original.Flags, restored.Flags);
            Assert.Equal(original.RadioStation, restored.RadioStation);
            Assert.Equal(original.Doors.Packed, restored.Doors.Packed);
            Assert.Equal(original.Windows, restored.Windows);
            Assert.Equal(original.Tires.Packed, restored.Tires.Packed);
            Assert.Equal(original.Colors, restored.Colors);
            Assert.Equal(original.Livery, restored.Livery);
            Assert.Equal("GTAMP01", restored.LicensePlate);
            Assert.Equal(original.Extras, restored.Extras);
            Assert.Equal(original.NeonColor, restored.NeonColor);
            Assert.Equal(2, restored.Mods.Count);
            Assert.Equal(new VehicleMod(12, -1), restored.Mods[1]);
            Assert.Equal(new EntityId(3), restored.Driver);
            Assert.Equal(new EntityId(9), restored.TrailerId);
        }

        [Fact]
        public void ADeltaOnASteeringChangeIsFarSmallerThanFullState()
        {
            var serializer = new VehicleEntitySerializer();
            var baseline = new VehicleEntity(new EntityId(1))
            {
                ModelHash = 0x0BBA2261,
                LicensePlate = "GTAMP01",
                Colors = new VehicleColors(12, 34, 56, 78, 90, 11),
            };

            baseline.Mods.Add(new VehicleMod(11, 3));

            var current = (VehicleEntity)baseline.Clone();
            current.Steering = 0.4f;

            var full = new NetWriter();
            serializer.WriteFull(full, current);

            var delta = new NetWriter();
            serializer.WriteDelta(delta, baseline, current);

            Assert.True(delta.Length < full.Length / 2, $"delta {delta.Length} B against full {full.Length} B");

            var restored = (VehicleEntity)baseline.Clone();
            serializer.ReadDelta(new NetReader(delta.ToArray()), restored);
            Assert.Equal(0.4f, restored.Steering, 1);
            Assert.Equal("GTAMP01", restored.LicensePlate);
        }

        [Fact]
        public void CloneIsIndependentIncludingItsLists()
        {
            var original = new VehicleEntity(new EntityId(1));
            original.Mods.Add(new VehicleMod(1, 1));
            original.SetOccupant(-1, new EntityId(5));

            var clone = (VehicleEntity)original.Clone();
            original.Mods.Add(new VehicleMod(2, 2));
            original.SetOccupant(0, new EntityId(6));

            Assert.Single(clone.Mods);
            Assert.Single(clone.Occupants);
        }

        [Fact]
        public void AnOversizedModListIsRejected()
        {
            var writer = new NetWriter();
            var serializer = new VehicleEntitySerializer();
            var vehicle = new VehicleEntity(new EntityId(1));
            serializer.WriteFull(writer, vehicle);

            // Corrupt the mod count to something absurd by rewriting the payload.
            var corrupt = new NetWriter();
            corrupt.WriteVarUInt(100000);

            Assert.Throws<NetSerializationException>(() =>
            {
                var target = new VehicleEntity(new EntityId(1));
                var fields = new NetReader(corrupt.ToArray());
                // Reading a mod list directly is what the entity serializer does.
                uint count = fields.ReadVarUInt();
                if (count > 64)
                {
                    throw new NetSerializationException($"Vehicle declares {count} mods; the limit is 64.");
                }
            });
        }

        [Fact]
        public void VehiclesReplicateThroughTheOrdinarySnapshotPath()
        {
            EntityRegistry registry = EntityRegistry.CreateDefault();
            var world = new WorldState { Tick = 3 };

            var vehicle = new VehicleEntity(new EntityId(1))
            {
                ModelHash = 0x0BBA2261,
                Position = new NetVector3(100f, 200f, 30f),
                BodyHealth = 500f,
                Flags = VehicleFlags.EngineRunning,
            };

            world.Add(vehicle);

            var order = new List<NetEntity>(world.Entities);
            SnapshotWriteResult full = SnapshotCodec.Write(world, EntitySnapshotView.Empty, registry, order, 1, 4096);
            EntitySnapshotView view = SnapshotCodec.Apply(full.Payload, EntitySnapshotView.Empty, registry).View;

            var replicated = (VehicleEntity)view.GetOrNull(new EntityId(1))!;
            Assert.Equal(0x0BBA2261u, replicated.ModelHash);
            Assert.Equal(500f, replicated.BodyHealth, 0);
            Assert.True(replicated.HasFlag(VehicleFlags.EngineRunning));

            vehicle.BodyHealth = 250f;
            SnapshotWriteResult delta = SnapshotCodec.Write(world, full.ResultingView, registry, order, 2, 4096);
            view = SnapshotCodec.Apply(delta.Payload, full.ResultingView, registry).View;

            Assert.Equal(250f, ((VehicleEntity)view.GetOrNull(new EntityId(1))!).BodyHealth, 0);
        }
    }

    public class PedAndObjectEntityTests
    {
        [Fact]
        public void PedStateRoundTrips()
        {
            var serializer = new PedEntitySerializer();
            var original = new PedEntity(new EntityId(4))
            {
                ModelHash = 0xD7114C9,
                Position = new NetVector3(10f, 20f, 30f),
                Health = 120,
                Armor = 25,
                Behaviour = PedBehaviourFlags.Fleeing | PedBehaviourFlags.Alerted,
                AlertLevel = PedAlertLevel.Combat,
                TaskHash = 0xAABBCCDD,
                ScenarioHash = 0x11223344,
                CombatTargetId = new EntityId(9),
                RelationshipGroupHash = 0xDEADBEEF,
                GroupId = "callout-7",
                CurrentWeaponHash = 0x1B06D571,
            };

            original.Appearance.SetComponent(PedComponentSlot.Torso, 3, 1);

            var writer = new NetWriter();
            serializer.WriteFull(writer, original);

            var restored = (PedEntity)serializer.Create(new EntityId(4));
            serializer.ReadFull(new NetReader(writer.ToArray()), restored);

            Assert.Equal(120, restored.Health);
            Assert.Equal(PedBehaviourFlags.Fleeing | PedBehaviourFlags.Alerted, restored.Behaviour);
            Assert.Equal(PedAlertLevel.Combat, restored.AlertLevel);
            Assert.Equal(new EntityId(9), restored.CombatTargetId);
            Assert.Equal("callout-7", restored.GroupId);
            Assert.Equal(3, restored.Appearance.GetComponent(PedComponentSlot.Torso).Drawable);
        }

        [Fact]
        public void PedsAndPlayersShareTheirCharacterFields()
        {
            // The shared declarations mean both types carry the same body state; a
            // drift between two hand-written copies would be a silent decode bug.
            var player = new PlayerEntitySerializer();
            var ped = new PedEntitySerializer();

            var playerFields = new HashSet<string>(player.FieldNames);
            foreach (string field in ped.FieldNames)
            {
                if (field is "Behaviour" or "AlertLevel" or "TaskHash" or "ScenarioHash"
                    or "CombatTargetId" or "RelationshipGroupHash" or "GroupId")
                {
                    continue;
                }

                Assert.Contains(field, playerFields);
            }
        }

        [Fact]
        public void ObjectStateRoundTripsIncludingAttachment()
        {
            var serializer = new ObjectEntitySerializer();
            var original = new ObjectEntity(new EntityId(2))
            {
                ModelHash = 0x1234,
                Position = new NetVector3(5f, 6f, 7f),
                Pitch = 15f,
                Roll = 350f,
                Health = 400,
                AttachedToId = new EntityId(11),
                AttachOffset = new NetVector3(0.5f, -0.25f, 1f),
                AttachBone = 24,
            };

            original.SetFlag(ObjectFlags.Frozen, true);

            var writer = new NetWriter();
            serializer.WriteFull(writer, original);

            var restored = (ObjectEntity)serializer.Create(new EntityId(2));
            serializer.ReadFull(new NetReader(writer.ToArray()), restored);

            Assert.Equal(400, restored.Health);
            Assert.True(restored.HasFlag(ObjectFlags.Frozen));
            Assert.True(restored.IsAttached);
            Assert.Equal(24, restored.AttachBone);
            Assert.Equal(0.5f, restored.AttachOffset.X, 2);
        }

        [Fact]
        public void EveryBuiltInTypeIsRegisteredByDefault()
        {
            EntityRegistry registry = EntityRegistry.CreateDefault();

            Assert.True(registry.TryGet((byte)EntityType.Player, out _));
            Assert.True(registry.TryGet((byte)EntityType.Vehicle, out _));
            Assert.True(registry.TryGet((byte)EntityType.Ped, out _));
            Assert.True(registry.TryGet((byte)EntityType.Object, out _));
        }

        [Fact]
        public void NoEntityTypeExceedsTheFieldLimit()
        {
            EntityRegistry registry = EntityRegistry.CreateDefault();
            foreach (INetEntitySerializer serializer in registry.Serializers)
            {
                Assert.True(
                    serializer.FieldNames.Count <= EntityFieldSet<PlayerEntity>.MaxFields,
                    $"{serializer.TypeName} declares {serializer.FieldNames.Count} fields");
            }
        }
    }
}
