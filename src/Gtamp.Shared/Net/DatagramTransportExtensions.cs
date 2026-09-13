using System.Net;
using Gtamp.Shared.Protocol;

namespace Gtamp.Shared.Net
{
    public static class DatagramTransportExtensions
    {
        /// <summary>Sends a handshake-stage packet, which carries no session id or sequencing.</summary>
        public static void SendConnectionless(
            this IDatagramTransport transport,
            IPEndPoint target,
            NetMessageType type,
            byte[] payload)
        {
            byte[] datagram = ConnectionlessPacket.Write(type, payload);
            transport.Send(target, datagram, 0, datagram.Length);
        }
    }
}
