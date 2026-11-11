using System;
using System.Drawing;

namespace BgaSpnfdr
{
    /// <summary>
    /// Recognises the game's GAME SETUP menu in a frame, and with it exactly
    /// where the game picture is. The program is usually set up on that
    /// menu, where nothing in the game moves and there is no score: what
    /// moves is a webcam beside it, or the capturing program's own meters.
    /// The menu's backdrop (the girder with the title, the rubble) is always
    /// the same picture, so a small copy of it is looked for instead, the
    /// way DigitReader looks for digits.
    ///
    /// How it works:
    ///  - The copy is 48 x 36 brightness values of the menu as the reference
    ///    run shows it (864 bytes). The part where the menu's text goes is
    ///    left out of the comparison: it changes from page to page.
    ///  - A candidate rectangle of the frame is averaged down to the same
    ///    grid and compared by normalized correlation, which ignores overall
    ///    brightness and contrast.
    ///  - Candidates of every size (down to a quarter of the frame's
    ///    height), shape (narrower than 4:3 to wider than 16:9) and place
    ///    are first compared 16 x 12; the best few are then moved and
    ///    resized in ever smaller steps to where the full copy matches best.
    /// On the reference run and a tester's capture the menu matches 0.92 to
    /// 0.99 and is placed within a pixel or two; nothing else (gameplay,
    /// other screens, a stream layout, the capturing program's own window)
    /// matches better than 0.6.
    /// </summary>
    internal sealed class MenuFinder
    {
        private const int Across = 48, Down = 36, Block = 3;      // the copy, and the coarse grid's cell
        private const int CoarseAcross = Across / Block, CoarseDown = Down / Block;
        private const float Match = 0.8f;                          // this good or it isn't the menu

        // brightness / 8, two values per byte (high nibble first), row by row
        private static readonly byte[] Data =
        {
            16, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 35, 49, 0, 1, 16, 0, 0, 0, 0, 0, 1,
            17, 0, 0, 1, 32, 0, 0, 0, 0, 0, 0, 0, 0, 0, 35, 33, 0, 1, 16, 20, 52, 32, 0, 17,
            17, 33, 17, 17, 18, 34, 33, 17, 17, 17, 17, 17, 17, 17, 17, 35, 50, 17, 34, 35, 52, 0, 0, 0,
            34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 34, 35, 50, 0, 0, 0,
            17, 17, 37, 17, 19, 33, 22, 7, 56, 37, 49, 17, 98, 5, 32, 69, 38, 53, 116, 36, 50, 0, 0, 0,
            69, 69, 87, 69, 87, 74, 123, 138, 197, 198, 164, 51, 104, 105, 131, 40, 87, 150, 197, 35, 52, 0, 0, 0,
            52, 51, 50, 51, 51, 35, 67, 36, 53, 35, 65, 35, 68, 51, 83, 34, 66, 34, 83, 36, 69, 0, 0, 17,
            17, 0, 0, 0, 0, 0, 0, 0, 1, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 19, 36, 16, 0, 17,
            33, 16, 0, 0, 0, 0, 0, 0, 0, 1, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 18,
            17, 17, 16, 0, 0, 0, 0, 0, 0, 0, 1, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 17, 17,
            16, 17, 17, 0, 16, 1, 0, 0, 0, 0, 0, 18, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 17, 17,
            17, 17, 17, 17, 17, 17, 17, 17, 17, 16, 0, 0, 17, 0, 0, 0, 0, 0, 0, 0, 0, 1, 17, 17,
            17, 16, 18, 51, 68, 67, 51, 34, 17, 17, 17, 17, 17, 33, 34, 48, 0, 0, 0, 0, 0, 1, 1, 18,
            17, 0, 17, 0, 16, 16, 18, 34, 35, 35, 51, 68, 51, 51, 34, 32, 0, 0, 0, 0, 0, 0, 0, 17,
            34, 17, 16, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 18, 34, 48, 0, 0, 0, 0, 0, 1, 17, 34,
            33, 17, 17, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 18, 17, 18,
            17, 17, 34, 51, 18, 17, 18, 18, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 33, 17, 17,
            34, 17, 35, 33, 1, 1, 17, 16, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 18, 34, 33, 34,
            17, 18, 51, 50, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 35, 50, 17, 17,
            18, 35, 34, 35, 33, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 18, 66, 70, 32, 16,
            18, 50, 34, 49, 34, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 34, 18, 82, 35, 34,
            18, 34, 35, 35, 50, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 35, 84, 50, 53, 68,
            18, 35, 50, 51, 51, 32, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 51, 36, 50, 36, 51,
            34, 35, 51, 52, 52, 81, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 21, 67, 67, 50, 18, 18,
            34, 50, 36, 85, 52, 84, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 53, 70, 100, 33, 16, 0,
            35, 34, 34, 36, 51, 85, 48, 0, 0, 0, 0, 0, 0, 0, 0, 2, 16, 2, 69, 53, 115, 0, 0, 0,
            50, 2, 33, 34, 35, 51, 49, 0, 0, 0, 0, 0, 0, 0, 0, 2, 67, 54, 98, 35, 115, 33, 17, 33,
            33, 18, 34, 51, 34, 34, 35, 0, 0, 0, 0, 0, 0, 0, 0, 18, 87, 153, 65, 21, 86, 114, 34, 0,
            52, 102, 51, 50, 84, 51, 51, 49, 0, 0, 2, 65, 0, 16, 0, 34, 69, 132, 81, 19, 17, 84, 34, 67,
            103, 85, 101, 51, 52, 36, 52, 50, 32, 1, 53, 103, 112, 48, 20, 49, 34, 2, 67, 18, 36, 19, 52, 87,
            101, 69, 85, 68, 68, 37, 85, 70, 68, 71, 67, 85, 117, 83, 52, 49, 17, 35, 70, 85, 85, 54, 115, 54,
            104, 88, 119, 85, 52, 66, 35, 69, 69, 86, 137, 67, 87, 102, 119, 100, 17, 36, 68, 85, 69, 85, 121, 99,
            69, 69, 103, 155, 102, 119, 115, 52, 70, 103, 102, 98, 69, 68, 87, 119, 53, 83, 53, 85, 84, 103, 102, 100,
            18, 50, 52, 71, 121, 116, 33, 21, 54, 51, 85, 50, 33, 51, 20, 67, 85, 101, 37, 102, 85, 83, 53, 66,
            101, 49, 35, 33, 20, 101, 20, 136, 85, 85, 49, 69, 102, 67, 19, 50, 19, 85, 51, 105, 116, 84, 82, 37,
            50, 49, 18, 53, 83, 35, 55, 151, 99, 49, 17, 52, 66, 50, 17, 20, 100, 50, 68, 136, 117, 34, 17, 20,
        };

