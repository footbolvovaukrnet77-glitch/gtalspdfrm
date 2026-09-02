using System.Collections.Generic;
using System.Linq;
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
    }
}
