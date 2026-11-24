using System;
using System.Drawing;

namespace BgaSpnfdr
{
    /// <summary>
    /// Keeps the crop (where the game picture is in what's captured) right,
    /// for both apps: when to look for the game with Core/GameFinder, what
    /// to do with what it finds, and what to tell the user. The apps
    /// capture, read the score and show things; they call this from their
    /// UI thread:
    ///
    ///  - Start when a window starts being captured, Stop when it stops.
    ///  - Tick about 5 to 10 times a second while frames are coming.
    ///  - Feed with a whole frame about 10 times a second while Busy.
    ///  - ScoreSeen when a read stood, MoveCrop when it says the crop is
    ///    off (ScoreBand.Recenter).
    ///
    /// The app saves Crop, Verified and FrameSize when Changed is raised,
    /// and reads the score through Crop only while Reading.
    ///
    /// What it does: a newly picked window, or one whose size has changed,
    /// is looked at for a few seconds (the menu, or what moves). A crop no
    /// score has confirmed yet is checked in the background until one does;
    /// a confirmed one again after 15 s without a score (a menu, or the game
    /// was moved inside the window), and every 10 s until its height has
    /// been measured from the picture's top edge.
    /// </summary>
    internal sealed class GameWatch
    {
        private const int DetectFrames = 40; // ~4 s of frames after a window is picked
        // A confirmed crop is looked into again when no score has been read
        // through it for this long (ms): the game may have been moved inside
        // the window, which doesn't change the window's size
        private const int LostAfter = 15000;

        /// <summary>The game picture in the frame (Empty: not known), and
        /// whether a score has confirmed it. Loaded and saved by the app,
        /// with the frame size the crop was made for.</summary>
        public Rectangle Crop;
        public bool Verified;
        public Size FrameSize;

        /// <summary>The crop is where the game's menu was recognised.</summary>
        public bool FromMenu;

        /// <summary>A confirmed crop, but no score read through it for a
        /// while.</summary>
        public bool Lost;

        /// <summary>Crop, Verified, FrameSize or Reading has changed.</summary>
        public Action Changed;

        /// <summary>Something to tell the user, for so many seconds.</summary>
        public Action<string, int> Message;

        // Core/GameFinder at work: _detector when a window has just been
        // picked or has changed size (the user waits for it), _checker in
        // the background
        private GameFinder _detector, _checker;
        private bool _reported;    // the finder at work has given a result already
        private bool _stale;       // the frame changed size: the crop is for another
        private Size _sizeChecked; // frame size the crop was last checked against
        private long _lastScore;   // when a read last stood (ms)
        // The crop's height has been measured from the picture's top edge
        // since the program started. Not saved: checked once per run of the
        // program, so a wrong one can't stick.
        private bool _heightMeasured;
        private long _measureNext; // when to try again to measure it

        /// <summary>Whole frames are wanted (Feed).</summary>
        public bool Busy => _detector != null || _checker != null;

        /// <summary>The game is being looked for after a window was picked
        /// or changed size.</summary>
        public bool Detecting => _detector != null;

        /// <summary>The score can be read through Crop (not while a crop
        /// made for another frame size is being replaced: that would only
        /// produce misreads).</summary>
        public bool Reading => !_stale && Crop.Width > 0 && Crop.Height > 0;

        /// <summary>A window starts being captured at time now (ms).
        /// detect: it is another window than the crop was for.</summary>
        public void Start(long now, bool detect)
        {
            _detector = _checker = null;
            _stale = false;
            _lastScore = now;
            _sizeChecked = Size.Empty;
            if (!detect)
                return;
            Verified = false;
            Detect();
            Changed?.Invoke();
        }

        /// <summary>Capturing has stopped.</summary>
        public void Stop()
        {
            _detector = _checker = null;
            if (!_stale)
                return;
            _stale = false; // back to the crop there was
            Changed?.Invoke();
        }

        private void Detect()
        {
            _detector = GameFinder.Detect(DetectFrames);
            _checker = null;
            _reported = false;
        }

        /// <summary>A read stood at this time (ms).</summary>
        public void ScoreSeen(long at)
        {
            if (at > _lastScore)
                _lastScore = at;
        }

        /// <summary>Seconds since a read last stood.</summary>
        public long SinceScore(long now) => (now - _lastScore) / 1000;

        /// <summary>The score was read off the middle of where the crop
        /// says it is: the crop belongs here (ScoreBand.Recenter).</summary>
        public void MoveCrop(Rectangle moved, Size frame)
        {
            if (_detector == null && moved != Crop)
                SetCrop(moved, Verified, FromMenu, frame);
        }

        /// <summary>Call regularly while frames (of this size) are coming.</summary>
        public void Tick(long now, Size frame)
        {
            // The crop is in frame pixels, so it no longer fits once the
            // captured window changes size: the game is looked for again
            // (once per size, so a failed detection doesn't loop). So it is
            // when there is no crop at all.
            if (_detector == null && frame != _sizeChecked)
            {
                _sizeChecked = frame;
                if (!Crop.IsEmpty && FrameSize.IsEmpty)
                {
                    FrameSize = frame; // a crop saved by an early version: taken to fit
                    Changed?.Invoke();
                }
                else if (Crop.IsEmpty || FrameSize != frame)
                {
                    if (!Crop.IsEmpty)
                        Message?.Invoke("The captured window changed size — re-detecting the game feed.", 6);
                    Verified = false; // it was confirmed for the other size
                    _stale = true;
                    Detect();
                    Changed?.Invoke();
                }
            }

            // a crop no score has confirmed yet is checked until one does
            // (also with no crop at all: the menu or a score will give one),
            // and a confirmed one when no score has been read through it
            // for a while (a menu, or the game was moved inside the window)
            // or its height hasn't been measured yet (the picture's top
            // edge doesn't show in a dark scene): tried again every 10 s
            Lost = Verified && now - _lastScore > LostAfter;
            bool look = !Verified || Lost || (!_heightMeasured && now >= _measureNext);
            if (_detector != null)
                return;
            if (_checker == null && look)
            {
                _checker = GameFinder.Check(Crop, Verified || FromMenu);
                _reported = false;
            }
            else if (_checker != null && !look && !_checker.Refining)
                _checker = null; // the score is being read again
        }

