using System;
using System.Collections.Generic;
using System.Drawing;

namespace BgaSpnfdr
{
    /// <summary>
    /// Finds the game picture in what is captured, which may hold much more
    /// than the game: window chrome, a stream layout, a webcam, animations.
    /// Shared by both apps, which feed it whole frames (~10 a second).
    ///
    /// Two jobs, because the program is usually set up on the game's menu,
    /// where there is no score to go by:
    ///
    ///  - Detect: no score needed. Watches a few seconds for what moves,
    ///    takes the largest moving region and grows it to the picture's own
    ///    edge (the black around it, or a straight line where a backdrop
    ///    starts): a still sky or a mostly still menu doesn't move, but it is
    ///    part of the picture. Nothing is assumed about the picture's shape,
    ///    so a stretched one is found the same way. The user sees the result
    ///    in the preview and can correct it by hand (Snap tidies a hand-drawn
    ///    box onto the picture's edges).
    ///
    ///  - Check: once a score is on screen, looks for it with Core/ScoreFinder
    ///    near the crop there is and, failing that, anywhere in the frame.
    ///    Where the score really is fixes the picture far more exactly than
    ///    any box, and tells the game from everything else that moves: only
    ///    the game has a BattleTanx score in it. A find must repeat on a
    ///    later frame before it is believed (Feed then returns the picture,
    ///    with Refining set); a few more over the next seconds settle its
    ///    height, which one frame can put 3% off (Feed returns again).
    /// </summary>
    internal sealed class GameFinder
    {
        private const int GridStep = 2;         // motion grid downsample factor
        private const int MotionDelta = 15;     // luma change that counts as motion
        private const int MinMotionHits = 3;    // frames a cell must move to count
        private const int LumaThreshold = 18;   // video black bars are ~0
        private const int EdgeStep = 20;        // luma step that makes an edge
        private const int Candidates = 3;       // moving regions tried against the score

        // Time spent on the wider search per frame while checking, so
        // that reading the score isn't held up; and the size of one piece
        // of it (reader positions, about that long on a slow PC)
        private const double CheckMilliseconds = 12;
        private const int CheckChunk = 30000;

        // While checking: the crop itself is tried this often (in frames)
        private const int NearEvery = 10;
        // A find is sampled this many times, this many frames apart
        // (more if the picture's left edge hasn't shown by then: a dark
        // scene hides it against a black border)
        private const int Samples = 7, SampleGap = 3, SampleTries = 14, EdgeTries = 40;

        private enum Stage { Motion, Near, Wide, Anywhere, Done }

        private readonly bool _detect;
        private readonly int _frameBudget;
        private readonly Rectangle _crop;      // Check: the crop to check
        private Stage _stage;
        private int _framesLeft;
        private int[] _rowMax, _colMax;
        private byte[] _prevLuma, _motionHits, _maxLuma;
        private int _gridW, _gridH;
        private Rectangle _area;
        private int _width, _height;

        private readonly ScoreFinder _finder = new ScoreFinder();
        private readonly List<ScoreFinder.Probe> _plan = new List<ScoreFinder.Probe>();
        private int _next;
        private readonly List<ScoreFinder.Hit> _finds = new List<ScoreFinder.Hit>();
        private readonly List<int> _findLefts = new List<int>(); // the picture's left edge, where one could be seen
        private readonly List<int> _findTops = new List<int>();  // its top edge, where one could be seen
        private byte[] _px;                                      // the frame being fed
        private int _stride;
        private bool _rgbOrder;
        private int _sampleTries, _sinceSample;
        private byte[] _glance;  // a coarse copy of the last frame checked
        private bool _reported;  // Result has been handed out once already
        private int _frame;      // frames fed while checking

        private GameFinder(bool detect, int frames, Rectangle crop)
        {
            _detect = detect;
            _frameBudget = frames;
            _crop = crop;
            _stage = detect ? Stage.Motion : Stage.Near;
        }

        /// <summary>Finds the picture by what moves over this many frames.</summary>
        public static GameFinder Detect(int frames) => new GameFinder(true, frames, Rectangle.Empty);

        /// <summary>Looks for the score: near this crop first, then anywhere.</summary>
        public static GameFinder Check(Rectangle crop) => new GameFinder(false, 0, crop);

