using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Gtamp.Client.Missions;
using Gtamp.Server.Core;
using Gtamp.Server.Missions;
using Gtamp.Server.Players;
using Gtamp.Shared.Entities;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>Records what a mod would have been told.</summary>
    public sealed class RecordingActivityHandler : IActivityHandler
    {
        public List<string> Started { get; } = new List<string>();

        public List<string> Objectives { get; } = new List<string>();

        public List<ActivityState> Finished { get; } = new List<ActivityState>();

        public void OnStarted(ActivityEntity activity) => Started.Add(activity.DefinitionId);

        public void OnObjectiveChanged(ActivityEntity activity, ActivityObjective objective) =>
            Objectives.Add($"{objective.Id}:{objective.State}");

        public void OnFinished(ActivityEntity activity) => Finished.Add(activity.State);
    }

    public class ActivityManagerTests
    {
        private static ServerConfig Config() => new ServerConfig
        {
            PersistenceEnabled = false,
            SaveIntervalSeconds = 0,
            FinishedActivityLingerSeconds = 1,
        };

        private static ActivityDefinition Traffic() =>
            new ActivityDefinition("traffic-stop", "Traffic stop")
                .WithObjective(1, "Pull the vehicle over")
                .WithObjective(2, "Speak to the driver")
                .WithObjective(3, "Resolve the stop");

        [Fact]
        public void StartingAnActivityActivatesItsFirstObjective()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Activities.Register(Traffic());

            ActivityEntity? activity = harness.Server.Activities.Start("traffic-stop", 0, harness.Now);

            Assert.NotNull(activity);
            Assert.Equal(ActivityState.Running, activity!.State);
            Assert.Equal(3, activity.Objectives.Count);
            Assert.Equal(ObjectiveState.Active, activity.Objectives[0].State);
            Assert.Equal(ObjectiveState.Pending, activity.Objectives[1].State);
        }

        [Fact]
        public void AnUnknownActivityIsRefused()
        {
            using var harness = new TestHarness(Config());
            Assert.Null(harness.Server.Activities.Start("never-registered", 0, harness.Now));
        }

        [Fact]
        public void CompletingObjectivesAdvancesAndThenFinishesTheActivity()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Activities.Register(Traffic());
            ActivityEntity activity = harness.Server.Activities.Start("traffic-stop", 0, harness.Now)!;

            harness.Server.Activities.SetObjectiveState(activity.Id, 1, ObjectiveState.Completed, harness.Now);
            Assert.Equal(ObjectiveState.Active, activity.Objectives[1].State);

            harness.Server.Activities.SetObjectiveState(activity.Id, 2, ObjectiveState.Completed, harness.Now);
            Assert.Equal(ObjectiveState.Active, activity.Objectives[2].State);
            Assert.Equal(ActivityState.Running, activity.State);

            harness.Server.Activities.SetObjectiveState(activity.Id, 3, ObjectiveState.Completed, harness.Now);
            Assert.Equal(ActivityState.Completed, activity.State);
            Assert.Equal(1, harness.Server.Activities.Completed);
        }

        [Fact]
        public void AFailedObjectiveFailsTheActivityAndSkipsWhatIsLeft()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Activities.Register(Traffic());
            ActivityEntity activity = harness.Server.Activities.Start("traffic-stop", 0, harness.Now)!;

            harness.Server.Activities.SetObjectiveState(activity.Id, 1, ObjectiveState.Failed, harness.Now);

            Assert.Equal(ActivityState.Failed, activity.State);

            // Nothing is left showing as live on a finished activity.
            Assert.DoesNotContain(activity.Objectives, o => o.State is ObjectiveState.Active or ObjectiveState.Pending);
        }

        [Fact]
        public void AnActivityWhoseObjectivesDoNotDecideTheEndingKeepsRunning()
        {
            using var harness = new TestHarness(Config());

            var definition = new ActivityDefinition("patrol", "Patrol") { CompleteWhenObjectivesResolved = false };
            definition.WithObjective(1, "Drive around");
            harness.Server.Activities.Register(definition);

            ActivityEntity activity = harness.Server.Activities.Start("patrol", 0, harness.Now)!;
            harness.Server.Activities.SetObjectiveState(activity.Id, 1, ObjectiveState.Completed, harness.Now);

            Assert.Equal(ActivityState.Running, activity.State);

            harness.Server.Activities.Finish(activity.Id, ActivityState.Completed, "the mod said so", harness.Now);
            Assert.Equal(ActivityState.Completed, activity.State);
        }

        [Fact]
        public void ATimeLimitFailsTheActivity()
        {
            using var harness = new TestHarness(Config());

            var definition = new ActivityDefinition("delivery", "Delivery") { TimeLimitSeconds = 1.0 };
            definition.WithObjective(1, "Deliver the package");
            harness.Server.Activities.Register(definition);

            ActivityEntity activity = harness.Server.Activities.Start("delivery", 0, harness.Now)!;
            Assert.Equal(ActivityState.Running, activity.State);

            harness.Advance(2.0);

            Assert.Equal(ActivityState.Failed, activity.State);
            Assert.Equal(1, harness.Server.Activities.Failed);
        }

        [Fact]
        public void AFinishedActivityAndItsEntitiesAreCleanedUpAfterTheLinger()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Activities.Register(Traffic());
            ActivityEntity activity = harness.Server.Activities.Start("traffic-stop", 0, harness.Now)!;

            var suspect = new PedEntity(harness.Server.World.AllocateEntityId()) { GroupId = "traffic-stop" };
            harness.Server.World.Spawn(suspect);
            harness.Server.Activities.AddEntity(activity.Id, suspect.Id);

            harness.Server.Activities.Finish(activity.Id, ActivityState.Completed, "done", harness.Now);
            harness.Advance(3.0);

            Assert.False(harness.Server.World.State.Contains(activity.Id));
            Assert.False(harness.Server.World.State.Contains(suspect.Id));
        }

        [Fact]
        public void ADepartingPlayerLeavesEveryActivityTheyWereIn()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Activities.Register(Traffic());

            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

            uint playerId = alice.Client.LocalPlayerId;
            ActivityEntity activity = harness.Server.Activities.Start("traffic-stop", playerId, harness.Now)!;
            Assert.True(activity.HasParticipant(playerId));

            alice.Client.Disconnect("test");
            harness.RemoveClient(alice);
            Assert.True(harness.AdvanceUntil(() => harness.Server.Players.Count == 0, timeoutSeconds: 5));

            Assert.False(activity.HasParticipant(playerId));
        }
    }

    public class ActivityReplicationTests
    {
        private static ServerConfig Config() => new ServerConfig
        {
            PersistenceEnabled = false,
            SaveIntervalSeconds = 0,
            FinishedActivityLingerSeconds = 60,
        };

        [Fact]
        public void AnActivityReachesTheClientThroughTheOrdinaryEntityPath()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Activities.Register(
                new ActivityDefinition("heist", "Bank job").WithObjective(1, "Get inside"));

            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

            var handler = new RecordingActivityHandler();
            alice.Client.Sdk.RegisterMission("heist", handler);

            ActivityEntity activity = harness.Server.Activities.Start("heist", alice.Client.LocalPlayerId, harness.Now)!;

            Assert.True(
                harness.AdvanceUntil(() => handler.Started.Count == 1, timeoutSeconds: 5),
                "the client's mod was never told the activity started");

            Assert.Equal("heist", handler.Started[0]);
            Assert.Equal(1, alice.Client.Activities.TrackedCount);

            harness.Server.Activities.SetObjectiveState(activity.Id, 1, ObjectiveState.Completed, harness.Now);

            Assert.True(
                harness.AdvanceUntil(() => handler.Finished.Count == 1, timeoutSeconds: 5),
                "the client's mod was never told the activity finished");

            Assert.Equal(ActivityState.Completed, handler.Finished[0]);
            Assert.Contains("1:Completed", handler.Objectives);
        }

        [Fact]
        public void AClientWithNoHandlerForAnActivityIsUnaffected()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Activities.Register(new ActivityDefinition("heist", "Bank job").WithObjective(1, "Get inside"));

            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

            harness.Server.Activities.Start("heist", 0, harness.Now);
            harness.Advance(1.0);

            // It still replicates and is still visible; there is simply nothing local
            // to run for it.
            Assert.Equal(1, alice.Client.Activities.TrackedCount);
            Assert.True(alice.Client.IsConnected);
        }

        [Fact]
        public void AHandlerThatThrowsDoesNotBreakTheSession()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Activities.Register(new ActivityDefinition("bad", "Broken mod").WithObjective(1, "x"));

            TestClient alice = harness.CreateClient("alice");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => alice.PlayerCount >= 1));

            alice.Client.Sdk.RegisterMission("bad", new ThrowingHandler());
            harness.Server.Activities.Start("bad", 0, harness.Now);
            harness.Advance(1.0);

            Assert.True(alice.Client.IsConnected);
            Assert.Equal(1, alice.Client.Activities.TrackedCount);
        }

        private sealed class ThrowingHandler : IActivityHandler
        {
            public void OnStarted(ActivityEntity activity) => throw new InvalidOperationException("boom");

            public void OnObjectiveChanged(ActivityEntity activity, ActivityObjective objective) =>
                throw new InvalidOperationException("boom");

            public void OnFinished(ActivityEntity activity) => throw new InvalidOperationException("boom");
        }
    }

    public class RpcTests
    {
        private static ServerConfig Config() => new ServerConfig
        {
            PersistenceEnabled = false,
            SaveIntervalSeconds = 0,
        };

        private static TestClient Connected(TestHarness harness, string name)
        {
            TestClient client = harness.CreateClient(name);
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.PlayerCount >= 1), $"{name} never connected");
            return client;
        }

        [Fact]
        public void AClientCallsTheServerAndGetsAnAnswer()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Mods.RegisterRpc(
                "bank.balance",
                (session, payload) => Encoding.UTF8.GetBytes($"{session.Name}:2500"));

            TestClient alice = Connected(harness, "alice");

            RpcResult? result = null;
            alice.Client.Sdk.CallServerRpc("bank.balance", Array.Empty<byte>(), r => result = r);

            Assert.True(harness.AdvanceUntil(() => result != null, timeoutSeconds: 5), "the RPC never came back");
            Assert.True(result!.Value.Success);
            Assert.Equal("alice:2500", Encoding.UTF8.GetString(result.Value.Payload));
        }

        [Fact]
        public void TheRequestPayloadReachesTheHandler()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Mods.RegisterRpc(
                "echo",
                (_, payload) => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).ToUpperInvariant()));

            TestClient alice = Connected(harness, "alice");

            RpcResult? result = null;
            alice.Client.Sdk.CallServerRpc("echo", Encoding.UTF8.GetBytes("hello"), r => result = r);

            Assert.True(harness.AdvanceUntil(() => result != null, timeoutSeconds: 5));
            Assert.Equal("HELLO", Encoding.UTF8.GetString(result!.Value.Payload));
        }

        [Fact]
        public void AnUnknownProcedureFailsWithAReadableReason()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = Connected(harness, "alice");

            RpcResult? result = null;
            alice.Client.Sdk.CallServerRpc("never.registered", Array.Empty<byte>(), r => result = r);

            Assert.True(harness.AdvanceUntil(() => result != null, timeoutSeconds: 5));
            Assert.False(result!.Value.Success);
            Assert.Contains("no handler", result.Value.Error);
        }

        [Fact]
        public void AHandlerThatThrowsFailsTheCallRatherThanHangingIt()
        {
            using var harness = new TestHarness(Config());
            harness.Server.Mods.RegisterRpc("boom", (_, _) => throw new InvalidOperationException("bad mod"));

            TestClient alice = Connected(harness, "alice");

            RpcResult? result = null;
            alice.Client.Sdk.CallServerRpc("boom", Array.Empty<byte>(), r => result = r);

            Assert.True(harness.AdvanceUntil(() => result != null, timeoutSeconds: 5));
            Assert.False(result!.Value.Success);
            Assert.Contains("bad mod", result.Value.Error);
            Assert.True(alice.Client.IsConnected);
        }

        [Fact]
        public void ACallWithNoAnswerTimesOutRatherThanHangingForever()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = Connected(harness, "alice");

            // Cut the link so nothing can come back.
            harness.PacketLoss = 1.0;

            RpcResult? result = null;
            alice.Client.Sdk.CallServerRpc("anything", Array.Empty<byte>(), r => result = r, timeoutSeconds: 1.0);

            Assert.True(harness.AdvanceUntil(() => result != null, timeoutSeconds: 5), "the call never timed out");
            Assert.False(result!.Value.Success);
            Assert.Contains("timed out", result.Value.Error);
        }

        [Fact]
        public void CallingWhileDisconnectedFailsImmediately()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = harness.CreateClient("alice");

            RpcResult? result = null;
            alice.Client.Sdk.CallServerRpc("anything", Array.Empty<byte>(), r => result = r);

            // No advancing: it must already have failed rather than waiting out a timeout.
            Assert.NotNull(result);
            Assert.False(result!.Value.Success);
            Assert.Contains("not connected", result.Value.Error);
        }

        [Fact]
        public void TheServerCallsTheClientAndGetsAnAnswer()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = Connected(harness, "alice");

            alice.Client.Sdk.RegisterRPC("client.ping", payload => Encoding.UTF8.GetBytes("pong"));
            harness.Advance(0.2);

            RpcResult? result = null;
            harness.Server.Mods.CurrentTime = harness.Now;
            harness.Server.Mods.CallClientRpc(
                harness.Server.Players.Sessions[0], "client.ping", Array.Empty<byte>(), r => result = r);

            Assert.True(harness.AdvanceUntil(() => result != null, timeoutSeconds: 5), "the client never answered");
            Assert.True(result!.Value.Success);
            Assert.Equal("pong", Encoding.UTF8.GetString(result.Value.Payload));
        }

        [Fact]
        public void OutstandingCallsFailWhenTheConnectionDrops()
        {
            using var harness = new TestHarness(Config());
            TestClient alice = Connected(harness, "alice");

            harness.PacketLoss = 1.0;

            RpcResult? result = null;
            alice.Client.Sdk.CallServerRpc("anything", Array.Empty<byte>(), r => result = r, timeoutSeconds: 60.0);

            // A mod holding a callback for a minute after a disconnect looks like a hang.
            alice.Client.Disconnect("test");
            harness.Advance(0.5);

            Assert.NotNull(result);
            Assert.False(result!.Value.Success);
        }

        [Fact]
        public void EventsWorkEvenWhenTheTwoSidesRegisterThemInDifferentOrders()
        {
            // Assigning ids in registration order would mean the two sides only agree
            // while they register in the same order, and adding an event on one side
            // would silently renumber every later one on that side alone.
            using var harness = new TestHarness(Config());

            var serverGotFirst = new List<string>();
            var serverGotSecond = new List<string>();
            harness.Server.Mods.RegisterNetworkEvent("mod.alpha", (_, p) => serverGotFirst.Add(Encoding.UTF8.GetString(p)));
            harness.Server.Mods.RegisterNetworkEvent("mod.beta", (_, p) => serverGotSecond.Add(Encoding.UTF8.GetString(p)));

            TestClient alice = Connected(harness, "alice");

            // Deliberately the other way round.
            alice.Client.Sdk.RegisterNetworkEvent("mod.beta", (_, _) => { });
            alice.Client.Sdk.RegisterNetworkEvent("mod.alpha", (_, _) => { });

            alice.Client.Sdk.SendNetworkEvent("mod.alpha", Encoding.UTF8.GetBytes("A"));
            alice.Client.Sdk.SendNetworkEvent("mod.beta", Encoding.UTF8.GetBytes("B"));

            Assert.True(
                harness.AdvanceUntil(() => serverGotFirst.Count == 1 && serverGotSecond.Count == 1, timeoutSeconds: 5),
                "events were mis-routed when the registration orders differed");

            Assert.Equal("A", serverGotFirst[0]);
            Assert.Equal("B", serverGotSecond[0]);
        }

        [Fact]
        public void AServerModEventReachesTheClientAndBack()
        {
            using var harness = new TestHarness(Config());

            var receivedOnServer = new List<string>();
            harness.Server.Mods.RegisterNetworkEvent(
                "mymod.ping", (session, payload) => receivedOnServer.Add(Encoding.UTF8.GetString(payload)));

            TestClient alice = Connected(harness, "alice");

            var receivedOnClient = new List<string>();
            alice.Client.Sdk.RegisterNetworkEvent(
                "mymod.ping", (sender, payload) => receivedOnClient.Add(Encoding.UTF8.GetString(payload)));

            harness.Server.Mods.BroadcastNetworkEvent("mymod.ping", Encoding.UTF8.GetBytes("from-server"));
            Assert.True(harness.AdvanceUntil(() => receivedOnClient.Count == 1, timeoutSeconds: 5));
            Assert.Equal("from-server", receivedOnClient[0]);

            alice.Client.Sdk.SendNetworkEvent("mymod.ping", Encoding.UTF8.GetBytes("from-client"));
            Assert.True(harness.AdvanceUntil(() => receivedOnServer.Count == 1, timeoutSeconds: 5));
            Assert.Equal("from-client", receivedOnServer[0]);
        }
    }
}
