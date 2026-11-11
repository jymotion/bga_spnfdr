using System;
using System.Collections.Generic;
using System.Drawing;

namespace BgaSpnfdr
{
    /// <summary>
    /// Finds the game picture in what is captured, which may hold much more
    /// than the game: window chrome, a stream layout, a webcam, animations,
    /// the capturing program's own window. Shared by both apps, which feed
    /// it whole frames (~10 a second). There is no cropping by hand: a box
    /// drawn by hand would have to be right to within a few pixel rows.
    ///
    /// The program is usually set up on the game's menu, where there is no
    /// score to go by and nothing in the game moves. So:
    ///
    ///  - Detect first looks for the menu itself (Core/MenuFinder): if it is
    ///    showing, that says exactly where the picture is, at once. Failing
    ///    that, it watches a few seconds for what moves, takes the largest
    ///    moving region and grows it to the picture's own edge (the black
    ///    around it, or a straight line where a backdrop starts): a still
    ///    sky doesn't move, but it is part of the picture. Nothing is
    ///    assumed about the picture's shape, so a stretched one is found
    ///    the same way. The user sees the result in the preview.
    ///
    ///  - Check runs after that until a score has been found. It looks for
    ///    the menu now and then (the game may get to it later), and for the
    ///    score with Core/ScoreFinder: near the crop there is and, failing
    ///    that, anywhere in the frame. Where the score really is fixes the
    ///    picture far more exactly than a guess from movement, and tells
    ///    the game from everything else that moves: only the game has a
    ///    BattleTanx score in it. A find must repeat on a later frame
    ///    before it is believed (Feed then returns the picture, with
    ///    Refining set); a few more over the next seconds settle it (Feed
    ///    returns again).
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
        private const int Samples = 7, SampleGap = 3, SampleTries = 14;
        // The game's menu is looked for this often (in frames) while
        // watching for motion and while checking
        private const int MenuEveryDetect = 5, MenuEveryCheck = 30;

        private enum Stage { Motion, Near, Wide, Anywhere, Done }

        private readonly bool _detect;
        private readonly int _frameBudget;
        private readonly Rectangle _crop;      // Check: the crop to check
        private readonly bool _trusted;        // Check: that crop is from the menu or an earlier score
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
        private readonly MenuFinder _menu = new MenuFinder();
        private Rectangle _menuAt;   // where the menu was last recognised (to be seen twice)
        private int _fed;            // frames fed
        private bool _menuReported;  // Check: the menu has been recognised once

        // What is seen left of a find over its samples, for the picture's
        // left edge (Watch, LeftEdge): rows x columns of the frame
        private int _watchX, _watchColumns, _watchSamples;
        private int[] _watchRows, _watchMoved, _watchEdges;
        private byte[] _watchLast, _watchMost;
        private readonly List<int> _findTops = new List<int>();  // its top edge, where one could be seen
        private byte[] _px;                                      // the frame being fed
        private int _stride;
        private bool _rgbOrder;
        private int _sampleTries, _sinceSample;
        private byte[] _glance;  // a coarse copy of the last frame checked
        private bool _reported;  // Result has been handed out once already
        private int _frame;      // frames fed while checking

        private GameFinder(bool detect, int frames, Rectangle crop, bool trusted)
        {
            _trusted = trusted;
            _detect = detect;
            _frameBudget = frames;
            _crop = crop;
            _stage = detect ? Stage.Motion : Stage.Near;
        }

        /// <summary>Finds the picture by what moves over this many frames.</summary>
        public static GameFinder Detect(int frames) => new GameFinder(true, frames, Rectangle.Empty, false);

        /// <summary>Looks for the score: near this crop first, then anywhere.
        /// trusted: the crop is from the game's menu or was confirmed by a
        /// score before, as opposed to a guess from what moves. It is then
        /// kept unless the score clearly says otherwise.</summary>
        public static GameFinder Check(Rectangle crop, bool trusted) => new GameFinder(false, 0, crop, trusted);

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

        /// <summary>With Verified: the picture's top edge was seen, so its
        /// height is measured (to a pixel or two in 423 rows). Without, the
        /// height is from the digits alone and can be 5% off: worth checking
        /// again later, in a scene that shows the edge.</summary>
        public bool Ruled;

        /// <summary>Set once Feed returns true: Result is where the game's
        /// menu was recognised (Core/MenuFinder); no score yet.</summary>
        public bool Recognized;

