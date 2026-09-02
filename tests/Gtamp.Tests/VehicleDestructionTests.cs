using System.Linq;
using Gtamp.Client.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The explosion everybody else's screen was missing.
    /// <para>
    /// A car blowing up in front of another player turned into a blackened wreck
    /// between two frames on every screen but theirs: no fireball, no sound, no
    /// reason for the wreck to be there. <c>VehicleFlags.Burnt</c> was declared in
    /// the first version of the flags and filed under &#34;derived from engine and body
    /// health&#34; — which nothing derived, so it was always false, and which is not
    /// reliably derivable anyway: telling &#34;the engine is dead&#34; from &#34;the car
    /// exploded&#34; means picking a threshold below zero and hoping.
    /// </para>
    /// </summary>
    public class VehicleDestructionTests
    {
        private static TestClient Join(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            return client;
        }

        private static VehicleEntity Car(TestHarness harness, VehicleFlags flags = VehicleFlags.None)
        {
            var car = new VehicleEntity(harness.Server.World.AllocateEntityId())
            {
                Position = new NetVector3(220f, -800f, 30f),
                ModelHash = 0x0BBA91E1,
                EngineHealth = 1000f,
                BodyHealth = 1000f,
                Flags = flags,
            };

            harness.Server.World.Spawn(car);
            return car;
        }

        [Fact]
        public void ACarDestroyedElsewhereExplodesHere()
        {
            using var harness = new TestHarness();
            TestClient watcher = Join(harness, "watcher");
            Assert.True(harness.AdvanceUntil(() => watcher.Client.IsConnected));

            VehicleEntity car = Car(harness);
            Assert.True(harness.AdvanceUntil(() =>
                watcher.Client.RemoteEntities.Vehicles.Any(v => v.VehicleHandle != 0)));

            // Seen alive first, which is what makes the next frame a transition.
            Assert.Empty(watcher.Bridge.VehicleExplosions);

            car.Flags |= VehicleFlags.Burnt;
            car.EngineHealth = -4000f;
            harness.Server.World.Touch(car);

            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.VehicleExplosions.Count == 1));
            Assert.Equal(1, watcher.Client.RemoteEntities.VehicleExplosionsDrawn);
        }

        [Fact]
        public void ItIsDrawnOnceRatherThanEveryFrameTheWreckIsVisible()
        {
            using var harness = new TestHarness();
            TestClient watcher = Join(harness, "watcher");
            Assert.True(harness.AdvanceUntil(() => watcher.Client.IsConnected));

            VehicleEntity car = Car(harness);
            Assert.True(harness.AdvanceUntil(() =>
                watcher.Client.RemoteEntities.Vehicles.Any(v => v.VehicleHandle != 0)));

            car.Flags |= VehicleFlags.Burnt;
            harness.Server.World.Touch(car);
            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.VehicleExplosions.Count == 1));

            // The wreck stays a wreck for as long as it is there, and a state that is
            // true every frame is not an event every frame.
            harness.Advance(2d);
            Assert.Single(watcher.Bridge.VehicleExplosions);
        }

        [Fact]
        public void AWreckThatWasAlreadyAWreckDoesNotDetonateOnArrival()
        {
            using var harness = new TestHarness();

            // The car was destroyed before this player ever saw it — a wreck standing
            // in the street when they joined. Redrawing its death for every arrival is
            // the obvious way to get this wrong.
            VehicleEntity car = Car(harness, VehicleFlags.Burnt);
            car.EngineHealth = -4000f;

            TestClient arrival = Join(harness, "arrival");
            Assert.True(harness.AdvanceUntil(() => arrival.Client.IsConnected));
            Assert.True(harness.AdvanceUntil(() =>
                arrival.Client.RemoteEntities.Vehicles.Any(v => v.VehicleHandle != 0)));

            harness.Advance(2d);
            Assert.Empty(arrival.Bridge.VehicleExplosions);
            Assert.Equal(0, arrival.Client.RemoteEntities.VehicleExplosionsDrawn);
        }

        [Fact]
        public void ARepairedCarCanExplodeAgain()
        {
            using var harness = new TestHarness();
            TestClient watcher = Join(harness, "watcher");
            Assert.True(harness.AdvanceUntil(() => watcher.Client.IsConnected));

            VehicleEntity car = Car(harness);
            Assert.True(harness.AdvanceUntil(() =>
                watcher.Client.RemoteEntities.Vehicles.Any(v => v.VehicleHandle != 0)));

            car.Flags |= VehicleFlags.Burnt;
            harness.Server.World.Touch(car);
            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.VehicleExplosions.Count == 1));

            // A mod repairing a wreck is a transition out; the next destruction is a
            // new event, not a repeat of the old one.
            car.Flags &= ~VehicleFlags.Burnt;
            harness.Server.World.Touch(car);
            harness.Advance(0.5d);
            Assert.Single(watcher.Bridge.VehicleExplosions);

            car.Flags |= VehicleFlags.Burnt;
            harness.Server.World.Touch(car);
            Assert.True(harness.AdvanceUntil(() => watcher.Bridge.VehicleExplosions.Count == 2));
        }

        [Fact]
        public void BurntSurvivesTheWire()
        {
            var baseline = new VehicleEntity(new EntityId(2));
            var current = new VehicleEntity(new EntityId(2)) { Flags = VehicleFlags.Burnt };

            var serializer = new VehicleEntitySerializer();
            var writer = new Gtamp.Shared.Net.NetWriter(64);
            serializer.WriteDelta(writer, baseline, current);

            var applied = new VehicleEntity(new EntityId(2));
            serializer.ReadDelta(new Gtamp.Shared.Net.NetReader(writer.ToArray()), applied);

            Assert.True(applied.HasFlag(VehicleFlags.Burnt));
        }
    }
}
