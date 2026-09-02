using System.Drawing;
using GTA.Native;

namespace Gtamp.Client.Shv.Ui
{
    /// <summary>
    /// Draws text and rectangles through ScriptHookV's natives directly, rather than
    /// through ScriptHookVDotNet's <c>TextElement</c> and <c>ContainerElement</c>.
    /// <para>
    /// <b>Why this exists.</b> The first real session died here. SHVDN's managed drawing
    /// classes reach into <c>SHVDN.NativeMemory</c>, which finds its addresses by
    /// scanning the game for byte patterns when it is first touched. On a GTA V build
    /// that release does not know, the scan finds nothing, the type initialiser throws a
    /// <c>NullReferenceException</c>, and ScriptHookVDotNet aborts the script. Nothing is
    /// drawn and nothing is recoverable — from the player's side, "the console does not
    /// open".
    /// </para>
    /// <para>
    /// <c>Function.Call</c> goes through ScriptHookV's own native invoker and touches
    /// none of that. ScriptHookV tracks game versions closely and had already accepted
    /// the build SHVDN choked on, so the console survives a version gap that the managed
    /// layer does not.
    /// </para>
    /// <para>
    /// This matters more than the average robustness tweak because the console <i>is</i>
    /// the diagnostic surface: when it cannot draw, nothing else about a session can be
    /// found out. It is the one part of the client that must work on the widest possible
    /// range of installations.
    /// </para>
    /// </summary>
    public static class NativeDraw
    {
        /// <summary>Reference resolution the caller's pixel coordinates are expressed in.</summary>
        private const float DesignWidth = 1280f;
        private const float DesignHeight = 720f;

        /// <summary>
        /// A filled rectangle. <c>DRAW_RECT</c> takes the <i>centre</i> and the size in
        /// screen fractions, where the caller thinks in top-left pixels — converting in
        /// one place is what keeps the callers readable.
        /// </summary>
        public static void Rect(float x, float y, float width, float height, Color color)
        {
            Function.Call(
                Hash.DRAW_RECT,
                (x + (width / 2f)) / DesignWidth,
                (y + (height / 2f)) / DesignHeight,
                width / DesignWidth,
                height / DesignHeight,
                color.R, color.G, color.B, color.A);
        }

        /// <summary>
        /// One line of left-aligned text at a top-left pixel position.
        /// </summary>
        /// <param name="font">GTA V font id; 4 is ChaletComprimeCologne, the console face.</param>
        public static void Text(string text, float x, float y, float scale, Color color, int font = 4)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Function.Call(Hash.SET_TEXT_FONT, font);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, color.R, color.G, color.B, color.A);
            Function.Call(Hash.SET_TEXT_CENTRE, false);
            Function.Call(Hash.SET_TEXT_DROP_SHADOW);

            // The game's text commands take the string as a component of a formatted
            // entry rather than as an argument, and cap each component at 99 bytes —
            // a console line is easily longer, so it goes in as several components.
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            foreach (string chunk in Chunk(text, 99))
            {
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, chunk);
            }

            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x / DesignWidth, y / DesignHeight);
        }

        /// <summary>
        /// A corner notification. Used by the two messages that have to arrive when the
        /// client is broken — "GTAMP failed to start" and the reply to a console key that
        /// cannot open anything — so it must not depend on the layer that breaks.
        /// </summary>
        public static void Notify(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, "STRING");
            foreach (string chunk in Chunk(text, 99))
            {
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, chunk);
            }

            Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, false);
        }

        /// <summary>A subtitle at the bottom of the screen, for the given milliseconds.</summary>
        public static void Subtitle(string text, int durationMilliseconds)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Function.Call(Hash.BEGIN_TEXT_COMMAND_PRINT, "STRING");
            foreach (string chunk in Chunk(text, 99))
            {
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, chunk);
            }

            Function.Call(Hash.END_TEXT_COMMAND_PRINT, durationMilliseconds, true);
        }

        /// <summary>
        /// Splits a string into pieces the text component accepts. Public for the test in
        /// <c>Gtamp.Tests</c>… which cannot reach this assembly, so the same rule is
        /// tested through <see cref="Gtamp.Client.Ui.TextChunker"/> and this calls it.
        /// </summary>
        private static System.Collections.Generic.IEnumerable<string> Chunk(string text, int size) =>
            Gtamp.Client.Ui.TextChunker.Split(text, size);
    }
}
