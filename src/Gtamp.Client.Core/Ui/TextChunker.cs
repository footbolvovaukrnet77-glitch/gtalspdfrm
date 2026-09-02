using System.Collections.Generic;

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
    }
}
