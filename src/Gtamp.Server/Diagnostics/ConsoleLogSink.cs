using System;
using Gtamp.Shared.Diagnostics;

namespace Gtamp.Server.Diagnostics
{
    /// <summary>
    /// Colour scheme from master prompt section 38, shared with the in-game console
    /// so a line looks the same in both places:
    /// errors red, critical bright red on a red field, warnings yellow, success green.
    /// </summary>
    public sealed class ConsoleLogSink : ILogSink
    {
        private readonly object _gate = new object();

        public ConsoleLogSink(bool useColor = true)
        {
            UseColor = useColor && !Console.IsOutputRedirected;
        }

        public bool UseColor { get; }

        public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

        public void Write(in LogEntry entry)
        {
            if (entry.Level < MinimumLevel)
            {
                return;
            }

            string line = entry.FormatLine();
            string? detail = entry.Detail;

            lock (_gate)
            {
                if (!UseColor)
                {
                    Console.WriteLine(line);
                    if (!string.IsNullOrEmpty(detail))
                    {
                        Console.WriteLine(detail);
                    }

                    return;
                }

                ConsoleColor previousForeground = Console.ForegroundColor;
                ConsoleColor previousBackground = Console.BackgroundColor;

                switch (entry.Level)
                {
                    case LogLevel.Debug:
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        break;
                    case LogLevel.Info:
                        Console.ForegroundColor = ConsoleColor.Gray;
                        break;
                    case LogLevel.Success:
                        Console.ForegroundColor = ConsoleColor.Green;
                        break;
                    case LogLevel.Warning:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        break;
                    case LogLevel.Error:
                        Console.ForegroundColor = ConsoleColor.Red;
                        break;
                    case LogLevel.Critical:
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.BackgroundColor = ConsoleColor.DarkRed;
                        break;
                }

                Console.WriteLine(line);
                if (!string.IsNullOrEmpty(detail))
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(detail);
                }

                Console.ForegroundColor = previousForeground;
                Console.BackgroundColor = previousBackground;
            }
        }
    }
}
