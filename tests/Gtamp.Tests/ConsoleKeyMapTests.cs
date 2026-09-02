using Gtamp.Client.Ui;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// The keyboard map the console types with.
    /// <para>
    /// It lived in the ScriptHookVDotNet host, taking a Windows Forms
    /// <c>KeyEventArgs</c>, and so could not be reached by any test — the same
    /// untestable layer that produced the first real session's three defects. A wrong
    /// entry here means typing <c>connect</c> produces something else, and a player
    /// cannot tell a mistyped command from a broken keyboard map.
    /// </para>
    /// </summary>
    public class ConsoleKeyMapTests
    {
        private const int A = 65;
        private const int D0 = 48;
        private const int NumPad0 = 96;

        [Fact]
        public void LettersAreLowerUntilShifted()
        {
            Assert.Equal('a', ConsoleKeyMap.Translate(A, shift: false));
            Assert.Equal('A', ConsoleKeyMap.Translate(A, shift: true));
            Assert.Equal('z', ConsoleKeyMap.Translate(A + 25, shift: false));
            Assert.Equal('Z', ConsoleKeyMap.Translate(A + 25, shift: true));
        }

        /// <summary>
        /// The command a player types first, character by character. If this fails,
        /// nothing else about the console matters.
        /// </summary>
        [Fact]
        public void ConnectCanBeTyped()
        {
            int[] keys = { A + 2, A + 14, A + 13, A + 13, A + 4, A + 2, A + 19 };
            var typed = new System.Text.StringBuilder();
            foreach (int key in keys)
            {
                typed.Append(ConsoleKeyMap.Translate(key, shift: false));
            }

            Assert.Equal("connect", typed.ToString());
        }

        [Theory]
        [InlineData(0, ')')]
        [InlineData(1, '!')]
        [InlineData(2, '@')]
        [InlineData(3, '#')]
        [InlineData(4, '$')]
        [InlineData(5, '%')]
        [InlineData(6, '^')]
        [InlineData(7, '&')]
        [InlineData(8, '*')]
        [InlineData(9, '(')]
        public void TheShiftedNumberRowMatchesTheKeyboard(int digit, char expected)
        {
            Assert.Equal((char)('0' + digit), ConsoleKeyMap.Translate(D0 + digit, shift: false));
            Assert.Equal(expected, ConsoleKeyMap.Translate(D0 + digit, shift: true));
        }

        /// <summary>
        /// A server address is digits and dots, and both routes to a dot have to work:
        /// the main keyboard's and the numeric keypad's.
        /// </summary>
        [Fact]
        public void AnAddressCanBeTypedFromEitherKeypad()
        {
            Assert.Equal('1', ConsoleKeyMap.Translate(D0 + 1, shift: false));
            Assert.Equal('1', ConsoleKeyMap.Translate(NumPad0 + 1, shift: false));
            Assert.Equal('.', ConsoleKeyMap.Translate(190, shift: false));  // OemPeriod
            Assert.Equal('.', ConsoleKeyMap.Translate(110, shift: false));  // Decimal
        }

        [Theory]
        [InlineData(32, false, ' ')]
        [InlineData(188, false, ',')]
        [InlineData(189, false, '-')]
        [InlineData(189, true, '_')]
        [InlineData(187, false, '=')]
        [InlineData(187, true, '+')]
        [InlineData(191, false, '/')]
        [InlineData(191, true, '?')]
        [InlineData(186, false, ';')]
        [InlineData(186, true, ':')]
        [InlineData(222, false, '\'')]
        [InlineData(222, true, '"')]
        [InlineData(220, false, '\\')]
        [InlineData(220, true, '|')]
        public void ThePunctuationKeysTypeWhatIsPrintedOnThem(int keyCode, bool shift, char expected)
        {
            Assert.Equal(expected, ConsoleKeyMap.Translate(keyCode, shift));
        }

        /// <summary>
        /// A key that types nothing must say so rather than returning a character. The
        /// caller appends whatever comes back, so a stray value here puts junk in the
        /// command line.
        /// </summary>
        [Theory]
        [InlineData(112)]  // F1
        [InlineData(119)]  // F8, the console key itself
        [InlineData(16)]   // Shift
        [InlineData(17)]   // Control
        [InlineData(192)]  // the tilde key, the documented alternative console key
        public void AKeyThatTypesNothingReturnsNothing(int keyCode)
        {
            Assert.Equal('\0', ConsoleKeyMap.Translate(keyCode, shift: false));
            Assert.Equal('\0', ConsoleKeyMap.Translate(keyCode, shift: true));
        }
    }
}
