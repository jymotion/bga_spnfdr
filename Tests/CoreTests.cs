using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BgaSpnfdr.Tests
{
    public class ScoreTrackerTests
    {
        private static readonly Dictionary<long, string> Route = new Dictionary<long, string>
        {
            [273850] = "note A",
            [468025] = "note B",
        };

        private static string Frame(ScoreTracker t, string read) => t.HandleRead(read, Route);

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
        [InlineData("47987#")]  // not just digits
        public void RejectsImplausibleReads(string read)
        {
            Assert.Null(new ScoreTracker().ValidCandidate(read));
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
        public void LowerReadWithAFaintDigitInFrontIsDropped()
        {
            // "39800" off 239800, whose "2" matched just under the threshold
            var t = ConfirmedAt("239775");
            for (int i = 0; i < 20; i++)
                Assert.Null(t.HandleRead("39800", Route, 0.55f));
            Assert.Equal("read 39800 rejected (end of a longer score)", t.Status);
            // nothing in front of it: a real drop (new run), the usual 5 reads
            for (int i = 0; i < 5; i++)
                t.HandleRead("0", Route, 0.2f);
            Assert.Equal("score 0", t.Status);
            // going up is never held back by it
            t.HandleRead("25", Route, 0.55f);
            t.HandleRead("25", Route, 0.55f);
            Assert.Equal("score 25", t.Status);
        }

        [Fact]
        public void HugeJumpNeedsFiveReads()
        {
            // junk-prefix read: an extra digit in front of 273850's tail
            var t = ConfirmedAt("273850");
            Frame(t, "773550");
            Frame(t, "773550");
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

        [Fact]
        public void ReadingEveryFrameNeedsThreeAndEightReads()
        {
            var t = new ScoreTracker();
            t.ReadEveryFrame(true);
            Frame(t, "273850");
            Frame(t, "273850");
            Assert.Equal("reading 273850 (2/3)", t.Status);
            Assert.Equal("note A", Frame(t, "273850"));
            Frame(t, "25");
            Assert.Equal("reading 25 (1/8)", t.Status);

            t.ReadEveryFrame(false); // back to 10 a second
            Frame(t, "273875");
            Assert.Equal("reading 273875 (1/2)", t.Status);
        }
    }

    public class ScoreBandTests
    {
        /// <summary>The Windows app copies only ColumnsNeeded x RowsNeeded
        /// back from the graphics card: Fill must give the same band when
        /// every other pixel is junk.</summary>
        [Fact]
        public void FillReadsOnlyTheColumnsAndRowsNeeded()
        {
            var random = new System.Random(1);
            for (int i = 0; i < 2000; i++)
            {
                int fw = random.Next(100, 2600), fh = random.Next(100, 1500);
                int cw = random.Next(20, fw + 1), ch = random.Next(20, fh + 1);
                var crop = i % 5 == 0 // some crops run off the frame
                    ? new System.Drawing.Rectangle(random.Next(0, fw), random.Next(0, fh), cw, ch)
                    : new System.Drawing.Rectangle(random.Next(0, fw - cw + 1), random.Next(0, fh - ch + 1), cw, ch);
                ScoreBand.RowsNeeded(crop, fh, out int first, out int last);
                ScoreBand.ColumnsNeeded(crop, fw, out int left, out int right);
                if (last < first || right < left)
                    continue;
                int stride = fw * 4;
                var frame = new byte[stride * (last - first + 1)];
                var strip = new byte[frame.Length];
                random.NextBytes(frame);
                random.NextBytes(strip); // junk outside the columns
                for (int y = 0; y <= last - first; y++)
                    System.Array.Copy(frame, y * stride + left * 4, strip, y * stride + left * 4, (right - left + 1) * 4);
                var expected = new byte[ScoreBand.Width * ScoreBand.Rows];
                var actual = new byte[expected.Length];
                ScoreBand.Fill(frame, stride, first, last, fw, crop, false, expected);
                ScoreBand.Fill(strip, stride, first, last, fw, crop, false, actual);
                Assert.Equal(expected, actual);
            }
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
        public void KeepsBlankLinesQuotesAndCommas()
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path,
                    "100,\"line 1\r\n\r\nline 3\"\r\n" +
                    "200,\"he said \"\"hi\"\", then left\"\n" +
                    "300,go left, then right\n" +
                    "400,it's 5\" wide\n" +
                    "500,  padded  ,,\n" +
                    "600,\" kept \",\"more\ncolumns\"\n" +
                    "\n" +
                    "no comma here\n" +
                    "700,last row has no line end");
                var table = RouteCsv.Load(path);
                Assert.Equal(7, table.Count);
                Assert.Equal("line 1\n\nline 3", table[100]);
                Assert.Equal("he said \"hi\", then left", table[200]);
                Assert.Equal("go left, then right", table[300]);
                Assert.Equal("it's 5\" wide", table[400]);
                Assert.Equal("padded", table[500]);
                Assert.Equal(" kept ", table[600]);
                Assert.Equal("last row has no line end", table[700]);
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