        /// <summary>Frames Detect still wants to watch (for a countdown).</summary>
        public int FramesLeft => _framesLeft;

        /// <summary>Set once Feed returns true. Empty = nothing found.</summary>
        public Rectangle Result;

        /// <summary>Set once Feed returns true: the score was found, and
        /// Result is the picture it belongs to.</summary>
        public bool Verified;

        /// <summary>Set when Feed returns true with a score just found: keep
        /// feeding, and it returns true once more with a steadier Result.</summary>
        public bool Refining;

        /// <summary>Takes one frame (4 bytes per pixel, R and B order given
        /// by rgbOrder); area is the part of it that can hold the game (a
        /// window's client area, or all of it). True when finished.</summary>
        public bool Feed(byte[] px, int width, int height, int stride, bool rgbOrder, Rectangle area)
        {
            area.Intersect(new Rectangle(0, 0, width, height));
            if (area.Width < 120 || area.Height < 90)
            {
                Result = _crop;
                return true;
            }
            if (_rowMax == null || area != _area || width != _width || height != _height)
            {
                if (_rowMax != null && !_detect)
                {
                    Result = _crop; // the frame changed under a check: the caller starts over
                    return true;
                }
                _area = area; _width = width; _height = height; // (re)start if the frame changed
                _rowMax = new int[area.Height];
                _colMax = new int[area.Width];
                _gridW = (area.Width + GridStep - 1) / GridStep;
                _gridH = (area.Height + GridStep - 1) / GridStep;
                _prevLuma = null;
                _motionHits = new byte[_gridW * _gridH];
                _maxLuma = new byte[_gridW * _gridH];
                _framesLeft = _frameBudget;
                _plan.Clear();
                _next = 0;
                _finds.Clear();
                _findLefts.Clear();
                _findTops.Clear();
                if (_detect)
                    _stage = Stage.Motion;
            }

            _finder.SetFrame(px, stride, width, height, rgbOrder);
            _px = px; _stride = stride; _rgbOrder = rgbOrder;
            if (_finds.Count > 0)
                return Sample();
            if (_stage == Stage.Motion)
            {
                Accumulate(px, stride, rgbOrder);
                if (--_framesLeft > 0)
                    return false;
                // what moved, best guess first; a score already on screen
                // settles it at once, otherwise the first guess stands
                var boxes = MotionBoxes(px, stride, rgbOrder);
                if (boxes.Count == 0)
                {
                    Rectangle bright = BrightnessTrimBox();
                    if (!bright.IsEmpty)
                        boxes.Add(bright);
                }
                Result = boxes.Count > 0 ? boxes[0] : Rectangle.Empty;
                _stage = Stage.Done;
                foreach (Rectangle box in boxes)
                    ScoreFinder.PlanNear(box, 0.06, 2, int.MaxValue, _plan);
                return !Look(double.MaxValue);
            }
            if (_stage == Stage.Done)
                return true; // Detect, its boxes tried: the first guess stands

            // Check: the crop itself about once a second, so a score that
            // comes up is caught promptly; the wider search in between
            if (_frame++ % NearEvery == 0)
            {
                var near = new List<ScoreFinder.Probe>();
                ScoreFinder.PlanNear(_crop, 0.06, 2, int.MaxValue, near);
                foreach (ScoreFinder.Probe probe in near)
                    if (Try(probe))
                        return false;
                return false;
            }
            // nothing on screen changes (the menu): no score will have come up
            if (Changed(px, stride, rgbOrder))
                Look(CheckMilliseconds);
            return false;
        }

        /// <summary>Whether this frame differs from the last one fed, going
        /// by a coarse grid of its pixels.</summary>
        private bool Changed(byte[] px, int stride, bool rgbOrder)
        {
            const int across = 48, down = 27;
            if (_glance == null)
                _glance = new byte[across * down];
            int ri = rgbOrder ? 0 : 2, bi = rgbOrder ? 2 : 0;
            bool changed = false;
            for (int j = 0; j < down; j++)
                for (int i = 0; i < across; i++)
                {
                    int x = _area.X + (2 * i + 1) * _area.Width / (2 * across), y = _area.Y + (2 * j + 1) * _area.Height / (2 * down);
                    byte luma = (byte)ScoreBand.Luma(px, stride, 0, _height - 1, _width, x, y, ri, bi);
                    if (Math.Abs(luma - _glance[j * across + i]) > MotionDelta)
                        changed = true;
                    _glance[j * across + i] = luma;
                }
            return changed;
        }

