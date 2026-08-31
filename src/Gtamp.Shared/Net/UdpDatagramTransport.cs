using System;
using System.Net;
using System.Net.Sockets;

namespace Gtamp.Shared.Net
{
    /// <summary>Real UDP transport used by the shipped client and server.</summary>
    public sealed class UdpDatagramTransport : IDatagramTransport
    {
        private readonly Socket _socket;
        private readonly byte[] _receiveBuffer = new byte[2048];
        private bool _disposed;

        public UdpDatagramTransport(IPEndPoint bindTo)
        {
            _socket = new Socket(bindTo.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            {
                Blocking = false,
                SendBufferSize = 1 << 20,
                ReceiveBufferSize = 1 << 20,
            };

            // Windows raises ECONNRESET on a UDP socket when a previous send was
            // answered with ICMP port-unreachable. Left unhandled it kills the
            // receive loop, so suppress it (SIO_UDP_CONNRESET).
            try
            {
                _socket.IOControl(unchecked((int)0x9800000C), new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception)
            {
                // Not supported off Windows; harmless.
            }

            _socket.Bind(bindTo);
            LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        }

        public IPEndPoint LocalEndPoint { get; }

        public void Send(IPEndPoint target, byte[] buffer, int offset, int count)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _socket.SendTo(buffer, offset, count, SocketFlags.None, target);
            }
            catch (SocketException)
            {
                // Datagram loss is normal; the reliability layer handles it.
            }
        }

        public bool TryReceive(out IPEndPoint source, out byte[] payload)
        {
            source = default!;
            payload = Array.Empty<byte>();

            if (_disposed || _socket.Available <= 0)
            {
                return false;
            }

            EndPoint from = new IPEndPoint(
                _socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

            try
            {
                int received = _socket.ReceiveFrom(_receiveBuffer, SocketFlags.None, ref from);
                if (received <= 0)
                {
                    return false;
                }

                payload = new byte[received];
                Array.Copy(_receiveBuffer, payload, received);
                source = (IPEndPoint)from;
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _socket.Close();
            }
            catch (Exception)
            {
                // Nothing useful to do while tearing down.
            }
        }
    }
}
