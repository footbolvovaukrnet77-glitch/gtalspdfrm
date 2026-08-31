using System;
using System.Collections.Concurrent;
using System.Text;

namespace Gtamp.Shared.Interop
{
    /// <summary>
    /// A message queue shared between two .NET plugin hosts inside one GTA V process.
    /// <para>
    /// RAGE Plugin Hook and ScriptHookVDotNet each load their own assemblies, on
    /// their own schedulers, with no supported way to call each other. This is the
    /// seam between them: each side owns an endpoint, and they exchange <b>bytes
    /// under a topic name</b> — never objects, never types.
    /// </para>
    /// <para>
    /// <b>Why bytes.</b> The two hosts may load different copies of the same
    /// assembly, in which case a type from one is not the same type to the other and
    /// any shared object is uncastable. The only things reliably shared are the
    /// framework's own types, so the rendezvous uses <see cref="AppDomain"/> data
    /// holding <see cref="ConcurrentQueue{T}"/> of <c>byte[]</c>, and the payload
    /// contract is a wire format rather than an interface.
    /// </para>
    /// <para>
    /// <b>Why it is not just a lock-free queue.</b> Neither side may block the other:
    /// RPH work runs on a <c>GameFiber</c> and SHVDN work on the script thread, and a
    /// blocking call from one into the other deadlocks the game. Both queues are
    /// therefore bounded and non-blocking — a full queue drops its oldest message and
    /// counts it, which costs freshness rather than the frame.
    /// </para>
    /// </summary>
    public static class InProcessChannel
    {
        /// <summary>Bumped when the framing or rendezvous changes incompatibly.</summary>
        public const int Version = 1;

        public const int DefaultCapacity = 512;

        private const string CoreInboxKey = "GTAMP.Interop.CoreInbox.v1";
        private const string PluginInboxKey = "GTAMP.Interop.PluginInbox.v1";
        private const string RendezvousLockKey = "GTAMP.Interop.Lock.v1";

        /// <summary>
        /// Opens the multiplayer-core side of the channel: the endpoint the
        /// ScriptHookVDotNet-hosted client uses.
        /// </summary>
        public static InProcessEndpoint OpenCoreSide(int capacity = DefaultCapacity) =>
            Open(inboxKey: CoreInboxKey, outboxKey: PluginInboxKey, capacity);

        /// <summary>
        /// Opens the plugin side: the endpoint the RAGE Plugin Hook-hosted bridge uses.
        /// </summary>
        public static InProcessEndpoint OpenPluginSide(int capacity = DefaultCapacity) =>
            Open(inboxKey: PluginInboxKey, outboxKey: CoreInboxKey, capacity);

        /// <summary>True when the other side has already opened its endpoint.</summary>
        public static bool IsCoreSidePresent() => AppDomain.CurrentDomain.GetData(CoreInboxKey) != null;

        public static bool IsPluginSidePresent() => AppDomain.CurrentDomain.GetData(PluginInboxKey) != null;

        /// <summary>
        /// Creates an unattached pair, for tests and for running both sides in one
        /// process without touching AppDomain state.
        /// </summary>
        public static (InProcessEndpoint Core, InProcessEndpoint Plugin) CreateLoopbackPair(
            int capacity = DefaultCapacity)
        {
            var coreInbox = new ConcurrentQueue<byte[]>();
            var pluginInbox = new ConcurrentQueue<byte[]>();

            return (
                new InProcessEndpoint(coreInbox, pluginInbox, capacity),
                new InProcessEndpoint(pluginInbox, coreInbox, capacity));
        }

        private static InProcessEndpoint Open(string inboxKey, string outboxKey, int capacity)
        {
            // Interned strings are shared across every assembly in the process, which
            // makes this the one lock object both hosts can reach without agreeing on
            // a type.
            lock (string.Intern(RendezvousLockKey))
            {
                var inbox = AppDomain.CurrentDomain.GetData(inboxKey) as ConcurrentQueue<byte[]>;
                if (inbox == null)
                {
                    inbox = new ConcurrentQueue<byte[]>();
                    AppDomain.CurrentDomain.SetData(inboxKey, inbox);
                }

                var outbox = AppDomain.CurrentDomain.GetData(outboxKey) as ConcurrentQueue<byte[]>;
                if (outbox == null)
                {
                    outbox = new ConcurrentQueue<byte[]>();
                    AppDomain.CurrentDomain.SetData(outboxKey, outbox);
                }

                return new InProcessEndpoint(inbox, outbox, capacity);
            }
        }
    }

