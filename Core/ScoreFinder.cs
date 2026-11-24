using System;
using System.Collections.Generic;
using System.Drawing;

namespace BgaSpnfdr
{
    /// <summary>
    /// Finds the score in a whole frame with the game's own digit shapes, and
    /// works the game picture out from it. A crop only has to say roughly
    /// where to look: where the score really is, and how big its digits are
    /// across and down, fixes the picture's top, height and width far more
    /// exactly than a hand-drawn box or a region that happened to move.
    /// It also tells the game from everything else in a frame (a webcam, an
    /// animation, a timer): only the game has a BattleTanx score in it.
    ///
    /// How it works:
    ///  - A probe cuts a strip out of the frame at one guess of the scale
    ///    (frame pixels per reference pixel, across and down separately, so
    ///    a stretched picture is just another guess) and lets DigitReader
    ///    look for digits at every position in it.
    ///  - To spot digits, a guess can be 10% off in height and 10% narrower
    ///    to 16% wider (measured on the reference run: 125 of 131 scores or
    ///    more still found), so guesses are spaced about 20% apart. Reading
    ///    them right takes 4%, which the tuning below is for.
    ///  - Around a find, the height scale is tuned to where the digits match
    ///    best, and the width scale is measured from how far apart the
    ///    digits are: the font's spacing is known to a fraction of a pixel.
    /// Core/GameFinder decides which probes to run and when.
    /// </summary>
    internal sealed class ScoreFinder
    {
        /// <summary>One strip to look in: frame position of its top left
        /// corner, frame pixels per reference pixel, and its size in
        /// reference pixels.</summary>
        public struct Probe
        {
            public double X0, Y0, Sx, Sy;
            public int Cols, Rows;
            public int MinDigits;
            public Rectangle Hint; // the box this probe is for (Empty: none, the score is looked for anywhere)

            /// <summary>Places the reader tries: what a probe costs.</summary>
            public int Positions => Math.Max(0, Cols - DigitReader.GlyphWidth + 1) * Math.Max(0, Rows - DigitReader.BandHeight + 1);
        }

        /// <summary>A score found: scales, the frame row its digits start
        /// on, and the frame columns they span.</summary>
        public struct Hit
        {
            public double Sx, Sy, Top, Left, Right;
            public float Gain;
            public string Text;
            public Rectangle Hint;
        }

        // Digits must match this well on average: to be worth tuning, and to
        // be believed once tuned. Real digits match 0.8-0.98 at the right
        // scale.
        private const float RoughMatch = 0.72f, TunedMatch = 0.82f;

        // A window this flat can't hold a digit (white glyph, dark shadow):
        // a standard deviation of 12 brightness levels over its pixels
        private const double MinSpread = 12 * 12 * DigitReader.BandHeight * DigitReader.GlyphWidth;

        private const double W = DigitReader.ReferenceCropWidth, H = DigitReader.ReferenceCropHeight;

        // The font's spacing in reference pixels: from one digit's left edge
        // (as DigitReader places it) to the next's is After[first] +
        // Before[next]. Fitted to 2,039 reads of the reference run, where it
        // predicts all 72 digit pairs that occur to within 0.15 px.
        private static readonly double[] After = { 11.92, 9.80, 12.55, 11.00, 11.85, 11.94, 13.70, 12.17, 13.26, 13.28 };
        private static readonly double[] Before = { 0, 0.14, 1.27, 0.76, 1.94, -0.10, 0.04, -0.33, 0.55, 0.54 };

        /// <summary>Reference pixels from one digit's left edge to the next's.</summary>
        internal static double Advance(char digit, char next) => After[digit - '0'] + Before[next - '0'];

        private readonly DigitReader _reader = new DigitReader();
        private byte[] _strip = new byte[0];
        private byte[] _px;
        private int _stride, _width, _height;
        private bool _rgbOrder;

        /// <summary>The frame the next Run/Tune/Crop calls look at: 4 bytes
        /// per pixel, all rows.</summary>
        public void SetFrame(byte[] px, int stride, int width, int height, bool rgbOrder)
        {
            _px = px; _stride = stride; _width = width; _height = height; _rgbOrder = rgbOrder;
        }

        // ---------- planning ----------

