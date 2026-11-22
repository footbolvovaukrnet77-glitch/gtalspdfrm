using System;
using System.Collections.Generic;
using System.Threading;

namespace Gtamp.Shared.Diagnostics
{
    /// <summary>
    /// Central log fan-out. Everything — server console, in-game F8 console, crash
    /// report writer, bug report collector — subscribes here, so a single call site
    /// feeds every consumer and nothing has to be logged twice.
    /// </summary>
    public sealed class LogBus
    {
        private readonly List<ILogSink> _sinks = new List<ILogSink>();
        private readonly object _gate = new object();
        private long _nextId;

        public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

        public void AddSink(ILogSink sink)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            lock (_gate)
            {
                _sinks.Add(sink);
            }
        }

        public void RemoveSink(ILogSink sink)
        {
            lock (_gate)
            {
                _sinks.Remove(sink);
            }
        }

        public LogEntry Write(LogLevel level, LogCategory category, string message, string? detail = null, string? tag = null)
        {
            var entry = new LogEntry(
                Interlocked.Increment(ref _nextId), DateTime.UtcNow, level, category, message, detail, tag);

            if (level < MinimumLevel)
            {
                return entry;
            }

            ILogSink[] snapshot;
            lock (_gate)
            {
                snapshot = _sinks.ToArray();
            }

            foreach (ILogSink sink in snapshot)
            {
                try
                {
                    sink.Write(in entry);
                }
                catch (Exception)
                {
                    // A broken sink must never take the tick loop down with it.
                }
            }

            return entry;
        }

        public LogEntry Debug(LogCategory category, string message, string? tag = null) =>
            Write(LogLevel.Debug, category, message, null, tag);

        public LogEntry Info(LogCategory category, string message, string? tag = null) =>
            Write(LogLevel.Info, category, message, null, tag);

        public LogEntry Success(LogCategory category, string message, string? tag = null) =>
            Write(LogLevel.Success, category, message, null, tag);

        public LogEntry Warning(LogCategory category, string message, string? tag = null) =>
            Write(LogLevel.Warning, category, message, null, tag);

        public LogEntry Error(LogCategory category, string message, string? detail = null, string? tag = null) =>
            Write(LogLevel.Error, category, message, detail, tag);

        public LogEntry Error(LogCategory category, string message, Exception exception, string? tag = null) =>
            Write(LogLevel.Error, category, message, exception.ToString(), tag);

        public LogEntry Critical(LogCategory category, string message, string? detail = null, string? tag = null) =>
            Write(LogLevel.Critical, category, message, detail, tag);
    }
}
