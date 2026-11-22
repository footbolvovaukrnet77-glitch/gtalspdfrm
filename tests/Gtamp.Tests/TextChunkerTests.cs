using System.Collections.Generic;
using System.Linq;
using System.Text;
using Gtamp.Client.Ui;
using Xunit;

namespace Gtamp.Tests
{
    /// <summary>
    /// Splitting a line into the pieces GTA V's text components accept.
    /// <para>
    /// The game takes a string to draw as components of a formatted entry, each capped
    /// at 99 bytes, and text past the cap is <b>silently dropped</b>. That is the failure
    /// worth guarding: a truncated log line reads as a shorter log line, not as a bug, so
    /// nobody would ever report it.
    /// </para>
    /// </summary>
    public class TextChunkerTests
    {
        [Fact]
        public void AShortLineIsOnePiece()
        {
            List<string> pieces = TextChunker.Split("connect 127.0.0.1 27015", 99).ToList();

            Assert.Single(pieces);
            Assert.Equal("connect 127.0.0.1 27015", pieces[0]);
        }

        /// <summary>
        /// Nothing is lost and nothing is reordered — the pieces put back together are
        /// the line that went in. This is the whole contract.
        /// </summary>
        [Fact]
        public void ALongLineSurvivesBeingSplitAndRejoined()
        {
            string line = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"word{i}"));
            Assert.True(line.Length > 99, "the fixture has to be longer than the cap to test anything");

            List<string> pieces = TextChunker.Split(line, 99).ToList();

            Assert.True(pieces.Count > 1);
            Assert.All(pieces, piece => Assert.True(piece.Length <= 99));
            Assert.Equal(line, string.Concat(pieces));
        }

        [Fact]
        public void ALineExactlyOnTheCapIsNotSplit()
        {
            string line = new string('x', 99);

            Assert.Single(TextChunker.Split(line, 99));
        }

        [Fact]
        public void OneCharacterPastTheCapBecomesTwoPieces()
        {
            List<string> pieces = TextChunker.Split(new string('x', 100), 99).ToList();

            Assert.Equal(2, pieces.Count);
            Assert.Equal(99, pieces[0].Length);
            Assert.Equal(1, pieces[1].Length);
        }

        /// <summary>
        /// Nothing to draw yields nothing to draw, rather than one empty component: the
        /// caller opens a text command per piece, and an empty one is a wasted native
        /// call in a per-frame loop.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void NothingYieldsNothing(string? text)
        {
            Assert.Empty(TextChunker.Split(text, 99));
        }

        [Fact]
        public void ANonsenseSizeYieldsNothingRatherThanLoopingForever()
        {
            Assert.Empty(TextChunker.Split("text", 0));
            Assert.Empty(TextChunker.Split("text", -1));
        }

        [Fact]
        public void SplittingOnBytesYieldsTheWholeStringWhenItFits()
        {
            Assert.Equal(new[] { "hello" }, TextChunker.SplitUtf8("hello", 99));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void SplittingOnBytesYieldsNothingForNothing(string? text)
        {
            Assert.Empty(TextChunker.SplitUtf8(text, 99));
        }

        [Fact]
        public void SplittingOnBytesRefusesANonPositiveLimit()
        {
            Assert.Empty(TextChunker.SplitUtf8("hello", 0));
        }

        /// <summary>
        /// The defect this exists for. Cyrillic is two bytes per character, so ten
        /// characters is twenty bytes: counting characters would have called this one
        /// piece and let the game silently drop the second half.
        /// </summary>
        [Fact]
        public void CyrillicIsMeasuredInBytesNotCharacters()
        {
            string[] pieces = TextChunker.SplitUtf8("привет", 4).ToArray();

            Assert.Equal(new[] { "пр", "ив", "ет" }, pieces);
            Assert.All(pieces, piece => Assert.True(Encoding.UTF8.GetByteCount(piece) <= 4));
        }

        [Fact]
        public void NoPieceEverExceedsTheByteLimit()
        {
            const string mixed = "GTAMP — сессия: 12 игроков, пинг 34 мс, потери 0.2%";

            foreach (string piece in TextChunker.SplitUtf8(mixed, 7))
            {
                Assert.True(Encoding.UTF8.GetByteCount(piece) <= 7, piece);
            }
        }

        [Fact]
        public void SplittingOnBytesLosesNothing()
        {
            const string mixed = "GTAMP — сессия: 12 игроков";

            Assert.Equal(mixed, string.Concat(TextChunker.SplitUtf8(mixed, 5)));
        }

        /// <summary>
        /// Half a surrogate pair is not a character. Split between the two chars and each
        /// half encodes to the replacement glyph, so the name on screen would be two boxes
        /// instead of one emoji.
        /// </summary>
        [Fact]
        public void ASurrogatePairIsNeverSplit()
        {
            string[] pieces = TextChunker.SplitUtf8("ab\U0001F600cd", 3).ToArray();

            Assert.Equal(new[] { "ab", "\U0001F600", "cd" }, pieces);
        }

        /// <summary>
        /// A single character larger than the limit still has to come out, or the loop
        /// would never advance. It is emitted whole and over the limit rather than
        /// dropped: the game truncates one component, where an infinite loop hangs
        /// the game.
        /// </summary>
        [Fact]
        public void ACharacterLargerThanTheLimitIsStillEmitted()
        {
            Assert.Equal(new[] { "\U0001F600" }, TextChunker.SplitUtf8("\U0001F600", 1).ToArray());
        }
    }
}
