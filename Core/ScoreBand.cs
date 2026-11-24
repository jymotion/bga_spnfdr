using System;
using System.Drawing;

namespace BgaSpnfdr
{
    /// <summary>
    /// Cuts the score band out of a captured frame and scales it to the
    /// reference size DigitReader expects, as brightness bytes. Shared by both
    /// builds: Windows hands it rows copied from the captured bitmap, Linux
    /// rows copied from the PipeWire frame.
    /// </summary>
    internal static class ScoreBand
    {
        // Extra rows above/below the band, each one more vertical offset the
        // reader tries. A crop (auto-detected or dragged by hand) is easily a
        // few rows off, and on the reference run a band 3 rows off confirmed
        // wrong scores; with 5, every shift up to 5 rows read like a perfect
        // crop. Costs little: once the reader locks onto a row it reads only
        // that one.
        public const int Margin = 5;
        // The band starts this far left of the crop. One level puts the score
        // 3 px from the picture's left edge, so a crop whose left edge is a
        // little too far right would cut the first digit off, and nothing can
        // tell a score that lost its first digit that way from a real one.
        public const int LeftMargin = 24;
        // BattleTanx.ScoreFraction's 35% of the 617-px reference picture (the
        // score never ends past 30% of it, which leaves the same slack on
        // the right), plus the margin
        public const int Width = 216 + LeftMargin;
        public const int Rows = DigitReader.BandHeight + 2 * Margin;
        private const int Top = DigitReader.BandTop - Margin;

        /// <summary>A crop whose left edge is too far right shows when a
        /// level puts the score at the picture's very edge: its first digit
        /// is then found in the margin (firstColumn: DigitReader.FoundFirst
        /// of a read that stands). Returns the crop moved left so that the
        /// score starts 8 reference pixels inside it, or unchanged if it
        /// already starts inside.
        /// It is also moved up or down when the score is read 2 or more
        /// rows from the middle of the band (offset: DigitReader.FoundOffset):
        /// the reader only looks Margin rows either way, and a crop whose
        /// height was worked out without sight of the picture's top edge can
        /// use most of that up.</summary>
        public static Rectangle Recenter(Rectangle crop, int firstColumn, int offset)
        {
            if (crop.Width <= 0 || crop.Height <= 0)
                return crop;
            int inside = firstColumn - LeftMargin;
            if (inside < 2)
                crop.X -= (int)Math.Ceiling((8 - inside) * crop.Width / (double)DigitReader.ReferenceCropWidth);
            if (offset >= 0 && Math.Abs(offset - Margin) >= 2)
                crop.Y += (int)Math.Round((offset - Margin) * crop.Height / (double)DigitReader.ReferenceCropHeight);
            return crop;
        }

        /// <summary>The frame rows (inclusive) Fill reads for this crop.</summary>
        public static void RowsNeeded(Rectangle crop, int frameHeight, out int first, out int last)
        {
            double sy = crop.Height / (double)DigitReader.ReferenceCropHeight;
            first = Math.Max(0, (int)Math.Floor(crop.Y + Top * sy) - 1);
            last = Math.Min(frameHeight - 1, (int)Math.Ceiling(crop.Y + (Top + Rows) * sy) + 1);
        }

        /// <summary>The frame columns (inclusive) Fill reads for this crop.
        /// The Windows app copies only these columns of RowsNeeded's rows back
        /// from the graphics card.</summary>
        public static void ColumnsNeeded(Rectangle crop, int frameWidth, out int first, out int last)
        {
            double sx = crop.Width / (double)DigitReader.ReferenceCropWidth;
            double x0 = crop.X + BattleTanx.ScoreFraction.X * crop.Width - LeftMargin * sx;
            first = Math.Max(0, (int)Math.Floor(x0) - 1);
            last = Math.Min(frameWidth - 1, (int)Math.Ceiling(x0 + Width * sx) + 1);
        }

        /// <summary>
        /// Fills band (Rows x Width) from px, which holds frame rows
        /// first..last (stride bytes each, 4 bytes per pixel: B,G,R,x, or
        /// R,G,B,x with rgbOrder). The crop is mapped onto the reference
        /// capture size: 1:1 when the game picture is that size, bilinear
        /// for small scale changes, box-averaged when shrinking a lot (e.g. a
        /// 1080p capture).
        /// </summary>
        public static void Fill(byte[] px, int stride, int first, int last, int frameWidth,
            Rectangle crop, bool rgbOrder, byte[] band)
        {
            double sx = crop.Width / (double)DigitReader.ReferenceCropWidth;
            double sy = crop.Height / (double)DigitReader.ReferenceCropHeight;
            Sample(px, stride, first, last, frameWidth,
                crop.X + BattleTanx.ScoreFraction.X * crop.Width - LeftMargin * sx, crop.Y + Top * sy,
                sx, sy, Width, Rows, rgbOrder, band);
        }

        // Sample's working space, kept from call to call (each thread's:
        // the Linux app reads on PipeWire's thread and searches on the UI's)
        [ThreadStatic] private static byte[] _luma;   // brightness of the frame pixels a call reads
        [ThreadStatic] private static int[] _from;    // per band column: its first frame column ...
        [ThreadStatic] private static int[] _to;      // ... and, averaging, the one after its last
        [ThreadStatic] private static double[] _part; // ... or, bilinear, how far towards the next

