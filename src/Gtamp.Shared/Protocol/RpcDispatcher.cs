using System;
using System.Collections.Generic;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Shared.Protocol
{
    /// <summary>The answer to an RPC, or the reason there is not one.</summary>
    public readonly struct RpcResult
    {
        private RpcResult(bool success, byte[] payload, string error)
        {
            Success = success;
            Payload = payload;
            Error = error;
        }

        public bool Success { get; }

        public byte[] Payload { get; }

        public string Error { get; }

        public static RpcResult Ok(byte[] payload) => new RpcResult(true, payload, string.Empty);

        public static RpcResult Failed(string error) => new RpcResult(false, Array.Empty<byte>(), error);
    }

    /// <summary>
    /// Request/response plumbing for mod-defined procedures, used by both sides.
    /// <para>
    /// Calls are matched by id and <b>always complete</b>: with an answer, with the
    /// remote handler's error, or with a timeout. A call that can hang forever is a
    /// mod bug that presents as a frozen game, so the timeout is not optional.
    /// </para>
    /// <para>
    /// Handlers are synchronous by design. Both sides run their mod code on a single
    /// thread — the game's script thread and the server's tick thread — so an
    /// asynchronous handler would need a scheduler that does not exist and would
    /// invite mods to do slow work in the middle of a frame.
    /// </para>
    /// </summary>
    public sealed class RpcDispatcher<TContext>
    {
        public const double DefaultTimeoutSeconds = 5.0;

        private readonly Dictionary<string, Func<TContext, byte[], byte[]>> _handlers =
            new Dictionary<string, Func<TContext, byte[], byte[]>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<uint, PendingCall> _pending = new Dictionary<uint, PendingCall>();
        private readonly List<uint> _expired = new List<uint>();
        private readonly LogBus _log;

        private uint _nextCallId = 1;

        public RpcDispatcher(LogBus log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public int PendingCallCount => _pending.Count;

        public int HandlerCount => _handlers.Count;

        public int TimedOutCalls { get; private set; }

        public void RegisterHandler(string name, Func<TContext, byte[], byte[]> handler)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("An RPC name must not be empty.", nameof(name));
            }

            _handlers[name] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public bool IsRegistered(string name) => _handlers.ContainsKey(name);

        /// <summary>Prepares a call. The caller sends the returned message and awaits the response.</summary>
        public ModRpcRequestMessage BeginCall(
            string name, byte[] payload, Action<RpcResult> callback, double now, double timeoutSeconds = DefaultTimeoutSeconds)
        {
            uint callId = _nextCallId++;
            _pending[callId] = new PendingCall(callback, now + timeoutSeconds, name);

            return new ModRpcRequestMessage
            {
                CallId = callId,
                Name = name,
                Payload = payload ?? Array.Empty<byte>(),
            };
        }

        /// <summary>Runs a handler for an incoming request and builds the response.</summary>
        public ModRpcResponseMessage HandleRequest(ModRpcRequestMessage request, TContext context)
        {
            if (!_handlers.TryGetValue(request.Name, out Func<TContext, byte[], byte[]>? handler))
            {
                return new ModRpcResponseMessage
                {
                    CallId = request.CallId,
                    Success = false,
                    Error = $"no handler is registered for '{request.Name}'",
                };
            }

            try
            {
                byte[] result = handler(context, request.Payload) ?? Array.Empty<byte>();
                return new ModRpcResponseMessage { CallId = request.CallId, Success = true, Payload = result };
            }
            catch (Exception exception)
            {
                // A mod's handler throwing is its problem, not the session's. The caller
                // is told why rather than being left waiting for the timeout.
                _log.Error(LogCategory.Mod, $"RPC handler '{request.Name}' threw.", exception);

                return new ModRpcResponseMessage
                {
                    CallId = request.CallId,
                    Success = false,
                    Error = $"{exception.GetType().Name}: {exception.Message}",
                };
            }
        }

        /// <summary>Completes a pending call. Returns false when the id is unknown or already answered.</summary>
        public bool HandleResponse(ModRpcResponseMessage response)
        {
            if (!_pending.TryGetValue(response.CallId, out PendingCall call))
            {
                return false;
            }

            _pending.Remove(response.CallId);
            Invoke(call, response.Success ? RpcResult.Ok(response.Payload) : RpcResult.Failed(response.Error));
            return true;
        }

        /// <summary>Fails calls whose answer never arrived.</summary>
        public void Update(double now)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            _expired.Clear();
            foreach (KeyValuePair<uint, PendingCall> pair in _pending)
            {
                if (now >= pair.Value.ExpiresAt)
                {
                    _expired.Add(pair.Key);
                }
            }

            foreach (uint callId in _expired)
            {
                PendingCall call = _pending[callId];
                _pending.Remove(callId);
                TimedOutCalls++;
                Invoke(call, RpcResult.Failed($"'{call.Name}' timed out"));
            }
        }

        /// <summary>Fails every outstanding call, e.g. when the connection drops.</summary>
        public void FailAllPending(string reason)
        {
            var calls = new List<PendingCall>(_pending.Values);
            _pending.Clear();

            foreach (PendingCall call in calls)
            {
                Invoke(call, RpcResult.Failed(reason));
            }
        }

        private void Invoke(PendingCall call, RpcResult result)
        {
            try
            {
                call.Callback(result);
            }
            catch (Exception exception)
            {
                _log.Error(LogCategory.Mod, $"The callback for RPC '{call.Name}' threw.", exception);
            }
        }

        private readonly struct PendingCall
        {
            public PendingCall(Action<RpcResult> callback, double expiresAt, string name)
            {
                Callback = callback;
                ExpiresAt = expiresAt;
                Name = name;
            }

            public Action<RpcResult> Callback { get; }

            public double ExpiresAt { get; }

            public string Name { get; }
        }
    }
}
