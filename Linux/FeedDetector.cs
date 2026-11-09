using System;
using System.Collections.Generic;
using System.Drawing;

namespace BgaSpnfdr
{
    /// <summary>
    /// Finds the gameplay picture inside a captured window by watching a
    /// budget of frames. Port of Windows/GameFeedDetector.cs (same constants
    /// and steps; keep the two in step when tuning) that reads raw frame
    /// bytes instead of a GDI+ Bitmap, so the Windows build stays untouched.
    /// Primary signal: MOTION (the gameplay feed is the one region that keeps
    /// changing). Fallback when nothing moves: brightness (trims black bars).
    /// </summary>
    internal sealed class FeedDetector
    {
        private const int GridStep = 2;         // motion grid downsample factor
        private const int MotionDelta = 15;     // luma change that counts as motion
        private const int MinMotionHits = 3;    // frames a cell must move to count
        private const int LumaThreshold = 18;   // video black bars are ~0

        private readonly int _frameBudget;
        private int _framesLeft;
        private int[] _rowMax, _colMax;
        private byte[] _prevLuma, _motionHits;
        private int _gridW, _gridH, _w, _h;

        public FeedDetector(int frameBudget)
        {
            _frameBudget = frameBudget;
        }

        public int FramesLeft => _framesLeft;

        /// <summary>Set once Feed returns true. Empty = detection failed.</summary>
        public Rectangle Result { get; private set; }

        /// <summary>Accumulates one frame (4 bytes per pixel, R and B order
        /// given by rgbOrder); returns true when finished.</summary>
        public bool Feed(byte[] px, int width, int height, int stride, bool rgbOrder)
        {
            if (width < 120 || height < 90)
            {
                Result = Rectangle.Empty;
                return true;
            }
            if (_rowMax == null || width != _w || height != _h)
            {
                _w = width; _h = height; // (re)start if the frame changed size
                _rowMax = new int[height];
                _colMax = new int[width];
                _gridW = (width + GridStep - 1) / GridStep;
                _gridH = (height + GridStep - 1) / GridStep;
                _prevLuma = null;
                _motionHits = new byte[_gridW * _gridH];
                _framesLeft = _frameBudget;
            }

            Accumulate(px, stride, rgbOrder);
            if (--_framesLeft > 0)
                return false;

            Rectangle box = LargestMotionBox();
            if (box.IsEmpty)
                box = BrightnessTrimBox();
            Result = box;
            return true;
        }

        private void Accumulate(byte[] px, int stride, bool rgbOrder)
        {
            int ri = rgbOrder ? 0 : 2, bi = rgbOrder ? 2 : 0;
            var colCounts = new int[_w];
            var curLuma = new byte[_gridW * _gridH];
            for (int y = 0; y < _h; y++)
            {
                int row = y * stride, rowCount = 0;
                bool gridRow = y % GridStep == 0;
                int gy = y / GridStep;
                for (int x = 0; x < _w; x++)
                {
                    int o = row + x * 4;
                    int luma = (px[o + bi] * 114 + px[o + 1] * 587 + px[o + ri] * 299) / 1000;
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
            for (int x = 0; x < _w; x++)
                _colMax[x] = Math.Max(_colMax[x], colCounts[x]);
            _prevLuma = curLuma;
        }

        /// <summary>Bounding box of the largest connected region that kept
        /// moving. Empty if nothing plausible moved.</summary>
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
                if (area > bestArea)
                {
                    bestArea = area;
                    best = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
                }
            }
            if (best.IsEmpty)
                return Rectangle.Empty;
            var full = new Rectangle(best.X * GridStep, best.Y * GridStep, best.Width * GridStep, best.Height * GridStep);
            full.Intersect(new Rectangle(0, 0, _w, _h));
            // must look like a game picture: big enough and near-4:3
            double aspect = (double)full.Width / full.Height;
            if (full.Width < 120 || full.Height < 90 || aspect < 1.0 || aspect > 1.85)
                return Rectangle.Empty;
            return full;
        }

        /// <summary>Fallback for static feeds: trims black bars.</summary>
        private Rectangle BrightnessTrimBox()
        {
            int minRow = Math.Max(4, _w / 50), minCol = Math.Max(4, _h / 50);
            int top = Array.FindIndex(_rowMax, c => c >= minRow);
            int bottom = Array.FindLastIndex(_rowMax, c => c >= minRow);
            int left = Array.FindIndex(_colMax, c => c >= minCol);
            int right = Array.FindLastIndex(_colMax, c => c >= minCol);
            if (top < 0 || left < 0 || bottom - top < 90 || right - left < 120)
                return Rectangle.Empty;
            return new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }
    }
}
