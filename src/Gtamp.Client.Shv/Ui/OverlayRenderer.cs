using System;
using System.Collections.Generic;
using System.Drawing;
using Gtamp.Client.Diagnostics;

namespace Gtamp.Client.Shv.Ui
{
    /// <summary>
    /// Draws the network readout in the corner of the screen. Like
    /// <see cref="ConsoleRenderer"/> it holds no state: <see cref="NetworkOverlay"/>
    /// decides what to say and how alarming it is, and this only puts it on screen.
    /// </summary>
    public sealed class OverlayRenderer
    {
        private const float LineHeight = 18f;
        private const float TextScale = 0.30f;
        private const float Padding = 8f;
        private const float Width = 320f;

        /// <summary>Top right, clear of the minimap and the weapon wheel.</summary>
        private const float Left = 1280f - Width - 16f;
        private const float Top = 16f;

        public void Draw(IReadOnlyList<OverlayLine> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return;
            }

            // Through the natives rather than SHVDN's managed elements, for the reason
            // written up in NativeDraw: the managed path dies on a game build SHVDN
            // does not know, and took the whole script with it on the first real run.
            NativeDraw.Rect(
                Left, Top, Width, (LineHeight * lines.Count) + (Padding * 2),
                Color.FromArgb(170, 8, 10, 14));

            float y = Top + Padding;
            foreach (OverlayLine line in lines)
            {
                NativeDraw.Text(line.Text, Left + Padding, y, TextScale, ColourFor(line.Severity), font: 0);

                y += LineHeight;
            }
        }

        /// <summary>
        /// The same colour roles the console uses, so a player who has learned what
        /// red means in one place has learned it in both.
        /// </summary>
        private static Color ColourFor(OverlaySeverity severity) => severity switch
        {
            OverlaySeverity.Bad => Color.FromArgb(255, 235, 80, 70),
            OverlaySeverity.Warning => Color.FromArgb(255, 235, 200, 70),
            _ => Color.FromArgb(230, 220, 225, 235),
        };
    }
}
