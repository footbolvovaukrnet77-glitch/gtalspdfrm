using System.Linq;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    public class AppearanceReplicationTests
    {
        private static TestClient Connected(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.PlayerCount >= 1), $"{name} never connected");
            return client;
        }

        [Fact]
        public void APedIsRebuiltWhenThePlayersModelChanges()
        {
            // GTA V cannot change a ped's model in place, so a stale model means a
            // destroy and rebuild. Without it every remote player keeps whatever body
            // they were first drawn with — and that is almost never the right one,
            // because the ped is created from the first snapshot while the model
            // arrives in the first state update, which lands after it.
            using var harness = new TestHarness();

            TestClient alice = Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 2));
            Assert.True(
                harness.AdvanceUntil(() => bob.Bridge.Peds.Count > 0, timeoutSeconds: 5),
                "bob never created a ped for alice");

            int firstHandle = bob.Client.RemotePlayers.Players.Single().PedHandle;
            Assert.NotEqual(0, firstHandle);

            const uint FranklinModel = 0x9B810FA2;
            alice.Bridge.Sample.ModelHash = FranklinModel;

            Assert.True(
                harness.AdvanceUntil(
                    () => bob.Client.RemotePlayers.Players.Single().PedHandle != firstHandle,
                    timeoutSeconds: 5),
                "bob kept the old ped after alice's model changed");

            Assert.Equal(FranklinModel, bob.Client.RemotePlayers.Players.Single().ModelHash);

            // And it settles: an unchanged model must not rebuild the ped every frame.
            int rebuiltHandle = bob.Client.RemotePlayers.Players.Single().PedHandle;
            harness.Advance(1.0);
            Assert.Equal(rebuiltHandle, bob.Client.RemotePlayers.Players.Single().PedHandle);
        }

        [Fact]
        public void ClothingReachesTheOtherPlayersPed()
        {
            using var harness = new TestHarness();

            TestClient alice = Connected(harness, "alice");
            alice.Bridge.Sample.Appearance = new PedAppearance();
            alice.Bridge.Sample.Appearance.SetComponent(PedComponentSlot.Torso, 42, 3);
            alice.Bridge.Sample.Appearance.SetComponent(PedComponentSlot.Legs, 17, 1);
            alice.Bridge.Sample.Appearance.SetProp(PedPropSlot.Hat, 9, 2);

            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 2));

            Assert.True(
                harness.AdvanceUntil(() => bob.Bridge.PedAppearances.Count == 1, timeoutSeconds: 5),
                "alice's clothing never reached bob");

            foreach (PedAppearance applied in bob.Bridge.PedAppearances.Values)
            {
                Assert.Equal(42, applied.GetComponent(PedComponentSlot.Torso).Drawable);
                Assert.Equal(3, applied.GetComponent(PedComponentSlot.Torso).Texture);
                Assert.Equal(17, applied.GetComponent(PedComponentSlot.Legs).Drawable);
                Assert.Equal(9, applied.GetProp(PedPropSlot.Hat).Drawable);
            }
        }

        [Fact]
        public void ClothingIsAppliedOnChangeNotEveryFrame()
        {
            using var harness = new TestHarness();

            TestClient alice = Connected(harness, "alice");
            alice.Bridge.Sample.Appearance = new PedAppearance();
            alice.Bridge.Sample.Appearance.SetComponent(PedComponentSlot.Top, 5, 0);

            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.Bridge.PedAppearances.Count == 1, timeoutSeconds: 5));

            harness.Advance(3.0);

            // Writing component variations is ~20 native calls; doing it every frame
            // for every remote player would be pure waste.
            foreach (int applications in bob.Bridge.AppearanceApplications.Values)
            {
                Assert.True(applications <= 2, $"clothing was written {applications} times over three seconds");
            }
        }

        [Fact]
        public void AChangeOfClothesIsReplicated()
        {
            using var harness = new TestHarness();

            TestClient alice = Connected(harness, "alice");
            alice.Bridge.Sample.Appearance = new PedAppearance();
            alice.Bridge.Sample.Appearance.SetComponent(PedComponentSlot.Top, 5, 0);

            TestClient bob = Connected(harness, "bob");
            Assert.True(harness.AdvanceUntil(() => bob.Bridge.PedAppearances.Count == 1, timeoutSeconds: 5));

            alice.Bridge.Sample.Appearance.SetComponent(PedComponentSlot.Top, 77, 4);

            Assert.True(
                harness.AdvanceUntil(
                    () =>
                    {
                        foreach (PedAppearance applied in bob.Bridge.PedAppearances.Values)
                        {
                            if (applied.GetComponent(PedComponentSlot.Top).Drawable == 77)
                            {
                                return true;
                            }
                        }

                        return false;
                    },
                    timeoutSeconds: 5),
                "the change of clothes never reached bob");
        }

        [Fact]
        public void APlayerWithDefaultClothingCostsNoAppearanceWrites()
        {
            using var harness = new TestHarness();
            Connected(harness, "alice");
            TestClient bob = Connected(harness, "bob");

            Assert.True(harness.AdvanceUntil(() => bob.PlayerCount == 2));
            harness.Advance(2.0);

            // Nothing to apply, so the bridge is never asked to.
            Assert.Empty(bob.Bridge.AppearanceApplications);
        }
    }

    public class CorrectionTests
    {
        [Fact]
        public void AnAcceptedChangeIsNotUndoneByASnapshotThatPredatesIt()
        {
            // The race: the client reports a change, the server accepts it, but a
            // snapshot encoded before that report arrived is still in flight carrying
            // the old value. Measured against the report, that stale echo is
            // indistinguishable from a rejection — and correcting on it snaps the
            // player back to a value the server has already accepted, after which the
            // client reports the reverted value and the change is lost for good.
            //
            // The snapshot header echoes the client update sequence it took into
            // account, so each snapshot is judged against the report it answers.
            using var harness = new TestHarness();
            harness.Latency = 0.08;

            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));
            harness.Advance(1.0);

            int correctionsBefore = alice.Client.CorrectionsApplied;
            alice.Bridge.Sample.Health = 120;

            Assert.True(
                harness.AdvanceUntil(
                    () => harness.Server.World.GetPlayer(alice.Client.LocalEntityId)?.Health == 120,
                    timeoutSeconds: 5),
                "the server never accepted the health the client reported");

            harness.Advance(2.0);

            Assert.Equal(120, alice.Bridge.Sample.Health);
            Assert.Equal(120, harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!.Health);
            Assert.Equal(correctionsBefore, alice.Client.CorrectionsApplied);
        }

        [Fact]
        public void OrdinaryDamageAtLatencyDoesNotCauseACorrection()
        {
            // The snapshot answering an earlier report still shows the old health.
            // Measuring against the current local health would read that as
            // disagreement and undo the damage the player just took.
            using var harness = new TestHarness();
            harness.Latency = 0.1;
            harness.Network.Jitter = 0.04;

            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1, timeoutSeconds: 10));

            int corrections = alice.Client.CorrectionsApplied;

            for (int hit = 0; hit < 8; hit++)
            {
                alice.Bridge.Sample.Health -= 20;
                harness.Advance(0.25);
            }

            harness.Advance(1.0);

            Assert.Equal(corrections, alice.Client.CorrectionsApplied);
            Assert.Equal(40, alice.Bridge.Sample.Health);
            Assert.Equal(40, harness.Server.World.GetPlayer(alice.Client.LocalEntityId)!.Health);
        }

        [Fact]
        public void WalkingAtLatencyDoesNotCauseACorrection()
        {
            using var harness = new TestHarness();
            harness.Latency = 0.12;
            harness.Network.Jitter = 0.05;

            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1, timeoutSeconds: 10));

            int corrections = alice.Client.CorrectionsApplied;
            harness.Walk(alice, metres: 50f);
            harness.Advance(1.0);

            Assert.Equal(corrections, alice.Client.CorrectionsApplied);
        }
    }
}
