using System;
using System.IO;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Server.Diagnostics
{
    /// <summary>
    /// Appends to <c>logs/server-yyyy-MM-dd.log</c>. Failures are swallowed on
    /// purpose: a full disk must not stop the simulation.
    /// </summary>
    public sealed class FileLogSink : ILogSink, IDisposable
    {
        private readonly object _gate = new object();
        private StreamWriter? _writer;

        public FileLogSink(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, $"server-{DateTime.UtcNow:yyyy-MM-dd}.log");
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };

                Path_ = path;
            }
            catch (Exception)
            {
                _writer = null;
            }
        }

        public string Path_ { get; } = string.Empty;

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
                    // Keep running without file logging.
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