        /// <summary>Hands a whole frame (as for GameFinder.Feed) to the
        /// finder at work, if any, and acts on what it finds.</summary>
        public void Feed(byte[] px, int width, int height, int stride, bool rgbOrder, Rectangle area, long now)
        {
            GameFinder finder = _detector ?? _checker;
            if (finder == null)
                return;
            bool finished = finder.Feed(px, width, height, stride, rgbOrder, area);
            if (finder.MenuSeen)
                FromMenu = true; // the menu is showing, right where the crop is
            if (!finished)
                return;

            var frame = new Size(width, height);
            bool first = !_reported, detector = finder == _detector;
            _reported = true;
            if (finder.Recognized)
            {
                // the game's menu is showing: that is where the picture is
                _heightMeasured = true; // the whole picture was matched
                SetCrop(finder.Result, false, true, frame);
                Message?.Invoke("Game menu recognised — the crop is set to it. The first score will confirm it.", 10);
            }
            else if (detector && finder.Result.IsEmpty)
            {
                Message?.Invoke("Couldn't find the game yet — it is found when its menu or a score is on screen.", 10);
            }
            else if (detector)
            {
                // no menu and perhaps no score to go by: the box is from
                // what moves and the picture's edges
                _heightMeasured = finder.Verified && finder.Ruled;
                SetCrop(finder.Result, finder.Verified, false, frame);
                if (first)
                    Message?.Invoke(finder.Verified
                        ? "Game found and confirmed by the score."
                        : "Game found by what moves, which other moving things can fool. Its GAME SETUP menu or " +
                          "the first score of a run will set it right.", 12);
            }
            else if (finder.Verified)
            {
                bool confirmed = Verified, moved = finder.Result != Crop;
                bool ruled = finder.Ruled || (!moved && _heightMeasured);
                if (moved || !confirmed || ruled != _heightMeasured)
                {
                    _heightMeasured = ruled;
                    SetCrop(finder.Result, true, FromMenu && !moved, frame);
                }
                _lastScore = now;
                _measureNext = now + 10000;
                if (confirmed && moved && Lost)
                    Message?.Invoke("The game was found somewhere else in the window — now reading it there.", 8);
                else if (first && !confirmed)
                    Message?.Invoke("Score found — the game's place in the window is confirmed.", 8);
            }
            if (detector)
            {
                _sizeChecked = frame; // (not again for this size, found or not)
                if (_stale)
                {
                    // re-detected, or (if that failed) back to the crop there was
                    _stale = false;
                    Changed?.Invoke();
                }
            }
            // a finder that has just found the score goes on to steady its
            // result; one that hasn't is started afresh by Tick
            if (!finder.Refining)
                _detector = _checker = null;
        }

        private void SetCrop(Rectangle crop, bool verified, bool fromMenu, Size frame)
        {
            FromMenu = fromMenu;
            Crop = crop;
            Verified = verified;
            FrameSize = _sizeChecked = frame;
            Changed?.Invoke();
        }

        // ---------- what to tell the user ----------

        /// <summary>The status line while capturing. score: the score
        /// that stands (-1: none yet). With debug, what the reader is doing
        /// (reading) and how the frames come (capture) are in it.</summary>
        public string Status(long now, long score, bool debug, string reading, string capture)
        {
            if (_detector != null)
            {
                int seconds = (_detector.FramesLeft + 9) / 10; // fed ~10 frames a second
                return seconds > 0 ? $"Looking for the game… {seconds}s" : "Looking for the game… checking it against the score";
            }
            // what the user needs to know ...
            if (!debug)
                return Lost ? "Ready — no score on screen; checking the game is still where it was."
                    : Crop.IsEmpty ? "Looking for the game: it is found when its GAME SETUP menu or a score is on screen."
                    : !Verified && FromMenu ? "Game found by its menu. The first score of a run will confirm it."
                    : !Verified ? "Game not confirmed yet: go to its GAME SETUP menu, or start a run."
                    : score >= 0 ? $"Ready — score {score}."
                    : "Ready — waiting for a score.";
            // ... or, asked for, what the reader is doing
            long since = SinceScore(now);
            return reading + (since >= 1 ? $" ({since} s)" : "") + $"  ·  crop {Crop.X},{Crop.Y} {Crop.Width}x{Crop.Height} "
                + (Lost ? "(no score for 15 s: checking where the game is)"
                    : Verified ? (score >= 0 ? "(confirmed by the score)" : "(confirmed last time; no score seen yet this time)")
                    : FromMenu ? "(from the menu)" : "(not confirmed)")
                + "  ·  " + capture
                + (_checker == null || Lost ? "" : !Verified ? "  ·  looking for the game" : "  ·  measuring the picture's height");
        }

        /// <summary>The preview's label on the crop.</summary>
        public string Label(long score) =>
            Lost ? "game feed (no score for a while: checking it is still here)"
            : Verified ? (score >= 0 ? "game feed (confirmed by the score)" : "game feed (as confirmed last time; no score seen yet)")
            : FromMenu ? "game feed (from the game's menu)" : "game feed (not confirmed yet)";
    }
}
