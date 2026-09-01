using System.Collections.Generic;
using Gtamp.Client.Diagnostics;
using Gtamp.Shared.Core;
using Gtamp.Shared.Entities;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The self test, which is the only place the suite and the engine meet.
    /// <para>
    /// Every game-layer feature in this project is written against an engine no test
    /// here can reach. The suite proves the decisions; it cannot prove that a
    /// particle effect name is spelled the way Rockstar spells it, or that a ped ends
    /// up in seat -1. This turns one play session into a report, and what is tested
    /// here is that the report tells the truth about what it did and did not check.
    /// </para>
    /// </summary>
    public class SelfTestTests
    {
        private static SelfTestResult Find(List<SelfTestResult> results, string name)
        {
            foreach (SelfTestResult result in results)
            {
                if (result.Name == name)
                {
                    return result;
                }
            }

            Assert.Fail($"the self test reported nothing named '{name}'");
            return default;
        }

        [Fact]
        public void AnUnconnectedClientSaysSoAndStops()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("alice");

            List<SelfTestResult> results = BridgeSelfTest.Run(client.Client);

            // One line, not a page of failures: nothing is broken, nothing was tried.
            Assert.Single(results);
            Assert.Equal(SelfTestOutcome.NotExercised, results[0].Outcome);
        }

        [Fact]
        public void AloneOnAServerNothingIsCalledBroken()
        {
            // The distinction the whole thing rests on. A lone player has exercised
            // almost none of this, and a self test that reported that as failure would
            // be a red light nobody could act on.
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("alice");
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.Client.IsConnected));
            harness.Advance(0.5d);

            List<SelfTestResult> results = BridgeSelfTest.Run(client.Client);

            foreach (SelfTestResult result in results)
            {
                Assert.False(
                    result.Outcome == SelfTestOutcome.Broken,
                    $"{result.Name} was reported broken on an idle session: {result.Detail}");
            }

            Assert.Contains(results, r => r.Name == "remote ped" && r.Outcome == SelfTestOutcome.NotExercised);
        }

        [Fact]
        public void ASecondPlayerMovesTheRemotePedCheckToWorking()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Bridge.Peds.Count > 0));

            List<SelfTestResult> results = BridgeSelfTest.Run(alice.Client);

            Assert.Contains(results, r => r.Name == "remote ped" && r.Outcome == SelfTestOutcome.Works);
        }

        [Fact]
        public void ThingsOnlyAHumanCanJudgeAreNotClaimedToWork()
        {
            // Blips, name tags and tracers are on the screen or they are not, and no
            // amount of state says which. Reporting them as working would be the exact
            // overclaim this project keeps finding in itself.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Bridge.Peds.Count > 0));

            List<SelfTestResult> results = BridgeSelfTest.Run(alice.Client);

            SelfTestResult blips = Find(results, "player blips");
            SelfTestResult names = Find(results, "player names");

            Assert.Equal(SelfTestOutcome.NeedsEyes, blips.Outcome);
            Assert.Equal(SelfTestOutcome.NeedsEyes, names.Outcome);
            Assert.Contains("look", blips.Detail);
        }

        [Fact]
        public void ASwitchedOffFeatureIsNotExercisedRatherThanUnseen()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Config.ShowPlayerBlips = false;
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Bridge.Peds.Count > 0));

            List<SelfTestResult> results = BridgeSelfTest.Run(alice.Client);

            Assert.Equal(SelfTestOutcome.NotExercised, Find(results, "player blips").Outcome);
        }

        [Fact]
        public void ABrokenReadIsReportedAsBroken()
        {
            // The failure the self test exists to catch: the bridge answered, and the
            // answer was wrong. Staged by making the fake bridge report no model.
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            alice.Bridge.Sample.ModelHash = 0;
            alice.Bridge.Sample.AimPosition = NetVector3.Zero;

            List<SelfTestResult> results = BridgeSelfTest.Run(alice.Client);

            Assert.Equal(SelfTestOutcome.Broken, Find(results, "local model").Outcome);
            Assert.Equal(SelfTestOutcome.Broken, Find(results, "aim position").Outcome);
        }

        [Fact]
        public void EveryResultCarriesSomethingToActOn()
        {
            using var harness = new TestHarness();
            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.Client.IsConnected));

            foreach (SelfTestResult result in BridgeSelfTest.Run(alice.Client))
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Name));
                Assert.False(
                    string.IsNullOrWhiteSpace(result.Detail),
                    $"{result.Name} reported an outcome with no detail to act on");
            }
        }

        [Fact]
        public void TheSummaryCountsWhatItListed()
        {
            var results = new List<SelfTestResult>
            {
                new SelfTestResult("a", SelfTestOutcome.Works, "fine"),
                new SelfTestResult("b", SelfTestOutcome.Broken, "wrong"),
                new SelfTestResult("c", SelfTestOutcome.NotExercised, "nothing to try"),
                new SelfTestResult("d", SelfTestOutcome.NeedsEyes, "go and look"),
            };

            string text = BridgeSelfTest.Format(results);

            Assert.Contains("1 working, 1 broken, 1 not exercised, 1 need a human to look", text);
            Assert.Contains("FAIL", text);

            // The nudge only appears when there is something the reader can do about it.
            Assert.Contains("run it again", text);
            Assert.DoesNotContain(
                "run it again",
                BridgeSelfTest.Format(new List<SelfTestResult>
                {
                    new SelfTestResult("a", SelfTestOutcome.Works, "fine"),
                }));
        }
    }
}
