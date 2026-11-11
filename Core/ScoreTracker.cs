using System.Collections.Generic;

namespace BgaSpnfdr
{
    /// <summary>
    /// Decides which reads become the displayed score. Platform-neutral: each
    /// build hands it Core/DigitReader's read of every frame, and it applies
    /// every safeguard (digits only, leading zeros, length cap, 25-point
    /// increments, fragments of a longer score, consecutive-read
    /// confirmation, hold on loss).
    /// </summary>
    internal sealed class ScoreTracker
    {
        // Identical consecutive reads a new value needs. Lower values and huge
        // jumps (BackwardConfirmReads) are usually fragments or junk-prefix
        // reads and need much stronger evidence. Reading every frame (~30 a
        // second) takes 3 and 8 to cover about the same ~0.1 s span as 2 and 5
        // at 10 a second: back-to-back frames look almost identical, so a
        // misread can repeat. On the reference run (Tools/ReaderBench) 3/8 gave
        // no wrong scores and missed none that were on screen for 2+ frames.
        // The Linux build always reads every frame; the Windows app does with
        // Windows.Graphics.Capture and switches to 10 a second when it has to
        // fall back to PrintWindow (ReadEveryFrame).
#if READ_EVERY_FRAME
        private int ConfirmReads = 3;
        private int BackwardConfirmReads = 8;
#else
        private int ConfirmReads = 2;
        private int BackwardConfirmReads = 5;
#endif

        // A read below the current score with at least this strong a match
        // where one more leading digit would sit (DigitReader.LeftMatch) is
        // the end of a longer score whose leading digit fell just under the
        // reader's threshold. At some capture sizes that lasts long enough
        // to pass BackwardConfirmReads: on the reference run scaled 1.1x and
        // 1.4x, "39800" and "9800" off 239800 got confirmed, with the lost
        // digit matching 0.53-0.58. Background there matched under 0.35.
        private const float FragmentMatch = 0.4f;

        /// <summary>Picks the rule for the read rate: true for ~30 reads a
        /// second (3 and 8 reads), false for 10 (2 and 5).</summary>
        public void ReadEveryFrame(bool on)
        {
            ConfirmReads = on ? 3 : 2;
            BackwardConfirmReads = on ? 8 : 5;
        }

        // plain fields rather than properties: properties generate extra
        // methods and metadata, and the Windows exe is kept small
        private string _pendingRead; // last read awaiting confirmation
        private int _pendingCount;
        private long _confirmedScore = -1;

        /// <summary>Human-readable reading activity for the status bar. The
        /// host app may overwrite it.</summary>
        public string Status = "Waiting for the score";

        /// <summary>False while nobody can see Status (setup window hidden):
        /// it is then left as it was instead of being rebuilt on every
        /// read.</summary>
        public bool WantStatus = true;

        /// <summary>After HandleRead: this frame's read was the score that
        /// stands (so where its digits were found can be trusted).</summary>
        public bool ReadStands;


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
                if (WantStatus) Status = $"read \"{read}\" rejected (leading zero)";
                return null;
            }

            // Safeguard: absurdly long reads are garbage
            if (read.Length > BattleTanx.MaxScoreDigits)
            {
                if (WantStatus) Status = $"read \"{read}\" rejected (too long)";
                return null;
            }

            // Safeguard: BattleTanx scores only move in 25-point increments
            long value = long.Parse(read);
            if (value % BattleTanx.ScoreMultiple != 0)
            {
                if (WantStatus) Status = $"read {value} rejected (not a multiple of {BattleTanx.ScoreMultiple})";
                return null;
            }
            return read;
        }

        /// <summary>Feeds one frame's read. Returns the note to show when this
        /// frame confirms a new score that has one in <paramref name="route"/>;
        /// otherwise null, and whatever note is up stays up (a stale note
        /// beats a vanished one mid-run). <paramref name="leftMatch"/> is
        /// DigitReader.LeftMatch for this read.</summary>
        public string HandleRead(string read, Dictionary<long, string> route, float leftMatch = 0)
        {
            ReadStands = false;
            string candidate = ValidCandidate(read);

            // Safeguard: a lower value with signs of another digit in front
            // of it is a fragment of the real score, however often it repeats
            if (candidate != null && _confirmedScore >= 0 && leftMatch >= FragmentMatch
                && long.Parse(candidate) < _confirmedScore)
            {
                if (WantStatus) Status = $"read {candidate} rejected (end of a longer score)";
                candidate = null;
            }
            if (candidate == null)
            {
                // Safeguard: score hidden/unreadable -> hold the last
                // confirmed value (lost notes are worse than stale ones)
                _pendingRead = null;
                _pendingCount = 0;
                if (string.IsNullOrEmpty(read))
                    if (WantStatus) Status = _confirmedScore >= 0
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
                if (WantStatus) Status = $"reading {score} ({_pendingCount}/{required})";
                return null;
            }

            ReadStands = true;
            string note = null;
            if (score != _confirmedScore)
            {
                _confirmedScore = score;
                route.TryGetValue(score, out note);
            }
            if (WantStatus) Status = route.ContainsKey(score)
                ? $"score {score} (note shown)"
                : $"score {score}";
            return note;
        }
    }
}