        /// <summary>Probes for a box that is roughly the game picture: off by
        /// up to tolerance (a fraction of its size) in place and in size.
        /// Each probe is at most chunk positions.</summary>
        public static void PlanNear(Rectangle box, double tolerance, int minDigits, int chunk, List<Probe> plan)
        {
            if (box.Width < 40 || box.Height < 40)
                return;
            double sx0 = box.Width / W, sy0 = box.Height / H;
            // one guess covers a box within 8% of the picture's size; two each
                    // way, one within about 20% (widths lean wide, which
                    // digits take better than narrow)
            double[] heights = tolerance <= 0.08 ? new[] { 1.0 } : new[] { 0.9, 1.1 };
            double[] widths = tolerance <= 0.08 ? new[] { 1.03 } : new[] { 0.9, 1.06 };
            double slackY = (tolerance + 0.02) * box.Height, slackX = (tolerance + 0.03) * box.Width;
            foreach (double fy in heights)
            {
                double sy = sy0 * fy;
                foreach (double fx in widths)
                {
                    double sx = sx0 * fx;
                    AddStrip(plan, box.X - slackX, box.Y + DigitReader.BandTop * sy0 - slackY, sx, sy,
                        (int)Math.Ceiling((BattleTanx.ScoreFraction.Width * box.Width + 2 * slackX) / sx),
                        (int)Math.Ceiling(2 * slackY / sy) + 1, minDigits, box, chunk);
                }
            }
        }

        /// <summary>Probes for a game picture anywhere inside area, between
        /// minHeight and maxHeight frame pixels tall, at its natural shape or
        /// stretched up to 16:9. Biggest first: the game is usually the main
        /// thing in a frame, and big pictures are quick to look through.
        /// Each probe is at most chunk positions.</summary>
        public static void PlanAnywhere(Rectangle area, double minHeight, double maxHeight, int chunk, List<Probe> plan)
        {
            // as captured (4:3) and stretched to 16:9; each, tried 3% wide,
            // covers 11% narrower to 16% wider than itself, which leaves
            // only a sliver in between
            double[] stretches = { 1.0, 4.0 / 3 };
            for (double h = maxHeight / 1.08; h >= minHeight / 1.1; h /= 1.2)
            {
                double sy = h / H;
                foreach (double stretch in stretches)
                {
                    double sx = sy * stretch * 1.03, w = W * sy * stretch;
                    if (w > area.Width * 1.03)
                        continue;
                    // the digits' first row lies 89% down the picture, and the
                    // picture lies inside the area
                    double top = area.Y + DigitReader.BandTop * sy - 0.03 * h;
                    double bottom = area.Bottom - (H - DigitReader.BandTop) * sy + 0.03 * h;
                    double left = area.X - 0.03 * w;
                    double right = area.Right - (1 - BattleTanx.ScoreFraction.Width) * w + 0.03 * w;
                    AddStrip(plan, left, top, sx, sy, (int)Math.Ceiling((right - left) / sx),
                        (int)Math.Ceiling((bottom - top) / sy) + 1, 2, Rectangle.Empty, chunk);
                }
            }
        }

        /// <summary>Adds probes that between them try the given number of
        /// vertical offsets from (x0, y0) down.</summary>
        private static void AddStrip(List<Probe> plan, double x0, double y0, double sx, double sy,
            int cols, int offsets, int minDigits, Rectangle hint, int chunk)
        {
            if (cols < DigitReader.GlyphWidth || offsets < 1)
                return;
            int perProbe = Math.Max(1, chunk / (cols - DigitReader.GlyphWidth + 1));
            for (int o = 0; o < offsets; o += perProbe)
                plan.Add(new Probe
                {
                    Sx = sx, Sy = sy,
                    X0 = x0, Y0 = y0 + o * sy,
                    Cols = cols,
                    Rows = Math.Min(perProbe, offsets - o) + DigitReader.BandHeight - 1,
                    MinDigits = minDigits,
                    Hint = hint,
                });
        }

        // ---------- looking ----------

        /// <summary>Looks through one probe's strip. True if it holds what
        /// may be a score (to be confirmed by Tune).</summary>
        public bool Run(Probe p, out Hit hit)
        {
            string text = Look(p.X0, p.Y0, p.Sx, p.Sy, p.Cols, p.Rows);
            hit = new Hit
            {
                Sx = p.Sx, Sy = p.Sy,
                Top = p.Y0 + _reader.FoundOffset * p.Sy,
                Left = p.X0 + _reader.FoundFirst * p.Sx,
                Right = p.X0 + (_reader.FoundLast + DigitReader.GlyphWidth) * p.Sx,
                Gain = _reader.FoundGain,
                Text = text,
                Hint = p.Hint,
            };
            return Plausible(text, _reader.FoundGain, p.MinDigits, RoughMatch);
        }

