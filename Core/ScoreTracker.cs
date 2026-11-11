using System.Collections.Generic;

namespace BgaSpnfdr
{
    /// <summary>
    /// Decides which reads become the displayed score. Platform-neutral: each
    /// build hands it Core/DigitReader's read of every frame, and it applies
    /// every safeguard (digits only, leading zeros, length cap, 25-point
    /// increments, consecutive-read confirmation, hold on loss).
    /// </summary>
    internal sealed class ScoreTracker
    {
#if GRAPHICS_CAPTURE
        // Windows build with Windows.Graphics.Capture: reads 30 a second with
        // it and 10 a second when it falls back to PrintWindow, so the host
        // picks the rule to match (ReadEveryFrame). Starts at 10 a second.
        private int ConfirmReads = 2;
        private int BackwardConfirmReads = 5;

        /// <summary>true: 30 reads a second, the Linux build's 3 and 8
        /// reads; false: 10 a second, the normal Windows build's 2 and 5.</summary>
        public void ReadEveryFrame(bool on)
        {
            ConfirmReads = on ? 3 : 2;
            BackwardConfirmReads = on ? 8 : 5;
        }
#elif READ_EVERY_FRAME
        // Builds that read every frame (~30 a second, the Linux build) need
        // more reads to cover the same ~0.1 s span: back-to-back frames look
        // almost identical, so a misread can repeat. On the reference run
        // (Tools/ReaderBench) 3/8 gave no wrong scores and missed none that
        // were on screen for 2+ frames.
        private const int ConfirmReads = 3;
        private const int BackwardConfirmReads = 8;
#else
        private const int ConfirmReads = 2;         // identical consecutive reads needed
        private const int BackwardConfirmReads = 5; // lower values / huge jumps are usually
                                                    // fragments or junk-prefix reads and
                                                    // need much stronger evidence
#endif

        // plain fields rather than properties: properties generate extra
        // methods and metadata, and the Windows exe is kept small
        private string _pendingRead; // last read awaiting confirmation
        private int _pendingCount;
        private long _confirmedScore = -1;

        /// <summary>Human-readable reading activity for the status bar. The
        /// host app may overwrite it.</summary>
        public string Status = "Waiting for the score";

#if PERF_TEST
        /// <summary>Windows 30-reads-a-second test build only.</summary>
        public long Confirmed => _confirmedScore;
#endif

        /// <summary>Returns the read if it passes every sanity check,
        /// otherwise null (with the reason in Status).</summary>
        public string ValidCandidate(string read)
        {
            if (string.IsNullOrEmpty(read))
                return null;
            foreach (char c in read)
                if (c < '0' || c > '9')
                    return null;

            // Safeguard: BattleTanx never zero-pads its score, so a leading
            // zero means a fragment (a lost "2" makes 039950 out of 239950)
            if (read.Length > 1 && read[0] == '0')
            {
                Status = $"read \"{read}\" rejected (leading zero)";
                return null;
            }

            // Safeguard: absurdly long reads are garbage
            if (read.Length > BattleTanx.MaxScoreDigits)
            {
                Status = $"read \"{read}\" rejected (too long)";
                return null;
            }

            // Safeguard: BattleTanx scores only move in 25-point increments
            long value = long.Parse(read);
            if (value % BattleTanx.ScoreMultiple != 0)
            {
                Status = $"read {value} rejected (not a multiple of {BattleTanx.ScoreMultiple})";
                return null;
            }
            return read;
        }

        /// <summary>Feeds one frame's read. Returns the note to show when this
        /// frame confirms a new score that has one in <paramref name="route"/>;
        /// otherwise null, and whatever note is up stays up (a stale note
        /// beats a vanished one mid-run).</summary>
        public string HandleRead(string read, Dictionary<long, string> route)
        {
            string candidate = ValidCandidate(read);
            if (candidate == null)
            {
                // Safeguard: score hidden/unreadable -> hold the last
                // confirmed value (lost notes are worse than stale ones)
                _pendingRead = null;
                _pendingCount = 0;
                if (string.IsNullOrEmpty(read))
                    Status = _confirmedScore >= 0
                        ? $"no digits visible — holding {_confirmedScore}"
                        : "no digits visible";
                return null;
            }

            // Safeguard: a new value must be read on ConfirmReads consecutive
            // frames before it is accepted (kills one-frame misreads)
            if (candidate == _pendingRead)
                _pendingCount++;
            else
            {
                _pendingRead = candidate;
                _pendingCount = 1;
            }

            long score = long.Parse(candidate);
            // Safeguard: backward moves and implausibly large jumps are almost
            // always fragments (lost leading digits, "25" off 468025) or junk
            // (an extra digit in front), so they need much stronger evidence
            // (scores only go up mid-run)
            bool suspicious = _confirmedScore >= 0
                && (score < _confirmedScore || score - _confirmedScore > BattleTanx.MaxScoreJump);
            int required = suspicious ? BackwardConfirmReads : ConfirmReads;
            if (_pendingCount < required)
            {
                Status = $"reading {score} ({_pendingCount}/{required})";
                return null;
            }

            string note = null;
            if (score != _confirmedScore)
            {
                _confirmedScore = score;
                route.TryGetValue(score, out note);
            }
            Status = route.ContainsKey(score)
                ? $"score {score} (note shown)"
                : $"score {score}";
            return note;
        }
    }
}
