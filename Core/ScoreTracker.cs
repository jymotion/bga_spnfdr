using System.Collections.Generic;

namespace BgaSpnfdr
{
    /// <summary>
    /// Decides which OCR reads become the displayed score. Platform-neutral:
    /// each build's OCR hands it raw text from two image variants per frame,
    /// and it applies every safeguard (lookalike mapping, leading zeros,
    /// length cap, 25-point increments, variant agreement, consecutive-read
    /// confirmation, hold on loss).
    /// </summary>
    internal sealed class ScoreTracker
    {
#if READ_EVERY_FRAME
        // Builds that read every frame (~30 a second, the Linux build) need
        // more reads to cover the same ~0.1 s span: back-to-back frames look
        // almost identical, so a misread can repeat. On the reference run
        // (Tools/ReaderBench) 3/8 gave no wrong scores and missed none that
        // were on screen for 2+ frames. Builds without this symbol (Windows)
        // compile exactly as before.
        private const int ConfirmReads = 3;
        private const int BackwardConfirmReads = 8;
#else
        private const int ConfirmReads = 2;         // identical consecutive reads needed
        private const int BackwardConfirmReads = 5; // lower values / huge jumps are usually
                                                    // fragments or junk-prefix reads and
                                                    // need much stronger evidence
#endif

        // plain fields rather than properties: properties generate extra
        // methods and metadata, and the Windows exe is kept as small as it was
        // before this logic moved here
        private string _pendingRead; // last read awaiting confirmation
        private int _pendingCount;
        private long _confirmedScore = -1;

        /// <summary>Human-readable OCR activity for the status bar. The host
        /// app may overwrite it (e.g. with OCR engine errors).</summary>
        public string Status = "OCR idle";

#if SIDE_BY_SIDE
        /// <summary>Side-by-side test builds only: the confirmed score.</summary>
        public long Confirmed => _confirmedScore;
#endif

        /// <summary>Returns the digit string if this read passes every sanity
        /// check, otherwise null (with the reason in Status).</summary>
        public string ValidCandidate(string text)
        {
            // Safeguard: map lookalike letters back to digits; reject reads
            // with unmappable characters instead of dropping them
            string digits = ScoreParser.Normalize(text);
            if (string.IsNullOrEmpty(digits))
                return null;

            // Safeguard: BattleTanx never zero-pads its score, so a leading
            // zero means a fragment ("038so" off 273850 isn't a real 3850)
            if (digits.Length > 1 && digits[0] == '0')
            {
                Status = $"read \"{digits}\" rejected (leading zero)";
                return null;
            }

            // Safeguard: absurdly long reads are garbage
            if (digits.Length > BattleTanx.MaxScoreDigits)
            {
                Status = $"read \"{digits}\" rejected (too long)";
                return null;
            }

            // Safeguard: BattleTanx scores only move in 25-point increments
            long value = long.Parse(digits);
            if (value % BattleTanx.ScoreMultiple != 0)
            {
                Status = $"read {value} rejected (not a multiple of {BattleTanx.ScoreMultiple})";
                return null;
            }
            return digits;
        }

        /// <summary>Feeds one frame's two OCR reads. Returns the note to show
        /// when this frame confirms a new score that has one in
        /// <paramref name="route"/>; otherwise null, and whatever note is up
        /// stays up (a stale note beats a vanished one mid-run).</summary>
        public string HandleReads(string textA, string textB, Dictionary<long, string> route)
        {
            string a = ValidCandidate(textA);
            string b = ValidCandidate(textB);

            string candidate;
            if (a != null && b != null && a != b)
            {
                // Safeguard: both variants read a plausible but different
                // number — can't tell which is right, skip this frame
                Status = $"ambiguous read ({a} vs {b}) — skipped";
                return null;
            }
            else if (a != null || b != null)
            {
                candidate = a ?? b;
            }
            else
            {
                // Safeguard: score hidden/unreadable -> hold the last
                // confirmed value (lost notes are worse than stale ones)
                _pendingRead = null;
                _pendingCount = 0;
                if (string.IsNullOrEmpty(textA) && string.IsNullOrEmpty(textB))
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
            // always junk-prefix reads ("77 3sso" → 773550) or fragments, so
            // they need much stronger evidence (scores only go up mid-run)
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
