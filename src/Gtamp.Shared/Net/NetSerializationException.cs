using System;

namespace Gtamp.Shared.Net
{
    /// <summary>
    /// Thrown when an inbound buffer cannot be decoded. Callers are expected to
    /// drop the offending packet, raise a SECURITY/NETWORK log entry and keep the
    /// connection alive; a decode failure is never fatal to the tick loop.
    /// </summary>
    public sealed class NetSerializationException : Exception
    {
        public NetSerializationException(string message)
            : base(message)
        {
        }

        public NetSerializationException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
