using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using BgaSpnfdr.Capture;

namespace BgaSpnfdr
{
    /// <summary>
    /// Turns captured frames into notes. Runs on PipeWire's thread: for each
    /// frame (at most ~30 a second) it resamples the score band to the
    /// reference size (Core/ScoreBand), reads it with Core/DigitReader and
    /// feeds Core/ScoreTracker.
    /// The UI thread only swaps in new settings (crop, route) and, while it
    /// needs them (preview, auto-crop), takes ~10 full-frame snapshots a
    /// second.
    /// </summary>
    internal sealed unsafe class FrameProcessor
    {
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
        private readonly byte[] _band = new byte[ScoreBand.Width * ScoreBand.Rows];
        private byte[] _rows = Array.Empty<byte>(); // frame rows under the band
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
                    ScoreBand.RowsNeeded(cfg.Crop, f.Height, out int first, out int last);
                    int n = (last - first + 1) * f.Stride;
                    if (_rows.Length < n)
                        _rows = new byte[n];
                    Marshal.Copy((IntPtr)(f.Data + (long)first * f.Stride), _rows, 0, n);
                    ScoreBand.Fill(_rows, f.Stride, first, last, f.Width, cfg.Crop, f.RgbOrder, _band);
                    string read = _reader.Read(_band, ScoreBand.Width, ScoreBand.Rows);
                    string note = _tracker.HandleRead(read, cfg.Route);
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
    }
}
