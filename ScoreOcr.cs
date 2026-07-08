using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace BgaSpnfdr
{
    /// <summary>Thin wrapper around the OCR engine built into Windows 10/11.</summary>
    internal sealed class OcrReader
    {
        private readonly OcrEngine _engine;

        public OcrReader()
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_engine == null)
            {
                try { _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US")); }
                catch { /* no usable language pack */ }
            }
        }

        public bool Available => _engine != null;

        /// <summary>Runs OCR on a 32bppArgb bitmap and returns the recognized text.</summary>
        public async Task<string> ReadAsync(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixels;
            try
            {
                pixels = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            using (var soft = SoftwareBitmap.CreateCopyFromBuffer(
                       pixels.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height))
            {
                OcrResult result = await _engine.RecognizeAsync(soft);
                return result?.Text ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Prepares the tiny score region for OCR. The pipeline (each detail is
    /// load-bearing, tuned against the real feed):
    ///  - 6x bicubic upscale (at 4x the later dilation filled the loops of
    ///    the 8, and 273850 read as 273550)
    ///  - threshold at median luminance + margin with Otsu as a floor, digits
    ///    are the distinctly-bright pixels (plain Otsu mis-splits midtone
    ///    backgrounds, cutting between the digits' dark outlines and the rest)
    ///  - 3x3 dilation (the thin game font reads badly undilated); no
    ///    blob-removal pass — it ate faint digits, and noisy frames already
    ///    fail score validation safely
    ///  - white padding, drawn slightly scaled with bilinear filtering so the
    ///    hard binary edges soften (antialiased text reads better)
    /// Thresholding and dilation run on plain arrays with a single read lock
    /// and a single write lock.
    /// </summary>
    internal static class ScoreImage
    {
        public static Bitmap PrepareBinarized(Bitmap source, Rectangle region)
        {
            const int margin = 32;
            int scale = Math.Max(1, Math.Min(6, 2400 / Math.Max(1, region.Width)));
            int w = region.Width * scale, h = region.Height * scale;

            // one read pass: luminance + histogram
            var luma = new byte[w * h];
            var histogram = new int[256];
            using (var big = Upscale(source, region, w, h))
            {
                var data = big.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var px = new byte[data.Stride * h];
                    Marshal.Copy(data.Scan0, px, 0, px.Length);
                    int i = 0;
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * data.Stride;
                        for (int x = 0; x < w; x++, i++)
                        {
                            int o = row + x * 4;
                            byte l = (byte)((px[o] * 114 + px[o + 1] * 587 + px[o + 2] * 299) / 1000);
                            luma[i] = l;
                            histogram[l]++;
                        }
                    }
                }
                finally
                {
                    big.UnlockBits(data);
                }
            }

            int threshold = Math.Max(
                OtsuThreshold(histogram, luma.Length),
                MedianLuma(histogram, luma.Length) + 45);

            // dilate straight from the luma array into a text mask
            var text = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (luma[y * w + x] <= threshold)
                        continue;
                    int y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1);
                    int x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1);
                    for (int ny = y0; ny <= y1; ny++)
                        for (int nx = x0; nx <= x1; nx++)
                            text[ny * w + nx] = true;
                }
            }

            // one write pass into an unpadded binary image
            Bitmap padded;
            using (var binary = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var data = binary.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var px = new byte[data.Stride * h];
                    int i = 0;
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * data.Stride;
                        for (int x = 0; x < w; x++, i++)
                        {
                            int o = row + x * 4;
                            byte v = text[i] ? (byte)0 : (byte)255;
                            px[o] = px[o + 1] = px[o + 2] = v;
                            px[o + 3] = 255;
                        }
                    }
                    Marshal.Copy(px, 0, data.Scan0, px.Length);
                }
                finally
                {
                    binary.UnlockBits(data);
                }

                // pad with white; the slightly-shrunken bilinear draw softens
                // the binary edges (OCR dislikes text at edges and hard pixels)
                padded = new Bitmap(w + margin * 2, h + margin * 2, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(padded))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(binary, new Rectangle(margin, margin, w - 3, h - 3));
                }
            }
            return padded;
        }

        /// <summary>Plain color upscale — no binarization. The two variants
        /// fail on different frames, so the caller OCRs both.</summary>
        public static Bitmap PrepareColor(Bitmap source, Rectangle region)
        {
            int scale = Math.Max(1, Math.Min(6, 2400 / Math.Max(1, region.Width)));
            return Upscale(source, region, region.Width * scale, region.Height * scale);
        }

        private static Bitmap Upscale(Bitmap source, Rectangle region, int w, int h)
        {
            var big = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(source, new Rectangle(0, 0, w, h), region, GraphicsUnit.Pixel);
            }
            return big;
        }

        private static int MedianLuma(int[] histogram, int total)
        {
            int half = total / 2, seen = 0;
            for (int t = 0; t < 256; t++)
            {
                seen += histogram[t];
                if (seen >= half) return t;
            }
            return 255;
        }

        private static int OtsuThreshold(int[] histogram, int total)
        {
            long sum = 0;
            for (int t = 0; t < 256; t++) sum += (long)t * histogram[t];

            long sumBackground = 0;
            long weightBackground = 0;
            double best = -1;
            int threshold = 127;
            for (int t = 0; t < 256; t++)
            {
                weightBackground += histogram[t];
                if (weightBackground == 0) continue;
                long weightForeground = total - weightBackground;
                if (weightForeground == 0) break;
                sumBackground += (long)t * histogram[t];
                double meanB = (double)sumBackground / weightBackground;
                double meanF = (double)(sum - sumBackground) / weightForeground;
                double between = (double)weightBackground * weightForeground
                                 * (meanB - meanF) * (meanB - meanF);
                if (between > best)
                {
                    best = between;
                    threshold = t;
                }
            }
            return threshold;
        }
    }
}