        // ---------- looking for the score ----------

        /// <summary>Works through the wider plan for this frame. True if it
        /// found what may be a score (sampled on the next frames).</summary>
        private bool Look(double milliseconds)
        {
            long until = milliseconds >= 1e9 ? long.MaxValue
                : System.Diagnostics.Stopwatch.GetTimestamp() + (long)(milliseconds * System.Diagnostics.Stopwatch.Frequency / 1000);
            do
            {
                if (_next >= _plan.Count)
                {
                    if (_detect)
                        return false;
                    NextPlan();
                    if (_plan.Count == 0)
                        return false;
                }
                if (Try(_plan[_next++]))
                    return true;
            }
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until);
            return false;
        }

        private bool Try(ScoreFinder.Probe probe)
        {
            if (!_finder.Run(probe, out ScoreFinder.Hit hit) || !_finder.Tune(ref hit, probe.MinDigits))
                return false;
            _finds.Add(hit);
            _findLefts.Clear();
            _findTops.Clear();
            NoteEdges(hit);
            _sampleTries = 0;
            _sinceSample = 0;
            _reported = false;
            return true;
        }

        /// <summary>Check's wider plans, in turn: around the crop for one
        /// that is up to 15% out, then the whole area.</summary>
        private void NextPlan()
        {
            _plan.Clear();
            _next = 0;
            if (_stage != Stage.Wide)
            {
                ScoreFinder.PlanNear(_crop, 0.15, 2, CheckChunk, _plan);
                _stage = Stage.Wide;
            }
            else
            {
                double tallest = Math.Min(_area.Height, _area.Width / (PictureShape * 0.97));
                ScoreFinder.PlanAnywhere(_area, Math.Max(200, 0.35 * _area.Height), tallest, CheckChunk, _plan);
                _stage = Stage.Anywhere;
            }
        }

        private const double PictureShape = DigitReader.ReferenceCropWidth / (double)DigitReader.ReferenceCropHeight;

