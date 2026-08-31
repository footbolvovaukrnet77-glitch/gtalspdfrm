using System;
using System.Globalization;

namespace Gtamp.Shared.Diagnostics
{
    /// <summary>Severity, ordered so a filter can be expressed as "at least this level".</summary>
    public enum LogLevel : byte
    {
        Debug = 0,
        Info = 1,
        Success = 2,
        Warning = 3,
        Error = 4,
        Critical = 5,
    }

    /// <summary>
    /// Subsystem a line came from. The in-game console filters on this, and the bug
    /// report groups recent lines by it (master prompt sections 38-40).
    /// </summary>
    public enum LogCategory : byte
    {
        General = 0,
        Network = 1,
        Server = 2,
        Client = 3,
        Mod = 4,
        Security = 5,
        World = 6,
        Entity = 7,
        Persistence = 8,
        Console = 9,
    }

    public readonly struct LogEntry
    {
        public LogEntry(
            long id,
            DateTime timestampUtc,
            LogLevel level,
            LogCategory category,
            string message,
            string? detail = null,
            string? tag = null)
        {
            Id = id;
            TimestampUtc = timestampUtc;
            Level = level;
            Category = category;
            Message = message ?? string.Empty;
            Detail = detail;
            Tag = tag;
        }

        /// <summary>Monotonic id. Doubles as the "Error ID" the console search accepts.</summary>
        public long Id { get; }

        public DateTime TimestampUtc { get; }

        public LogLevel Level { get; }

        public LogCategory Category { get; }

        public string Message { get; }

        /// <summary>Stack trace or structured payload, shown by "Copy Full Error".</summary>
        public string? Detail { get; }

        /// <summary>Free-form correlation tag, e.g. "entity:152" or "player:3".</summary>
        public string? Tag { get; }

        public bool IsProblem => Level >= LogLevel.Warning;

        public string FormatLine() => string.Format(
            CultureInfo.InvariantCulture,
            "[{0:HH:mm:ss.fff}] [{1}] [{2}] {3}",
            TimestampUtc,
            Level.ToString().ToUpperInvariant(),
            Category.ToString().ToUpperInvariant(),
            Message);

        public override string ToString() => FormatLine();
    }

    public interface ILogSink
    {
        void Write(in LogEntry entry);
    }
}