        /// <summary>One value of the copy, as brightness (0-120): the tests
        /// draw the menu with it.</summary>
        internal static byte Value(int column, int row)
        {
            int i = row * Across + column;
            return (byte)((Data[i >> 1] >> ((i & 1) == 0 ? 4 : 0) & 15) * 8);
        }

        // zero-mean, unit-length copies (the left-out part is 0 in both)
        private static readonly float[] Fine = Load(1), Coarse = Load(Block);

        private uint[] _sums = new uint[0]; // brightness summed from the area's top left corner
        private int _width, _height;        // the area's size
        private readonly float[] _cells = new float[Across * Down];

        /// <summary>True if cell (column, row) of the copy is where the
        /// menu's text goes.</summary>
        private static bool Text(int column, int row) =>
            row >= Down * 23 / 100 && row < Down * 80 / 100 && column >= Across * 2 / 100 && column < Across * 56 / 100;

        private static float[] Load(int block)
        {
            int across = Across / block, down = Down / block;
            var t = new float[across * down];
            var used = new bool[t.Length];
            double mean = 0;
            int count = 0;
            for (int r = 0; r < down; r++)
                for (int c = 0; c < across; c++)
                {
                    double sum = 0;
                    bool text = false;
                    for (int y = r * block; y < (r + 1) * block; y++)
                        for (int x = c * block; x < (c + 1) * block; x++)
                        {
                            int i = y * Across + x;
                            sum += Data[i >> 1] >> ((i & 1) == 0 ? 4 : 0) & 15;
                            text |= Text(x, y);
                        }
                    if (text)
                        continue;
                    t[r * across + c] = (float)sum;
                    used[r * across + c] = true;
                    mean += sum;
                    count++;
                }
            mean /= count;
            double norm = 0;
            for (int i = 0; i < t.Length; i++)
                if (used[i])
                {
                    t[i] -= (float)mean;
                    norm += t[i] * t[i];
                }
            norm = Math.Sqrt(norm);
            for (int i = 0; i < t.Length; i++)
                t[i] = used[i] ? (float)(t[i] / norm) : 0;
            return t;
        }

