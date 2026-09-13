using System;
using System.Collections.Generic;
using Gtamp.Shared.Interop;

namespace Gtamp.Client.Mods
{
    /// <summary>
    /// The core side of the in-process channel to <c>Gtamp.RphBridge.dll</c>, shared
    /// by every adapter that needs it.
    /// <para>
    /// <b>Why this exists rather than each adapter opening its own endpoint.</b>
    /// <see cref="InProcessChannel.OpenCoreSide"/> hands back a view onto one queue
    /// pair, not a private one. Two adapters draining the same inbox would steal each
    /// other's messages — the RPH adapter would swallow the LSPDFR events and the
    /// LSPDFR adapter would swallow the handshake. So exactly one object owns the
    /// endpoint and fans messages out by topic; subscribers see every message on the
    /// topics they asked for and nothing else.
    /// </para>
    /// <para>
    /// Dispatch is synchronous, on whichever thread calls <see cref="Pump"/> — the
    /// client update thread. Handlers must not block: the bridge is on RPH's
    /// <c>GameFiber</c> and the core is on the ScriptHookVDotNet script thread, and
    /// the whole point of the channel is that neither waits on the other.
    /// </para>
    /// </summary>
    public sealed class BridgeLink
    {
        private static readonly object SharedLock = new object();
        private static BridgeLink? _shared;

        private readonly InProcessEndpoint _endpoint;
        private readonly Dictionary<string, List<Action<byte[]>>> _subscribers =
            new Dictionary<string, List<Action<byte[]>>>(StringComparer.Ordinal);

        public BridgeLink(InProcessEndpoint endpoint)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        }

        /// <summary>
        /// The process-wide link, opened on first use. Opening is safe even when no
        /// bridge is loaded: the queues simply stay empty.
        /// </summary>
        public static BridgeLink Shared
        {
            get
            {
                lock (SharedLock)
                {
                    return _shared ??= new BridgeLink(InProcessChannel.OpenCoreSide());
                }
            }
        }

        /// <summary>Replaces the shared link. For tests, which drive both sides of a loopback pair.</summary>
        public static void OverrideShared(BridgeLink? link)
        {
            lock (SharedLock)
            {
                _shared = link;
            }
        }

        /// <summary>Messages received that no adapter had subscribed to. Reported by /diagnostics.</summary>
        public int UnroutedMessages { get; private set; }

        /// <summary>Total messages dispatched to at least one subscriber.</summary>
        public int RoutedMessages { get; private set; }

        /// <summary>Handler exceptions swallowed so one adapter cannot take the channel down.</summary>
        public int HandlerFailures { get; private set; }

        public InProcessEndpoint Endpoint => _endpoint;

        public void Subscribe(string topic, Action<byte[]> handler)
        {
            if (string.IsNullOrEmpty(topic))
            {
                throw new ArgumentException("A channel topic must not be empty.", nameof(topic));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!_subscribers.TryGetValue(topic, out List<Action<byte[]>> handlers))
            {
                handlers = new List<Action<byte[]>>();
                _subscribers[topic] = handlers;
            }

            handlers.Add(handler);
        }

        public void Unsubscribe(string topic, Action<byte[]> handler)
        {
            if (topic != null && _subscribers.TryGetValue(topic, out List<Action<byte[]>> handlers))
            {
                handlers.Remove(handler);
            }
        }

        public void Send(string topic, byte[] payload) => _endpoint.Send(topic, payload);

        /// <summary>
        /// Drains the inbox and dispatches. Safe to call more than once per frame —
        /// each adapter calls it and whichever gets there first delivers to all of
        /// them, so no adapter has to know whether another one exists.
        /// </summary>
        public int Pump()
        {
            int delivered = 0;

            while (_endpoint.TryReceive(out string topic, out byte[] payload))
            {
                if (!_subscribers.TryGetValue(topic, out List<Action<byte[]>> handlers) || handlers.Count == 0)
                {
                    UnroutedMessages++;
                    continue;
                }

                RoutedMessages++;
                delivered++;

                for (int i = 0; i < handlers.Count; i++)
                {
                    try
                    {
                        handlers[i](payload);
                    }
                    catch (Exception)
                    {
                        // Counted rather than rethrown: a broken adapter must not stop
                        // the messages the other adapters are waiting for.
                        HandlerFailures++;
                    }
                }
            }

            return delivered;
        }
    }
}
