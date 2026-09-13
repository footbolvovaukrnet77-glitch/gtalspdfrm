namespace Gtamp.Client.Ui
{
    /// <summary>
    /// Turns a Windows virtual key code into the character it types.
    /// <para>
    /// This lived in the ScriptHookVDotNet host, taking a <c>KeyEventArgs</c>, which
    /// made it untestable: the host layer needs a running game and a Windows Forms
    /// assembly, so nothing in the suite could reach it. That layer produced three
    /// defects in the first real session — the game directory, the mod scan, and the
    /// crash log path — and the lesson each of them taught is the same: what is a
    /// decision belongs where it can be checked, and only the engine call belongs in
    /// the host.
    /// </para>
    /// <para>
    /// A wrong entry here is not subtle in effect and is very easy to miss in review:
    /// it means typing <c>connect</c> into the console produces something else, and the
    /// player cannot tell a mistyped command from a broken keyboard map.
    /// </para>
    /// </summary>
    public static class ConsoleKeyMap
    {
        // Windows virtual key codes. Named rather than numeric so the table below reads
        // as the keyboard it describes; the host passes (int)Keys.X, which is the same
        // number.
        private const int A = 65;
        private const int Z = 90;
        private const int D0 = 48;
        private const int D9 = 57;
        private const int NumPad0 = 96;
        private const int NumPad9 = 105;
        private const int Space = 32;
        private const int Multiply = 106;
        private const int Add = 107;
        private const int Subtract = 109;
        private const int Decimal = 110;
        private const int Divide = 111;
        private const int Oem1 = 186;        // ; :
        private const int Oemplus = 187;     // = +
        private const int Oemcomma = 188;    // , <
        private const int OemMinus = 189;    // - _
        private const int OemPeriod = 190;   // . >
        private const int OemQuestion = 191; // / ?
        private const int Oem5 = 220;        // \ |
        private const int Oem7 = 222;        // ' "

        /// <summary>
        /// The character <paramref name="keyCode"/> types, or <c>'\0'</c> for a key that
        /// types nothing. <c>'\0'</c> is the caller's signal to ignore the key, not a
        /// character to append.
        /// </summary>
        public static char Translate(int keyCode, bool shift)
        {
            if (keyCode >= A && keyCode <= Z)
            {
                char letter = (char)('a' + (keyCode - A));
                return shift ? char.ToUpperInvariant(letter) : letter;
            }

            if (keyCode >= D0 && keyCode <= D9)
            {
                if (!shift)
                {
                    return (char)('0' + (keyCode - D0));
                }

                // The shifted row, in keyboard order rather than code order, because
                // that is how it is checked against a real keyboard.
                return keyCode switch
                {
                    D0 => ')',
                    D0 + 1 => '!',
                    D0 + 2 => '@',
                    D0 + 3 => '#',
                    D0 + 4 => '$',
                    D0 + 5 => '%',
                    D0 + 6 => '^',
                    D0 + 7 => '&',
                    D0 + 8 => '*',
                    D0 + 9 => '(',
                    _ => '\0',
                };
            }

            // The numeric keypad is unshifted by definition: Shift turns it into the
            // arrow keys, and the host never sees a shifted NumPad digit.
            if (keyCode >= NumPad0 && keyCode <= NumPad9)
            {
                return (char)('0' + (keyCode - NumPad0));
            }

            return keyCode switch
            {
                Space => ' ',
                OemPeriod or Decimal => '.',
                Oemcomma => ',',
                OemMinus or Subtract => shift ? '_' : '-',
                Oemplus or Add => shift ? '+' : '=',
                OemQuestion or Divide => shift ? '?' : '/',
                Multiply => '*',
                Oem1 => shift ? ':' : ';',
                Oem7 => shift ? '"' : '\'',
                Oem5 => shift ? '|' : '\\',
                _ => '\0',
            };
        }
    }
}
