using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gtamp.Adapters.Lspdfr;
using Gtamp.Adapters.Rph;
using Gtamp.Client.Mods;
using Gtamp.Shared.Interop;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The seam between the two plugin hosts (master prompt sections 19 and 18).
    /// <para>
    /// None of this can be tested against a real RAGE Plugin Hook here — RPH only
    /// exists inside a running Windows GTA V process. What <em>can</em> be tested is
    /// everything either side does with the bytes: the framing both sides must agree
    /// on, the bounded queues, the fan-out to more than one adapter, and the LSPDFR
    /// state actually reaching another player's client through a server that knows
    /// nothing about LSPDFR. That is the part that would otherwise only be found out
    /// in-game.
    /// </para>
    /// </summary>
    public class InteropTests : IDisposable
    {
        public void Dispose() => BridgeLink.OverrideShared(null);

        [Fact]
        public void FramingRoundTripsTheTopicAndThePayload()
        {
            byte[] payload = { 1, 2, 3, 250, 0 };
            byte[] frame = InProcessEndpoint.Frame(InteropTopics.LspdfrEvent, payload);

            Assert.True(InProcessEndpoint.TryUnframe(frame, out string topic, out byte[] decoded));
            Assert.Equal(InteropTopics.LspdfrEvent, topic);
            Assert.Equal(payload, decoded);
        }

        [Fact]
        public void AnEmptyPayloadSurvivesTheRoundTrip()
        {
            byte[] frame = InProcessEndpoint.Frame(InteropTopics.Describe, Array.Empty<byte>());

            Assert.True(InProcessEndpoint.TryUnframe(frame, out string topic, out byte[] decoded));
            Assert.Equal(InteropTopics.Describe, topic);
            Assert.Empty(decoded);
        }

        [Fact]
        public void AFrameFromADifferentChannelVersionIsSkippedRatherThanThrowing()
        {
            // The realistic case: the player updated one half of the install and not
            // the other. Tearing the channel down would take the working half with it.
            byte[] frame = InProcessEndpoint.Frame(InteropTopics.Hello, new byte[] { 7 });
            frame[0] = (byte)(InProcessChannel.Version + 1);

            Assert.False(InProcessEndpoint.TryUnframe(frame, out _, out _));

            (InProcessEndpoint core, InProcessEndpoint plugin) = InProcessChannel.CreateLoopbackPair();
            plugin.Send(InteropTopics.Hello, new byte[] { 7 });
            core.Send(InteropTopics.Hello, Array.Empty<byte>()); // keeps the pair usable

            Assert.True(plugin.TryReceive(out string topic, out _));
            Assert.Equal(InteropTopics.Hello, topic);
        }

        [Fact]
        public void ATruncatedFrameIsRejected()
        {
            byte[] frame = InProcessEndpoint.Frame(InteropTopics.PluginList, new byte[] { 1 });
            Array.Resize(ref frame, 4);

            Assert.False(InProcessEndpoint.TryUnframe(frame, out _, out _));
        }

        [Fact]
        public void AnEmptyTopicIsRefusedAtTheSender()
        {
            (InProcessEndpoint core, _) = InProcessChannel.CreateLoopbackPair();
            Assert.Throws<ArgumentException>(() => core.Send(string.Empty, Array.Empty<byte>()));
        }

        [Fact]
        public void AFullQueueDropsTheOldestMessageAndCountsIt()
        {
            // A stalled reader must cost freshness, never the frame and never unbounded
            // memory: the bridge runs on a GameFiber and cannot be allowed to block.
            (InProcessEndpoint core, InProcessEndpoint plugin) = InProcessChannel.CreateLoopbackPair(capacity: 4);

            for (int i = 0; i < 10; i++)
            {
                plugin.Send(InteropTopics.LspdfrEvent, new[] { (byte)i });
            }

            Assert.Equal(6, plugin.Dropped);
            Assert.Equal(4, core.Pending);

            var survived = new List<byte>();
            while (core.TryReceive(out _, out byte[] payload))
            {
                survived.Add(payload[0]);
            }

            // The newest four, because newer state is the more useful state.
            Assert.Equal(new byte[] { 6, 7, 8, 9 }, survived);
        }

        [Fact]
        public void TheLoopbackPairCarriesMessagesInBothDirections()
        {
            (InProcessEndpoint core, InProcessEndpoint plugin) = InProcessChannel.CreateLoopbackPair();

            core.Send(InteropTopics.Describe, Array.Empty<byte>());
            Assert.True(plugin.TryReceive(out string toPlugin, out _));
            Assert.Equal(InteropTopics.Describe, toPlugin);

            plugin.Send(InteropTopics.Hello, Encoding.UTF8.GetBytes("state=running"));
            Assert.True(core.TryReceive(out string toCore, out byte[] hello));
            Assert.Equal(InteropTopics.Hello, toCore);
            Assert.Equal("state=running", Encoding.UTF8.GetString(hello));

            Assert.False(core.TryReceive(out _, out _));
            Assert.Equal(1, core.Sent);
            Assert.Equal(1, core.Received);
        }

        [Fact]
        public void TwoSidesOpenedIndependentlyFindEachOther()
        {
            // This is the rendezvous the real thing depends on: two assemblies, loaded
            // by two different hosts, that never see each other's types.
            InProcessEndpoint core = InProcessChannel.OpenCoreSide();
            InProcessEndpoint plugin = InProcessChannel.OpenPluginSide();

            Assert.True(InProcessChannel.IsCoreSidePresent());
            Assert.True(InProcessChannel.IsPluginSidePresent());

            try
            {
                plugin.Send(InteropTopics.PluginList, Encoding.UTF8.GetBytes("LSPDFR|0.4.9"));

                Assert.True(core.TryReceive(out string topic, out byte[] payload));
                Assert.Equal(InteropTopics.PluginList, topic);
                Assert.Equal("LSPDFR|0.4.9", Encoding.UTF8.GetString(payload));
            }
            finally
            {
                core.Clear();
                plugin.Clear();
            }
        }

        [Fact]
        public void EveryAdapterOnATopicSeesTheMessageAndNoOtherTopic()
        {
            // The bug this guards: two adapters each draining the same endpoint would
            // steal each other's messages, so the RPH adapter would swallow the LSPDFR
            // events and nobody would ever find out except in-game.
            (InProcessEndpoint core, InProcessEndpoint plugin) = InProcessChannel.CreateLoopbackPair();
            var link = new BridgeLink(core);

            var first = new List<string>();
            var second = new List<string>();
            var otherTopic = new List<string>();

            link.Subscribe(InteropTopics.LspdfrEvent, p => first.Add(Encoding.UTF8.GetString(p)));
            link.Subscribe(InteropTopics.LspdfrEvent, p => second.Add(Encoding.UTF8.GetString(p)));
            link.Subscribe(InteropTopics.Hello, p => otherTopic.Add(Encoding.UTF8.GetString(p)));

            plugin.Send(InteropTopics.LspdfrEvent, Encoding.UTF8.GetBytes("onDuty=True"));
            link.Pump();

            Assert.Equal(new[] { "onDuty=True" }, first);
            Assert.Equal(new[] { "onDuty=True" }, second);
            Assert.Empty(otherTopic);
        }

        [Fact]
        public void AThrowingAdapterDoesNotStopTheOthers()
        {
            (InProcessEndpoint core, InProcessEndpoint plugin) = InProcessChannel.CreateLoopbackPair();
            var link = new BridgeLink(core);
            var reached = new List<string>();

            link.Subscribe(InteropTopics.LspdfrEvent, _ => throw new InvalidOperationException("adapter bug"));
            link.Subscribe(InteropTopics.LspdfrEvent, p => reached.Add(Encoding.UTF8.GetString(p)));

            plugin.Send(InteropTopics.LspdfrEvent, Encoding.UTF8.GetBytes("pursuit.active=True"));
            link.Pump();

            Assert.Equal(new[] { "pursuit.active=True" }, reached);
            Assert.Equal(1, link.HandlerFailures);
            Assert.Equal(1, link.RoutedMessages);
        }

        [Fact]
        public void AMessageNobodySubscribedToIsCountedRatherThanSilentlyLost()
        {
            (InProcessEndpoint core, InProcessEndpoint plugin) = InProcessChannel.CreateLoopbackPair();
            var link = new BridgeLink(core);

            plugin.Send("gtamp.something.new", Array.Empty<byte>());
            Assert.Equal(0, link.Pump());
            Assert.Equal(1, link.UnroutedMessages);
        }

        [Fact]
        public void UnsubscribingStopsDelivery()
        {
            (InProcessEndpoint core, InProcessEndpoint plugin) = InProcessChannel.CreateLoopbackPair();
            var link = new BridgeLink(core);

            var seen = new List<string>();
            Action<byte[]> handler = p => seen.Add(Encoding.UTF8.GetString(p));
            link.Subscribe(InteropTopics.Hello, handler);
            link.Unsubscribe(InteropTopics.Hello, handler);

            plugin.Send(InteropTopics.Hello, Encoding.UTF8.GetBytes("state=running"));
            link.Pump();

            Assert.Empty(seen);
            Assert.Equal(1, link.UnroutedMessages);
        }

        [Theory]
        [InlineData("onDuty=True;callout.running=False", true, "onDuty=True;callout.running=False")]
        [InlineData("", false, "")]
        [InlineData("garbage", false, "")]
        [InlineData("=novalue", false, "")]
        public void MergeReportsExactlyWhatChanged(string payload, bool expectedChanged, string expectedDelta)
        {
            var state = new Dictionary<string, string>(StringComparer.Ordinal);

            Assert.Equal(expectedChanged, LspdfrAdapter.Merge(state, payload, out string changed));
            Assert.Equal(expectedDelta, changed);
        }

        [Fact]
        public void MergeIgnoresAValueThatDidNotActuallyMove()
        {
            // The bridge polls; without this every poll would become a packet.
            var state = new Dictionary<string, string>(StringComparer.Ordinal);
            Assert.True(LspdfrAdapter.Merge(state, "onDuty=True;pursuit.active=False", out _));

            Assert.False(LspdfrAdapter.Merge(state, "onDuty=True;pursuit.active=False", out string unchanged));
            Assert.Equal(string.Empty, unchanged);

            Assert.True(LspdfrAdapter.Merge(state, "onDuty=True;pursuit.active=True", out string changed));
            Assert.Equal("pursuit.active=True", changed);
        }

        [Fact]
        public void AValueContainingAnEqualsSignSurvivesIntact()
        {
            var state = new Dictionary<string, string>(StringComparer.Ordinal);
            Assert.True(LspdfrAdapter.Merge(state, "callout.current=Traffic Stop = Code 3", out _));
            Assert.Equal("Traffic Stop = Code 3", state["callout.current"]);
        }

        [Fact]
        public void TheRphAdapterStaysQuietUntilItsBridgeAnswersAndThenReportsIt()
        {
            (InProcessEndpoint core, InProcessEndpoint plugin) = InProcessChannel.CreateLoopbackPair();
            BridgeLink.OverrideShared(new BridgeLink(core));

            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("officer");
            ModEnvironment environment = FakeInstall(rph: true, lspdfr: false);

            var adapter = new RphAdapter();
            client.Client.Adapters.Add(adapter, client.Client.Sdk, environment);

            Assert.Contains("waiting for the RPH bridge", adapter.DescribeStatus());

            // The bridge is asked to describe itself as soon as the adapter opens.
            Assert.True(plugin.TryReceive(out string asked, out _));
            Assert.Equal(InteropTopics.Describe, asked);

            plugin.Send(InteropTopics.Hello, Encoding.UTF8.GetBytes("state=running;bridge=0.1.0;rph=1.124;lspdfr=0.4.9"));
            plugin.Send(InteropTopics.PluginList, Encoding.UTF8.GetBytes("LSPDFR|0.4.9;Callout Pack|2.1"));
            adapter.Update(harness.Now);

            Assert.Contains("bridge 0.1.0", adapter.DescribeStatus());
            Assert.Contains("2 plugin(s)", adapter.DescribeStatus());

            // Plugins RPH loaded are only visible once RPH has loaded them, so they
            // reach the manifest this way rather than through the file scan.
            Assert.Contains(environment.Mods, m => m.Id == "rph.plugin.callout-pack" && m.Version == "2.1");
        }

        [Fact]
        public void ARphBridgeThatNeverAnswersIsReportedRatherThanWaitedOnForever()
        {
            (InProcessEndpoint core, _) = InProcessChannel.CreateLoopbackPair();
            BridgeLink.OverrideShared(new BridgeLink(core));

            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("officer");

            var adapter = new RphAdapter();
            client.Client.Adapters.Add(adapter, client.Client.Sdk, FakeInstall(rph: true, lspdfr: false));

            adapter.Update(0);
            adapter.Update(RphAdapter.BridgeHandshakeTimeout + 1);

            Assert.Contains("never answered", adapter.DescribeStatus());
        }

        [Fact]
        public void AnLspdfrStateChangeOnOneClientReachesTheOther()
        {
            // The Phase 8 acceptance case, end to end: the bridge on alice's machine
            // observes a pursuit, and bob's client — through a server that has never
            // heard of LSPDFR — knows about it.
            using var harness = new TestHarness();

            (InProcessEndpoint aliceCore, InProcessEndpoint aliceBridge) = InProcessChannel.CreateLoopbackPair();
            (InProcessEndpoint bobCore, _) = InProcessChannel.CreateLoopbackPair();

            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");

            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(
                harness.AdvanceUntil(() => alice.Client.IsConnected && bob.Client.IsConnected),
                "the clients never connected");

            ModEnvironment environment = FakeInstall(rph: true, lspdfr: true);

            BridgeLink.OverrideShared(new BridgeLink(aliceCore));
            var aliceAdapter = new LspdfrAdapter();
            alice.Client.Adapters.Add(aliceAdapter, alice.Client.Sdk, environment);

            BridgeLink.OverrideShared(new BridgeLink(bobCore));
            var bobAdapter = new LspdfrAdapter();
            bob.Client.Adapters.Add(bobAdapter, bob.Client.Sdk, environment);

            aliceBridge.Send(
                InteropTopics.LspdfrEvent,
                Encoding.UTF8.GetBytes("onDuty=True;pursuit.active=True;callout.current=Officer in trouble"));

            Assert.True(
                harness.AdvanceUntil(() => bobAdapter.RemoteState.Count > 0),
                "bob never received alice's LSPDFR state");

            Assert.Equal("True", aliceAdapter.LocalState["pursuit.active"]);

            uint alicePlayerId = alice.Client.LocalPlayerId;
            // The event is attributed to alice, not to "somebody": a relayed event
            // that loses its origin is useless for anything per-player.
            Assert.True(
                bobAdapter.RemoteState.ContainsKey(alicePlayerId),
                $"bob attributed the state to {string.Join(",", bobAdapter.RemoteState.Keys)} rather than to alice ({alicePlayerId})");

            Dictionary<string, string> asBobSeesIt = bobAdapter.RemoteState[alicePlayerId];
            Assert.Equal("True", asBobSeesIt["onDuty"]);
            Assert.Equal("True", asBobSeesIt["pursuit.active"]);
            Assert.Equal("Officer in trouble", asBobSeesIt["callout.current"]);

            // Alice does not hear her own state back as a peer's.
            Assert.Empty(aliceAdapter.RemoteState);
        }

        [Fact]
        public void AServerWithRelayingTurnedOffDoesNotPassModEventsBetweenClients()
        {
            // An operator's call, and it has to actually take effect: a relayed event
            // is unvalidated bytes from one client to another.
            var config = new Gtamp.Server.Core.ServerConfig
            {
                ServerName = "no-relay",
                SaveIntervalSeconds = 0,
            };
            config.RelayedModEvents.Clear();

            using var harness = new TestHarness(config);

            (InProcessEndpoint aliceCore, InProcessEndpoint aliceBridge) = InProcessChannel.CreateLoopbackPair();
            (InProcessEndpoint bobCore, _) = InProcessChannel.CreateLoopbackPair();

            TestClient alice = harness.CreateClient("alice");
            TestClient bob = harness.CreateClient("bob");
            alice.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            bob.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(
                harness.AdvanceUntil(() => alice.Client.IsConnected && bob.Client.IsConnected),
                "the clients never connected");

            ModEnvironment environment = FakeInstall(rph: true, lspdfr: true);

            BridgeLink.OverrideShared(new BridgeLink(aliceCore));
            alice.Client.Adapters.Add(new LspdfrAdapter(), alice.Client.Sdk, environment);

            BridgeLink.OverrideShared(new BridgeLink(bobCore));
            var bobAdapter = new LspdfrAdapter();
            bob.Client.Adapters.Add(bobAdapter, bob.Client.Sdk, environment);

            aliceBridge.Send(InteropTopics.LspdfrEvent, Encoding.UTF8.GetBytes("onDuty=True"));
            harness.Advance(1.0);

            Assert.Empty(bobAdapter.RemoteState);
        }

        /// <summary>
        /// A directory that looks like a GTA V install with the given mods in it.
        /// <para>
        /// The adapters read <see cref="ModEnvironment"/>, and the only supported way
        /// to build one is <see cref="ModEnvironment.Detect"/> — so the test lays out
        /// the files rather than reaching past the API with a test-only setter. It
        /// exercises detection at the same time.
        /// </para>
        /// </summary>
        private static ModEnvironment FakeInstall(bool rph, bool lspdfr)
        {
            string directory = Path.Combine(Path.GetTempPath(), "gtamp-interop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            File.WriteAllText(Path.Combine(directory, "ScriptHookV.dll"), "stub");
            if (rph)
            {
                File.WriteAllText(Path.Combine(directory, "RAGEPluginHook.exe"), "stub");
            }

            if (lspdfr)
            {
                File.WriteAllText(Path.Combine(directory, "LSPD First Response.dll"), "stub");
            }

            ModEnvironment environment = ModEnvironment.Detect(directory);
            Directory.Delete(directory, recursive: true);

            Assert.Equal(rph, environment.RagePluginHook);
            Assert.Equal(lspdfr, environment.Lspdfr);
            return environment;
        }
    }
}
