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
        public const int Margin = 2;  // extra rows above/below: setups differ by a pixel or two
        public const int Width = 216; // BattleTanx.ScoreFraction's 35% of the 617-px reference picture
        public const int Rows = DigitReader.BandHeight + 2 * Margin;
        private const int Top = DigitReader.BandTop - Margin;

        /// <summary>The frame rows (inclusive) Fill reads for this crop.</summary>
        public static void RowsNeeded(Rectangle crop, int frameHeight, out int first, out int last)
        {
            double sy = crop.Height / (double)DigitReader.ReferenceCropHeight;
            first = Math.Max(0, (int)Math.Floor(crop.Y + Top * sy) - 1);
            last = Math.Min(frameHeight - 1, (int)Math.Ceiling(crop.Y + (Top + Rows) * sy) + 1);
        }

#if GRAPHICS_CAPTURE
        /// <summary>The frame columns (inclusive) Fill reads for this crop.
        /// Windows.Graphics.Capture build: only these columns of RowsNeeded's
        /// rows are copied back from the graphics card.</summary>
        public static void ColumnsNeeded(Rectangle crop, int frameWidth, out int first, out int last)
        {
            double sx = crop.Width / (double)DigitReader.ReferenceCropWidth;
            double x0 = crop.X + BattleTanx.ScoreFraction.X * crop.Width;
            first = Math.Max(0, (int)Math.Floor(x0) - 1);
            last = Math.Min(frameWidth - 1, (int)Math.Ceiling(x0 + Width * sx) + 1);
        }
#endif

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
            double x0 = crop.X + BattleTanx.ScoreFraction.X * crop.Width;
            int ri = rgbOrder ? 0 : 2, bi = rgbOrder ? 2 : 0;
            bool average = sx > 1.5 || sy > 1.5;
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < Width; c++)
                {
                    int v;
                    if (average)
                    {
                        int xa = (int)(x0 + c * sx), xb = Math.Max(xa + 1, (int)Math.Ceiling(x0 + (c + 1) * sx));
                        int ya = (int)(crop.Y + (Top + r) * sy), yb = Math.Max(ya + 1, (int)Math.Ceiling(crop.Y + (Top + r + 1) * sy));
                        int sum = 0, n = 0;
                        for (int y = ya; y < yb; y++)
                            for (int x = xa; x < xb; x++, n++)
                                sum += Luma(px, stride, first, last, frameWidth, x, y, ri, bi);
                        v = sum / n;
                    }
                    else
                    {
                        double fx = x0 + (c + 0.5) * sx - 0.5, fy = crop.Y + (Top + r + 0.5) * sy - 0.5;
                        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                        double tx = fx - ix, ty = fy - iy;
                        double a = Luma(px, stride, first, last, frameWidth, ix, iy, ri, bi);
                        double b = Luma(px, stride, first, last, frameWidth, ix + 1, iy, ri, bi);
                        double cc = Luma(px, stride, first, last, frameWidth, ix, iy + 1, ri, bi);
                        double d = Luma(px, stride, first, last, frameWidth, ix + 1, iy + 1, ri, bi);
                        v = (int)Math.Round((a * (1 - tx) + b * tx) * (1 - ty) + (cc * (1 - tx) + d * tx) * ty);
                    }
                    band[r * Width + c] = (byte)Math.Max(0, Math.Min(255, v));
                }
            }
        }

        private static int Luma(byte[] px, int stride, int first, int last, int width, int x, int y, int ri, int bi)
        {
            x = Math.Max(0, Math.Min(width - 1, x));
            y = Math.Max(first, Math.Min(last, y));
            int o = (y - first) * stride + x * 4;
            return (px[o + bi] * 114 + px[o + 1] * 587 + px[o + ri] * 299) / 1000;
        }
    }
}
