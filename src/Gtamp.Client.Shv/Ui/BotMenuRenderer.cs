using System;
using System.Collections.Generic;
using System.Drawing;
using Gtamp.Client.Ui;

namespace Gtamp.Client.Shv.Ui
{
    /// <summary>
    /// Draws <see cref="BotMenu"/>. Like <see cref="ConsoleRenderer"/> it holds no
    /// state and goes through <see cref="NativeDraw"/> only, so it keeps working on a
    /// game build the script host's pattern scan does not know.
    /// </summary>
    public sealed class BotMenuRenderer
    {
        private const float Left = 40f;
        private const float Top = 120f;
        private const float Width = 520f;
        private const float RowHeight = 26f;
        private const float TextScale = 0.34f;
        private const int FontChaletLondon = 0;
        private const int FontChaletComprimeCologne = 4;

        private readonly BotMenu _menu;

        public BotMenuRenderer(BotMenu menu)
        {
            _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        }

        public void Draw()
        {
            if (!_menu.IsOpen)
            {
                return;
            }

            IReadOnlyList<BotMenuRow> rows = _menu.Rows;
            IReadOnlyList<string> lines = _menu.RecentLines;

            float bodyHeight = RowHeight * rows.Count;
            float logHeight = lines.Count == 0 ? 0f : RowHeight * (lines.Count + 1);
            float statusHeight = _menu.Status.Length == 0 ? 0f : RowHeight;
            float height = RowHeight + bodyHeight + statusHeight + logHeight + 16f;

            NativeDraw.Rect(Left, Top, Width, height, Color.FromArgb(220, 8, 10, 14));
            NativeDraw.Rect(Left, Top, Width, RowHeight, Color.FromArgb(235, 20, 26, 36));

            NativeDraw.Text(
                $"БОТЫ   запущено: {_menu.RunningCount}   ←→ менять, Enter — выполнить, F7 — закрыть",
                Left + 10f,
                Top + 4f,
                TextScale,
                Color.FromArgb(255, 235, 235, 235),
                FontChaletComprimeCologne);

            float y = Top + RowHeight + 4f;
            for (int i = 0; i < rows.Count; i++)
            {
                bool selected = i == _menu.Selected;
                if (selected)
                {
                    NativeDraw.Rect(Left, y - 2f, Width, RowHeight, Color.FromArgb(120, 40, 90, 150));
                }

                Color colour = selected
                    ? Color.FromArgb(255, 255, 255, 255)
                    : Color.FromArgb(255, 190, 195, 200);

                NativeDraw.Text(rows[i].Label, Left + 14f, y, TextScale, colour, FontChaletLondon);

                string value = rows[i].Value() ?? string.Empty;
                if (value.Length > 0)
                {
                    string shown = rows[i].IsAction ? value : "< " + value + " >";
                    NativeDraw.Text(shown, Left + 260f, y, TextScale, colour, FontChaletLondon);
                }

                y += RowHeight;
            }

            if (_menu.Status.Length > 0)
            {
                NativeDraw.Text(
                    _menu.Status, Left + 14f, y + 2f, TextScale, Color.FromArgb(255, 240, 200, 120), FontChaletLondon);
                y += RowHeight;
            }

            if (lines.Count == 0)
            {
                return;
            }

            NativeDraw.Text(
                "— вывод ботов —", Left + 14f, y + 2f, TextScale, Color.FromArgb(255, 140, 145, 150), FontChaletLondon);
            y += RowHeight;

            foreach (string line in lines)
            {
                // A verdict line is what the player is actually looking for, so it is
                // picked out rather than left in the same grey as the progress chatter.
                Color colour = line.IndexOf("FAIL", StringComparison.Ordinal) >= 0
                    ? Color.FromArgb(255, 235, 110, 110)
                    : line.IndexOf(" ok ", StringComparison.Ordinal) >= 0
                        ? Color.FromArgb(255, 140, 220, 140)
                        : Color.FromArgb(255, 175, 180, 185);

                NativeDraw.Text(Trim(line), Left + 14f, y, TextScale, colour, FontChaletLondon);
                y += RowHeight;
            }
        }

        private static string Trim(string line) => line.Length <= 78 ? line : line.Substring(0, 78) + "…";
    }
}
