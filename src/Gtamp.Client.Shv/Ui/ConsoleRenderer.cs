using System;
using System.Collections.Generic;
using System.Drawing;
using Gtamp.Client.Ui;

namespace Gtamp.Client.Shv.Ui
{
    /// <summary>
    /// Draws <see cref="DeveloperConsole"/> on screen. It holds no state of its own:
    /// every frame it reads the console model and renders it, so the console's
    /// behaviour stays testable outside the game.
    /// <para>
    /// Everything goes through <see cref="NativeDraw"/> rather than ScriptHookVDotNet's
    /// <c>TextElement</c>, because the managed classes reach into memory SHVDN locates
    /// by pattern scan and that scan fails on a game build the release does not know —
    /// which aborted this script on its first run in a real game. The console is the
    /// diagnostic surface; when it cannot draw, nothing else can be found out, so it
    /// depends on ScriptHookV alone.
    /// </para>
    /// </summary>
    public sealed class ConsoleRenderer
    {
        private const float Margin = 12f;
        private const float LineHeight = 22f;
        private const float TextScale = 0.34f;

        /// <summary>GTA V font ids, which the natives take as numbers.</summary>
        private const int FontChaletLondon = 0;

        private const int FontChaletComprimeCologne = 4;

        private readonly DeveloperConsole _console;

        public ConsoleRenderer(DeveloperConsole console)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
        }

        /// <summary>Header shown above the log, refreshed by the host each frame.</summary>
        public string StatusLine { get; set; } = string.Empty;

        public void Draw()
        {
            if (!_console.IsOpen)
            {
                return;
            }

            float width = 1280f;
            float height = (LineHeight * (_console.VisibleLineCount + 3)) + (Margin * 2);

            NativeDraw.Rect(0f, 0f, width, height, Color.FromArgb(215, 8, 10, 14));
            NativeDraw.Rect(0f, 0f, width, LineHeight + 6f, Color.FromArgb(230, 20, 26, 36));

            NativeDraw.Text(
                "MULTIPLAYER DEVELOPER CONSOLE   " + StatusLine,
                Margin,
                4f,
                TextScale,
                Color.FromArgb(255, 235, 235, 235),
                FontChaletComprimeCologne);

            float y = LineHeight + 12f;
            List<ConsoleLine> lines = _console.VisibleLines();
            foreach (ConsoleLine line in lines)
            {
                (byte r, byte g, byte b, byte a) = ConsolePalette.Rgba(line.Role);

                if (line.Role == ConsoleColorRole.Critical)
                {
                    // Critical must be impossible to miss, so it gets a filled row
                    // rather than just a colour (master prompt section 38).
                    NativeDraw.Rect(0f, y - 2f, width, LineHeight, Color.FromArgb(90, 120, 0, 0));
                }

                NativeDraw.Text(line.Text, Margin, y, TextScale, Color.FromArgb(a, r, g, b), FontChaletLondon);

                y += LineHeight;
            }

            string filter = _console.Filter == ConsoleFilter.All ? string.Empty : $"[filter:{_console.Filter}] ";
            string search = string.IsNullOrEmpty(_console.SearchQuery) ? string.Empty : $"[search:{_console.SearchQuery}] ";

            NativeDraw.Text(
                filter + search + "> " + _console.InputLine + "_",
                Margin,
                y + 4f,
                TextScale,
                Color.FromArgb(255, 255, 255, 255),
                FontChaletLondon);
        }
    }
}
