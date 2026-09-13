using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Client.Ui
{
    /// <summary>
    /// The in-game developer console (master prompt sections 37-46), minus any
    /// drawing. It owns the log buffer, the filter and search state, the command
    /// table and the copy/bug-report actions; the host script only renders
    /// <see cref="VisibleLines"/> and forwards key input.
    /// <para>
    /// Keeping it renderer-free is what lets the console be covered by unit tests —
    /// filtering and search are exactly the kind of logic that quietly rots when it
    /// can only be exercised by standing in Los Santos and pressing F8.
    /// </para>
    /// </summary>
    public sealed class DeveloperConsole : ILogSink
    {
        public const int DefaultCapacity = 2000;

        private readonly List<LogEntry> _entries = new List<LogEntry>();
        private readonly List<string> _inputHistory = new List<string>();
        private readonly Dictionary<string, ConsoleCommand> _commands =
            new Dictionary<string, ConsoleCommand>(StringComparer.OrdinalIgnoreCase);

        private readonly object _gate = new object();
        private int _historyCursor = -1;

        public DeveloperConsole(IClipboard? clipboard = null, int capacity = DefaultCapacity)
        {
            Clipboard = clipboard ?? new NullClipboard();
            Capacity = capacity < 32 ? 32 : capacity;
            RegisterBuiltIns();
        }

        public IClipboard Clipboard { get; }

        public int Capacity { get; }

        public bool IsOpen { get; private set; }

        /// <summary>Gates the developer-only commands. Off for ordinary players.</summary>
        public bool DeveloperMode { get; set; }

        public ConsoleFilter Filter { get; set; } = ConsoleFilter.All;

        /// <summary>Free-text search. Matches message, detail, tag, level, category and entry id.</summary>
        public string SearchQuery { get; set; } = string.Empty;

        public string InputLine { get; set; } = string.Empty;

        /// <summary>Lines scrolled back from the bottom. 0 follows the newest output.</summary>
        public int ScrollOffset { get; private set; }

        public int VisibleLineCount { get; set; } = 18;

        public int EntryCount
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        public IEnumerable<ConsoleCommand> Commands => _commands.Values;

        public void Toggle() => IsOpen = !IsOpen;

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void Write(in LogEntry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
                while (_entries.Count > Capacity)
                {
                    _entries.RemoveAt(0);
                }
            }
        }

        public void RegisterCommand(ConsoleCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }

            _commands[command.Name] = command;
        }

        public bool TryGetCommand(string name, out ConsoleCommand command) => _commands.TryGetValue(name, out command!);

        /// <summary>Entries that pass the current filter and search, oldest first.</summary>
        public List<LogEntry> FilteredEntries()
        {
            lock (_gate)
            {
                var results = new List<LogEntry>();
                foreach (LogEntry entry in _entries)
                {
                    if (Matches(entry))
                    {
                        results.Add(entry);
                    }
                }

                return results;
            }
        }

        /// <summary>The window of lines the renderer should draw, honouring scrollback.</summary>
        public List<ConsoleLine> VisibleLines()
        {
            List<LogEntry> filtered = FilteredEntries();
            var lines = new List<ConsoleLine>();

            int end = filtered.Count - ScrollOffset;
            if (end < 0)
            {
                end = 0;
            }

            int start = end - VisibleLineCount;
            if (start < 0)
            {
                start = 0;
            }

            for (int i = start; i < end; i++)
            {
                LogEntry entry = filtered[i];
                lines.Add(new ConsoleLine(entry.Id, entry.FormatLine(), ConsolePalette.RoleFor(in entry)));
            }

            return lines;
        }

        public void Scroll(int lines)
        {
            int filteredCount = FilteredEntries().Count;
            ScrollOffset += lines;
            if (ScrollOffset < 0)
            {
                ScrollOffset = 0;
            }

            int maximum = Math.Max(0, filteredCount - VisibleLineCount);
            if (ScrollOffset > maximum)
            {
                ScrollOffset = maximum;
            }
        }

        public void ScrollToBottom() => ScrollOffset = 0;

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
            }

            ScrollOffset = 0;
        }

        /// <summary>Runs a line of console input and logs both the echo and the result.</summary>
        public string Submit(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            line = line.Trim();
            _inputHistory.Add(line);
            _historyCursor = _inputHistory.Count;
            InputLine = string.Empty;
            ScrollToBottom();

            Write(new LogEntry(0, DateTime.UtcNow, LogLevel.Info, LogCategory.Console, "> " + line));

            string[] parts = line.TrimStart('/').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return string.Empty;
            }

            string name = parts[0];
            var arguments = new List<string>();
            for (int i = 1; i < parts.Length; i++)
            {
                arguments.Add(parts[i]);
            }

            string rawArguments = line.TrimStart('/').Substring(name.Length).Trim();

            if (!_commands.TryGetValue(name, out ConsoleCommand? command))
            {
                string unknown = $"Unknown command '{name}'. Type 'help'.";
                Write(new LogEntry(0, DateTime.UtcNow, LogLevel.Warning, LogCategory.Console, unknown));
                return unknown;
            }

            if (command.DeveloperOnly && !DeveloperMode)
            {
                string refused = $"'{name}' is a developer command. Enable developer mode first.";
                Write(new LogEntry(0, DateTime.UtcNow, LogLevel.Warning, LogCategory.Security, refused));
                return refused;
            }

            try
            {
                string result = command.Handler(new ConsoleCommandContext(name, arguments, rawArguments));
                if (!string.IsNullOrEmpty(result))
                {
                    foreach (string resultLine in result.Split('\n'))
                    {
                        Write(new LogEntry(0, DateTime.UtcNow, LogLevel.Info, LogCategory.Console, resultLine.TrimEnd('\r')));
                    }
                }

                return result;
            }
            catch (Exception exception)
            {
                string failure = $"Command '{name}' threw {exception.GetType().Name}: {exception.Message}";
                Write(new LogEntry(0, DateTime.UtcNow, LogLevel.Error, LogCategory.Console, failure, exception.ToString()));
                return failure;
            }
        }

        public string HistoryPrevious()
        {
            if (_inputHistory.Count == 0)
            {
                return InputLine;
            }

            _historyCursor = Math.Max(0, _historyCursor - 1);
            InputLine = _inputHistory[_historyCursor];
            return InputLine;
        }

        public string HistoryNext()
        {
            if (_inputHistory.Count == 0)
            {
                return InputLine;
            }

            _historyCursor = Math.Min(_inputHistory.Count, _historyCursor + 1);
            InputLine = _historyCursor >= _inputHistory.Count ? string.Empty : _inputHistory[_historyCursor];
            return InputLine;
        }

        /// <summary>The most recent entry at warning level or above, which is what "copy error" acts on.</summary>
        public LogEntry? LastProblem()
        {
            lock (_gate)
            {
                for (int i = _entries.Count - 1; i >= 0; i--)
                {
                    if (_entries[i].IsProblem)
                    {
                        return _entries[i];
                    }
                }
            }

            return null;
        }

        public LogEntry? FindById(long id)
        {
            lock (_gate)
            {
                foreach (LogEntry entry in _entries)
                {
                    if (entry.Id == id)
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        /// <summary>Copies an error in one of the four shapes from master prompt section 41.</summary>
        public string Copy(CopyMode mode, LogEntry? entry, Func<string>? bugReportFactory = null)
        {
            string text;
            switch (mode)
            {
                case CopyMode.Message:
                    text = entry?.Message ?? string.Empty;
                    break;

                case CopyMode.StackTrace:
                    text = entry?.Detail ?? "(no stack trace attached)";
                    break;

                case CopyMode.FullError:
                    text = entry.HasValue
                        ? entry.Value.FormatLine() + (string.IsNullOrEmpty(entry.Value.Detail) ? string.Empty : Environment.NewLine + entry.Value.Detail)
                        : string.Empty;
                    break;

                case CopyMode.BugReport:
                    text = bugReportFactory?.Invoke() ?? string.Empty;
                    break;

                default:
                    text = string.Empty;
                    break;
            }

            Clipboard.SetText(text);
            return text;
        }

        /// <summary>Recent lines for the bug report, newest last.</summary>
        public List<LogEntry> RecentEntries(int count)
        {
            lock (_gate)
            {
                var results = new List<LogEntry>();
                int start = Math.Max(0, _entries.Count - count);
                for (int i = start; i < _entries.Count; i++)
                {
                    results.Add(_entries[i]);
                }

                return results;
            }
        }

        public List<LogEntry> RecentProblems(int count)
        {
            lock (_gate)
            {
                var results = new List<LogEntry>();
                for (int i = _entries.Count - 1; i >= 0 && results.Count < count; i--)
                {
                    if (_entries[i].IsProblem)
                    {
                        results.Add(_entries[i]);
                    }
                }

                results.Reverse();
                return results;
            }
        }

        private bool Matches(in LogEntry entry)
        {
            switch (Filter)
            {
                case ConsoleFilter.All:
                    break;
                case ConsoleFilter.Info:
                    if (entry.Level != LogLevel.Info && entry.Level != LogLevel.Success)
                    {
                        return false;
                    }

                    break;
                case ConsoleFilter.Debug:
                    if (entry.Level != LogLevel.Debug)
                    {
                        return false;
                    }

                    break;
                case ConsoleFilter.Warning:
                    if (entry.Level != LogLevel.Warning)
                    {
                        return false;
                    }

                    break;
                case ConsoleFilter.Error:
                    if (entry.Level != LogLevel.Error && entry.Level != LogLevel.Critical)
                    {
                        return false;
                    }

                    break;
                case ConsoleFilter.Critical:
                    if (entry.Level != LogLevel.Critical)
                    {
                        return false;
                    }

                    break;
                case ConsoleFilter.Network:
                    if (entry.Category != LogCategory.Network)
                    {
                        return false;
                    }

                    break;
                case ConsoleFilter.Server:
                    if (entry.Category != LogCategory.Server)
                    {
                        return false;
                    }

                    break;
                case ConsoleFilter.Client:
                    if (entry.Category != LogCategory.Client)
                    {
                        return false;
                    }

                    break;
                case ConsoleFilter.Mod:
                    if (entry.Category != LogCategory.Mod)
                    {
                        return false;
                    }

                    break;
                case ConsoleFilter.Security:
                    if (entry.Category != LogCategory.Security)
                    {
                        return false;
                    }

                    break;
            }

            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                return true;
            }

            string query = SearchQuery.Trim();

            // An all-digit query is treated as an error id and matched exactly. A
            // substring match on the id would also hit every timestamp containing
            // those digits, which makes "search 152" useless for finding error 152.
            if (long.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
                && entry.Id == id)
            {
                return true;
            }

            // The timestamp is only searched when the query looks like a time.
            // Otherwise "1" would match every line logged in the 11th hour or minute,
            // which drowns the result the user was actually after.
            bool timestampMatch = query.IndexOf(':') >= 0
                                  && Contains(entry.TimestampUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture), query);

            return Contains(entry.Message, query)
                   || Contains(entry.Detail, query)
                   || Contains(entry.Tag, query)
                   || Contains(entry.Level.ToString(), query)
                   || Contains(entry.Category.ToString(), query)
                   || timestampMatch;
        }

        private static bool Contains(string? haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && haystack!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void RegisterBuiltIns()
        {
            RegisterCommand(new ConsoleCommand("help", "help [command]", "list commands, or explain one", Help));
            RegisterCommand(new ConsoleCommand("clear", "clear", "empty the console buffer", _ =>
            {
                Clear();
                return "Console cleared.";
            }));

            RegisterCommand(new ConsoleCommand("filter", "filter <all|info|debug|warning|error|critical|network|server|client|mod|security>", "show only matching lines", context =>
            {
                if (context.Arguments.Count == 0)
                {
                    return $"Filter is {Filter}.";
                }

                if (!Enum.TryParse(context.Argument(0), true, out ConsoleFilter filter))
                {
                    return $"Unknown filter '{context.Argument(0)}'.";
                }

                Filter = filter;
                ScrollToBottom();
                return $"Filter set to {filter}.";
            }));

            RegisterCommand(new ConsoleCommand("search", "search [text]", "filter lines by text; no argument clears it", context =>
            {
                SearchQuery = context.RawArguments;
                ScrollToBottom();
                return SearchQuery.Length == 0
                    ? "Search cleared."
                    : $"Searching for '{SearchQuery}' — {FilteredEntries().Count} match(es).";
            }));

            RegisterCommand(new ConsoleCommand("copy", "copy [message|stack|full] [errorId]", "copy the last problem, or one by id", context =>
            {
                LogEntry? entry = context.Arguments.Count > 1 && long.TryParse(context.Argument(1), out long id)
                    ? FindById(id)
                    : LastProblem();

                if (entry == null)
                {
                    return "Nothing to copy.";
                }

                CopyMode mode = context.Argument(0).ToLowerInvariant() switch
                {
                    "stack" => CopyMode.StackTrace,
                    "full" => CopyMode.FullError,
                    _ => CopyMode.Message,
                };

                string copied = Copy(mode, entry);
                return $"Copied {copied.Length} character(s) to the clipboard.";
            }));
        }

        private string Help(ConsoleCommandContext context)
        {
            if (context.Arguments.Count > 0 && _commands.TryGetValue(context.Argument(0), out ConsoleCommand? command))
            {
                return $"{command.Usage}\n  {command.Description}";
            }

            var names = new List<string>(_commands.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            var builder = new StringBuilder();
            builder.AppendLine("Commands:");
            foreach (string name in names)
            {
                ConsoleCommand entry = _commands[name];
                if (entry.DeveloperOnly && !DeveloperMode)
                {
                    continue;
                }

                builder.AppendLine($"  {entry.Usage,-46} {entry.Description}");
            }

            return builder.ToString().TrimEnd();
        }
    }
}
