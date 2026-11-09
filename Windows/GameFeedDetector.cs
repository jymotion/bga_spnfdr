using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BgaSpnfdr
{
    /// <summary>
    /// Finds the gameplay picture inside a captured window by watching a
    /// budget of frames. Primary signal: MOTION — the gameplay feed is the one
    /// region that changes constantly, which singles it out even when embedded
    /// in a busy window (e.g. the OBS main UI). Fallback signal when nothing
    /// moves: brightness (trims black letterbox bars), accumulated across
    /// frames because a single dark scene edge can look like a bar.
    /// </summary>
    internal sealed class GameFeedDetector
    {
        private const int GridStep = 2;         // motion grid downsample factor
        private const int MotionDelta = 15;     // luma change that counts as motion
        private const int MinMotionHits = 3;    // frames a cell must move to count
        private const int LumaThreshold = 18;   // video black bars are ~0

        private readonly int _frameBudget;
        private int _framesLeft;
        private int[] _rowMax;
        private int[] _colMax;
        private byte[] _prevLuma;
        private byte[] _motionHits;
        private int _gridW, _gridH;
        private Rectangle _client;

        public GameFeedDetector(int frameBudget)
        {
            _frameBudget = frameBudget;
        }

        public int FramesLeft => _framesLeft;

        /// <summary>Set once Feed returns true. Empty = detection failed.</summary>
        public Rectangle Result { get; private set; }

        public bool FromMotion { get; private set; }

        /// <summary>Accumulates one frame; returns true when finished.</summary>
        public bool Feed(Bitmap capture, Rectangle client)
        {
            if (client.Width < 120 || client.Height < 90)
            {
                Result = Rectangle.Empty;
                return true;
            }
            if (_rowMax == null || client != _client)
            {
                _client = client; // (re)start if the window changed shape
                _rowMax = new int[client.Height];
                _colMax = new int[client.Width];
                _gridW = (client.Width + GridStep - 1) / GridStep;
                _gridH = (client.Height + GridStep - 1) / GridStep;
                _prevLuma = null;
                _motionHits = new byte[_gridW * _gridH];
                _framesLeft = _frameBudget;
            }

            Accumulate(capture, client);
            if (--_framesLeft > 0)
                return false;

            Rectangle box = LargestMotionBox();
            FromMotion = !box.IsEmpty;
            if (box.IsEmpty)
                box = BrightnessTrimBox();
            Result = box;
            return true;
        }

        /// <summary>One pass over the client area: per-row/column bright-pixel
        /// counts (kept as per-line maxima) and downsampled frame-to-frame
        /// motion hits.</summary>
        private void Accumulate(Bitmap capture, Rectangle client)
        {
            var data = capture.LockBits(client, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var px = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                var colCounts = new int[client.Width];
                var curLuma = new byte[_gridW * _gridH];
                for (int y = 0; y < client.Height; y++)
                {
                    int row = y * data.Stride;
                    int rowCount = 0;
                    bool gridRow = y % GridStep == 0;
                    int gy = y / GridStep;
                    for (int x = 0; x < client.Width; x++)
                    {
                        int o = row + x * 4;
                        int luma = (px[o] * 114 + px[o + 1] * 587 + px[o + 2] * 299) / 1000;
                        if (luma > LumaThreshold)
                        {
                            rowCount++;
                            colCounts[x]++;
                        }
                        if (gridRow && x % GridStep == 0)
                        {
                            int cell = gy * _gridW + x / GridStep;
                            curLuma[cell] = (byte)luma;
                            if (_prevLuma != null
                                && Math.Abs(luma - _prevLuma[cell]) > MotionDelta
                                && _motionHits[cell] < byte.MaxValue)
                                _motionHits[cell]++;
                        }
                    }
                    _rowMax[y] = Math.Max(_rowMax[y], rowCount);
                }
                for (int x = 0; x < client.Width; x++)
                    _colMax[x] = Math.Max(_colMax[x], colCounts[x]);
                _prevLuma = curLuma;
            }
            finally
            {
                capture.UnlockBits(data);
            }
        }

        /// <summary>Bounding box (capture coords) of the largest connected
        /// region that kept moving. Empty if nothing plausible moved.</summary>
        private Rectangle LargestMotionBox()
        {
            var visited = new bool[_motionHits.Length];
            var stack = new Stack<int>();
            Rectangle best = Rectangle.Empty;
            long bestArea = 0;

            for (int start = 0; start < _motionHits.Length; start++)
            {
                if (visited[start] || _motionHits[start] < MinMotionHits)
                    continue;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                long area = 0;
                visited[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int cell = stack.Pop();
                    int cx = cell % _gridW, cy = cell / _gridW;
                    area++;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || nx >= _gridW || ny < 0 || ny >= _gridH)
                                continue;
                            int n = ny * _gridW + nx;
                            if (!visited[n] && _motionHits[n] >= MinMotionHits)
                            {
                                visited[n] = true;
                                stack.Push(n);
                            }
                        }
                    }
                }
                if (area > bestArea)
                {
                    bestArea = area;
                    best = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
                }
            }

            if (best.IsEmpty)
                return Rectangle.Empty;
            var full = new Rectangle(
                _client.X + best.X * GridStep,
                _client.Y + best.Y * GridStep,
                best.Width * GridStep,
                best.Height * GridStep);
            // must look like a game picture: big enough and near-4:3
            double aspect = (double)full.Width / full.Height;
            if (full.Width < 120 || full.Height < 90 || aspect < 1.0 || aspect > 1.85)
                return Rectangle.Empty;
            return full;
        }

        /// <summary>Fallback for static feeds: trims black letterbox bars from
        /// the client area using the accumulated brightness counts.</summary>
        private Rectangle BrightnessTrimBox()
        {
            // bars aren't perfectly black on real captures; require a small
            // fraction of bright pixels before calling a line "picture"
            int minRow = Math.Max(4, _client.Width / 50);
            int minCol = Math.Max(4, _client.Height / 50);
            int top = Array.FindIndex(_rowMax, c => c >= minRow);
            int bottom = Array.FindLastIndex(_rowMax, c => c >= minRow);
            int left = Array.FindIndex(_colMax, c => c >= minCol);
            int right = Array.FindLastIndex(_colMax, c => c >= minCol);
            if (top < 0 || left < 0 || bottom - top < 90 || right - left < 120)
                return Rectangle.Empty;
            return new Rectangle(
                _client.X + left, _client.Y + top, right - left + 1, bottom - top + 1);
        }
    }
}