        /// <summary>A find has to show up again on a later frame, in the same
        /// place at the same size: a chance lookalike doesn't. Then it is
        /// believed (true, with Refining), and sampled a few more times: the
        /// middle one's height is used, as a single frame's can be 3% off.</summary>
        private bool Sample()
        {
            if (++_sinceSample < SampleGap)
                return false;
            _sinceSample = 0;
            ScoreFinder.Hit first = _finds[0];
            var again = new ScoreFinder.Probe
            {
                Sx = first.Sx, Sy = first.Sy,
                X0 = first.Left - 30 * first.Sx, Y0 = first.Top - 4 * first.Sy,
                Cols = (int)Math.Ceiling((first.Right - first.Left) / first.Sx) + 60,
                Rows = DigitReader.BandHeight + 8,
                MinDigits = 2,
                HintX = first.HintX,
            };
            if (_finder.Run(again, out ScoreFinder.Hit hit) && _finder.Tune(ref hit, 2)
                && Math.Abs(hit.Top - first.Top) <= 2 * first.Sy
                && Math.Abs(hit.Sy / first.Sy - 1) <= 0.08 && Math.Abs(hit.Sx / first.Sx - 1) <= 0.04)
            {
                if (_finds.Count < Samples)
                    _finds.Add(hit);
                NoteEdges(hit);
            }
            _sampleTries++;
            bool last = _finds.Count >= Samples
                ? _findLefts.Count >= 2 || _sampleTries >= EdgeTries
                : _sampleTries >= SampleTries;
            if (_finds.Count < 2)
            {
                if (last)
                {
                    _finds.Clear(); // not seen again: carry on looking
                    _findLefts.Clear();
                }
                return false;
            }
            if (_reported && !last)
                return false;

            var bySize = new List<ScoreFinder.Hit>(_finds);
            bySize.Sort((a, b) => a.Sy.CompareTo(b.Sy));
            var lefts = new List<int>(_findLefts);
            lefts.Sort();
            ScoreFinder.Hit middle = bySize[bySize.Count / 2];
            // the width from the longest score seen: its digits' spacing is
            // the longest ruler
            ScoreFinder.Hit latest = _finds[_finds.Count - 1];
            foreach (ScoreFinder.Hit f in _finds)
                if (f.Text.Length > middle.Text.Length || (f.Text.Length == middle.Text.Length && middle.Sx != f.Sx && f.Text.Length > latest.Text.Length))
                    middle.Sx = f.Sx;
            // The height the digits match best at can be several percent
            // off. The picture's top edge is a far longer ruler: the digits
            // start 423 reference rows below it. Used if frames agree on it.
            double height = middle.Sy;
            if (_findTops.Count >= 2 && _findTops.Count * 2 >= _finds.Count)
            {
                var tops = new List<int>(_findTops);
                tops.Sort();
                if (tops[tops.Count - 1] - tops[0] <= 3)
                    height = (latest.Top - tops[tops.Count / 2]) / DigitReader.BandTop;
            }
            // that height, around where the digits were last seen
            middle.Top = latest.Top + DigitReader.BandHeight / 2.0 * (latest.Sy - height);
            middle.Sy = height;
            middle.Left = latest.Left; middle.Right = latest.Right;
            Rectangle crop = _finder.Crop(middle, out _);
            if (lefts.Count >= 2)
                crop.X = lefts[lefts.Count / 2]; // the edge as most frames that show one have it
            Result = crop;
            Verified = true;
            Refining = !last;
            _reported = true;
            if (last)
            {
                _finds.Clear();
                _findLefts.Clear();
                _findTops.Clear();
                _stage = Stage.Done;
                _plan.Clear();
                _next = 0;
            }
            return true;
        }

        /// <summary>Notes where the picture's left and top edges are in this
        /// frame, going by a find, if they can be seen. The top one is a
        /// straight edge across the picture, within 8% of the picture's
        /// height of where the find's own height puts it.</summary>
        private void NoteEdges(ScoreFinder.Hit hit)
        {
            Rectangle crop = _finder.Crop(hit, out bool leftSeen);
            if (leftSeen)
                _findLefts.Add(crop.X);

            double h = DigitReader.ReferenceCropHeight * hit.Sy, w = DigitReader.ReferenceCropWidth * hit.Sx;
            int expected = (int)Math.Round(hit.Top - DigitReader.BandTop * hit.Sy), reach = (int)(0.08 * h);
            // columns sure to be inside the picture: from the score rightwards
            int from = (int)hit.Left, to = (int)Math.Min(_width - 1, hit.Left + 0.6 * w);
            int best = -1;
            double bestShare = 0.5;
            for (int d = 0; d <= reach; d++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    double share = StraightEdge(_px, _stride, _width, _height, _rgbOrder, true, expected + sign * d, from, to);
                    if (share > bestShare + 0.05)
                    {
                        bestShare = share;
                        best = expected + sign * d;
                    }
                }
            if (best >= 0)
                _findTops.Add(best);
        }

        // ---------- what moves ----------