        /// <summary>
        /// Fills band (rows x width) with the part of the frame that starts
        /// at frame position (x0, y0), scaled so that sx by sy frame pixels
        /// make one band pixel. px as for Fill. Positions outside px's rows
        /// or the frame's width take the nearest pixel inside.
        /// </summary>
        public static void Sample(byte[] px, int stride, int first, int last, int frameWidth,
            double x0, double y0, double sx, double sy, int width, int rows, bool rgbOrder, byte[] band)
        {
            if (width <= 0 || rows <= 0)
                return;
            bool average = sx > 1.5 || sy > 1.5;
            if (_from == null || _from.Length < width)
            {
                _from = new int[width];
                _to = new int[width];
                _part = new double[width];
            }
            int[] from = _from, to = _to;
            double[] part = _part;

            // which frame columns and rows the band is made of
            int xMin, xMax, yMin, yMax;
            if (average)
            {
                for (int c = 0; c < width; c++)
                {
                    from[c] = (int)Math.Floor(x0 + c * sx);
                    to[c] = Math.Max(from[c] + 1, (int)Math.Ceiling(x0 + (c + 1) * sx));
                }
                xMin = from[0];
                xMax = to[width - 1] - 1;
                yMin = (int)Math.Floor(y0);
                yMax = Math.Max((int)Math.Floor(y0 + (rows - 1) * sy) + 1, (int)Math.Ceiling(y0 + rows * sy)) - 1;
            }
            else
            {
                for (int c = 0; c < width; c++)
                {
                    double fx = x0 + (c + 0.5) * sx - 0.5;
                    from[c] = (int)Math.Floor(fx);
                    part[c] = fx - from[c];
                }
                xMin = from[0];
                xMax = from[width - 1] + 1;
                yMin = (int)Math.Floor(y0 + 0.5 * sy - 0.5);
                yMax = (int)Math.Floor(y0 + (rows - 0.5) * sy - 0.5) + 1;
            }

            // their brightness, worked out once per frame pixel (a band
            // pixel shares most of its frame pixels with its neighbours)
            int across = xMax - xMin + 1, down = yMax - yMin + 1;
            if (_luma == null || _luma.Length < across * down)
                _luma = new byte[across * down];
            byte[] luma = _luma;
            int ri = rgbOrder ? 0 : 2, bi = rgbOrder ? 2 : 0;
            // (columns left and right of the frame take its edge's pixel)
            int inFrom = Math.Min(across, Math.Max(0, -xMin)), inTo = Math.Max(inFrom, Math.Min(across, frameWidth - xMin));
            for (int j = 0; j < down; j++)
            {
                int y = Math.Max(first, Math.Min(last, yMin + j));
                int row = (y - first) * stride, at = j * across;
                int o = row + (xMin + inFrom) * 4;
                for (int i = inFrom; i < inTo; i++, o += 4)
                    luma[at + i] = (byte)((px[o + bi] * 114 + px[o + 1] * 587 + px[o + ri] * 299) / 1000);
                if (inFrom > 0 || inTo < across)
                {
                    byte leftmost = (byte)Luma(px, stride, first, last, frameWidth, 0, y, ri, bi);
                    byte rightmost = (byte)Luma(px, stride, first, last, frameWidth, frameWidth - 1, y, ri, bi);
                    for (int i = 0; i < inFrom; i++)
                        luma[at + i] = leftmost;
                    for (int i = inTo; i < across; i++)
                        luma[at + i] = rightmost;
                }
            }

            for (int r = 0; r < rows; r++)
            {
                int o = r * width;
                if (average)
                {
                    int ya = (int)Math.Floor(y0 + r * sy), yb = Math.Max(ya + 1, (int)Math.Ceiling(y0 + (r + 1) * sy));
                    for (int c = 0; c < width; c++)
                    {
                        int xa = from[c] - xMin, xb = to[c] - xMin;
                        int sum = 0;
                        for (int y = ya - yMin; y < yb - yMin; y++)
                            for (int k = y * across + xa, stop = y * across + xb; k < stop; k++)
                                sum += luma[k];
                        band[o + c] = (byte)(sum / ((yb - ya) * (xb - xa)));
                    }
                }
                else
                {
                    double fy = y0 + (r + 0.5) * sy - 0.5;
                    int iy = (int)Math.Floor(fy);
                    double ty = fy - iy;
                    int upper = (iy - yMin) * across - xMin, lower = upper + across;
                    for (int c = 0; c < width; c++)
                    {
                        int ix = from[c];
                        double tx = part[c];
                        double a = luma[upper + ix], b = luma[upper + ix + 1];
                        double cc = luma[lower + ix], d = luma[lower + ix + 1];
                        int v = (int)Math.Round((a * (1 - tx) + b * tx) * (1 - ty) + (cc * (1 - tx) + d * tx) * ty);
                        band[o + c] = (byte)Math.Max(0, Math.Min(255, v));
                    }
                }
            }
        }

        internal static int Luma(byte[] px, int stride, int first, int last, int width, int x, int y, int ri, int bi)
        {
            x = Math.Max(0, Math.Min(width - 1, x));
            y = Math.Max(first, Math.Min(last, y));
            int o = (y - first) * stride + x * 4;
            return (px[o + bi] * 114 + px[o + 1] * 587 + px[o + ri] * 299) / 1000;
        }
    }
}
