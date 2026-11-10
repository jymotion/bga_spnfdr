#if SIDE_BY_SIDE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace BgaSpnfdr
{
    /// <summary>
    /// Side-by-side test build only (compiled out of the normal build). Runs
    /// the Linux build's digit reader on every captured frame next to the
    /// Windows OCR, which still drives the notes, and logs both to
    /// %APPDATA%\bga_spnfdr\sidebyside\(date-time)\:
    ///  - reads.csv: every read from each reader, with the time it took
    ///  - events.csv: every newly confirmed score from either reader, and
    ///    lasting disagreements (with a picture of the game feed saved)
    /// The status bar shows the running comparison.
    /// </summary>
    internal sealed class SideBySide
    {
        private const int Margin = 2;
        private const int BandWidth = 216;
        private const int Rows = DigitReader.BandHeight + 2 * Margin;
        private const int DisagreeFrames = 20; // 2 s at 10 frames/s before it counts
        private const int MaxPictures = 40;

        private readonly DigitReader _reader = new DigitReader();
        private readonly ScoreTracker _tracker = new ScoreTracker();
        private readonly Dictionary<long, string> _everyScore = new Dictionary<long, string>();
        private readonly byte[] _band = new byte[BandWidth * Rows];
        private byte[] _px = new byte[0];
        private readonly string _dir;
        private readonly StreamWriter _reads, _events;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TimeSpan _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;

        private Rectangle _lastCrop;
        private long _readerScore = -1, _ocrScore = -1;
        private long _frames, _ocrFrames;
        private double _readerMs, _ocrMs;
        private int _diffRun, _disagreements, _pictures;
        private int _readerFirst, _ocrFirst;

        public SideBySide()
        {
            for (long s = 0; s < 1000000; s += 25) _everyScore[s] = s.ToString(CultureInfo.InvariantCulture);
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "bga_spnfdr", "sidebyside", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(_dir);
            _reads = new StreamWriter(Path.Combine(_dir, "reads.csv")) { AutoFlush = true };
            _events = new StreamWriter(Path.Combine(_dir, "events.csv")) { AutoFlush = true };
            _reads.WriteLine("seconds,reader,read,ms,extra");
            _events.WriteLine("seconds,event,ocr_score,reader_score,note");
        }

        private string Now => _clock.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);

        /// <summary>Every captured frame (10 a second).</summary>
        public void OnFrame(Bitmap capture, Rectangle crop, long ocrScore)
        {
            if (capture == null || crop.Width <= 0 || crop.Height <= 0)
                return;
            try
            {
                if (crop != _lastCrop)
                {
                    _reader.Reset();
                    _lastCrop = crop;
                }
                var sw = Stopwatch.StartNew();
                ExtractBand(capture, crop);
                string read = _reader.Read(_band, BandWidth, Rows);
                double ms = sw.Elapsed.TotalMilliseconds;
                _frames++;
                _readerMs += ms;
                _reads.WriteLine($"{Now},digit,{read},{ms.ToString("F3", CultureInfo.InvariantCulture)},");

                string confirmed = _tracker.HandleReads(read, read, _everyScore);
                if (confirmed != null)
                {
                    _readerScore = long.Parse(confirmed, CultureInfo.InvariantCulture);
                    bool first = _readerScore != _ocrScore;
                    if (first) _readerFirst++;
                    _events.WriteLine($"{Now},digit confirmed,{_ocrScore},{_readerScore},{(first ? "reader first" : "")}");
                }

                // a lasting disagreement (not just one confirming a moment earlier)
                if (_readerScore >= 0 && _ocrScore >= 0 && _readerScore != _ocrScore)
                {
                    if (++_diffRun == DisagreeFrames)
                    {
                        _disagreements++;
                        string picture = "";
                        if (_pictures < MaxPictures)
                        {
                            picture = $"disagree_{++_pictures:00}_ocr{_ocrScore}_digit{_readerScore}.png";
                            var r = Rectangle.Intersect(crop, new Rectangle(Point.Empty, capture.Size));
                            using (var img = capture.Clone(r, PixelFormat.Format32bppArgb))
                                img.Save(Path.Combine(_dir, picture), ImageFormat.Png);
                        }
                        _events.WriteLine($"{Now},DISAGREE 2s,{_ocrScore},{_readerScore},{picture}");
                    }
                }
                else
                    _diffRun = 0;
            }
            catch (Exception e)
            {
                _events.WriteLine($"{Now},error,{_ocrScore},{_readerScore},\"{e.Message.Replace('"', '\'')}\"");
            }
        }

        /// <summary>Every finished Windows OCR read.</summary>
        public void OnOcr(string textBin, string textColor, double prepMs, double ocrMs, long ocrScore)
        {
            _ocrFrames++;
            _ocrMs += prepMs + ocrMs;
            _reads.WriteLine($"{Now},ocr,{Clean(textBin)}|{Clean(textColor)}," +
                             $"{(prepMs + ocrMs).ToString("F1", CultureInfo.InvariantCulture)}," +
                             $"prep {prepMs.ToString("F1", CultureInfo.InvariantCulture)}");
            if (ocrScore != _ocrScore)
            {
                _ocrScore = ocrScore;
                bool first = ocrScore != _readerScore;
                if (first) _ocrFirst++;
                _events.WriteLine($"{Now},ocr confirmed,{_ocrScore},{_readerScore},{(first ? "ocr first" : "")}");
            }
        }

        public string Summary
        {
            get
            {
                double cpu = (Process.GetCurrentProcess().TotalProcessorTime - _cpuStart).TotalSeconds
                             / Math.Max(1, _clock.Elapsed.TotalSeconds) * 100;
                return $"OCR {Show(_ocrScore)} | digit reader {Show(_readerScore)}  ·  per frame: OCR " +
                       $"{(_ocrFrames > 0 ? _ocrMs / _ocrFrames : 0):F0} ms, reader {(_frames > 0 ? _readerMs / _frames : 0):F2} ms" +
                       $"  ·  first to confirm: OCR {_ocrFirst}, reader {_readerFirst}  ·  disagreements: {_disagreements}" +
                       $"  ·  app CPU {cpu:F1}%  ·  log: {_dir}";
            }
        }

        private static string Show(long score) => score < 0 ? "-" : score.ToString(CultureInfo.InvariantCulture);

        private static string Clean(string s) =>
            (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace(",", " ").Replace("|", " ").Trim();

        /// <summary>Same band sampling as Linux/FrameProcessor.ExtractBand:
        /// the crop mapped onto the reference capture size.</summary>
        private void ExtractBand(Bitmap bmp, Rectangle crop)
        {
            double sx = crop.Width / (double)DigitReader.ReferenceCropWidth;
            double sy = crop.Height / (double)DigitReader.ReferenceCropHeight;
            int top = DigitReader.BandTop - Margin;
            int y0 = Math.Max(0, (int)Math.Floor(crop.Y + top * sy) - 1);
            int y1 = Math.Min(bmp.Height - 1, (int)Math.Ceiling(crop.Y + (top + Rows) * sy) + 1);
            if (y1 < y0) return;
            var data = bmp.LockBits(new Rectangle(0, y0, bmp.Width, y1 - y0 + 1), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            int stride;
            try
            {
                stride = data.Stride;
                int n = stride * (y1 - y0 + 1);
                if (_px.Length < n) _px = new byte[n];
                Marshal.Copy(data.Scan0, _px, 0, n);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            int w = bmp.Width;
            bool average = sx > 1.5 || sy > 1.5;
            for (int r = 0; r < Rows; r++)
            {
                for (int c = 0; c < BandWidth; c++)
                {
                    int v;
                    if (average)
                    {
                        int xa = (int)(crop.X + c * sx), xb = Math.Max(xa + 1, (int)Math.Ceiling(crop.X + (c + 1) * sx));
                        int ya = (int)(crop.Y + (top + r) * sy), yb = Math.Max(ya + 1, (int)Math.Ceiling(crop.Y + (top + r + 1) * sy));
                        int sum = 0, count = 0;
                        for (int y = ya; y < yb; y++)
                            for (int x = xa; x < xb; x++, count++)
                                sum += Luma(x, y, w, y0, y1, stride);
                        v = sum / count;
                    }
                    else
                    {
                        double fx = crop.X + (c + 0.5) * sx - 0.5, fy = crop.Y + (top + r + 0.5) * sy - 0.5;
                        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                        double tx = fx - ix, ty = fy - iy;
                        double a = Luma(ix, iy, w, y0, y1, stride), b = Luma(ix + 1, iy, w, y0, y1, stride);
                        double cc = Luma(ix, iy + 1, w, y0, y1, stride), d = Luma(ix + 1, iy + 1, w, y0, y1, stride);
                        v = (int)Math.Round((a * (1 - tx) + b * tx) * (1 - ty) + (cc * (1 - tx) + d * tx) * ty);
                    }
                    _band[r * BandWidth + c] = (byte)Math.Max(0, Math.Min(255, v));
                }
            }
        }

        private int Luma(int x, int y, int w, int y0, int y1, int stride)
        {
            x = Math.Max(0, Math.Min(w - 1, x));
            y = Math.Max(y0, Math.Min(y1, y));
            int o = (y - y0) * stride + x * 4;
            return (_px[o] * 114 + _px[o + 1] * 587 + _px[o + 2] * 299) / 1000;
        }
    }
}
#endif