        private void Accumulate(byte[] px, int stride, bool rgbOrder)
        {
            int ri = rgbOrder ? 0 : 2, bi = rgbOrder ? 2 : 0;
            var colCounts = new int[_area.Width];
            var curLuma = new byte[_gridW * _gridH];
            for (int y = 0; y < _area.Height; y++)
            {
                int row = (y + _area.Y) * stride + _area.X * 4, rowCount = 0;
                bool gridRow = y % GridStep == 0;
                int gy = y / GridStep;
                for (int x = 0; x < _area.Width; x++)
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
                        if (luma > _maxLuma[cell])
                            _maxLuma[cell] = (byte)luma;
                        if (_prevLuma != null
                            && Math.Abs(luma - _prevLuma[cell]) > MotionDelta
                            && _motionHits[cell] < byte.MaxValue)
                            _motionHits[cell]++;
                    }
                }
                _rowMax[y] = Math.Max(_rowMax[y], rowCount);
            }
            for (int x = 0; x < _area.Width; x++)
                _colMax[x] = Math.Max(_colMax[x], colCounts[x]);
            _prevLuma = curLuma;
        }

        /// <summary>The regions that kept moving, each grown to the picture
        /// around it, largest first. Empty if nothing plausible moved.</summary>
        private List<Rectangle> MotionBoxes(byte[] px, int stride, bool rgbOrder)
        {
            var visited = new bool[_motionHits.Length];
            var stack = new Stack<int>();
            var found = new List<KeyValuePair<long, Rectangle>>();
            for (int start = 0; start < _motionHits.Length; start++)
            {
                if (visited[start] || _motionHits[start] < MinMotionHits)
                    continue;
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                long cells = 0;
                visited[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int cell = stack.Pop();
                    int cx = cell % _gridW, cy = cell / _gridW;
                    cells++;
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
                found.Add(new KeyValuePair<long, Rectangle>(cells, Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1)));
            }
            found.Sort((a, b) => b.Key.CompareTo(a.Key));

            var boxes = new List<Rectangle>();
            foreach (KeyValuePair<long, Rectangle> region in found)
            {
                Rectangle g = Grow(region.Value, px, stride, rgbOrder);
                var box = new Rectangle(_area.X + g.X * GridStep, _area.Y + g.Y * GridStep, g.Width * GridStep, g.Height * GridStep);
                box.Intersect(_area);
                // must look like a game picture: big enough, wider than tall
                // (anything from 4:3 to a stretched 16:9, with room to spare)
                double aspect = (double)box.Width / Math.Max(1, box.Height);
                if (box.Width < 120 || box.Height < 90 || aspect < 1.0 || aspect > 2.1 || boxes.Contains(box))
                    continue;
                boxes.Add(box);
                if (boxes.Count == Candidates)
                    break;
            }
            return boxes;
        }

        /// <summary>The moving region (in grid cells) can be smaller than the
        /// picture: a sky that holds still, a menu where only the cursor
        /// moves. The picture goes on over everything that was lit at some
        /// point, until the black around it. Where there is no black (a
        /// backdrop around the picture, lit all the way to the frame's edge),
        /// it ends at the outermost straight edge across the box instead: a
        /// border, the start of the backdrop. Outermost, and not trusted over
        /// black, because the picture has straight edges too (a menu's
        /// panels, a horizon).</summary>
        private Rectangle Grow(Rectangle b, byte[] px, int stride, bool rgbOrder)
        {
            // as far as anything is lit
            int l = b.Left, r = b.Right, t = b.Top, m = b.Bottom;
            for (int pass = 0; pass < 3; pass++)
            {
                // a dark menu has rows with next to nothing lit in them: a
                // gap of up to 1.5% of the box is stepped over if the
                // picture goes on beyond it
                int gapY = Math.Max(1, (m - t) * 3 / 200), gapX = Math.Max(1, (r - l) * 3 / 200);
                for (int next; (next = NextLit(true, t - 1, -1, gapY, l, r)) >= 0;) t = next;
                for (int next; (next = NextLit(true, m, 1, gapY, l, r)) >= 0;) m = next + 1;
                for (int next; (next = NextLit(false, l - 1, -1, gapX, t, m)) >= 0;) l = next;
                for (int next; (next = NextLit(false, r, 1, gapX, t, m)) >= 0;) r = next + 1;
            }
            if (l > 0 && t > 0 && r < _gridW && m < _gridH)
                return Rectangle.FromLTRB(l, t, r, m); // black all round

            // sides that ran to the frame's edge found no black: for those,
            // the outermost straight edge between the moving region and
            // there. Each is looked for along the moving region's own extent
            // (or the other sides' edges once found), not along a side that
            // ran away to the frame's edge: that would thin any edge out.
            int el = -1, er = -1, et = -1, em = -1;
            for (int pass = 0; pass < 2; pass++)
            {
                int a = el >= 0 ? el : b.Left, z = er >= 0 ? er : b.Right;
                if (t == 0) et = OutermostEdge(px, stride, rgbOrder, true, b.Top, 0, a, z);
                if (m == _gridH) em = OutermostEdge(px, stride, rgbOrder, true, b.Bottom, _gridH, a, z);
                a = et >= 0 ? et : b.Top; z = em >= 0 ? em : b.Bottom;
                if (l == 0) el = OutermostEdge(px, stride, rgbOrder, false, b.Left, 0, a, z);
                if (r == _gridW) er = OutermostEdge(px, stride, rgbOrder, false, b.Right, _gridW, a, z);
            }
            return Rectangle.FromLTRB(el >= 0 ? el : l, et >= 0 ? et : t, er >= 0 ? er : r, em >= 0 ? em : m);
        }

        /// <summary>The grid line farthest from 'from' towards 'to' with a
        /// straight edge along the span a..b; -1 if there is none.</summary>
        private int OutermostEdge(byte[] px, int stride, bool rgbOrder, bool horizontal, int from, int to, int a, int b)
        {
            int step = to < from ? 1 : -1;
            for (int line = to + step; line != from + step; line += step)
                if (EdgeAcross(px, stride, rgbOrder, horizontal, line, a, b))
                    return line;
            return -1;
        }

        /// <summary>The first lit row (or column) from 'from' going in
        /// direction step, no more than gap unlit ones away; -1 if none.</summary>
        private int NextLit(bool row, int from, int step, int gap, int a, int b)
        {
            int limit = row ? _gridH : _gridW;
            for (int i = from, skipped = 0; i >= 0 && i < limit && skipped <= gap; i += step, skipped++)
                if (row ? RowLit(i, a, b) : ColumnLit(i, a, b))
                    return i;
            return -1;
        }

        private bool RowLit(int gy, int x0, int x1)
        {
            int lit = 0;
            for (int x = x0; x < x1; x++)
                if (_maxLuma[gy * _gridW + x] > LumaThreshold)
                    lit++;
            return lit >= Math.Max(2, (x1 - x0) / 40); // a dark menu is mostly black
        }

        private bool ColumnLit(int gx, int y0, int y1)
        {
            int lit = 0;
            for (int y = y0; y < y1; y++)
                if (_maxLuma[y * _gridW + gx] > LumaThreshold)
                    lit++;
            return lit >= Math.Max(2, (y1 - y0) / 40);
        }

        /// <summary>Whether a straight edge runs along grid line at (a row
        /// boundary if horizontal, else a column boundary) for most of the
        /// span from..to, in this frame.</summary>
        private bool EdgeAcross(byte[] px, int stride, bool rgbOrder, bool horizontal, int at, int from, int to)
        {
            int line = (horizontal ? _area.Y : _area.X) + at * GridStep;
            int a = (horizontal ? _area.X : _area.Y) + from * GridStep, b = (horizontal ? _area.X : _area.Y) + to * GridStep;
            // the grid is 2 px coarse: the edge may sit on either pixel line
            return StraightEdge(px, stride, _width, _height, rgbOrder, horizontal, line, a, b) >= 0.55
                || StraightEdge(px, stride, _width, _height, rgbOrder, horizontal, line - 1, a, b) >= 0.55;
        }

        /// <summary>EdgeShare, but 0 unless the edge is on this line alone:
        /// busy picture content steps in brightness on every line, a border
        /// only on its own.</summary>
        private static double StraightEdge(byte[] px, int stride, int width, int height, bool rgbOrder,
            bool horizontal, int at, int from, int to)
        {
            double share = EdgeShare(px, stride, width, height, rgbOrder, horizontal, at, from, to);
            if (share < 0.5)
                return 0;
            // (a line off the frame's edge says nothing either way)
            int limit = (horizontal ? height : width) - 2;
            double before = at - 4 >= 1 ? EdgeShare(px, stride, width, height, rgbOrder, horizontal, at - 4, from, to) : 1;
            double after = at + 4 <= limit ? EdgeShare(px, stride, width, height, rgbOrder, horizontal, at + 4, from, to) : 1;
            return Math.Min(before, after) <= 0.5 * share ? share : 0;
        }

        /// <summary>The share of the span from..to along which brightness
        /// steps across pixel line at (between the pixels either side).</summary>
        private static double EdgeShare(byte[] px, int stride, int width, int height, bool rgbOrder,
            bool horizontal, int at, int from, int to)
        {
            int ri = rgbOrder ? 0 : 2, bi = rgbOrder ? 2 : 0;
            int limit = horizontal ? height : width;
            if (at < 1 || at > limit - 2 || to <= from)
                return 0;
            int step = Math.Max(1, (to - from) / 160), count = 0, total = 0;
            for (int i = from; i < to; i += step, total++)
            {
                int p = horizontal
                    ? ScoreBand.Luma(px, stride, 0, height - 1, width, i, at - 1, ri, bi)
                    : ScoreBand.Luma(px, stride, 0, height - 1, width, at - 1, i, ri, bi);
                int q = horizontal
                    ? ScoreBand.Luma(px, stride, 0, height - 1, width, i, at + 1, ri, bi)
                    : ScoreBand.Luma(px, stride, 0, height - 1, width, at + 1, i, ri, bi);
                if (Math.Abs(p - q) >= EdgeStep)
                    count++;
            }
            return total == 0 ? 0 : (double)count / total;
        }

        /// <summary>Fallback for static feeds: trims black bars.</summary>
        private Rectangle BrightnessTrimBox()
        {
            int minRow = Math.Max(4, _area.Width / 50), minCol = Math.Max(4, _area.Height / 50);
            int top = Array.FindIndex(_rowMax, c => c >= minRow);
            int bottom = Array.FindLastIndex(_rowMax, c => c >= minRow);
            int left = Array.FindIndex(_colMax, c => c >= minCol);
            int right = Array.FindLastIndex(_colMax, c => c >= minCol);
            if (top < 0 || left < 0 || bottom - top < 90 || right - left < 120)
                return Rectangle.Empty;
            return new Rectangle(_area.X + left, _area.Y + top, right - left + 1, bottom - top + 1);
        }

        // ---------- a hand-drawn box ----------

        /// <summary>Moves each side of a hand-drawn box onto the picture's
        /// edge if one runs close by (within 6% of the box's size) in this
        /// frame: a border or the start of a backdrop, along most of that
        /// side. Sides with no such edge stay where they were drawn.</summary>
        public static Rectangle Snap(byte[] px, int width, int height, int stride, bool rgbOrder, Rectangle box)
        {
            if (box.Width < 40 || box.Height < 40)
                return box;
            int l = box.Left, t = box.Top, r = box.Right, b = box.Bottom;
            for (int pass = 0; pass < 2; pass++)
            {
                int reachY = Math.Max(3, (b - t) * 6 / 100), reachX = Math.Max(3, (r - l) * 6 / 100);
                int inX = (r - l) / 8, inY = (b - t) / 8; // corners are often cut short
                t = BestEdge(px, stride, width, height, rgbOrder, true, t, reachY, -1, l + inX, r - inX);
                b = BestEdge(px, stride, width, height, rgbOrder, true, b, reachY, 1, l + inX, r - inX);
                l = BestEdge(px, stride, width, height, rgbOrder, false, l, reachX, -1, t + inY, b - inY);
                r = BestEdge(px, stride, width, height, rgbOrder, false, r, reachX, 1, t + inY, b - inY);
            }
            return r - l >= 40 && b - t >= 40 ? Rectangle.FromLTRB(l, t, r, b) : box;
        }

        private static int BestEdge(byte[] px, int stride, int width, int height, bool rgbOrder,
            bool horizontal, int at, int reach, int outward, int from, int to)
        {
            int best = at;
            double bestShare = 0.5; // an edge along at least half the side
            for (int d = 0; d <= reach; d++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int line = at + sign * d;
                    double share = StraightEdge(px, stride, width, height, rgbOrder, horizontal, line, from, to);
                    if (share > bestShare + 0.05) // nearest first: a farther one must be clearly better
                    {
                        bestShare = share;
                        best = line;
                    }
                }
            // busy content just inside a border can pass for the border too:
            // of lines a few pixels apart, the outermost is the border
            for (int d = 1; d <= 4 && best != at; d++)
                if (StraightEdge(px, stride, width, height, rgbOrder, horizontal, best + outward * d, from, to) >= 0.9 * bestShare)
                {
                    best += outward * d;
                    d = 0;
                }
            return best;
        }
    }
}
