using System.Collections.Generic;
using System.Net;
using System.Text;
using Gtamp.Shared.Net;
using Gtamp.Shared.Protocol;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Exercises <see cref="NetPeer"/> over a network that loses, delays and reorders
    /// datagrams. These are the conditions the reliability layer exists for, so they
    /// are tested directly rather than hoped for.
    /// </summary>
    public class ReliabilityTests
    {
        private const uint Session = 0xC0FFEE;

        private static readonly IPEndPoint Left = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1000);
        private static readonly IPEndPoint Right = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 2000);

        private static byte[] Body(string text) => Encoding.UTF8.GetBytes(text);

        [Fact]
        public void ReliableMessagesArriveOnAPerfectLink()
        {
            var harness = new ReliableHarness();
            for (int i = 0; i < 20; i++)
            {
                harness.A.Send(NetMessageType.ChatMessage, Body("m" + i), DeliveryMethod.ReliableOrdered);
            }

            harness.Run(1.0);
            Assert.Equal(20, harness.Received.Count);
        }

        [Fact]
        public void AReliableMessageSentBeforeThePeerExistsIsStillDelivered()
        {
            // The defect this guards against, found while wiring the LSPDFR relay.
            // A peer's remote sequence starts at zero, so its very first packet used
            // to carry ack = 0, and the other side read that as an acknowledgement of
            // its own packet 0. The reliable message in that packet was dropped from
            // the retransmission list and never resent; because delivery is ordered,
            // every later reliable message then queued behind a sequence that would
            // never arrive and the channel wedged in silence for the rest of the
            // connection.
            //
            // The shape below is the one that actually happens: the server queues a
            // join notification the instant it accepts a client, but the client's peer
            // only exists once it has processed the connect-accept, so that first
            // packet lands on nobody — and the client's first state update, carrying
            // ack = 0, arrives before the retransmission timer fires.
            var network = new LoopbackNetwork(7);
            IDatagramTransport serverTransport = network.CreateTransport(Left);
            IDatagramTransport clientTransport = network.CreateTransport(Right);

            var server = new NetPeer(serverTransport, Right, Session, 0);
            server.Send(NetMessageType.ServerEvent, Body("welcome"), DeliveryMethod.ReliableOrdered);
            server.Flush(network.Now);
            network.Advance(0.001);

            while (clientTransport.TryReceive(out IPEndPoint _, out byte[] _))
            {
                // Delivered to a peer that does not exist yet, and therefore lost.
            }

            var client = new NetPeer(clientTransport, Left, Session, 0);
            client.Send(NetMessageType.ClientStateUpdate, Body("hello"), DeliveryMethod.Unreliable);

            var received = new List<string>();
            for (double elapsed = 0; elapsed < 2.0; elapsed += 0.02)
            {
                double now = network.Now;
                server.Flush(now);
                client.Flush(now);
                network.Advance(0.02);

                while (serverTransport.TryReceive(out IPEndPoint _, out byte[] toServer))
                {
                    server.HandleDatagram(toServer, network.Now);
                }

                while (clientTransport.TryReceive(out IPEndPoint _, out byte[] toClient))
                {
                    client.HandleDatagram(toClient, network.Now);
                }

                while (client.TryDequeue(out ReceivedMessage message))
                {
                    received.Add(Encoding.UTF8.GetString(message.Payload));
                }
            }

            Assert.Equal(new[] { "welcome" }, received);

            // And the channel still works afterwards, rather than staying wedged
            // behind a sequence number that was never delivered.
            server.Send(NetMessageType.ChatMessage, Body("second"), DeliveryMethod.ReliableOrdered);
            for (double elapsed = 0; elapsed < 1.0; elapsed += 0.02)
            {
                double now = network.Now;
                server.Flush(now);
                client.Flush(now);
                network.Advance(0.02);

                while (clientTransport.TryReceive(out IPEndPoint _, out byte[] toClient))
                {
                    client.HandleDatagram(toClient, network.Now);
                }

                while (serverTransport.TryReceive(out IPEndPoint _, out byte[] toServer))
                {
                    server.HandleDatagram(toServer, network.Now);
                }

                while (client.TryDequeue(out ReceivedMessage message))
                {
                    received.Add(Encoding.UTF8.GetString(message.Payload));
                }
            }

            Assert.Equal(new[] { "welcome", "second" }, received);
        }

        [Fact]
        public void PacketSequenceNumbersNeverUseTheReservedZero()
        {
            var harness = new ReliableHarness();
            harness.A.Send(NetMessageType.ChatMessage, Body("first"), DeliveryMethod.ReliableOrdered);
            harness.Run(0.1);

            Assert.True(harness.B.RemoteSequence > 0, "packet sequence 0 is reserved for 'nothing received yet'");
        }

        [Fact]
        public void ReliableMessagesSurviveHeavyLossAndArriveInOrder()
        {
            var harness = new ReliableHarness(loss: 0.4, latency: 0.03, jitter: 0.02);
            var expected = new List<string>();
            for (int i = 0; i < 30; i++)
            {
                string text = "m" + i;
                expected.Add(text);
                harness.A.Send(NetMessageType.ChatMessage, Body(text), DeliveryMethod.ReliableOrdered);
            }

            harness.Run(20.0);

            Assert.Equal(expected, harness.Received);
            Assert.True(harness.A.Stats.ReliableRetransmits > 0, "the lossy link should have forced retransmissions");
        }

        [Fact]
        public void ReliableDeliveryIsExactlyOnceDespiteRetransmits()
        {
            var harness = new ReliableHarness(loss: 0.5, latency: 0.05, jitter: 0.05, seed: 99);
            for (int i = 0; i < 15; i++)
            {
                harness.A.Send(NetMessageType.ChatMessage, Body("m" + i), DeliveryMethod.ReliableOrdered);
            }

            harness.Run(25.0);

            var seen = new HashSet<string>();
            foreach (string message in harness.Received)
            {
                Assert.True(seen.Add(message), $"'{message}' was delivered more than once");
            }

            Assert.Equal(15, seen.Count);
        }

        [Fact]
        public void UnreliableMessagesAreNotRetransmitted()
        {
            var harness = new ReliableHarness(loss: 0.5, seed: 3);
            for (int i = 0; i < 40; i++)
            {
                harness.A.Send(NetMessageType.ClientStateUpdate, Body("u" + i), DeliveryMethod.Unreliable);
                harness.Step(0.05);
            }

            harness.Run(2.0);

            Assert.True(harness.Received.Count > 0, "some unreliable traffic should get through");
            Assert.True(harness.Received.Count < 40, "unreliable traffic must not be retransmitted to completion");
            Assert.Equal(0, harness.A.Stats.ReliableRetransmits);
        }

        [Fact]
        public void AllReliableMessagesAreAcknowledgedOnceTheLinkSettles()
        {
            var harness = new ReliableHarness(loss: 0.25, latency: 0.02);
            for (int i = 0; i < 10; i++)
            {
                harness.A.Send(NetMessageType.ChatMessage, Body("m" + i), DeliveryMethod.ReliableOrdered);
            }

            harness.Run(15.0);
            Assert.Equal(0, harness.A.UnackedReliableCount);
        }

        [Fact]
        public void RoundTripTimeIsEstimatedFromAcknowledgements()
        {
            var harness = new ReliableHarness(latency: 0.05);
            harness.A.Send(NetMessageType.ChatMessage, Body("ping"), DeliveryMethod.ReliableOrdered);
            harness.Run(3.0);

            // One-way latency 50 ms both directions, so the RTT should land near 100 ms.
            Assert.InRange(harness.A.Stats.RoundTripTime, 0.05, 0.30);
            Assert.InRange(harness.A.Stats.PingMilliseconds, 50, 300);
        }

        [Fact]
        public void LostPacketsAreCounted()
        {
            var harness = new ReliableHarness(loss: 0.5, seed: 11);
            for (int i = 0; i < 60; i++)
            {
                harness.A.Send(NetMessageType.ClientStateUpdate, Body("u" + i), DeliveryMethod.Unreliable);
                harness.Step(0.03);
            }

            harness.Run(5.0);
            Assert.True(harness.A.Stats.PacketsLost > 0, "loss accounting should notice a 50% loss link");
            Assert.InRange(harness.A.Stats.PacketLoss, 0.1, 0.9);
        }

        [Fact]
        public void OversizedUnreliableMessagesAreRejectedRatherThanTruncated()
        {
            // Unreliable fragmentation multiplies the loss rate by the fragment count,
            // so it is refused rather than offered as a footgun.
            var harness = new ReliableHarness();
            NetSerializationException exception = Assert.Throws<NetSerializationException>(() =>
                harness.A.Send(NetMessageType.Snapshot, new byte[NetPeer.MaxMessagePayload + 1], DeliveryMethod.Unreliable));

            Assert.Contains("send it reliably", exception.Message);
        }

        [Fact]
        public void ALargeReliableMessageIsFragmentedAndReassembled()
        {
            var harness = new ReliableHarness();
            var payload = new byte[NetPeer.MaxMessagePayload * 4 + 137];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i * 31);
            }

            harness.A.Send(NetMessageType.ModManifest, payload, DeliveryMethod.ReliableOrdered);
            harness.Run(3.0);

            Assert.True(harness.A.Stats.FragmentsSent >= 5, $"only {harness.A.Stats.FragmentsSent} fragments were sent");
            Assert.Single(harness.RawReceived);
            Assert.Equal(NetMessageType.ModManifest, harness.RawReceived[0].Type);
            Assert.Equal(payload, harness.RawReceived[0].Payload);
        }

        [Fact]
        public void AFragmentedMessageSurvivesHeavyLoss()
        {
            var harness = new ReliableHarness(loss: 0.35, latency: 0.03, jitter: 0.02, seed: 606);
            var payload = new byte[NetPeer.MaxMessagePayload * 6];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 251);
            }

            harness.A.Send(NetMessageType.ModManifest, payload, DeliveryMethod.ReliableOrdered);
            harness.Run(30.0);

            Assert.Single(harness.RawReceived);
            Assert.Equal(payload, harness.RawReceived[0].Payload);
        }

        [Fact]
        public void FragmentedAndOrdinaryMessagesStayInOrder()
        {
            var harness = new ReliableHarness(loss: 0.2, seed: 42);
            var big = new byte[NetPeer.MaxMessagePayload * 3];

            harness.A.Send(NetMessageType.ChatMessage, Body("first"), DeliveryMethod.ReliableOrdered);
            harness.A.Send(NetMessageType.ModManifest, big, DeliveryMethod.ReliableOrdered);
            harness.A.Send(NetMessageType.ChatMessage, Body("last"), DeliveryMethod.ReliableOrdered);
            harness.Run(20.0);

            Assert.Equal(3, harness.RawReceived.Count);
            Assert.Equal(NetMessageType.ChatMessage, harness.RawReceived[0].Type);
            Assert.Equal(NetMessageType.ModManifest, harness.RawReceived[1].Type);
            Assert.Equal(NetMessageType.ChatMessage, harness.RawReceived[2].Type);
            Assert.Equal(big.Length, harness.RawReceived[1].Payload.Length);
        }

        [Fact]
        public void AMessageBeyondTheReassemblyLimitIsRefused()
        {
            var harness = new ReliableHarness();
            Assert.Throws<NetSerializationException>(() =>
                harness.A.Send(
                    NetMessageType.ModManifest,
                    new byte[NetPeer.MaxFragmentedMessage + 1],
                    DeliveryMethod.ReliableOrdered));
        }

        [Fact]
        public void PacketsForAnotherSessionAreIgnored()
        {
            var harness = new ReliableHarness();
            var writer = new NetWriter();
            writer.WriteUInt32(ProtocolConstants.Magic);
            writer.WriteByte(1);
            writer.WriteUInt32(Session + 1);
            writer.WriteUInt16(0);
            writer.WriteUInt16(0);
            writer.WriteUInt32(0);

            Assert.False(harness.B.HandleDatagram(writer.ToArray(), 0));
        }

        [Fact]
        public void GarbageIsDroppedWithoutThrowing()
        {
            var harness = new ReliableHarness();
            Assert.False(harness.B.HandleDatagram(new byte[] { 1, 2, 3 }, 0));
            Assert.False(harness.B.HandleDatagram(System.Array.Empty<byte>(), 0));
        }

        [Fact]
        public void SequenceComparisonHandlesWraparound()
        {
            Assert.True(SequenceMath.GreaterThan(1, 65535));
            Assert.False(SequenceMath.GreaterThan(65535, 1));
            Assert.True(SequenceMath.GreaterThan(100, 99));
            Assert.Equal(2, SequenceMath.Difference(1, 65535));
        }

        /// <summary>Two peers on one loopback network, pumped in lockstep with virtual time.</summary>
        private sealed class ReliableHarness
        {
            private readonly IDatagramTransport _leftTransport;
            private readonly IDatagramTransport _rightTransport;

            public ReliableHarness(double loss = 0, double latency = 0, double jitter = 0, int seed = 7)
            {
                Network = new LoopbackNetwork(seed) { PacketLoss = loss, Latency = latency, Jitter = jitter };
                _leftTransport = Network.CreateTransport(Left);
                _rightTransport = Network.CreateTransport(Right);
                A = new NetPeer(_leftTransport, Right, Session, 0);
                B = new NetPeer(_rightTransport, Left, Session, 0);
            }

            public LoopbackNetwork Network { get; }

            public NetPeer A { get; }

            public NetPeer B { get; }

            public List<string> Received { get; } = new List<string>();

            /// <summary>Everything B received, before it is turned into text.</summary>
            public List<ReceivedMessage> RawReceived { get; } = new List<ReceivedMessage>();

            public void Run(double seconds, double step = 0.02)
            {
                for (double elapsed = 0; elapsed < seconds; elapsed += step)
                {
                    Step(step);
                }
            }

            public void Step(double step)
            {
                double now = Network.Now;
                A.Flush(now);
                B.Flush(now);
                Network.Advance(step);

                Drain(_leftTransport, A, null);
                Drain(_rightTransport, B, Received);
            }

            private void Drain(IDatagramTransport transport, NetPeer peer, List<string>? sink)
            {
                while (transport.TryReceive(out IPEndPoint _, out byte[] payload))
                {
                    peer.HandleDatagram(payload, Network.Now);
                }

                while (peer.TryDequeue(out ReceivedMessage message))
                {
                    if (sink == null)
                    {
                        continue;
                    }

                    RawReceived.Add(message);
                    sink.Add(Encoding.UTF8.GetString(message.Payload));
                }
            }
        }
    }
}
