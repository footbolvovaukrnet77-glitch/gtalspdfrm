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