        /// <summary>Check: the menu has been recognised right where the crop
        /// being checked is (which Feed doesn't stop for).</summary>
        public bool MenuSeen;

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
                _findTops.Clear();
                _menuAt = Rectangle.Empty;
                if (_detect)
                    _stage = Stage.Motion;
            }

            _finder.SetFrame(px, stride, width, height, rgbOrder);
            _px = px; _stride = stride; _rgbOrder = rgbOrder;
            if (_finds.Count > 0)
                return Sample();
            _fed++;
            if (_stage == Stage.Motion)
            {
                // the game's menu, if that is what's showing, says exactly
                // where the picture is, at once
                if (Menu(MenuEveryDetect))
                    return true;
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

            // Check: the menu now and then (the program may have been set up
            // before the game was on it) ...
            if (Menu(_menuReported ? 4 * MenuEveryCheck : MenuEveryCheck))
            {
                _menuReported = true; // from now on only now and then
                MenuSeen = Math.Abs(Result.X - _crop.X) <= 3 && Math.Abs(Result.Y - _crop.Y) <= 3
                    && Math.Abs(Result.Right - _crop.Right) <= 3 && Math.Abs(Result.Bottom - _crop.Bottom) <= 3;
                if (!MenuSeen)
                    return true; // the crop belongs there instead
                Result = Rectangle.Empty;
                Recognized = false;
            }
            // ... the crop itself about once a second, so a score that comes
            // up is caught promptly; the wider search in between
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

        /// <summary>Looks for the game's menu every so many frames, and on
        /// the frame after it was seen: twice in the same place and it is
        /// believed (true, with Result and Recognized).</summary>
        private bool Menu(int every)
        {
            if (_menuAt.IsEmpty && _fed % every != 1)
                return false;
            if (!_menu.Find(_px, _stride, _rgbOrder, _area, out Rectangle at))
            {
                _menuAt = Rectangle.Empty;
                return false;
            }
            bool again = !_menuAt.IsEmpty && Math.Abs(at.X - _menuAt.X) <= 3 && Math.Abs(at.Y - _menuAt.Y) <= 3
                && Math.Abs(at.Right - _menuAt.Right) <= 3 && Math.Abs(at.Bottom - _menuAt.Bottom) <= 3;
            _menuAt = at;
            if (!again)
                return false;
            Result = at;
            Recognized = true;
            Verified = false;
            if (_detect)
                _stage = Stage.Done;
            return true;
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
            if (probe.Hint.IsEmpty)
            {
                // found with nothing to say it should be there: a 2-digit
                // score has to match very well, and the picture it implies
                // has to be in the frame
                if (hit.Text.Length < 3 && ScoreFinder.Match(hit) < 0.88)
                    return false;
                Rectangle crop = ScoreFinder.Crop(hit, double.NaN);
                var room = Rectangle.Inflate(_area, _area.Width / 25, _area.Height / 25);
                if (crop.Top < room.Top || crop.Bottom > room.Bottom || crop.Width > room.Width)
                    return false;
            }
            _finds.Add(hit);
            _findTops.Clear();
            Watch(hit, true);
            NoteTop(hit);
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
                Hint = first.Hint,
            };
            if (_finder.Run(again, out ScoreFinder.Hit hit) && _finder.Tune(ref hit, 2)
                && Math.Abs(hit.Top - first.Top) <= 2 * first.Sy
                && Math.Abs(hit.Sy / first.Sy - 1) <= 0.08 && Math.Abs(hit.Sx / first.Sx - 1) <= 0.04)
            {
                _finds.Add(hit);
                Watch(hit, false);
                NoteTop(hit);
            }
            bool last = _finds.Count >= Samples || ++_sampleTries >= SampleTries;
            if (_finds.Count < 2)
            {
                if (last)
                    _finds.Clear(); // not seen again: carry on looking
                return false;
            }
            if (_reported && !last)
                return false;

            var bySize = new List<ScoreFinder.Hit>(_finds);
            bySize.Sort((a, b) => a.Sy.CompareTo(b.Sy));
            ScoreFinder.Hit middle = bySize[bySize.Count / 2];
            // the width from the longest score seen: its digits' spacing is
            // the longest ruler
            ScoreFinder.Hit latest = _finds[_finds.Count - 1];
            foreach (ScoreFinder.Hit f in _finds)
                if (f.Text.Length > middle.Text.Length)
                {
                    middle.Sx = f.Sx;
                    middle.Text = f.Text;
                }
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

            // A box the score was looked for in and that fits what the score
            // says is kept as it is: it reads just as well, and it was put
            // there with more to go by (the menu, the picture's edges) than
            // the score alone gives, above all for the left edge.
            // The height from the digits alone (no top edge seen) can be 5%
            // off, so a trusted box is given that much room, and is kept
            // wherever in the frame the score was found.
            Rectangle hint = first.Hint.IsEmpty && _trusted ? _crop : first.Hint;
            bool ruled = height != bySize[bySize.Count / 2].Sy;
            int left = LeftEdge();
            Rectangle crop = ScoreFinder.Crop(middle, left >= 0 ? left : hint.IsEmpty ? double.NaN : hint.X);
            Result = !hint.IsEmpty && Fits(hint, crop, middle, _trusted && !ruled ? 0.08 : 0.03) ? hint : crop;
            Verified = true;
            Ruled = ruled;
            Recognized = false;
            Refining = !last;
            _reported = true;
            if (last)
            {
                _finds.Clear();
                _findTops.Clear();
                _stage = Stage.Done;
                _plan.Clear();
                _next = 0;
            }
            return true;
        }

        /// <summary>Whether a box reads as well as the crop worked out from
        /// the score: its score band within 3 reference rows, its height
        /// within the share given, its width between 3% narrower and 6%
        /// wider, and the score inside it.</summary>
        private static bool Fits(Rectangle box, Rectangle crop, ScoreFinder.Hit hit, double height)
        {
            double rows = DigitReader.ReferenceCropHeight, band = DigitReader.BandTop / rows;
            ScoreFinder.LeftRange(hit, out double min, out double max);
            return Math.Abs(box.Y + band * box.Height - (crop.Y + band * crop.Height)) <= 3 * crop.Height / rows
                && Math.Abs((double)box.Height / crop.Height - 1) <= height
                && box.Width >= 0.97 * crop.Width && box.Width <= 1.06 * crop.Width
                && box.X >= min - 1 && box.X <= max + 1;
        }

        // ---------- the picture's left edge, from a find ----------

        /// <summary>Takes in what this frame shows left of a find: a strip
        /// of 48 rows over the picture's height, from as far left as the
        /// picture can start to just short of the score.</summary>
        private void Watch(ScoreFinder.Hit hit, bool start)
        {
            const int rows = 48;
            if (start)
            {
                ScoreFinder.LeftRange(hit, out double min, out double max);
                _watchX = Math.Max(0, (int)Math.Floor(min) - 3);
                _watchColumns = Math.Max(0, Math.Min(_width - 1, (int)max) - _watchX + 1);
                _watchSamples = 0;
                double top = hit.Top - DigitReader.BandTop * hit.Sy, h = DigitReader.ReferenceCropHeight * hit.Sy;
                _watchRows = new int[rows];
                for (int r = 0; r < rows; r++)
                    _watchRows[r] = Math.Max(0, Math.Min(_height - 1, (int)(top + (0.1 + 0.75 * r / (rows - 1)) * h)));
                _watchLast = new byte[rows * _watchColumns];
                _watchMost = new byte[rows * _watchColumns];
                _watchMoved = new int[_watchColumns];
                _watchEdges = new int[_watchColumns];
            }
            if (_watchColumns < 4)
                return;
            int ri = _rgbOrder ? 0 : 2, bi = _rgbOrder ? 2 : 0;
            var steps = new int[_watchColumns];
            for (int r = 0; r < rows; r++)
            {
                int o = _watchRows[r] * _stride + _watchX * 4, k = r * _watchColumns;
                for (int c = 0; c < _watchColumns; c++, o += 4, k++)
                {
                    byte luma = (byte)((_px[o + bi] * 114 + _px[o + 1] * 587 + _px[o + ri] * 299) / 1000);
                    if (_watchSamples > 0 && Math.Abs(luma - _watchLast[k]) > MotionDelta)
                        _watchMoved[c]++;
                    if (c >= 2 && Math.Abs(luma - _watchLast[k - 2]) >= EdgeStep)
                        steps[c - 1]++; // _watchLast[k - 2] is already this frame's
                    if (luma > _watchMost[k])
                        _watchMost[k] = luma;
                    _watchLast[k] = luma;
                }
            }
            // an edge on this column alone: busy picture content steps in
            // brightness on every column (as in StraightEdge)
            for (int c = 0; c < _watchColumns; c++)
                if (steps[c] * 100 >= rows * 55
                    && Math.Min(c >= 5 ? steps[c - 4] : rows, c + 5 < _watchColumns ? steps[c + 4] : rows) * 2 <= steps[c])
                    _watchEdges[c]++;
            _watchSamples++;
        }

        /// <summary>The picture's left edge, going left from the score over
        /// what was watched: the game moves, and is lit even where it holds
        /// still. The picture ends at the first columns that never lit up
        /// (black beside it), or at a straight edge that stayed put over the
        /// samples (the start of a backdrop), or, where what lies beyond
        /// is lit and still for more than 6% of the picture's width, where
        /// the movement ended. -1 if none of that shows.</summary>
        private int LeftEdge()
        {
            const int rows = 48;
            if (_watchColumns < 4 || _watchSamples < 2)
                return -1;
            int dark = 0, still = 0, moving = 0;
            // black and stillness only count once watched for a second:
            // part of the game is often dark or still for a moment
            bool settled = _watchSamples >= 4;
            int widest = (int)(0.06 * 3 * _watchColumns); // the strip is about a third of the picture wide
            for (int c = _watchColumns - 1; c >= 0; c--)
            {
                bool moved = _watchMoved[c] * 100 >= 8 * rows * (_watchSamples - 1);
                int lit = 0;
                for (int r = 0; r < rows; r++)
                    if (_watchMost[r * _watchColumns + c] > LumaThreshold)
                        lit++;
                if (moved)
                    moving++;
                if (!moved && lit * 4 < rows)
                {
                    if (++dark >= 2 && settled)
                        return _watchX + c + dark;
                }
                else
                    dark = 0;
                if (_watchEdges[c] * 2 >= _watchSamples && c < _watchColumns - 3)
                    return _watchX + c;
                if (moved)
                    still = 0;
                else if (++still > widest && moving > 0 && settled)
                    return _watchX + c + still;
            }
            return -1;
        }

        /// <summary>Notes where the picture's top edge is in this frame,
        /// going by a find, if it can be seen: a straight edge across the
        /// picture, within 8% of the picture's height of where the find's
        /// own height puts it.</summary>
        private void NoteTop(ScoreFinder.Hit hit)
        {
            double h = DigitReader.ReferenceCropHeight * hit.Sy, w = DigitReader.ReferenceCropWidth * hit.Sx;
            int expected = (int)Math.Round(hit.Top - DigitReader.BandTop * hit.Sy), reach = (int)(0.08 * h);
            // columns sure to be inside the picture: from the score rightwards
            int from = (int)hit.Left, to = (int)Math.Min(_width - 1, hit.Left + 0.6 * w);
            int best = -1;
            double bestShare = 0.5;
            for (int d = 0; d <= reach; d++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    int line = expected + sign * d;
                    double share = StraightEdge(_px, _stride, _width, _height, _rgbOrder, true, line, from, to);
                    // the picture is what lies below its top edge: an edge
                    // with black below it is something else (where the
                    // capturing program's grey panel meets its black canvas,
                    // a little above the picture)
                    // (within 1% of its height: a capture often has a thin
                    // black border of its own between a backdrop and the picture)
                    if (share > bestShare + 0.05 && LitBelow(line, Math.Max(3, (int)(0.01 * h)), from, to))
                    {
                        bestShare = share;
                        best = expected + sign * d;
                    }
                }
            if (best >= 0)
                _findTops.Add(best);
        }

        /// <summary>Whether one of the rows just below a line is lit along
        /// most of from..to.</summary>
        private bool LitBelow(int line, int within, int from, int to)
        {
            for (int d = 2; d <= within + 2; d++)
                if (LitShare(line + d, from, to) >= 0.5)
                    return true;
            return false;
        }

        /// <summary>The share of row y between columns from..to that is lit
        /// in this frame.</summary>
        private double LitShare(int y, int from, int to)
        {
            if (y < 0 || y >= _height || to <= from)
                return 0;
            int ri = _rgbOrder ? 0 : 2, bi = _rgbOrder ? 2 : 0;
            int step = Math.Max(1, (to - from) / 160), lit = 0, total = 0;
            for (int x = from; x < to; x += step, total++)
                if (ScoreBand.Luma(_px, _stride, 0, _height - 1, _width, x, y, ri, bi) > LumaThreshold)
                    lit++;
            return total == 0 ? 0 : (double)lit / total;
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
    }
}
