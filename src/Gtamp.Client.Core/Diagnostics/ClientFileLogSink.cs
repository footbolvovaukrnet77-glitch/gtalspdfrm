using System;
using System.IO;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Client.Diagnostics
{
    /// <summary>
    /// Appends the client log to <c>Gtamp/logs/client-yyyy-MM-dd.log</c>. Failures
    /// are swallowed: losing the log file must never take the game process down.
    /// </summary>
    public sealed class ClientFileLogSink : ILogSink, IDisposable
    {
        private readonly object _gate = new object();
        private StreamWriter? _writer;

        public ClientFileLogSink(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                FilePath = Path.Combine(directory, $"client-{DateTime.UtcNow:yyyy-MM-dd}.log");
                _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception)
            {
                _writer = null;
            }
        }

        public string FilePath { get; } = string.Empty;

        public void Write(in LogEntry entry)
        {
            if (_writer == null)
            {
                return;
            }

            string line = entry.FormatLine();
            string? detail = entry.Detail;

            lock (_gate)
            {
                try
                {
                    _writer.WriteLine(line);
                    if (!string.IsNullOrEmpty(detail))
                    {
                        _writer.WriteLine(detail);
                    }
                }
                catch (IOException)
                {
                    // Keep playing without file logging.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