    /// <summary>One end of an <see cref="InProcessChannel"/>.</summary>
    public sealed class InProcessEndpoint
    {
        private readonly ConcurrentQueue<byte[]> _inbox;
        private readonly ConcurrentQueue<byte[]> _outbox;

        internal InProcessEndpoint(ConcurrentQueue<byte[]> inbox, ConcurrentQueue<byte[]> outbox, int capacity)
        {
            _inbox = inbox;
            _outbox = outbox;
            Capacity = capacity < 1 ? 1 : capacity;
        }

        public int Capacity { get; }

        public int Pending => _inbox.Count;

        public int Queued => _outbox.Count;

        public int Sent { get; private set; }

        public int Received { get; private set; }

        /// <summary>Messages dropped because the far side was not draining fast enough.</summary>
        public int Dropped { get; private set; }

        public void Send(string topic, byte[] payload)
        {
            if (string.IsNullOrEmpty(topic))
            {
                throw new ArgumentException("A channel topic must not be empty.", nameof(topic));
            }

            // Dropping the oldest keeps a stalled reader from turning into unbounded
            // memory growth on the writer. Newer state is always the more useful.
            while (_outbox.Count >= Capacity)
            {
                if (_outbox.TryDequeue(out byte[]? _))
                {
                    Dropped++;
                }
                else
                {
                    break;
                }
            }

            _outbox.Enqueue(Frame(topic, payload ?? Array.Empty<byte>()));
            Sent++;
        }

        public bool TryReceive(out string topic, out byte[] payload)
        {
            topic = string.Empty;
            payload = Array.Empty<byte>();

            while (_inbox.TryDequeue(out byte[]? framed))
            {
                if (!TryUnframe(framed, out topic, out payload))
                {
                    // A malformed frame means the other side is a different version.
                    // Skipping it is better than tearing the channel down.
                    continue;
                }

                Received++;
                return true;
            }

            return false;
        }

        public void Clear()
        {
            while (_inbox.TryDequeue(out byte[]? _))
            {
            }

            while (_outbox.TryDequeue(out byte[]? _))
            {
            }
        }

        /// <summary>
        /// <c>[u8 version][u16 topicLength][topic utf8][payload]</c>. Hand-rolled rather
        /// than reusing the network writer, because the two sides may hold different
        /// copies of that assembly and only the byte layout is guaranteed to agree.
        /// </summary>
        internal static byte[] Frame(string topic, byte[] payload)
        {
            byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
            if (topicBytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException("The topic name is too long.", nameof(topic));
            }

            var frame = new byte[3 + topicBytes.Length + payload.Length];
            frame[0] = InProcessChannel.Version;
            frame[1] = (byte)topicBytes.Length;
            frame[2] = (byte)(topicBytes.Length >> 8);

            Array.Copy(topicBytes, 0, frame, 3, topicBytes.Length);
            Array.Copy(payload, 0, frame, 3 + topicBytes.Length, payload.Length);
            return frame;
        }

        internal static bool TryUnframe(byte[] frame, out string topic, out byte[] payload)
        {
            topic = string.Empty;
            payload = Array.Empty<byte>();

            if (frame == null || frame.Length < 3 || frame[0] != InProcessChannel.Version)
            {
                return false;
            }

            int topicLength = frame[1] | (frame[2] << 8);
            if (3 + topicLength > frame.Length)
            {
                return false;
            }

            topic = Encoding.UTF8.GetString(frame, 3, topicLength);

            int payloadLength = frame.Length - 3 - topicLength;
            payload = new byte[payloadLength];
            Array.Copy(frame, 3 + topicLength, payload, 0, payloadLength);
            return true;
        }
    }

    /// <summary>Topic names both sides agree on. Kept in one place so they cannot drift.</summary>
    public static class InteropTopics
    {
        /// <summary>Plugin side announcing itself, with its version and what it can see.</summary>
        public const string Hello = "gtamp.hello";

        /// <summary>Core side asking the plugin side to describe itself again.</summary>
        public const string Describe = "gtamp.describe";

        /// <summary>Plugin side reporting the RPH plugins it can see.</summary>
        public const string PluginList = "gtamp.rph.plugins";

        /// <summary>An LSPDFR event the plugin side observed, forwarded verbatim.</summary>
        public const string LspdfrEvent = "gtamp.lspdfr.event";

        /// <summary>An opaque payload from a mod, in either direction.</summary>
        public const string ModPayload = "gtamp.mod.payload";
    }
}
