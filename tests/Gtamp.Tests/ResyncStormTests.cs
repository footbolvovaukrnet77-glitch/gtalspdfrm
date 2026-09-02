using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using Gtamp.Client.Entities;
using Gtamp.Client.Mods;
using Gtamp.Shared.Core;
using Gtamp.Server.Players;
using Gtamp.Shared.Entities;
using Gtamp.Shared.World;
using Gtamp.Shared.Diagnostics;
using Gtamp.Shared.Mods;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// What a client does when it cannot decode a delta, and what happens to a peer
    /// whose ordered channel can no longer deliver.
    /// <para>
    /// Both come from one real session. The client hit a snapshot whose baseline it no
    /// longer held, asked the server for a full one — and asked again for every snapshot
    /// that had already arrived behind it, because the request cleared the very history
    /// the rest of them needed. The log shows a hundred and eighty-four identical
    /// warnings inside one millisecond, three times over, each burst followed by silence
    /// and a connection timeout with nothing said about the cause.
    /// </para>
    /// </summary>
    public class ResyncStormTests
    {
        private static readonly IPEndPoint Left = new IPEndPoint(IPAddress.Loopback, 40100);
        private static readonly IPEndPoint Right = new IPEndPoint(IPAddress.Loopback, 40101);

        /// <summary>
        /// The storm itself. Clearing the client's history is exactly what happens when a
        /// baseline goes missing, so it is done directly here: every delta that follows is
        /// undecodable until the server's full snapshot lands.
        /// </summary>
        [Fact]
        public void AMissingBaselineAsksOnceNotOncePerSnapshot()
        {
            using var harness = new TestHarness();

            // A round trip long enough that several snapshots arrive before the answer
            // does. On a zero-latency loopback the full snapshot comes back on the next
            // tick and there is nothing queued to storm with — which is exactly why this
            // was never found in the suite and was found in one session in a real game.
            harness.Latency = 0.08;

            TestClient client = harness.CreateClient("Resync");
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.Client.Connection.IsConnected));

            harness.Advance(1d);
            int before = client.Client.ResyncsRequested;

            // The baseline the server is encoding against is now gone from this client.
            client.Client.ReplicatedWorld.Reset();
            harness.Advance(2d);

            int asked = client.Client.ResyncsRequested - before;

            // One request, or a second if the first was still in flight when the retry
            // window elapsed. Never one per snapshot: at 20 Hz two seconds is forty.
            Assert.InRange(asked, 1, 2);
            Assert.True(
                client.Client.ResyncsSuppressed > 0,
                "the snapshots arriving behind the first failure should have been suppressed, not re-asked");
        }

        /// <summary>
        /// And it has to recover, or suppressing the requests would only have replaced a
        /// storm with a client frozen on a view it can never advance.
        /// </summary>
        [Fact]
        public void AClientThatLostItsBaselineGetsAFullSnapshotAndCarriesOn()
        {
            using var harness = new TestHarness();
            TestClient client = harness.CreateClient("Recover");
            client.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => client.Client.Connection.IsConnected));

            harness.Advance(1d);
            client.Client.ReplicatedWorld.Reset();
            int applied = client.Client.SnapshotsApplied;

            Assert.True(
                harness.AdvanceUntil(() => client.Client.SnapshotsApplied > applied, timeoutSeconds: 5d),
                "the server never answered the resync with a full snapshot");
            Assert.True(client.Client.Connection.IsConnected);
        }

        /// <summary>
        /// The transport half. A gap in the ordered stream that never fills used to
        /// overflow the pending buffer, drop a message silently, and wedge the channel
        /// for the life of the connection — while the peer went on answering pings, so
        /// from outside it looked like an ordinary timeout fifteen seconds later.
        /// </summary>
        [Fact]
        public void AnOrderedChannelThatCanNoLongerDeliverSaysSo()
        {
            var network = new LoopbackNetwork(11);
            IDatagramTransport senderTransport = network.CreateTransport(Left);
            IDatagramTransport receiverTransport = network.CreateTransport(Right);

            var sender = new NetPeer(senderTransport, Right, 1, 0);
            var receiver = new NetPeer(receiverTransport, Left, 1, 0);

            // Big enough that only a few fit in a datagram, so dropping the first
            // datagram leaves a gap with far more than the buffer's worth behind it.
            var payload = new byte[200];
            for (int i = 0; i < 700; i++)
            {
                sender.Send(NetMessageType.ServerEvent, payload, DeliveryMethod.ReliableOrdered);
            }

            sender.Flush(network.Now);
            network.Advance(0.001);

            var datagrams = new List<byte[]>();
            while (receiverTransport.TryReceive(out IPEndPoint _, out byte[] datagram))
            {
                datagrams.Add(datagram);
            }

            Assert.True(datagrams.Count > 2, "the burst should not have fitted in one datagram");
            Assert.Null(receiver.Fault);

            // Everything except the first datagram, and no retransmissions: the gap at
            // the head of the stream is permanent, which is the condition being tested.
            for (int i = 1; i < datagrams.Count; i++)
            {
                receiver.HandleDatagram(datagrams[i], network.Now);
            }

            Assert.NotNull(receiver.Fault);
            Assert.Contains("ordered channel stalled", receiver.Fault!);
        }

        /// <summary>
        /// The framework's own assemblies live in <c>scripts\</c> like any other SHVDN
        /// script. Reporting them as mods made every connection warn three times that the
        /// server had no adapter for Gtamp.Client.Core, Gtamp.Client.Shv and Gtamp.Shared.
        /// </summary>
        [Fact]
        public void TheFrameworkIsNotAModOfItself()
        {
            using var directory = new TempDirectory();
            directory.WriteScript("Gtamp.Client.Core.dll");
            directory.WriteScript("Gtamp.Client.Shv.dll");
            directory.WriteScript("Gtamp.Shared.dll");
            directory.WriteScript("SomeoneElsesMod.dll");

            ModEnvironment environment = ModEnvironment.Detect(directory.Path);
            var ids = new List<string>();
            foreach (ModDescriptor mod in environment.Mods)
            {
                ids.Add(mod.Id);
            }

            Assert.Contains("script.someoneelsesmod", ids);
            Assert.DoesNotContain("script.gtamp.client.core", ids);
            Assert.DoesNotContain("script.gtamp.client.shv", ids);
            Assert.DoesNotContain("script.gtamp.shared", ids);
        }

        /// <summary>
        /// LSPDFR is a RAGE Plugin Hook plugin and lives in RPH's plugins folder. This
        /// looked in the game root instead, so a machine that was demonstrably running
        /// LSPDFR — the RPH log names the path on every start — was told it was not
        /// installed, and the LSPDFR adapter never activated.
        /// </summary>
        [Fact]
        public void LspdfrIsFoundWhereRagePluginHookActuallyKeepsIt()
        {
            using var directory = new TempDirectory();
            directory.WritePlugin("LSPD First Response.dll");

            ModEnvironment environment = ModEnvironment.Detect(directory.Path);

            Assert.True(environment.Lspdfr, "LSPDFR in Plugins\\ was not detected");
        }

        [Fact]
        public void AnInstallWithoutLspdfrStillSaysSo()
        {
            using var directory = new TempDirectory();
            directory.WriteScript("SomeoneElsesMod.dll");

            Assert.False(ModEnvironment.Detect(directory.Path).Lspdfr);
        }

        /// <summary>
        /// The interaction that crashed a real game: changing the player's model destroys
        /// the ped and builds another, and LSPDFR was holding the old one. Driven through
        /// the real seam — a directory with LSPDFR where RPH keeps it — so the detection
        /// and the decision are tested together rather than one being assumed.
        /// </summary>
        [Fact]
        public void AServerModelIsNotAppliedOverLspdfr()
        {
            using var directory = new TempDirectory();
            directory.WritePlugin("LSPD First Response.dll");

            using var harness = new TestHarness();
            TestClient player = harness.CreateClient("Officer");
            player.Client.InitializeMods(directory.Path, System.IO.Path.Combine(directory.Path, "Adapters"));
            Assert.True(player.Client.Environment.Lspdfr);

            player.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => player.Client.Connection.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));
            harness.Advance(1d);

            uint before = player.Bridge.Sample.ModelHash;
            const uint Skin = 0x9C9EFFD8u;
            Assert.True(harness.Server.Players.TryGetByPlayerId(
                player.Client.LocalPlayerId, out PlayerSession session));
            Assert.True(harness.Server.SetPlayerModel(session, Skin));

            harness.Advance(2d);

            Assert.Equal(0, player.Client.ModelChangesApplied);
            Assert.Equal(before, player.Bridge.Sample.ModelHash);
            // Declined, not refused: the model is installed and would have worked.
            // Counting it as refused made selftest say "the model is probably not
            // installed here" about six models that were nothing of the kind.
            Assert.True(player.Client.ModelChangesDeclined > 0);
            Assert.Equal(0, player.Client.ModelChangesRefused);
            Assert.Contains(
                player.Console.VisibleLines(),
                line => line.Text.Contains("LSPD First Response is installed"));
        }

        /// <summary>
        /// One vehicle, adopted once.
        /// <para>
        /// Accepting a spawn used to stamp the entity as "last seen at time zero", and
        /// <c>Stream</c> reads that as a timestamp: from the first frame onwards it is
        /// older than the grace period. So the streamer forgot the vehicle the server had
        /// just accepted, before the snapshot carrying it could arrive; the handle became
        /// unknown again, and the client asked the server to adopt the same car a second
        /// time. A real session turned about a dozen cars into twenty-four replicated
        /// ones, in pairs thirty milliseconds apart.
        /// </para>
        /// <para>
        /// Driven through <see cref="OwnedEntityStreamer"/> directly, with the snapshot
        /// deliberately not yet carrying the entity, because that gap is the whole
        /// condition and a live session closes it too fast to rely on.
        /// </para>
        /// </summary>
        [Fact]
        public void AnAcceptedVehicleIsNotForgottenBeforeItsFirstSnapshot()
        {
            var bridge = new FakeGameBridge();
            bridge.PutLocalPlayerInVehicle(0x1B38E955, new NetVector3(10f, 20f, 30f));

            var sent = new List<byte[]>();
            var streamer = new OwnedEntityStreamer(bridge, EntityRegistry.CreateDefault(), new LogBus())
            {
                LocalPlayerId = 1,
                Send = (type, payload, delivery) =>
                {
                    if (type == NetMessageType.EntitySpawnRequest)
                    {
                        sent.Add(payload);
                    }
                },
            };

            streamer.RegisterLocalVehicleIfNeeded(EntitySnapshotView.Empty, 100d);
            Assert.Single(sent);

            uint tag = EntitySpawnRequestMessage.Deserialize(sent[0]).RequestTag;
            streamer.HandleEntityEvent(new EntityEventMessage
            {
                Kind = EntityEventKind.SpawnAccepted,
                EntityId = new EntityId(7),
                RequestTag = tag,
            });
            Assert.Equal(1, streamer.OwnedCount);

            // The snapshot does not carry it yet — the acceptance is reliable and arrives
            // first. This is the frame the old code threw the vehicle away on.
            streamer.Stream(EntitySnapshotView.Empty, 100.1d, 0d);

            Assert.Equal(1, streamer.OwnedCount);

            streamer.RegisterLocalVehicleIfNeeded(EntitySnapshotView.Empty, 100.2d);
            Assert.Single(sent);
        }

        /// <summary>
        /// And it is still let go when the snapshot really never carries it, or a vehicle
        /// the server has genuinely dropped would be streamed for the rest of the session.
        /// </summary>
        [Fact]
        public void AVehicleTheSnapshotNeverCarriesIsStillLetGo()
        {
            var bridge = new FakeGameBridge();
            bridge.PutLocalPlayerInVehicle(0x1B38E955, new NetVector3(10f, 20f, 30f));

            var sent = new List<byte[]>();
            var streamer = new OwnedEntityStreamer(bridge, EntityRegistry.CreateDefault(), new LogBus())
            {
                LocalPlayerId = 1,
                Send = (type, payload, delivery) =>
                {
                    if (type == NetMessageType.EntitySpawnRequest)
                    {
                        sent.Add(payload);
                    }
                },
            };

            streamer.RegisterLocalVehicleIfNeeded(EntitySnapshotView.Empty, 100d);
            streamer.HandleEntityEvent(new EntityEventMessage
            {
                Kind = EntityEventKind.SpawnAccepted,
                EntityId = new EntityId(7),
                RequestTag = EntitySpawnRequestMessage.Deserialize(sent[0]).RequestTag,
            });

            streamer.Stream(EntitySnapshotView.Empty, 100.1d, 0d);
            streamer.Stream(EntitySnapshotView.Empty, 100.1d + OwnedEntityStreamer.MissingEntityGrace + 1d, 0d);

            Assert.Equal(0, streamer.OwnedCount);
        }

        /// <summary>
        /// A correction that lands makes the next disagreement smaller. One that does not
        /// is a defect, and it looks exactly like a working correction in the log — a
        /// line per snapshot, each reasonable on its own. The session that found this
        /// logged the same distance to the centimetre a hundred times running.
        /// </summary>
        [Fact]
        public void ACorrectionThatChangesNothingIsReported()
        {
            using var harness = new TestHarness();
            TestClient player = harness.CreateClient("Stuck");
            player.Client.Connect("127.0.0.1", TestHarness.ServerEndPoint.Port);
            Assert.True(harness.AdvanceUntil(() => player.Client.Connection.IsConnected));
            Assert.True(harness.AdvanceUntil(() => player.Client.LocalEntityId.IsValid));
            harness.Advance(1d);

            Assert.False(player.Client.CorrectionsAreStuck);

            // A bridge that accepts the correction and does not move, which is what the
            // game does when the player is in a vehicle and only the ped is placed.
            player.Bridge.IgnoreLocalCorrections = true;

            Assert.True(harness.Server.Players.TryGetByPlayerId(
                player.Client.LocalPlayerId, out PlayerSession session));

            // The server keeps asserting a position the game will not take. That is the
            // shape of the real failure: the client applies the correction on every
            // snapshot, the player does not move, and the disagreement is identical the
            // next time. Re-asserting it here is what a stuck game does by itself.
            var elsewhere = new NetVector3(1500f, 1500f, 40f);
            Assert.True(
                harness.AdvanceUntil(
                    () =>
                    {
                        harness.Server.TeleportPlayer(session, elsewhere, 0f);
                        return player.Client.CorrectionsAreStuck;
                    },
                    timeoutSeconds: 15d),
                $"corrections that changed nothing were never reported "
                    + $"({player.Client.CorrectionsApplied} applied)");
            Assert.Contains(
                player.Console.VisibleLines(),
                line => line.Text.Contains("not reaching the game"));
        }

        [Theory]
        [InlineData("Gtamp.Shared.dll", true)]
        [InlineData(@"C:\GTA V\scripts\GTAMP.CLIENT.CORE.DLL", true)]
        [InlineData("Gtamp.RphBridge.dll", true)]
        [InlineData("Gtamp.Adapters.Rph.dll", false)]
        [InlineData("SomeoneElsesMod.dll", false)]
        [InlineData("", false)]
        public void OwnAssembliesAreRecognisedWhereverTheyLive(string path, bool expected)
        {
            Assert.Equal(expected, ModEnvironment.IsOwnAssembly(path));
        }
    }

    /// <summary>
    /// A throwaway GTA V directory with a <c>scripts</c> folder, owned by one test and
    /// deleted with it. Named after its own GUID for the reason recorded in
    /// ARCHITECTURE.md: xUnit runs classes in parallel and a shared path is a race.
    /// </summary>
    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "gtamp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "scripts"));
        }

        public string Path { get; }

        /// <summary>An empty file is enough: the scan reads names, not assemblies.</summary>
        public void WriteScript(string fileName) =>
            File.WriteAllBytes(System.IO.Path.Combine(Path, "scripts", fileName), new byte[0]);

        /// <summary>The same, in RAGE Plugin Hook's own plugins folder.</summary>
        public void WritePlugin(string fileName)
        {
            string plugins = System.IO.Path.Combine(Path, "Plugins");
            Directory.CreateDirectory(plugins);
            File.WriteAllBytes(System.IO.Path.Combine(plugins, fileName), new byte[0]);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
