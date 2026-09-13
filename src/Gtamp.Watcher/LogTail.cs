using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gtamp.Watcher
{
    /// <summary>
    /// Follows a file that is being appended to by somebody else.
    /// <para>
    /// The client, ScriptHookV, ScriptHookVDotNet and RAGE Plugin Hook all hold
    /// their log files open while they write, so the file is opened with the
    /// widest possible sharing and never locked. A file that is rotated or
    /// truncated (a new day, a new session) is detected by the length going
    /// backwards, and reading restarts from the beginning rather than from an
    /// offset that now points into the middle of a line.
    /// </para>
    /// </summary>
    public sealed class LogTail
    {
        private long _offset;
        private string _carry = string.Empty;

        public LogTail(string path, bool fromStart = false)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            StartFromBeginning = fromStart;
        }

        public string Path { get; }

        public string Name { get; }

        /// <summary>When false, everything already in the file at startup is skipped.</summary>
        public bool StartFromBeginning { get; }

        public bool Primed { get; private set; }

        /// <summary>Returns the lines appended since the previous call. Never throws.</summary>
        public IReadOnlyList<string> ReadNewLines()
        {
            var lines = new List<string>();

            try
            {
                if (!File.Exists(Path))
                {
                    return lines;
                }

                using var stream = new FileStream(
                    Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                if (!Primed)
                {
                    Primed = true;
                    _offset = StartFromBeginning ? 0L : stream.Length;
                }

                if (stream.Length < _offset)
                {
                    // Rotated or truncated: an offset into the old file means nothing.
                    _offset = 0L;
                    _carry = string.Empty;
                }

                if (stream.Length == _offset)
                {
                    return lines;
                }

                stream.Seek(_offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                string text = _carry + reader.ReadToEnd();
                _offset = stream.Length;

                // A write can land mid-line; hold the tail back until its newline arrives.
                int lastBreak = text.LastIndexOf('\n');
                if (lastBreak < 0)
                {
                    _carry = text;
                    return lines;
                }

                _carry = text.Substring(lastBreak + 1);
                foreach (string line in text.Substring(0, lastBreak).Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r');
                    if (trimmed.Length > 0)
                    {
                        lines.Add(trimmed);
                    }
                }
            }
            catch (IOException)
            {
                // Being written to this instant. The next poll gets it.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return lines;
        }

        /// <summary>The last <paramref name="count"/> lines of the file, redacted, for the incident record.</summary>
        public static IReadOnlyList<string> Tail(string path, int count)
        {
            var kept = new LinkedList<string>();

            try
            {
                if (!File.Exists(path))
                {
                    return Array.Empty<string>();
                }

                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    kept.AddLast(line);
                    if (kept.Count > count)
                    {
                        kept.RemoveFirst();
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            var result = new List<string>(kept.Count);
            foreach (string line in kept)
            {
                result.Add(Redactor.Scrub(line));
            }

            return result;
        }
    }
}
