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
        private Rectangle _lastCrop, _lastMoved;
        private long _lastRead, _lastSnapshot;

        /// <summary>Set by the UI while it wants full-frame snapshots.</summary>
        public volatile bool WantSnapshots;
        /// <summary>PipeWire thread: a newly confirmed score's note.</summary>
        public Action<string> NoteReady;
        /// <summary>PipeWire thread: a new snapshot can be taken.</summary>
        public Action SnapshotReady;
        /// <summary>PipeWire thread: the score was read starting left of
        /// the crop's edge or well off the middle of its band, so the crop
        /// belongs here (ScoreBand.Recenter).</summary>
        public Action<Rectangle> CropMoved;

        private readonly object _snapshotLock = new object();
        private byte[] _snapshot = Array.Empty<byte>();
        private int _snapshotW, _snapshotH;
        private bool _snapshotNew; // not taken yet

        public volatile int FrameWidth, FrameHeight;
        public string Status => _tracker.Status;

        /// <summary>The score that stands; -1 before the first.</summary>
        public long Score => _tracker.Score;

        /// <summary>When (Environment.TickCount64) the score was last read
        /// where the crop says it is. The UI sets it when capture starts.</summary>
        public long LastScoreSeen;

        /// <summary>UI thread: whether the status text is being shown.</summary>
        public bool WantStatus { set => _tracker.WantStatus = value; }

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
                // a crop worked out from the score can reach a pixel or two
                // past the frame: what matters is that the score band is in it
                int first = 0, last = -1, left = 0, right = -1;
                if (cfg.Crop.Width > 0 && cfg.Crop.Height > 0)
                {
                    ScoreBand.RowsNeeded(cfg.Crop, f.Height, out first, out last);
                    ScoreBand.ColumnsNeeded(cfg.Crop, f.Width, out left, out right);
                }
                if (last >= first && right >= left)
                {
                    if (cfg.Crop != _lastCrop)
                    {
                        _reader.Reset(); // re-find the vertical offset for the new crop
                        _lastCrop = cfg.Crop;
                    }
                    int n = (last - first + 1) * f.Stride;
                    if (_rows.Length < n)
                        _rows = new byte[n];
                    // only the columns under the band: the rest of each row
                    // is never read (ScoreBand.Fill)
                    long columns = (right - left + 1) * 4L;
                    fixed (byte* rows = _rows)
                        for (int y = first; y <= last; y++)
                            Buffer.MemoryCopy(f.Data + (long)y * f.Stride + left * 4,
                                rows + (long)(y - first) * f.Stride + left * 4, columns, columns);
                    ScoreBand.Fill(_rows, f.Stride, first, last, f.Width, cfg.Crop, f.RgbOrder, _band);
                    string read = _reader.Read(_band, ScoreBand.Width, ScoreBand.Rows);
                    string note = _tracker.HandleRead(read, cfg.Route, _reader.LeftMatch);
                    if (note != null)
                        NoteReady?.Invoke(note);
                    if (_tracker.ReadStands)
                    {
                        LastScoreSeen = Environment.TickCount64;
                        Rectangle moved = ScoreBand.Recenter(cfg.Crop, _reader.FoundFirst, _reader.FoundOffset);
                        if (moved != cfg.Crop && moved != _lastMoved)
                        {
                            _lastMoved = moved;
                            CropMoved?.Invoke(moved);
                        }
                    }
                }
            }

            if (WantSnapshots && now - _lastSnapshot >= SnapshotInterval)
            {
                _lastSnapshot = now;
                CopySnapshot(f);
                SnapshotReady?.Invoke();
            }
            else if (!WantSnapshots && _snapshot.Length > 0)
            {
                // nobody is looking at whole pictures any more: the buffer
                // for them (8 MB at 1080p) isn't kept
                lock (_snapshotLock)
                {
                    _snapshot = Array.Empty<byte>();
                    _snapshotNew = false;
                }
            }
        }

        /// <summary>UI thread: hands over the newest snapshot (B,G,R,x
        /// packed, stride = width*4) in exchange for the caller's buffer,
        /// which the next one is written into (nothing is copied). False,
        /// with the caller's buffer left alone, if there is no snapshot
        /// newer than the one last taken.</summary>
        public bool TakeSnapshot(ref byte[] buffer, out int width, out int height)
        {
            lock (_snapshotLock)
            {
                width = _snapshotW;
                height = _snapshotH;
                if (!_snapshotNew)
                    return false;
                _snapshotNew = false;
                byte[] newest = _snapshot;
                _snapshot = buffer ?? Array.Empty<byte>();
                buffer = newest;
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
                _snapshotNew = true;
            }
        }
    }
}