        private string Look(double x0, double y0, double sx, double sy, int cols, int rows)
        {
            if (cols < DigitReader.GlyphWidth || rows < DigitReader.BandHeight)
            {
                _reader.Scan(_strip, 0, 0, 0); // clears what it found
                return "";
            }
            if (_strip.Length < cols * rows)
                _strip = new byte[cols * rows];
            ScoreBand.Sample(_px, _stride, 0, _height - 1, _width, x0, y0, sx, sy, cols, rows, _rgbOrder, _strip);
            return _reader.Scan(_strip, cols, rows, MinSpread);
        }

        private static bool Plausible(string text, float gain, int minDigits, float minMatch)
        {
            if (text.Length < minDigits || text.Length > BattleTanx.MaxScoreDigits)
                return false;
            if (text.Length > 1 && text[0] == '0')
                return false;
            if (long.Parse(text) % BattleTanx.ScoreMultiple != 0)
                return false;
            return DigitReader.MatchThreshold + gain / text.Length >= minMatch;
        }

        /// <summary>Tunes a find's two scales (same frame). False if it then
        /// doesn't hold up as a score.</summary>
        public bool Tune(ref Hit hit, int minDigits)
        {
            // height by where the digits match best, width by their
            // spacing; then each again, now that the other is close
            TuneHeight(ref hit, 0.14, 0.02);
            TuneWidth(ref hit);
            TuneHeight(ref hit, 0.04, 0.005);
            TuneWidth(ref hit);
            return Plausible(hit.Text, hit.Gain, minDigits, TunedMatch);
        }

        /// <summary>Looks again just around a find, at the scales given.
        /// False (hit unchanged) if nothing is there at those.</summary>
        private bool LookAround(ref Hit hit, double sx, double sy)
        {
            // the digits' middle stays where it was found while the scale changes
            const int around = 3, beside = 20;
            double x0 = hit.Left - beside * sx;
            double y0 = hit.Top + DigitReader.BandHeight / 2.0 * hit.Sy - (DigitReader.BandHeight / 2.0 + around) * sy;
            int cols = (int)Math.Ceiling((hit.Right - hit.Left) / sx) + 2 * beside;
            string text = Look(x0, y0, sx, sy, cols, DigitReader.BandHeight + 2 * around);
            if (_reader.FoundGain <= 0)
                return false;
            hit.Sx = sx; hit.Sy = sy;
            hit.Top = y0 + _reader.FoundOffset * sy;
            hit.Left = x0 + _reader.FoundFirst * sx;
            hit.Right = x0 + (_reader.FoundLast + DigitReader.GlyphWidth) * sx;
            hit.Gain = _reader.FoundGain;
            hit.Text = text;
            return true;
        }

        private void TuneHeight(ref Hit hit, double range, double step)
        {
            Hit start = hit, best = hit;
            best.Gain = 0;
            int count = (int)Math.Round(2 * range / step) + 1, bestAt = -1;
            var gains = new double[count];
            for (int i = 0; i < count; i++)
            {
                Hit trial = start;
                if (!LookAround(ref trial, start.Sx, start.Sy * (1 - range + i * step)))
                    continue;
                gains[i] = trial.Gain;
                if (trial.Gain > best.Gain)
                {
                    best = trial;
                    bestAt = i;
                }
            }
            if (bestAt < 0)
                return;
            hit = best;

            // The match changes little over a few percent of height, so the
            // best single try is partly luck. The top of a parabola fitted to
            // the tries around it is steadier.
            int from = Math.Max(0, bestAt - 5), to = Math.Min(count - 1, bestAt + 5), n = to - from + 1;
            if (n < 5)
                return;
            double sx = 0, sx2 = 0, sx3 = 0, sx4 = 0, sg = 0, sxg = 0, sx2g = 0;
            for (int i = from; i <= to; i++)
            {
                double x = i - bestAt, g = gains[i];
                sx += x; sx2 += x * x; sx3 += x * x * x; sx4 += x * x * x * x;
                sg += g; sxg += x * g; sx2g += x * x * g;
            }
            // least squares for g = a x^2 + b x + c
            double d = sx4 * (sx2 * n - sx * sx) - sx3 * (sx3 * n - sx * sx2) + sx2 * (sx3 * sx - sx2 * sx2);
            if (Math.Abs(d) < 1e-9)
                return;
            double a = (sx2g * (sx2 * n - sx * sx) - sx3 * (sxg * n - sx * sg) + sx2 * (sxg * sx - sx2 * sg)) / d;
            double b = (sx4 * (sxg * n - sx * sg) - sx2g * (sx3 * n - sx * sx2) + sx2 * (sx3 * sg - sx2 * sxg)) / d;
            if (a >= 0)
                return;
            double top = -b / (2 * a);
            if (Math.Abs(top) > 5)
                return;
            Hit fitted = start;
            if (LookAround(ref fitted, start.Sx, start.Sy * (1 - range + (bestAt + top) * step))
                && fitted.Text == best.Text)
                hit = fitted;
        }

