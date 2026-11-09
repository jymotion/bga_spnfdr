using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using BgaSpnfdr.Capture;

namespace BgaSpnfdr
{
    /// <summary>
    /// Turns captured frames into notes. Runs on PipeWire's thread: for each
    /// frame (at most ~30 a second) it resamples the score band to the
    /// reference size, reads it with DigitReader and feeds Core/ScoreTracker.
    /// The UI thread only swaps in new settings (crop, route) and, while it
    /// needs them (preview, auto-crop), takes ~10 full-frame snapshots a
    /// second.
    /// </summary>
    internal sealed unsafe class FrameProcessor
    {
        // the band DigitReader expects, in reference-capture pixels
        public static readonly int BandWidth =
            (int)Math.Round(BattleTanx.ScoreFraction.Width * DigitReader.ReferenceCropWidth);
        private const int Margin = 2; // rows searched above/below (setups differ by a pixel or two)
        public static readonly int BandRows = DigitReader.BandHeight + 2 * Margin;

        private static readonly long ReadInterval = Stopwatch.Frequency * 30 / 1000;   // ~30 reads/s
        private static readonly long SnapshotInterval = Stopwatch.Frequency / 10;      // 10/s

        private sealed class Config
        {
            public Rectangle Crop;
            public Dictionary<long, string> Route;
        }

        private volatile Config _config = new Config { Route = new Dictionary<long, string>() };
        private readonly DigitReader _reader = new DigitReader();
        private readonly ScoreTracker _tracker = new ScoreTracker();
        private readonly byte[] _band = new byte[BandWidth * BandRows];
        private Rectangle _lastCrop;
        private long _lastRead, _lastSnapshot;

        /// <summary>Set by the UI while it wants full-frame snapshots.</summary>
        public volatile bool WantSnapshots;
        /// <summary>PipeWire thread: a newly confirmed score's note.</summary>
        public Action<string> NoteReady;
        /// <summary>PipeWire thread: a new snapshot can be taken.</summary>
        public Action SnapshotReady;

        private readonly object _snapshotLock = new object();
        private byte[] _snapshot = Array.Empty<byte>();
        private int _snapshotW, _snapshotH;

        public volatile int FrameWidth, FrameHeight;
        public string Status => _tracker.Status;

        /// <summary>UI thread: new crop and/or route table.</summary>
        public void Configure(Rectangle crop, Dictionary<long, string> route) =>
            _config = new Config { Crop = crop, Route = route ?? new Dictionary<long, string>() };

        public void OnFrame(Frame f)
        {
            FrameWidth = f.Width;
            FrameHeight = f.Height;
            long now = Stopwatch.GetTimestamp();

            if (now - _lastRead >= ReadInterval)
            {
                _lastRead = now;
                var cfg = _config;
                var frameRect = new Rectangle(0, 0, f.Width, f.Height);
                if (cfg.Crop.Width > 0 && frameRect.Contains(cfg.Crop))
                {
                    if (cfg.Crop != _lastCrop)
                    {
                        _reader.Reset(); // re-find the vertical offset for the new crop
                        _lastCrop = cfg.Crop;
                    }
                    ExtractBand(f, cfg.Crop);
                    string read = _reader.Read(_band, BandWidth, BandRows);
                    string note = _tracker.HandleReads(read, read, cfg.Route);
                    if (note != null)
                        NoteReady?.Invoke(note);
                }
            }

            if (WantSnapshots && now - _lastSnapshot >= SnapshotInterval)
            {
                _lastSnapshot = now;
                CopySnapshot(f);
                SnapshotReady?.Invoke();
            }
        }

        /// <summary>UI thread: copies the latest snapshot (B,G,R,x packed,
        /// stride = width*4) into the caller's buffer.</summary>
        public bool TakeSnapshot(ref byte[] buffer, out int width, out int height)
        {
            lock (_snapshotLock)
            {
                width = _snapshotW;
                height = _snapshotH;
                if (width == 0)
                    return false;
                int n = width * height * 4;
                if (buffer == null || buffer.Length != n)
                    buffer = new byte[n];
                Buffer.BlockCopy(_snapshot, 0, buffer, 0, n);
                return true;
            }
        }

        private void CopySnapshot(Frame f)
        {
            lock (_snapshotLock)
            {
                int rowBytes = f.Width * 4;
                if (_snapshot.Length != rowBytes * f.Height)
                    _snapshot = new byte[rowBytes * f.Height];
                fixed (byte* dst = _snapshot)
                {
                    for (int y = 0; y < f.Height; y++)
                    {
                        byte* s = f.Data + (long)y * f.Stride;
                        byte* d = dst + (long)y * rowBytes;
                        if (!f.RgbOrder)
                            Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                        else
                            for (int x = 0; x < rowBytes; x += 4)
                            {
                                d[x] = s[x + 2]; d[x + 1] = s[x + 1]; d[x + 2] = s[x]; d[x + 3] = 255;
                            }
                    }
                }
                _snapshotW = f.Width;
                _snapshotH = f.Height;
            }
        }

        /// <summary>Fills _band (BandRows x BandWidth luma) from the frame,
        /// mapping the crop onto the reference capture size. 1:1 when the
        /// game picture is the reference size; bilinear for small scale
        /// changes; box-averaged when shrinking a lot (e.g. a 1080p capture).</summary>
        private void ExtractBand(Frame f, Rectangle crop)
        {
            double sx = crop.Width / (double)DigitReader.ReferenceCropWidth;
            double sy = crop.Height / (double)DigitReader.ReferenceCropHeight;
            double x0 = crop.X + BattleTanx.ScoreFraction.X * crop.Width;
            int top = DigitReader.BandTop - Margin;
            bool average = sx > 1.5 || sy > 1.5;
            for (int r = 0; r < BandRows; r++)
            {
                for (int c = 0; c < BandWidth; c++)
                {
                    byte v;
                    if (average)
                    {
                        int xa = (int)(x0 + c * sx), xb = Math.Max(xa + 1, (int)Math.Ceiling(x0 + (c + 1) * sx));
                        int ya = (int)(crop.Y + (top + r) * sy), yb = Math.Max(ya + 1, (int)Math.Ceiling(crop.Y + (top + r + 1) * sy));
                        int sum = 0, n = 0;
                        for (int y = ya; y < yb; y++)
                            for (int x = xa; x < xb; x++, n++)
                                sum += Luma(f, x, y);
                        v = (byte)(sum / n);
                    }
                    else
                    {
                        double fx = x0 + (c + 0.5) * sx - 0.5, fy = crop.Y + (top + r + 0.5) * sy - 0.5;
                        int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
                        double tx = fx - ix, ty = fy - iy;
                        double a = Luma(f, ix, iy), b = Luma(f, ix + 1, iy);
                        double cc = Luma(f, ix, iy + 1), d = Luma(f, ix + 1, iy + 1);
                        double val = (a * (1 - tx) + b * tx) * (1 - ty) + (cc * (1 - tx) + d * tx) * ty;
                        v = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
                    }
                    _band[r * BandWidth + c] = v;
                }
            }
        }

        private static int Luma(Frame f, int x, int y)
        {
            x = Math.Clamp(x, 0, f.Width - 1);
            y = Math.Clamp(y, 0, f.Height - 1);
            byte* p = f.Data + (long)y * f.Stride + x * 4;
            int r = f.RgbOrder ? p[0] : p[2], b = f.RgbOrder ? p[2] : p[0];
            return (b * 114 + p[1] * 587 + r * 299) / 1000;
        }
    }
}
