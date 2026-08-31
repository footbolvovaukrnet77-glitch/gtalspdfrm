using System;
using System.Collections.Generic;
using System.Net;

namespace Gtamp.Shared.Net
{
    /// <summary>
    /// Deterministic in-process network used by the automated tests. It models the
    /// three failure modes that break naive netcode — latency, loss and reordering —
    /// so the reliability layer can be verified without touching a real socket.
    /// </summary>
    public sealed class LoopbackNetwork
    {
        private readonly Dictionary<IPEndPoint, LoopbackTransport> _endpoints =
            new Dictionary<IPEndPoint, LoopbackTransport>();

        private readonly List<PendingDatagram> _inFlight = new List<PendingDatagram>();
        private readonly Random _random;

        public LoopbackNetwork(int seed = 1337)
        {
            _random = new Random(seed);
        }

        /// <summary>Fraction of datagrams silently dropped, 0..1.</summary>
        public double PacketLoss { get; set; }

        /// <summary>One-way delay in seconds applied to every datagram.</summary>
        public double Latency { get; set; }

        /// <summary>Uniform jitter in seconds added on top of <see cref="Latency"/>; causes reordering.</summary>
        public double Jitter { get; set; }

        public double Now { get; private set; }

        public int DatagramsSent { get; private set; }

        public int DatagramsDropped { get; private set; }

        public IDatagramTransport CreateTransport(IPEndPoint address)
        {
            var transport = new LoopbackTransport(this, address);
            _endpoints[address] = transport;
            return transport;
        }

        /// <summary>Advances virtual time and delivers every datagram whose arrival time has passed.</summary>
        public void Advance(double seconds)
        {
            Now += seconds;
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                PendingDatagram datagram = _inFlight[i];
                if (datagram.ArrivalTime > Now)
                {
                    continue;
                }

                _inFlight.RemoveAt(i);
                if (_endpoints.TryGetValue(datagram.Target, out LoopbackTransport? target))
                {
                    target.Deliver(datagram.Source, datagram.Payload);
                }
            }
        }

        internal void Enqueue(IPEndPoint source, IPEndPoint target, byte[] payload)
        {
            DatagramsSent++;
            if (PacketLoss > 0 && _random.NextDouble() < PacketLoss)
            {
                DatagramsDropped++;
                return;
            }

            double delay = Latency + (Jitter > 0 ? _random.NextDouble() * Jitter : 0);
            _inFlight.Add(new PendingDatagram(source, target, payload, Now + delay));
        }

        private readonly struct PendingDatagram
        {
            public PendingDatagram(IPEndPoint source, IPEndPoint target, byte[] payload, double arrivalTime)
            {
                Source = source;
                Target = target;
                Payload = payload;
                ArrivalTime = arrivalTime;
            }

            public IPEndPoint Source { get; }

            public IPEndPoint Target { get; }

            public byte[] Payload { get; }

            public double ArrivalTime { get; }
        }

        private sealed class LoopbackTransport : IDatagramTransport
        {
            private readonly LoopbackNetwork _network;
            private readonly Queue<KeyValuePair<IPEndPoint, byte[]>> _inbox =
                new Queue<KeyValuePair<IPEndPoint, byte[]>>();

            public LoopbackTransport(LoopbackNetwork network, IPEndPoint address)
            {
                _network = network;
                LocalEndPoint = address;
            }

            public IPEndPoint LocalEndPoint { get; }

            public void Send(IPEndPoint target, byte[] buffer, int offset, int count)
            {
                var copy = new byte[count];
                Array.Copy(buffer, offset, copy, 0, count);
                _network.Enqueue(LocalEndPoint, target, copy);
            }

            public bool TryReceive(out IPEndPoint source, out byte[] payload)
            {
                if (_inbox.Count == 0)
                {
                    source = default!;
                    payload = Array.Empty<byte>();
                    return false;
                }

                KeyValuePair<IPEndPoint, byte[]> item = _inbox.Dequeue();
                source = item.Key;
                payload = item.Value;
                return true;
            }

            internal void Deliver(IPEndPoint source, byte[] payload) =>
                _inbox.Enqueue(new KeyValuePair<IPEndPoint, byte[]>(source, payload));

            public void Dispose()
            {
                _inbox.Clear();
            }
        }
    }
}