        private void TuneWidth(ref Hit hit)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                Hit trial = hit;
                if (!LookAround(ref trial, hit.Sx, hit.Sy) || trial.Text.Length < 2)
                    return;
                double expected = 0;
                for (int i = 0; i + 1 < trial.Text.Length; i++)
                    expected += Advance(trial.Text[i], trial.Text[i + 1]);
                double ratio = (_reader.FoundX[trial.Text.Length - 1] - _reader.FoundX[0]) / expected;
                if (ratio < 0.7 || ratio > 1.4 || !LookAround(ref trial, hit.Sx * ratio, hit.Sy))
                    return;
                hit = trial;
            }
        }

        // ---------- the score, or another number? ----------

        // The game shows a second number in the score's font on the score's
        // row: the ammunition left, near the picture's right edge. It can be
        // a multiple of 25 as well, and is 100 at the start of a run, while
        // the score is a lone 0 that can't be searched for. The score is
        // always the one further left: it ends in the picture's left third,
        // and the ammunition starts 52 to 84% of the picture's width right
        // of where the score starts or ends (over the levels of the
        // reference run).
        private const double OtherNearest = 0.45, OtherFarthest = 0.90;

        /// <summary>What a (tuned) find has further left on its row, where
        /// the score would be if the find were the ammunition: 0 nothing,
        /// 1 a lone 0 (the score at the start of a run: too short to go
        /// by), 2 a number that may be the score (other, still to be
        /// tuned). Anything but 0 means the find isn't the score. If the
        /// find is the score, the place looked at is at least 15% of the
        /// picture's width outside the picture.</summary>
        public int LeftOf(Hit hit, int areaLeft, out Hit other)
        {
            other = hit;
            double w = W * hit.Sx;
            double x0 = Math.Max(areaLeft, hit.Left - OtherFarthest * w), y0 = hit.Top - hit.Sy;
            string text = Look(x0, y0, hit.Sx, hit.Sy, (int)((hit.Left - OtherNearest * w - x0) / hit.Sx), DigitReader.BandHeight + 2);
            if (!Plausible(text, _reader.FoundGain, 1, RoughMatch))
                return 0;
            other = new Hit
            {
                Sx = hit.Sx, Sy = hit.Sy,
                Top = y0 + _reader.FoundOffset * hit.Sy,
                Left = x0 + _reader.FoundFirst * hit.Sx,
                Right = x0 + (_reader.FoundLast + DigitReader.GlyphWidth) * hit.Sx,
                Gain = _reader.FoundGain,
                Text = text,
            };
            return text.Length >= 2 ? 2 : 1;
        }

        // ---------- from the score to the picture ----------

        /// <summary>The frame columns the picture's left edge can be at,
        /// going by a find: the score starts at least 2 px into the picture
        /// and ends within its left third.</summary>
        public static void LeftRange(Hit hit, out double min, out double max)
        {
            max = hit.Left - 2 * hit.Sx;
            min = hit.Right - 0.34 * W * hit.Sx;
        }

        /// <summary>The game picture a tuned find belongs to. Its top,
        /// height and width follow from the digits; its left edge is given
        /// (NaN: where the score sits in most levels), kept to where the
        /// score can be inside the picture.</summary>
        public static Rectangle Crop(Hit hit, double left)
        {
            double w = W * hit.Sx, h = H * hit.Sy;
            LeftRange(hit, out double min, out double max);
            double x = double.IsNaN(left) ? hit.Right - 0.23 * w : left;
            x = Math.Max(min, Math.Min(max, x));
            return new Rectangle((int)Math.Round(x), (int)Math.Round(hit.Top - DigitReader.BandTop * hit.Sy),
                (int)Math.Round(w), (int)Math.Round(h));
        }

        /// <summary>The mean match of a find's digits (0.6 to 1).</summary>
        public static double Match(Hit hit) => DigitReader.MatchThreshold + hit.Gain / Math.Max(1, hit.Text.Length);
    }
}
