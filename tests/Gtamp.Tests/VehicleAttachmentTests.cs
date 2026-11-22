using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// A vehicle carried by another one: lifted by a Cargobob, hooked to a tow truck,
    /// strapped to a flatbed.
    /// <para>
    /// <c>VehicleEntity.AttachedToId</c> has been on the wire since Phase 3. It was
    /// cloned, serialised, deserialised, compared for deltas and printed by the entity
    /// inspector — and read from nothing and applied to nothing, so a towed car was
    /// attached on the tower's screen and drifting free on everybody else's. Section 10
    /// of the specification lists attached vehicles; this is them.
    /// </para>
    /// <para>
    /// A trailer is a different thing and already worked: it has a hitch, its own
    /// native and a joint the game simulates. This is the other kind, where one vehicle
    /// simply becomes part of another until it is released.
    /// </para>
    /// </summary>
    public class VehicleAttachmentTests
    {
        private static (TestHarness Harness, TestClient Owner, TestClient Watcher) Pair()
        {
            var harness = new TestHarness();
            TestClient owner = harness.CreateClient("owner");
            TestClient watcher = harness.CreateClient("watcher");
            owner.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            watcher.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => owner.PlayerCount >= 2 && watcher.PlayerCount >= 2));
            return (harness, owner, watcher);
        }

        /// <summary>
        /// Two vehicles both owned by one client, one hanging off the other, and the
        /// question is whether the other client is told to hang them the same way.
        /// </summary>
        [Fact]
        public void AVehicleHangingOffAnotherIsHungTheSameWayOnEveryClient()
        {
            var (harness, owner, watcher) = Pair();
            using (harness)
            {
                // The carrier: the vehicle the owner is sitting in, which the existing
                // path already offers to the server.
                owner.Bridge.LocalVehicleHandle = 700;
                owner.Bridge.Vehicles[700] = new VehicleEntity(EntityId.None) { ModelHash = 0x4C80EB0E };

                // The carried one, offered through the same entry point the
                // shared-traffic controller uses for an ambient car. Called directly
                // rather than through that controller, because which client the server
                // nominates as the traffic source is not what this test is about.
                owner.Bridge.Vehicles[701] = new VehicleEntity(EntityId.None) { ModelHash = 0x9F05F101 };

                Assert.True(harness.AdvanceUntil(() => owner.Client.OwnedEntities.OwnedCount >= 1));
                owner.Client.OwnedEntities.RegisterVehicle(701, owner.Client.ReplicatedWorld.Current, harness.Now);

                Assert.True(
                    harness.AdvanceUntil(() => owner.Client.OwnedEntities.OwnedCount >= 2),
                    "the server never adopted both vehicles");

                // Now the game says one is hanging off the other.
                owner.Bridge.AttachedInGame[701] = 700;

                Assert.True(
                    harness.AdvanceUntil(() =>
                    {
                        foreach (var pair in watcher.Bridge.VehicleCarrier)
                        {
                            if (pair.Value != 0)
                            {
                                return true;
                            }
                        }

                        return false;
                    }),
                    "the watcher was never told to attach anything");
            }
        }

        /// <summary>
        /// Releasing has to travel too. A carried vehicle whose release is not
        /// replicated stays welded to its carrier on every screen but the owner's,
        /// which is worse than never attaching it — at least an unattached car falls
        /// somewhere sensible.
        /// </summary>
        [Fact]
        public void ReleasingTravelsAsWellAsAttaching()
        {
            var (harness, owner, watcher) = Pair();
            using (harness)
            {
                owner.Bridge.LocalVehicleHandle = 700;
                owner.Bridge.Vehicles[700] = new VehicleEntity(EntityId.None) { ModelHash = 0x4C80EB0E };
                owner.Bridge.Vehicles[701] = new VehicleEntity(EntityId.None) { ModelHash = 0x9F05F101 };

                Assert.True(harness.AdvanceUntil(() => owner.Client.OwnedEntities.OwnedCount >= 1));
                owner.Client.OwnedEntities.RegisterVehicle(701, owner.Client.ReplicatedWorld.Current, harness.Now);
                Assert.True(harness.AdvanceUntil(() => owner.Client.OwnedEntities.OwnedCount >= 2));

                owner.Bridge.AttachedInGame[701] = 700;
                Assert.True(harness.AdvanceUntil(() => AnyCarried(watcher)));

                owner.Bridge.AttachedInGame[701] = 0;
                Assert.True(
                    harness.AdvanceUntil(() => !AnyCarried(watcher)),
                    "the watcher was never told to let it go");
            }
        }

        /// <summary>
        /// A car attached to something that is not a replicated vehicle — an ambient tow
        /// truck nobody has adopted, or a prop a mod glued it to — must report nothing
        /// rather than a wrong id. An id nobody can resolve reaches every client, each
        /// of which looks it up and finds nothing.
        /// </summary>
        [Fact]
        public void ACarrierTheServerDoesNotKnowIsReportedAsNoCarrier()
        {
            var (harness, owner, _) = Pair();
            using (harness)
            {
                owner.Bridge.LocalVehicleHandle = 700;
                owner.Bridge.Vehicles[700] = new VehicleEntity(EntityId.None) { ModelHash = 0x4C80EB0E };

                Assert.True(harness.AdvanceUntil(() => owner.Client.OwnedEntities.OwnedCount >= 1));

                // 999 is a handle the server has never heard of.
                owner.Bridge.AttachedInGame[700] = 999;
                harness.Advance(1.0);

                foreach (NetEntity entity in harness.Server.World.State.Entities)
                {
                    if (entity is VehicleEntity vehicle)
                    {
                        Assert.Equal(EntityId.None, vehicle.AttachedToId);
                    }
                }
            }
        }

        private static bool AnyCarried(TestClient client)
        {
            foreach (var pair in client.Bridge.VehicleCarrier)
            {
                if (pair.Value != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