        /// <summary>Looks for the menu inside area of a frame (4 bytes per
        /// pixel, R and B order given by rgbOrder). True if it is there;
        /// picture is then the game picture, in frame pixels.</summary>
        public bool Find(byte[] px, int stride, bool rgbOrder, Rectangle area, out Rectangle picture)
        {
            picture = Rectangle.Empty;
            if (area.Width < 120 || area.Height < 90)
                return false;
            Sum(px, stride, rgbOrder, area);

            // every size, shape and place, coarsely: the best four
            var best = new double[4, 5]; // match, x, y, width, height
            for (double h = _height; h >= 0.25 * _height; h /= 1.12)
                foreach (double shape in new[] { 1.2, 1.36, 1.55, 1.77, 2.0 })
                {
                    double w = h * shape;
                    if (w > _width * 1.02)
                        continue;
                    double stepX = Math.Max(2, w / CoarseAcross), stepY = Math.Max(2, h / CoarseDown);
                    for (double y = 0; y < _height - h + stepY; y += stepY)
                        for (double x = 0; x < _width - w + stepX; x += stepX)
                        {
                            double match = Compare(x, y, w, h, Coarse, CoarseAcross, CoarseDown);
                            int worst = 0;
                            for (int i = 1; i < 4; i++)
                                if (best[i, 0] < best[worst, 0])
                                    worst = i;
                            if (match > best[worst, 0])
                            {
                                best[worst, 0] = match; best[worst, 1] = x; best[worst, 2] = y;
                                best[worst, 3] = w; best[worst, 4] = h;
                            }
                        }
                }

            // each of those moved and resized to where the full copy matches best
            double found = 0;
            var at = new double[4];
            var trial = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (best[i, 0] <= 0)
                    continue;
                var p = new[] { best[i, 1], best[i, 2], best[i, 3], best[i, 4] };
                double match = Compare(p[0], p[1], p[2], p[3], Fine, Across, Down);
                var step = new[] { p[2] / CoarseAcross, p[3] / CoarseDown, p[2] * 0.06, p[3] * 0.06 };
                while (Math.Max(Math.Max(step[0], step[1]), Math.Max(step[2], step[3])) >= 0.4)
                {
                    bool better = false;
                    for (int k = 0; k < 4; k++)
                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            p.CopyTo(trial, 0);
                            trial[k] += sign * step[k];
                            if (k >= 2)
                                trial[k - 2] -= sign * step[k] / 2; // sizes change about the middle
                            if (trial[2] < 20 || trial[3] < 20)
                                continue;
                            double m = Compare(trial[0], trial[1], trial[2], trial[3], Fine, Across, Down);
                            if (m > match + 1e-4)
                            {
                                match = m;
                                trial.CopyTo(p, 0);
                                better = true;
                            }
                        }
                    if (!better)
                        for (int k = 0; k < 4; k++)
                            step[k] /= 2;
                }
                if (match > found)
                {
                    found = match;
                    p.CopyTo(at, 0);
                }
            }
            if (found < Match)
                return false;
            picture = new Rectangle(area.X + (int)Math.Round(at[0]), area.Y + (int)Math.Round(at[1]),
                (int)Math.Round(at[2]), (int)Math.Round(at[3]));
            return true;
        }

        /// <summary>Sums the area's brightness so that any rectangle's
        /// average takes four lookups.</summary>
        private void Sum(byte[] px, int stride, bool rgbOrder, Rectangle area)
        {
            _width = area.Width; _height = area.Height;
            int n = (_width + 1) * (_height + 1);
            if (_sums.Length < n)
                _sums = new uint[n];
            int ri = rgbOrder ? 0 : 2, bi = rgbOrder ? 2 : 0, row = _width + 1;
            for (int x = 0; x <= _width; x++)
                _sums[x] = 0;
            for (int y = 0; y < _height; y++)
            {
                int o = (area.Y + y) * stride + area.X * 4, s = (y + 1) * row;
                uint line = 0;
                _sums[s] = 0;
                for (int x = 0; x < _width; x++, o += 4)
                {
                    line += (uint)((px[o + bi] * 114 + px[o + 1] * 587 + px[o + ri] * 299) / 1000);
                    _sums[s + x + 1] = _sums[s - row + x + 1] + line;
                }
            }
        }

        /// <summary>How well the rectangle (in area pixels) matches the copy
        /// at the given grid: its correlation with it, -1 to 1.</summary>
        private double Compare(double x, double y, double w, double h, float[] copy, int across, int down)
        {
            int row = _width + 1, k = 0;
            double sum = 0, squares = 0, dot = 0;
            int count = 0;
            int y0 = Edge(y, _height);
            for (int r = 0; r < down; r++)
            {
                int y1 = Edge(y + (r + 1) * h / down, _height);
                int x0 = Edge(x, _width);
                for (int c = 0; c < across; c++, k++)
                {
                    int x1 = Edge(x + (c + 1) * w / across, _width);
                    if (copy[k] != 0)
                    {
                        long total = (long)_sums[y1 * row + x1] - _sums[y0 * row + x1] - _sums[y1 * row + x0] + _sums[y0 * row + x0];
                        double v = total / (double)Math.Max(1, (y1 - y0) * (x1 - x0));
                        sum += v;
                        squares += v * v;
                        dot += v * copy[k];
                        count++;
                    }
                    x0 = x1;
                }
                y0 = y1;
            }
            double spread = squares - sum * sum / count; // the copy is zero-mean: the mean drops out of dot
            return spread > 1e-6 ? dot / Math.Sqrt(spread) : 0;
        }

        private static int Edge(double at, int limit) => Math.Max(0, Math.Min(limit, (int)Math.Round(at)));
    }
}
