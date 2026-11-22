using Gtamp.Client.Entities;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Five vehicle and object fields that were replicated end to end and touched by
    /// nothing in the game layer.
    /// <para>
    /// A sweep of every replicated field against the ScriptHookVDotNet bridge found
    /// them together: `RadioStation`, `NeonColor`, `NeonLayout`, `TrailerId` and the
    /// object attachment triple. Each was declared, serialised, delta encoded,
    /// cloned and persisted, and neither read from the game nor written to it — they
    /// travelled as their defaults and cost a mask bit to describe nothing.
    /// </para>
    /// </summary>
    public class AttachmentAndCosmeticsTests
    {
        private static TestClient Connected(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.Client.IsConnected));
            return client;
        }

        private static VehicleEntity Vehicle(TestHarness harness, NetVector3 position)
        {
            var vehicle = new VehicleEntity(harness.Server.World.AllocateEntityId())
            {
                Position = position,
                ModelHash = 0x0BBA91E1,
                EngineHealth = 1000f,
                BodyHealth = 1000f,
            };

            harness.Server.World.Spawn(vehicle);
            return vehicle;
        }

        [Fact]
        public void ATowedTrailerIsHitchedToItsTractor()
        {
            using var harness = new TestHarness();
            TestClient client = Connected(harness, "watcher");

            VehicleEntity trailer = Vehicle(harness, new NetVector3(220f, -800f, 30f));
            VehicleEntity tractor = Vehicle(harness, new NetVector3(230f, -800f, 30f));
            tractor.TrailerId = trailer.Id;
            harness.Server.World.Touch(tractor);

            Assert.True(harness.AdvanceUntil(() =>
                client.Client.RemoteEntities.TryGetVehicle(tractor.Id, out RemoteVehicle t)
                && t.VehicleHandle != 0
                && client.Bridge.Towing.TryGetValue(t.VehicleHandle, out int towed)
                && towed != 0));

            Assert.True(client.Client.RemoteEntities.TryGetVehicle(tractor.Id, out RemoteVehicle cab));
            Assert.True(client.Client.RemoteEntities.TryGetVehicle(trailer.Id, out RemoteVehicle box));
            Assert.Equal(box.VehicleHandle, client.Bridge.Towing[cab.VehicleHandle]);
        }

        [Fact]
        public void AVehicleTowingNothingIsToldSo()
        {
            using var harness = new TestHarness();
            TestClient client = Connected(harness, "watcher");

            VehicleEntity lone = Vehicle(harness, new NetVector3(220f, -800f, 30f));

            Assert.True(harness.AdvanceUntil(() =>
                client.Client.RemoteEntities.TryGetVehicle(lone.Id, out RemoteVehicle v)
                && v.VehicleHandle != 0
                && client.Bridge.Towing.ContainsKey(v.VehicleHandle)));

            Assert.True(client.Client.RemoteEntities.TryGetVehicle(lone.Id, out RemoteVehicle only));
            Assert.Equal(0, client.Bridge.Towing[only.VehicleHandle]);
        }

        [Fact]
        public void ATrailerThisClientHasNotBuiltIsNotHitchedToNothing()
        {
            // A trailer id pointing at an entity this client has no vehicle for must
            // read as "no trailer", not as a handle of zero being attached.
            using var harness = new TestHarness();
            TestClient client = Connected(harness, "watcher");

            VehicleEntity tractor = Vehicle(harness, new NetVector3(230f, -800f, 30f));
            tractor.TrailerId = new EntityId(99999);
            harness.Server.World.Touch(tractor);

            Assert.True(harness.AdvanceUntil(() =>
                client.Client.RemoteEntities.TryGetVehicle(tractor.Id, out RemoteVehicle v)
                && v.VehicleHandle != 0
                && client.Bridge.Towing.ContainsKey(v.VehicleHandle)));

            Assert.True(client.Client.RemoteEntities.TryGetVehicle(tractor.Id, out RemoteVehicle cab));
            Assert.Equal(0, client.Bridge.Towing[cab.VehicleHandle]);
        }

        [Fact]
        public void AnAttachedObjectIsGivenItsParentHandle()
        {
            using var harness = new TestHarness();
            TestClient client = Connected(harness, "watcher");

            VehicleEntity car = Vehicle(harness, new NetVector3(220f, -800f, 30f));

            var prop = new ObjectEntity(harness.Server.World.AllocateEntityId())
            {
                Position = new NetVector3(220f, -800f, 31f),
                ModelHash = 0x1F1F1F1F,
                AttachedToId = car.Id,
                AttachOffset = new NetVector3(0f, 0f, 1.2f),
                AttachBone = -1,
            };

            harness.Server.World.Spawn(prop);

            Assert.True(harness.AdvanceUntil(() =>
                client.Client.RemoteEntities.TryGetObjectHandle(prop.Id, out int h)
                && client.Bridge.AttachedTo.TryGetValue(h, out int parent)
                && parent != 0));

            Assert.True(client.Client.RemoteEntities.TryGetObjectHandle(prop.Id, out int propHandle));
            Assert.True(client.Client.RemoteEntities.TryGetVehicle(car.Id, out RemoteVehicle vehicle));
            Assert.Equal(vehicle.VehicleHandle, client.Bridge.AttachedTo[propHandle]);
        }

        [Fact]
        public void AnUnattachedObjectIsGivenNoParent()
        {
            using var harness = new TestHarness();
            TestClient client = Connected(harness, "watcher");

            var prop = new ObjectEntity(harness.Server.World.AllocateEntityId())
            {
                Position = new NetVector3(220f, -800f, 31f),
                ModelHash = 0x1F1F1F1F,
            };

            harness.Server.World.Spawn(prop);

            Assert.True(harness.AdvanceUntil(() =>
                client.Client.RemoteEntities.TryGetObjectHandle(prop.Id, out int h)
                && client.Bridge.AttachedTo.ContainsKey(h)));

            Assert.True(client.Client.RemoteEntities.TryGetObjectHandle(prop.Id, out int handle));
            Assert.Equal(0, client.Bridge.AttachedTo[handle]);
        }

        [Fact]
        public void NeonAndRadioAreCosmeticAndTravelOnTheAppearanceVersion()
        {
            // Applying them per frame would fight the game; they belong on the
            // change-only path, which means the change has to be noticed there.
            var vehicle = new RemoteVehicle(new EntityId(3));
            var state = new VehicleEntity(new EntityId(3)) { EngineHealth = 1000f };

            vehicle.Push(1d, state);
            int baseline = vehicle.AppearanceVersion;

            vehicle.Push(2d, state);
            Assert.Equal(baseline, vehicle.AppearanceVersion);

            state.NeonLayout = 0b1111;
            state.NeonColor = 0x00FF00FFu;
            vehicle.Push(3d, state);
            int afterNeon = vehicle.AppearanceVersion;
            Assert.NotEqual(baseline, afterNeon);

            state.RadioStation = 7;
            vehicle.Push(4d, state);
            Assert.NotEqual(afterNeon, vehicle.AppearanceVersion);
        }

        [Fact]
        public void NeonAndRadioSurviveTheWire()
        {
            var baseline = new VehicleEntity(new EntityId(3));
            var current = new VehicleEntity(new EntityId(3))
            {
                NeonColor = 0x0012ABCDu,
                NeonLayout = 0b1010,
                RadioStation = 12,
            };

            var serializer = new VehicleEntitySerializer();
            var writer = new Gtamp.Shared.Net.NetWriter(128);
            serializer.WriteDelta(writer, baseline, current);

            var applied = new VehicleEntity(new EntityId(3));
            serializer.ReadDelta(new Gtamp.Shared.Net.NetReader(writer.ToArray()), applied);

            Assert.Equal(0x0012ABCDu, applied.NeonColor);
            Assert.Equal(0b1010, applied.NeonLayout);
            Assert.Equal(12, applied.RadioStation);
        }
    }
}
