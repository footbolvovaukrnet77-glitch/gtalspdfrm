using System;
using System.Collections.Generic;
using System.Drawing;
using Gtamp.Client.Shv.Interop;
using GTA.Native;

namespace Gtamp.Client.Shv.Ui
{
    /// <summary>
    /// Draws text and rectangles through ScriptHookV's natives directly, rather than
    /// through ScriptHookVDotNet's <c>TextElement</c> and <c>ContainerElement</c>, and
    /// without handing ScriptHookVDotNet a single managed string.
    /// <para>
    /// <b>Why this exists.</b> SHVDN's managed drawing classes reach into
    /// <c>SHVDN.NativeMemory</c>, which finds its addresses by scanning the game for byte
    /// patterns when it is first touched. On a GTA V build that release does not know the
    /// scan finds nothing, the type initialiser throws, and the script is aborted. From
    /// the player's side: "the console does not open".
    /// </para>
    /// <para>
    /// <b>Why the first attempt at this was not enough.</b> Moving to <c>Function.Call</c>
    /// removed the managed UI classes but not the last thread back into
    /// <c>NativeMemory</c>: a <c>string</c> argument is converted by
    /// <c>InputArgument</c>'s implicit operator, which pins it through
    /// <c>SHVDN.ScriptDomain.PinString</c> — and that calls
    /// <c>NativeMemory.StringToCoTaskMemUTF8</c>. Every text native takes at least one
    /// string, so the notification thrown from the constructor took the whole script with
    /// it before the first frame. The strings are therefore pinned here, with
    /// <see cref="Marshal"/>, and passed as pointers.
    /// </para>
    /// <para>
    /// <b>Nothing here throws.</b> This is the surface the client uses to report that it
    /// is broken; a failure to draw that message must not become a second, louder
    /// failure. After the first refusal it stops trying, so a broken installation gets
    /// one logged line rather than one per frame.
    /// </para>
    /// </summary>
    public static class NativeDraw
    {
        /// <summary>Reference resolution the caller's pixel coordinates are expressed in.</summary>
        private const float DesignWidth = 1280f;
        private const float DesignHeight = 720f;

        /// <summary>
        /// Set once drawing has failed, so the failure is reported once instead of sixty
        /// times a second. <see cref="LastError"/> keeps what it was: the host logs it if
        /// it still has a logger, and it is the difference between "nothing is drawn" and
        /// "nothing is drawn, and here is why".
        /// </summary>
        public static bool Disabled { get; private set; }

        public static Exception? LastError { get; private set; }

        /// <summary>
        /// A filled rectangle. <c>DRAW_RECT</c> takes the <i>centre</i> and the size in
        /// screen fractions, where the caller thinks in top-left pixels — converting in
        /// one place is what keeps the callers readable.
        /// </summary>
        public static void Rect(float x, float y, float width, float height, Color color)
        {
            if (Disabled)
            {
                return;
            }

            try
            {
                Function.Call(
                    Hash.DRAW_RECT,
                    (x + (width / 2f)) / DesignWidth,
                    (y + (height / 2f)) / DesignHeight,
                    width / DesignWidth,
                    height / DesignHeight,
                    color.R, color.G, color.B, color.A);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }

        /// <summary>
        /// One line of left-aligned text at a top-left pixel position.
        /// </summary>
        /// <param name="font">GTA V font id; 4 is ChaletComprimeCologne, the console face.</param>
        public static void Text(string text, float x, float y, float scale, Color color, int font = 4)
        {
            if (Disabled || string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                Function.Call(Hash.SET_TEXT_FONT, font);
                Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
                Function.Call(Hash.SET_TEXT_COLOUR, color.R, color.G, color.B, color.A);
                Function.Call(Hash.SET_TEXT_CENTRE, false);
                Function.Call(Hash.SET_TEXT_DROP_SHADOW);

                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, NativeString.Arg("STRING"));
                AddComponents(text);
                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x / DesignWidth, y / DesignHeight);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
            finally
            {
                NativeString.Release();
            }
        }

        /// <summary>
        /// A corner notification. Used by the messages that have to arrive when the client
        /// is broken — "GTAMP failed to start", and the reply to a console key that cannot
        /// open anything — so it must not depend on the layer that breaks.
        /// </summary>
        public static void Notify(string text)
        {
            if (Disabled || string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                Function.Call(Hash.BEGIN_TEXT_COMMAND_THEFEED_POST, NativeString.Arg("STRING"));
                AddComponents(text);
                Function.Call(Hash.END_TEXT_COMMAND_THEFEED_POST_TICKER, false, false);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
            finally
            {
                NativeString.Release();
            }
        }

        /// <summary>A subtitle at the bottom of the screen, for the given milliseconds.</summary>
        public static void Subtitle(string text, int durationMilliseconds)
        {
            if (Disabled || string.IsNullOrEmpty(text))
            {
                return;
            }

            try
            {
                Function.Call(Hash.BEGIN_TEXT_COMMAND_PRINT, NativeString.Arg("STRING"));
                AddComponents(text);
                Function.Call(Hash.END_TEXT_COMMAND_PRINT, durationMilliseconds, true);
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
            finally
            {
                NativeString.Release();
            }
        }

        /// <summary>
        /// The text, as the components the game accepts. Split on bytes rather than
        /// characters because that is what the game counts — see
        /// <see cref="Gtamp.Client.Ui.TextChunker.SplitUtf8"/>.
        /// </summary>
        private static void AddComponents(string text)
        {
            foreach (InputArgument component in NativeString.Components(text))
            {
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, component);
            }
        }

        private static void Fail(Exception exception)
        {
            Disabled = true;
            LastError = exception;
        }
    }
}
