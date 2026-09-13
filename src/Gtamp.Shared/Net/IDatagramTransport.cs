using System;
using System.Net;

namespace Gtamp.Shared.Net
{
    /// <summary>
    /// Everything above this interface is testable without a socket. The unit and
    /// stress tests drive the whole stack through <see cref="LoopbackNetwork"/>,
    /// which can inject latency, jitter, loss and reordering deterministically.
    /// </summary>
    public interface IDatagramTransport : IDisposable
    {
        IPEndPoint LocalEndPoint { get; }

        void Send(IPEndPoint target, byte[] buffer, int offset, int count);

        /// <summary>Non-blocking poll. Returns false when no datagram is queued.</summary>
        bool TryReceive(out IPEndPoint source, out byte[] payload);
    }
}
