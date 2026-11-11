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
        /// already starts inside.</summary>
        public static Rectangle Recenter(Rectangle crop, int firstColumn)
        {
            int inside = firstColumn - LeftMargin;
            if (inside >= 2 || crop.Width <= 0)
                return crop;
            crop.X -= (int)Math.Ceiling((8 - inside) * crop.Width / (double)DigitReader.ReferenceCropWidth);
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

        /// <summary>
        /// Fills band (rows x width) with the part of the frame that starts
        /// at frame position (x0, y0), scaled so that sx by sy frame pixels
        /// make one band pixel. px as for Fill. Positions outside px's rows
        /// or the frame's width take the nearest pixel inside.
        /// </summary>
        public static void Sample(byte[] px, int stride, int first, int last, int frameWidth,
            double x0, double y0, double sx, double sy, int width, int rows, bool rgbOrder, byte[] band)
        {
            int ri = rgbOrder ? 0 : 2, bi = rgbOrder ? 2 : 0;
            bool average = sx > 1.5 || sy > 1.5;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < width; c++)
                {
                    int v;
                    if (average)
                    {
                        int xa = (int)Math.Floor(x0 + c * sx), xb = Math.Max(xa + 1, (int)Math.Ceiling(x0 + (c + 1) * sx));
                        int ya = (int)Math.Floor(y0 + r * sy), yb = Math.Max(ya + 1, (int)Math.Ceiling(y0 + (r + 1) * sy));
                        int sum = 0, n = 0;
                        for (int y = ya; y < yb; y++)
                            for (int x = xa; x < xb; x++, n++)
                                sum += Luma(px, stride, first, last, frameWidth, x, y, ri, bi);
                        v = sum / n;
                    }
                    else
                    {
                        double fx = x0 + (c + 0.5) * sx - 0.5, fy = y0 + (r + 0.5) * sy - 0.5;
                        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                        double tx = fx - ix, ty = fy - iy;
                        double a = Luma(px, stride, first, last, frameWidth, ix, iy, ri, bi);
                        double b = Luma(px, stride, first, last, frameWidth, ix + 1, iy, ri, bi);
                        double cc = Luma(px, stride, first, last, frameWidth, ix, iy + 1, ri, bi);
                        double d = Luma(px, stride, first, last, frameWidth, ix + 1, iy + 1, ri, bi);
                        v = (int)Math.Round((a * (1 - tx) + b * tx) * (1 - ty) + (cc * (1 - tx) + d * tx) * ty);
                    }
                    band[r * width + c] = (byte)Math.Max(0, Math.Min(255, v));
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
