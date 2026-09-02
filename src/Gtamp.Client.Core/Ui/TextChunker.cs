using System.Collections.Generic;
using System.Text;

namespace Gtamp.Client.Ui
{
    /// <summary>
    /// Splits a string into the pieces GTA V's text components accept.
    /// <para>
    /// The game takes a string to draw as components of a formatted entry, and each
    /// component is capped — a console line is easily longer than the cap, and text past
    /// it is silently dropped rather than refused. Silently dropped is the failure mode
    /// worth guarding: a truncated log line reads as a shorter log line, not as a bug.
    /// </para>
    /// <para>
    /// It lives in the core rather than beside the drawing code because it is a decision
    /// about strings and nothing else, and the drawing code is in the layer no test can
    /// reach.
    /// </para>
    /// </summary>
    public static class TextChunker
    {
        /// <summary>
        /// The string in pieces of at most <paramref name="size"/> characters, in order.
        /// An empty string yields nothing; a string shorter than the cap yields itself.
        /// </summary>
        public static IEnumerable<string> Split(string? text, int size)
        {
            if (string.IsNullOrEmpty(text) || size <= 0)
            {
                yield break;
            }

            for (int start = 0; start < text!.Length; start += size)
            {
                int length = text.Length - start;
                yield return text.Substring(start, length < size ? length : size);
            }
        }

        /// <summary>
        /// The same, but measured in UTF-8 <b>bytes</b>, which is what the game actually
        /// counts, and never splitting a character across two pieces.
        /// <para>
        /// <see cref="Split"/> counts characters, which is the same thing only while the
        /// text is ASCII. A Cyrillic player name is two bytes per character, so a name
        /// that measured 99 characters arrived as 198 bytes and lost half of itself on
        /// the way in — silently, because the game truncates rather than refuses.
        /// </para>
        /// <para>
        /// Surrogate pairs are kept together as well: half of an emoji is not a
        /// character, and encoding it alone produces the replacement glyph.
        /// </para>
        /// </summary>
        public static IEnumerable<string> SplitUtf8(string? text, int maxBytes)
        {
            if (string.IsNullOrEmpty(text) || maxBytes <= 0)
            {
                yield break;
            }

            var encoding = new UTF8Encoding(false);
            int start = 0;
            while (start < text!.Length)
            {
                int taken = 0;
                int bytes = 0;
                while (start + taken < text.Length)
                {
                    // A surrogate pair is one character in two chars: measured and taken
                    // together or not at all.
                    int step = char.IsHighSurrogate(text[start + taken]) && start + taken + 1 < text.Length
                        ? 2
                        : 1;
                    int next = encoding.GetByteCount(text.Substring(start + taken, step));
                    if (taken > 0 && bytes + next > maxBytes)
                    {
                        break;
                    }

                    bytes += next;
                    taken += step;
                }

                yield return text.Substring(start, taken);
                start += taken;
            }
        }
    }
}
