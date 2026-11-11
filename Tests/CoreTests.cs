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

    /// <summary>Core/GameFinder and Core/ScoreFinder on drawn frames: a
    /// "game picture" of noise (moving or still) with a score drawn from
    /// the reader's own digit shapes, on black or on a backdrop. The real
    /// footage is checked with Tools/ReaderBench (layouts).</summary>
    public class GameFinderTests
    {
        private const int W = 640, H = 480;
        private static readonly System.Drawing.Rectangle Picture = new System.Drawing.Rectangle(14, 2, 617, 474);

        /// <summary>A frame (B,G,R,x): backdrop all over, the picture's top
        /// part noise that changes with seed, the rest of it dim and still,
        /// and the score in its place (reference geometry, 1:1).</summary>
        private static byte[] Frame(int seed, byte backdrop, string score, System.Drawing.Rectangle picture,
            int width = W, int height = H, System.Drawing.Rectangle other = default)
        {
            var px = new byte[width * height * 4];
            var moving = new System.Random(seed);
            var still = new System.Random(99);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    byte v = backdrop;
                    if (picture.Contains(x, y))
                    {
                        byte a = (byte)moving.Next(256), b = (byte)(110 + still.Next(100));
                        v = y < picture.Y + picture.Height * 6 / 10 ? a : b;
                    }
                    else if (other.Contains(x, y))
                        v = (byte)moving.Next(256);
                    px[(y * width + x) * 4] = px[(y * width + x) * 4 + 1] = px[(y * width + x) * 4 + 2] = v;
                }
            // the score, right where the reference picture has it
            double at = 60;
            for (int i = 0; i < score.Length; i++)
            {
                for (int r = 0; r < DigitReader.BandHeight; r++)
                    for (int c = 0; c < DigitReader.GlyphWidth; c++)
                    {
                        int x = picture.X + (int)System.Math.Round(at) + c, y = picture.Y + DigitReader.BandTop + r;
                        byte v = DigitReader.Glyph(score[i] - '0', r, c);
                        px[(y * width + x) * 4] = px[(y * width + x) * 4 + 1] = px[(y * width + x) * 4 + 2] = v;
                    }
                if (i + 1 < score.Length)
                    at += ScoreFinder.Advance(score[i], score[i + 1]);
            }
            return px;
        }

        private static void AssertNear(System.Drawing.Rectangle expected, System.Drawing.Rectangle actual, int within)
        {
            Assert.True(System.Math.Abs(expected.Left - actual.Left) <= within
                && System.Math.Abs(expected.Top - actual.Top) <= within
                && System.Math.Abs(expected.Right - actual.Right) <= within
                && System.Math.Abs(expected.Bottom - actual.Bottom) <= within,
                $"expected {expected} within {within} px, got {actual}");
        }

        private static GameFinder Run(GameFinder finder, byte backdrop, string score, System.Drawing.Rectangle picture,
            int frames, int width = W, int height = H, System.Drawing.Rectangle other = default)
        {
            var area = new System.Drawing.Rectangle(0, 0, width, height);
            for (int i = 0; i < frames; i++)
                if (finder.Feed(Frame(i, backdrop, score, picture, width, height, other), width, height, width * 4, false, area)
                    && !finder.Refining)
                    break;
            return finder;
        }

        [Fact]
        public void DetectFindsThePictureOnBlackThoughPartOfItIsStill()
        {
            // no score: as on the game's menu
            GameFinder f = Run(GameFinder.Detect(8), 0, "", Picture, 12);
            Assert.False(f.Verified);
            AssertNear(Picture, f.Result, 2);
        }

        [Fact]
        public void DetectFindsAStretchedPictureAmongOtherThingsThatMove()
        {
            // 16:9 on a lit backdrop, with a smaller moving box beside it
            var wide = new System.Drawing.Rectangle(20, 30, 560, 315);
            var webcam = new System.Drawing.Rectangle(600, 30, 180, 130);
            GameFinder f = Run(GameFinder.Detect(8), 60, "", wide, 12, 800, 450, webcam);
            AssertNear(wide, f.Result, 3);
        }

        [Fact]
        public void DetectRecognisesTheMenuThoughSomethingElseMoves()
        {
            // the menu, still, stretched to 16:9 on a dim backdrop, beside a
            // box of moving noise: nothing about the game moves
            var menu = new System.Drawing.Rectangle(250, 100, 480, 270);
            var webcam = new System.Drawing.Rectangle(20, 40, 200, 150);
            const int width = 800, height = 450;
            var area = new System.Drawing.Rectangle(0, 0, width, height);
            GameFinder f = GameFinder.Detect(40);
            int frames = 0;
            for (bool done = false; !done && frames < 20; frames++)
            {
                byte[] px = Frame(frames, 30, "", System.Drawing.Rectangle.Empty, width, height, webcam);
                for (int y = menu.Top; y < menu.Bottom; y++)
                    for (int x = menu.Left; x < menu.Right; x++)
                    {
                        byte v = MenuFinder.Value((x - menu.X) * 48 / menu.Width, (y - menu.Y) * 36 / menu.Height);
                        px[(y * width + x) * 4] = px[(y * width + x) * 4 + 1] = px[(y * width + x) * 4 + 2] = v;
                    }
                done = f.Feed(px, width, height, width * 4, false, area);
            }
            Assert.True(f.Recognized);
            Assert.InRange(frames, 1, 3); // at once, not after watching for 4 s
            AssertNear(menu, f.Result, 4);
        }

        [Fact]
        public void CheckFitsASloppyCropToTheScore()
        {
            var sloppy = new System.Drawing.Rectangle(Picture.X + 20, Picture.Y - 25, Picture.Width - 30, Picture.Height + 40);
            GameFinder f = Run(GameFinder.Check(sloppy), 0, "239800", Picture, 200);
            Assert.True(f.Verified);
            AssertNear(Picture, f.Result, 10); // 2% of its height: reading takes 4%
        }

        [Fact]
        public void CheckFindsTheScoreAnywhereWhenTheCropIsPlainWrong()
        {
            var wrong = new System.Drawing.Rectangle(300, 100, 200, 150);
            GameFinder f = Run(GameFinder.Check(wrong), 0, "239800", Picture, 1500);
            Assert.True(f.Verified);
            AssertNear(Picture, f.Result, 10);
        }

        [Fact]
        public void CheckFindsNothingWithoutAScore()
        {
            GameFinder f = Run(GameFinder.Check(Picture), 0, "", Picture, 60);
            Assert.False(f.Verified);
        }

        [Fact]
        public void RecenterMovesACropWhoseEdgeCutsIntoTheScore()
        {
            var crop = new System.Drawing.Rectangle(100, 0, 617, 474);
            // first digit found 4 px into the margin: 20 px left of the crop
            Assert.Equal(new System.Drawing.Rectangle(72, 0, 617, 474), ScoreBand.Recenter(crop, ScoreBand.LeftMargin - 20));
            Assert.Equal(crop, ScoreBand.Recenter(crop, ScoreBand.LeftMargin + 3));
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
