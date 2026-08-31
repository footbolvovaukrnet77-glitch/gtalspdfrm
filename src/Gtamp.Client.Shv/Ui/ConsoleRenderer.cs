using System;
using System.Collections.Generic;
using System.Drawing;
using Gtamp.Client.Ui;
using GTA.UI;
using GtaFont = GTA.UI.Font;

namespace Gtamp.Client.Shv.Ui
{
    /// <summary>
    /// Draws <see cref="DeveloperConsole"/> on screen. It holds no state of its own:
    /// every frame it reads the console model and renders it, so the console's
    /// behaviour stays testable outside the game.
    /// </summary>
    public sealed class ConsoleRenderer
    {
        private const float Margin = 12f;
        private const float LineHeight = 22f;
        private const float TextScale = 0.34f;

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

            new ContainerElement(
                new PointF(0f, 0f),
                new SizeF(width, height),
                Color.FromArgb(215, 8, 10, 14)).ScaledDraw();

            new ContainerElement(
                new PointF(0f, 0f),
                new SizeF(width, LineHeight + 6f),
                Color.FromArgb(230, 20, 26, 36)).ScaledDraw();

            new TextElement(
                "MULTIPLAYER DEVELOPER CONSOLE   " + StatusLine,
                new PointF(Margin, 4f),
                TextScale,
                Color.FromArgb(255, 235, 235, 235),
                GtaFont.ChaletComprimeCologne).ScaledDraw();

            float y = LineHeight + 12f;
            List<ConsoleLine> lines = _console.VisibleLines();
            foreach (ConsoleLine line in lines)
            {
                (byte r, byte g, byte b, byte a) = ConsolePalette.Rgba(line.Role);

                if (line.Role == ConsoleColorRole.Critical)
                {
                    // Critical must be impossible to miss, so it gets a filled row
                    // rather than just a colour (master prompt section 38).
                    new ContainerElement(
                        new PointF(0f, y - 2f),
                        new SizeF(width, LineHeight),
                        Color.FromArgb(90, 120, 0, 0)).ScaledDraw();
                }

                new TextElement(
                    line.Text,
                    new PointF(Margin, y),
                    TextScale,
                    Color.FromArgb(a, r, g, b),
                    GtaFont.ChaletLondon).ScaledDraw();

                y += LineHeight;
            }

            string filter = _console.Filter == ConsoleFilter.All ? string.Empty : $"[filter:{_console.Filter}] ";
            string search = string.IsNullOrEmpty(_console.SearchQuery) ? string.Empty : $"[search:{_console.SearchQuery}] ";

            new TextElement(
                filter + search + "> " + _console.InputLine + "_",
                new PointF(Margin, y + 4f),
                TextScale,
                Color.FromArgb(255, 255, 255, 255),
                GtaFont.ChaletLondon).ScaledDraw();
        }
    }
}
