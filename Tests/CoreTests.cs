using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BgaSpnfdr.Tests
{
    public class ScoreParserTests
    {
        [Theory]
        [InlineData("273850", "273850")]
        [InlineData("47987S", "479875")]          // 5 read as S
        [InlineData("Z7385O", "273850")]          // 2 as Z, 0 as O
        [InlineData("4g8U2s", "498025")]          // 9 as g, 0 as U, 5 as s
        [InlineData("l0rB", "1078")]              // 1 as l, 7 as r, 8 as B
        [InlineData(" '27 3850.", "273850")]      // stray punctuation/spaces
        [InlineData("", "")]
        public void MapsLookalikesBackToDigits(string text, string expected)
        {
            Assert.Equal(expected, ScoreParser.Normalize(text));
        }

        [Theory]
        [InlineData("47987#")]
        [InlineData("k273850")]
        public void RejectsReadsWithUnmappableCharacters(string text)
        {
            // dropping the bad character would silently give a wrong number
            Assert.Null(ScoreParser.Normalize(text));
        }
    }

    public class ScoreTrackerTests
    {
        private static readonly Dictionary<long, string> Route = new Dictionary<long, string>
        {
            [273850] = "note A",
            [468025] = "note B",
        };

        /// <summary>One frame where both OCR variants read the same text.</summary>
        private static string Frame(ScoreTracker t, string read) => t.HandleReads(read, read, Route);

        private static ScoreTracker ConfirmedAt(string score)
        {
            var t = new ScoreTracker();
            Frame(t, score);
            Frame(t, score);
            return t;
        }

        [Fact]
        public void NewScoreNeedsTwoMatchingReads()
        {
            var t = new ScoreTracker();
            Assert.Null(Frame(t, "273850"));
            Assert.Equal("reading 273850 (1/2)", t.Status);
            Assert.Equal("note A", Frame(t, "273850"));
            Assert.Equal("score 273850 (note shown)", t.Status);
        }

        [Theory]
        [InlineData("03850")]   // leading zero = fragment of a longer number
        [InlineData("1000000")] // 7 digits
        [InlineData("273851")]  // not a multiple of 25
        public void RejectsImplausibleReads(string read)
        {
            Assert.Null(new ScoreTracker().ValidCandidate(read));
        }

        [Fact]
        public void SkipsFrameWhenVariantsDisagree()
        {
            var t = new ScoreTracker();
            Assert.Null(t.HandleReads("273850", "273875", Route));
            Assert.Equal("ambiguous read (273850 vs 273875) — skipped", t.Status);
        }

        [Fact]
        public void OneReadableVariantIsEnough()
        {
            var t = new ScoreTracker();
            t.HandleReads("273850", "", Route);
            Assert.Equal("note A", t.HandleReads("", "273850", Route));
        }

        [Fact]
        public void FragmentBelowCurrentScoreNeedsFiveReads()
        {
            // "25" read off the end of 468025
            var t = ConfirmedAt("468025");
            for (int i = 1; i <= 4; i++)
            {
                Frame(t, "25");
                Assert.Equal($"reading 25 ({i}/5)", t.Status);
            }
            Frame(t, "25");
            Assert.Equal("score 25", t.Status);
        }

        [Fact]
        public void HugeJumpNeedsFiveReads()
        {
            // junk-prefix read: "77 3sso" for 273850
            var t = ConfirmedAt("273850");
            Frame(t, "77 3sso");
            Frame(t, "77 3sso");
            Assert.Equal("reading 773550 (2/5)", t.Status);
        }

        [Fact]
        public void HoldsScoreWhileDigitsAreHidden()
        {
            var t = ConfirmedAt("273850");
            Frame(t, "");
            Assert.Equal("no digits visible — holding 273850", t.Status);
            // same score coming back doesn't re-send the note
            Frame(t, "273850");
            Assert.Null(Frame(t, "273850"));
        }

        [Fact]
        public void NoteStaysUpWhenNewScoreHasNone()
        {
            var t = ConfirmedAt("273850");
            Frame(t, "273875");
            Assert.Null(Frame(t, "273875")); // null = leave current note up
            Assert.Equal("score 273875", t.Status);
        }
    }

    public class RouteCsvTests
    {
        [Fact]
        public void LoadsMultilineNotesAndSkipsNonScoreRows()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path,
                    "score,note\n" +
                    "273850,\"Go left\nthen grab the nuke\"\n" +
                    " 468025 ,Boss\n");
                var table = RouteCsv.Load(path);
                Assert.Equal(2, table.Count);
                Assert.Equal("Go left\nthen grab the nuke", table[273850]);
                Assert.Equal("Boss", table[468025]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ThrowsWhenNoScoreRows()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "score,note\n");
                Assert.Throws<InvalidDataException>(() => RouteCsv.Load(path));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
