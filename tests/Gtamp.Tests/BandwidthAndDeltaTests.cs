using System.Linq;
using Gtamp.Server.Core;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Net;
using Xunit;

namespace Gtamp.Tests
{
    public class BandwidthShaperTests
    {
        [Fact]
        public void ItStartsAtTheFullBudget()
        {
            var shaper = new BandwidthShaper(2048);
            Assert.Equal(2048, shaper.CurrentBudget);
        }

        [Fact]
        public void CongestionCutsTheBudget()
        {
            var shaper = new BandwidthShaper(2048);
            shaper.Update(0, 0, 0);

            // 20 lost out of 100 in one second: well past the congestion threshold.
            shaper.Update(100, 20, 1.1);

            Assert.Equal(1536, shaper.CurrentBudget);
            Assert.Equal(1, shaper.Decreases);
            Assert.Equal(0.2, shaper.RecentLoss, 2);
        }

        [Fact]
        public void SustainedCongestionBottomsOutAtTheFloorRatherThanZero()
        {
            var shaper = new BandwidthShaper(2048, minimumBudget: 512);
            shaper.Update(0, 0, 0);

            int sent = 0;
            int lost = 0;
            for (int second = 1; second <= 20; second++)
            {
                sent += 100;
                lost += 30;
                shaper.Update(sent, lost, second + 0.1);
            }

            // Below the floor a client stops converging on the world at all.
            Assert.Equal(512, shaper.CurrentBudget);
        }

        [Fact]
        public void ACleanLinkCreepsBackUpToTheMaximum()
        {
            var shaper = new BandwidthShaper(2000, minimumBudget: 256);
            shaper.Update(0, 0, 0);
            shaper.Update(100, 40, 1.1);

            int afterCongestion = shaper.CurrentBudget;
            Assert.True(afterCongestion < 2000);

            int sent = 100;
            for (int second = 2; second <= 20; second++)
            {
                sent += 100;
                shaper.Update(sent, 40, second + 0.1);
            }

            Assert.Equal(2000, shaper.CurrentBudget);
            Assert.True(shaper.Increases > 0);
        }

        [Fact]
        public void RecoveryIsSlowerThanTheBackoff()
        {
            // Multiplicative decrease, additive increase: back off fast enough to
            // relieve congestion, recover slowly enough not to re-cause it.
            var shaper = new BandwidthShaper(1000, minimumBudget: 100);
            shaper.Update(0, 0, 0);
            shaper.Update(100, 50, 1.1);

            int dropped = 1000 - shaper.CurrentBudget;

            int before = shaper.CurrentBudget;
            shaper.Update(200, 50, 2.2);
            int gained = shaper.CurrentBudget - before;

            Assert.True(gained < dropped, $"gained {gained} in one step after dropping {dropped}");
        }

        [Fact]
        public void ItIgnoresIntervalsWithTooLittleTrafficToJudge()
        {
            var shaper = new BandwidthShaper(2048);
            shaper.Update(0, 0, 0);

            // Two packets, both lost. That is 100% loss on a sample far too small to
            // act on; reacting would throttle a client that has simply been idle.
            shaper.Update(2, 2, 1.1);

            Assert.Equal(2048, shaper.CurrentBudget);
            Assert.Equal(0, shaper.Decreases);
        }

        [Fact]
        public void ItOnlyActsOncePerInterval()
        {
            var shaper = new BandwidthShaper(2048);
            shaper.Update(0, 0, 0);

            shaper.Update(100, 30, 1.1);
            int after = shaper.CurrentBudget;

            shaper.Update(200, 60, 1.2);
            Assert.Equal(after, shaper.CurrentBudget);
        }
    }

    public class OwnedEntityDeltaTests
    {
        private const uint Adder = 0xB779A091;

        private static TestClient Connected(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.PlayerCount >= 1), $"{name} never connected");
            return client;
        }

        [Fact]
        public void TheOwnerStreamsDeltasOnceItHasASnapshotToBaseThemOn()
        {
            using var harness = new TestHarness(new ServerConfig
            {
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
            });

            TestClient alice = Connected(harness, "alice");
            int handle = alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f));

            Assert.True(harness.AdvanceUntil(() => alice.Client.OwnedEntities.OwnedCount == 1, timeoutSeconds: 5));
            harness.Advance(2.0);

            Assert.True(alice.Client.OwnedEntities.DeltaUpdatesSent > 10, "the owner never switched to deltas");
            Assert.True(harness.Server.Entities.DeltaUpdatesApplied > 10, "the server never applied a delta");
        }

        [Fact]
        public void DeltaStreamingStillDeliversTheOwnersChanges()
        {
            using var harness = new TestHarness(new ServerConfig
            {
                PersistenceEnabled = false,
                SaveIntervalSeconds = 0,
            });

            TestClient alice = Connected(harness, "alice");
            int handle = alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f));
            Assert.True(harness.AdvanceUntil(() => alice.Client.OwnedEntities.OwnedCount == 1, timeoutSeconds: 5));
            harness.Advance(1.0);

            alice.Bridge.Vehicles[handle].BodyHealth = 333f;
            alice.Bridge.Vehicles[handle].Flags = VehicleFlags.EngineRunning | VehicleFlags.Lights;
            alice.Bridge.Vehicles[handle].Doors = new VehicleDoorStates(0).WithOpen(2, true);

            Assert.True(
                harness.AdvanceUntil(
                    () =>
                    {
                        VehicleEntity? vehicle = harness.Server.World.State.OfType<VehicleEntity>().FirstOrDefault();
                        return vehicle != null && vehicle.BodyHealth < 400f;
                    },
                    timeoutSeconds: 5),
                "the delta never carried the change");

            VehicleEntity onServer = harness.Server.World.State.OfType<VehicleEntity>().First();
            Assert.Equal(333f, onServer.BodyHealth, 0);
            Assert.True(onServer.HasFlag(VehicleFlags.Lights));
            Assert.True(onServer.Doors.IsOpen(2));
        }

        [Fact]
        public void DeltaStreamingSurvivesPacketLoss()
        {
            // Owned updates are unreliable, so a delta is routinely lost. The baseline
            // is a snapshot the server still holds, not "whatever I sent last", so a
            // lost update costs one frame of freshness rather than desynchronising the
            // chain.
            using var harness = new TestHarness(
                new ServerConfig { PersistenceEnabled = false, SaveIntervalSeconds = 0 }, seed: 909);

            harness.PacketLoss = 0.25;
            harness.Latency = 0.05;

            TestClient alice = Connected(harness, "alice");
            int handle = alice.Bridge.PutLocalPlayerInVehicle(Adder, new NetVector3(220f, -810f, 30f));
            Assert.True(harness.AdvanceUntil(() => alice.Client.OwnedEntities.OwnedCount == 1, timeoutSeconds: 15));

            for (int step = 0; step < 20; step++)
            {
                alice.Bridge.Vehicles[handle].BodyHealth = 1000f - (step * 40f);
                harness.Advance(0.2);
            }

            harness.Advance(2.0);

            VehicleEntity onServer = harness.Server.World.State.OfType<VehicleEntity>().First();
            Assert.Equal(240f, onServer.BodyHealth, 0);
        }
    }
}
