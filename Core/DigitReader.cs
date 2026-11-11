using System;
#if !NETFRAMEWORK
using System.Numerics;
#endif

namespace BgaSpnfdr
{
    /// <summary>
    /// Reads the BattleTanx score straight from pixels by matching the game's
    /// own digit shapes; no OCR engine or extra download. Used by both the
    /// Windows and Linux builds. Learned from a real full-game run (footage
    /// branch) and checked against a hand-verified ground truth there (see
    /// Tools/ReaderBench); on Windows it also beat Windows OCR side by side
    /// on that run (104 vs 89 of 111 scores, none wrong, ~1 ms vs 45 ms).
    ///
    /// How it works:
    ///  - The score's font never changes, so each digit 0-9 is a small
    ///    brightness template (glyph + its drop shadow), averaged from real
    ///    frames.
    ///  - The score moves left/right between levels (it sits under the
    ///    minimap, whose size varies), so every horizontal position in the
    ///    band is scored against all ten digits (normalized correlation,
    ///    which ignores overall brightness/contrast).
    ///  - The font is proportional (a 1 is narrower than an 8), so digits are
    ///    chained left-to-right with 9-15 px steps, keeping the best-scoring
    ///    chain of confident matches.
    ///  - Being one pixel row off is enough to lose digits (and steadily lost
    ///    leading digits can get past the safeguards), so callers pass a
    ///    slightly taller band and every vertical offset is tried; the
    ///    strongest chain wins. The capture setup doesn't move, so once the
    ///    same offset wins LockVotes confident reads running, only that row is read,
    ///    with a full search every RecheckEvery frames in case it changed.
    ///    Call Reset() when the crop changes.
    /// Validation (multiples of 25, confirmation reads, ...) is not done
    /// here; reads go through Core/ScoreTracker. Core/ScoreBand prepares the
    /// band from a captured frame.
    /// </summary>
    internal sealed class DigitReader
    {
        // Geometry in reference-capture pixels: the game picture is
        // ReferenceCropWidth x ReferenceCropHeight (640x480 N64 capture,
        // picture area). Callers scale their band to this size first.
        public const int ReferenceCropWidth = 617;
        public const int ReferenceCropHeight = 474;
        public const int BandTop = 423;      // crop-relative y of the band's first row
        public const int BandHeight = 18;    // glyph rows + shadow + margin
        public const int GlyphWidth = 14;

        // Tuned on the reference run: 0.6 keeps the misreads one-off (the
        // safeguards reject them); higher values drop leading digits for
        // whole stretches, and two such fragments got confirmed at 0.7.
        private const float MatchThreshold = 0.6f;
        private const int MinStep = 9, MaxStep = 15; // px between digit starts

        private const int Pixels = BandHeight * GlyphWidth;
        private const int LockVotes = 5;
        private const int RecheckEvery = 60;
        // A read votes for its row only when its digits average this far
        // above MatchThreshold (0.8 in all). Real digits mostly match
        // 0.8-0.98; stray matches on a hidden score 0.6-0.7.
        private const float LockGain = 0.2f;

        // 10 digits x 18 rows x 14 columns, row-major, brightness in 16
        // levels packed two pixels per byte (high nibble first): 1.3 KB, which
        // keeps the Windows exe smaller than the OCR version was. 16 levels
        // read the reference run as well as 256 did (same scores, none wrong).
        private static readonly byte[] TemplateData =
        {
            51, 51, 51, 51, 51, 50, 34, 34, 35, 86, 118, 66, 34, 35,
            35, 35, 86, 118, 66, 35, 51, 35, 120, 101, 69, 153, 82, 35,
            36, 120, 100, 69, 154, 82, 35, 20, 136, 48, 3, 137, 48, 19,
            20, 137, 48, 3, 137, 48, 19, 36, 137, 48, 20, 119, 32, 35,
            36, 136, 48, 20, 120, 32, 35, 36, 137, 48, 20, 137, 48, 35,
            36, 136, 48, 20, 137, 48, 35, 20, 120, 82, 36, 137, 48, 35,
            20, 120, 82, 52, 136, 48, 35, 18, 52, 121, 152, 82, 0, 35,
            18, 36, 121, 152, 81, 0, 35, 35, 51, 33, 16, 0, 18, 35,
            35, 51, 33, 16, 0, 18, 35, 51, 51, 51, 34, 50, 51, 51,
            51, 51, 51, 51, 51, 51, 51, 51, 51, 51, 51, 51, 51, 106,
            51, 51, 51, 51, 51, 51, 106, 52, 139, 186, 67, 51, 52, 102,
            52, 138, 186, 66, 51, 52, 102, 51, 69, 153, 32, 35, 52, 102,
            51, 68, 153, 48, 35, 52, 102, 51, 68, 153, 48, 35, 51, 68,
            51, 68, 153, 48, 35, 51, 68, 51, 68, 153, 48, 35, 51, 51,
            51, 53, 153, 48, 35, 51, 51, 35, 68, 153, 32, 35, 51, 51,
            35, 69, 153, 48, 35, 51, 68, 36, 139, 204, 185, 100, 52, 86,
            36, 139, 203, 186, 100, 52, 86, 52, 67, 16, 0, 0, 35, 51,
            51, 51, 16, 0, 0, 35, 50, 51, 52, 51, 51, 51, 51, 51,
            17, 34, 34, 34, 17, 17, 17, 17, 73, 187, 132, 33, 17, 17,
            17, 73, 187, 132, 33, 17, 17, 17, 35, 51, 89, 131, 17, 17,
            18, 35, 51, 89, 131, 17, 17, 17, 17, 0, 55, 116, 33, 17,
            17, 17, 0, 55, 116, 33, 17, 17, 17, 17, 72, 113, 0, 17,
            17, 17, 18, 72, 97, 0, 17, 17, 17, 72, 131, 16, 1, 17,
            17, 18, 73, 131, 0, 1, 17, 18, 89, 132, 33, 17, 17, 17,
            18, 89, 132, 34, 17, 17, 17, 19, 88, 153, 152, 100, 33, 17,
            18, 71, 153, 152, 116, 33, 17, 17, 16, 0, 0, 0, 0, 1,
            17, 16, 0, 0, 0, 0, 1, 17, 17, 17, 17, 17, 17, 17,
            34, 34, 34, 34, 34, 34, 34, 34, 55, 171, 165, 34, 34, 34,
            34, 55, 171, 166, 34, 34, 34, 35, 67, 51, 89, 165, 34, 51,
            35, 51, 51, 89, 165, 34, 51, 34, 17, 17, 55, 148, 17, 35,
            34, 17, 17, 55, 147, 17, 35, 34, 34, 71, 168, 82, 1, 35,
            34, 34, 71, 152, 82, 1, 35, 34, 34, 33, 55, 148, 17, 35,
            34, 34, 17, 39, 148, 17, 35, 34, 50, 18, 56, 164, 17, 35,
            35, 50, 34, 56, 148, 17, 35, 35, 87, 153, 133, 32, 1, 35,
            35, 87, 153, 133, 16, 1, 34, 34, 33, 0, 0, 1, 17, 34,
            34, 33, 0, 0, 1, 17, 34, 34, 34, 34, 34, 34, 34, 34,
            51, 51, 51, 51, 51, 51, 51, 51, 51, 51, 51, 50, 51, 51,
            51, 51, 51, 51, 50, 51, 51, 51, 51, 53, 155, 115, 34, 35,
            51, 51, 53, 155, 115, 34, 35, 50, 52, 121, 152, 100, 34, 35,
            34, 52, 121, 152, 100, 34, 35, 35, 136, 84, 121, 100, 34, 36,
            35, 137, 84, 121, 100, 34, 52, 52, 155, 171, 204, 202, 66, 36,
            52, 138, 171, 204, 202, 66, 35, 51, 33, 1, 105, 100, 16, 35,
            51, 33, 1, 89, 99, 16, 35, 51, 51, 51, 68, 50, 17, 35,
            51, 51, 51, 51, 33, 18, 51, 51, 51, 51, 51, 33, 34, 51,
            51, 51, 51, 51, 33, 34, 51, 51, 51, 51, 51, 51, 51, 51,
            17, 17, 34, 34, 34, 17, 17, 18, 54, 155, 186, 116, 33, 17,
            18, 70, 155, 186, 116, 33, 18, 18, 121, 66, 34, 33, 0, 18,
            18, 121, 66, 17, 17, 0, 18, 18, 121, 100, 50, 17, 17, 18,
            18, 121, 100, 50, 17, 17, 18, 18, 52, 103, 153, 117, 49, 34,
            18, 52, 103, 153, 118, 50, 34, 18, 33, 16, 2, 122, 48, 18,
            17, 33, 16, 2, 122, 48, 18, 17, 35, 34, 52, 119, 32, 18,
            17, 35, 34, 52, 102, 32, 18, 18, 87, 153, 134, 32, 0, 18,
            18, 87, 153, 133, 32, 0, 18, 34, 33, 0, 0, 0, 18, 34,
            34, 33, 0, 0, 0, 18, 34, 34, 34, 34, 34, 34, 34, 34,
            35, 51, 51, 51, 51, 51, 51, 51, 51, 35, 70, 119, 66, 34,
            51, 51, 35, 70, 119, 66, 34, 34, 52, 137, 100, 50, 17, 34,
            35, 52, 137, 100, 50, 17, 34, 35, 69, 103, 84, 50, 34, 34,
            35, 69, 103, 84, 50, 34, 34, 36, 120, 153, 135, 153, 117, 67,
            36, 120, 153, 119, 153, 117, 67, 36, 103, 100, 32, 3, 137, 49,
            36, 103, 100, 16, 3, 137, 49, 35, 52, 121, 82, 53, 118, 32,
            35, 52, 120, 83, 69, 118, 32, 35, 51, 69, 137, 133, 32, 0,
            35, 51, 69, 137, 133, 32, 0, 51, 51, 51, 33, 0, 0, 35,
            51, 51, 51, 33, 0, 0, 34, 51, 51, 51, 51, 51, 51, 51,
            34, 34, 34, 34, 34, 34, 34, 34, 123, 187, 186, 116, 34, 34,
            34, 123, 187, 186, 116, 50, 34, 34, 51, 68, 87, 154, 81, 18,
            34, 35, 68, 87, 154, 81, 18, 34, 34, 17, 88, 116, 16, 2,
            34, 34, 17, 88, 99, 16, 18, 34, 34, 34, 105, 80, 1, 18,
            34, 34, 35, 104, 64, 1, 18, 34, 34, 104, 99, 16, 18, 34,
            18, 35, 104, 99, 16, 18, 34, 18, 35, 105, 65, 1, 34, 34,
            18, 35, 104, 64, 1, 34, 34, 18, 68, 50, 16, 18, 34, 35,
            18, 52, 50, 16, 18, 34, 35, 35, 51, 33, 18, 35, 51, 51,
            35, 51, 33, 18, 35, 51, 51, 51, 51, 51, 51, 51, 51, 51,
            0, 0, 1, 16, 0, 0, 0, 0, 1, 53, 117, 33, 0, 0,
            17, 17, 53, 117, 33, 0, 0, 3, 102, 84, 38, 166, 16, 0,
            3, 102, 100, 38, 166, 17, 0, 3, 119, 116, 20, 150, 16, 0,
            3, 119, 116, 20, 150, 16, 0, 0, 22, 172, 186, 148, 0, 0,
            0, 21, 171, 186, 148, 0, 0, 21, 150, 16, 4, 152, 82, 0,
            21, 166, 16, 4, 152, 82, 0, 21, 152, 99, 20, 151, 65, 0,
            20, 152, 99, 36, 134, 49, 0, 17, 20, 137, 151, 66, 0, 0,
            17, 20, 137, 151, 65, 0, 0, 34, 34, 33, 0, 0, 17, 34,
            34, 34, 33, 0, 0, 17, 34, 51, 34, 34, 34, 34, 34, 34,
            68, 68, 68, 67, 51, 50, 17, 68, 52, 102, 101, 67, 17, 16,
            68, 68, 102, 101, 67, 17, 17, 70, 152, 85, 88, 168, 33, 17,
            54, 168, 69, 88, 168, 33, 33, 54, 150, 0, 37, 151, 32, 17,
            54, 150, 0, 21, 151, 32, 17, 54, 153, 118, 55, 168, 49, 17,
            54, 169, 118, 71, 168, 49, 1, 51, 54, 188, 187, 183, 16, 17,
            67, 54, 187, 187, 183, 16, 17, 67, 50, 20, 120, 150, 16, 17,
            68, 50, 20, 120, 134, 0, 17, 68, 87, 154, 182, 32, 1, 50,
            68, 87, 154, 182, 33, 1, 50, 68, 68, 33, 0, 1, 52, 68,
            68, 67, 33, 0, 1, 52, 68, 68, 68, 68, 68, 68, 68, 68,
        };

        private static readonly float[] Templates = LoadTemplates();

        private float[] _best = Array.Empty<float>();
        private int[] _digit = Array.Empty<int>();
        private float[] _acc = Array.Empty<float>();
        private int[] _prev = Array.Empty<int>();
        private float[] _colSum = Array.Empty<float>();
        private float[] _colSq = Array.Empty<float>();
        private readonly float[] _window = new float[Pixels];
        private readonly char[] _text = new char[32];

        /// <summary>After Read: the strongest match (0..1) one digit step
        /// left of the read's first digit, where a further digit would sit.
        /// Real background stays low here; a leading digit that fell just
        /// under MatchThreshold (so the read is only the end of the score)
        /// does not. ScoreTracker uses it to reject such fragments.</summary>
        public float LeftMatch;

        private int _lockedOffset = -1;
        private int _candidate = -1;
        private int _candidateVotes;
        private int _frame;

        /// <summary>Forget the calibrated vertical offset (the crop or capture
        /// window changed).</summary>
        public void Reset()
        {
            _lockedOffset = -1;
            _candidate = -1;
            _candidateVotes = 0;
            _frame = 0;
        }

        /// <summary>Reads the score from a luma band (row-major, one byte per
        /// pixel) at least BandHeight rows tall; each extra row is one more
        /// vertical offset tried. Returns the digits found, or "" if nothing
        /// looks like a score.</summary>
        public string Read(byte[] band, int width, int height)
        {
            LeftMatch = 0;
            int nx = width - GlyphWidth + 1;
            if (nx <= 0 || height < BandHeight || band.Length < width * height)
                return "";
            EnsureBuffers(width, nx);
            int offsets = height - BandHeight + 1;
            if (_lockedOffset >= 0 && _lockedOffset < offsets && ++_frame % RecheckEvery != 0)
            {
                float score = ReadAt(band, _lockedOffset * width, width, nx, out int end);
                return score > 0 ? ChainText(end) : "";
            }

            // full search: until calibrated, then every RecheckEvery frames
            string best = "";
            float bestScore = 0;
            int bestOffset = -1;
            for (int dy = 0; dy < offsets; dy++)
            {
                float score = ReadAt(band, dy * width, width, nx, out int end);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = dy;
                    best = ChainText(end);
                }
            }
            // only a confident read votes: while the score is hidden, stray
            // weak matches on some other row would otherwise win the lock
            // and the real score go unread there once it is back
            if (bestOffset >= 0 && bestScore >= LockGain * best.Length)
            {
                if (bestOffset == _candidate)
                    _candidateVotes++;
                else
                {
                    _candidate = bestOffset;
                    _candidateVotes = 1;
                }
                if (_candidateVotes >= LockVotes)
                    _lockedOffset = bestOffset;
            }
            return best;
        }

        /// <summary>Scores one vertical offset; returns the best chain's
        /// total (0 = nothing) and where it ends.</summary>
        private float ReadAt(byte[] band, int start, int width, int nx, out int end)
        {

            // per-column sums for each window's mean and spread
            for (int x = 0; x < width; x++)
            {
                float s = 0, q = 0;
                for (int y = 0, i = start + x; y < BandHeight; y++, i += width)
                {
                    float v = band[i];
                    s += v;
                    q += v * v;
                }
                _colSum[x] = s;
                _colSq[x] = q;
            }

            for (int x = 0; x < nx; x++)
            {
                double sum = 0, sq = 0;
                for (int c = 0; c < GlyphWidth; c++)
                {
                    sum += _colSum[x + c];
                    sq += _colSq[x + c];
                }
                double spread = sq - sum * sum / Pixels; // sum of squared deviations
                if (spread < 1e-3)
                {
                    _best[x] = 0;
                    _digit[x] = 0;
                    continue;
                }
                int k = 0;
                for (int y = 0, row = start + x; y < BandHeight; y++, row += width)
                    for (int c = 0; c < GlyphWidth; c++)
                        _window[k++] = band[row + c];

                // templates are zero-mean, so the window's mean drops out
                float best = float.MinValue;
                int digit = 0;
#if NETFRAMEWORK
                // .NET Framework (the Windows build) has no in-box SIMD
                for (int d = 0, o = 0; d < 10; d++, o += Pixels)
                {
                    float dot = 0;
                    for (int i = 0; i < Pixels; i++)
                        dot += Templates[o + i] * _window[i];
#else
                int lanes = Vector<float>.Count;
                for (int d = 0, o = 0; d < 10; d++, o += Pixels)
                {
                    var acc = Vector<float>.Zero;
                    int i = 0;
                    for (; i <= Pixels - lanes; i += lanes)
                        acc += new Vector<float>(Templates, o + i) * new Vector<float>(_window, i);
                    float dot = Vector.Dot(acc, Vector<float>.One);
                    for (; i < Pixels; i++)
                        dot += Templates[o + i] * _window[i];
#endif
                    if (dot > best)
                    {
                        best = dot;
                        digit = d;
                    }
                }
                _best[x] = (float)(best / Math.Sqrt(spread));
                _digit[x] = digit;
            }

            // best chain of above-threshold matches, left to right
            end = -1;
            float endAcc = 0;
            for (int x = 0; x < nx; x++)
            {
                _prev[x] = -1;
                float gain = _best[x] - MatchThreshold;
                if (gain <= 0)
                {
                    _acc[x] = float.NegativeInfinity;
                    continue;
                }
                float acc = gain;
                int lo = Math.Max(0, x - MaxStep), hi = x - MinStep;
                if (hi >= lo)
                {
                    int j = lo;
                    for (int i = lo + 1; i <= hi; i++)
                        if (_acc[i] > _acc[j])
                            j = i;
                    if (_acc[j] > 0)
                    {
                        acc += _acc[j];
                        _prev[x] = j;
                    }
                }
                _acc[x] = acc;
                if (acc > endAcc)
                {
                    endAcc = acc;
                    end = x;
                }
            }
            return endAcc;
        }

        private string ChainText(int end)
        {
            int n = 0, first = end;
            for (int x = end; x >= 0 && n < _text.Length; x = _prev[x])
            {
                _text[n++] = (char)('0' + _digit[x]);
                first = x;
            }
            float left = 0;
            for (int x = Math.Max(0, first - MaxStep); x <= first - MinStep; x++)
                if (_best[x] > left)
                    left = _best[x];
            LeftMatch = left;
            Array.Reverse(_text, 0, n);
            return new string(_text, 0, n);
        }

        private void EnsureBuffers(int width, int nx)
        {
            if (_best.Length < nx)
            {
                _best = new float[nx];
                _digit = new int[nx];
                _acc = new float[nx];
                _prev = new int[nx];
            }
            if (_colSum.Length < width)
            {
                _colSum = new float[width];
                _colSq = new float[width];
            }
        }

        /// <summary>Decodes the templates and makes each zero-mean with unit
        /// length, so a dot product with a window is its correlation.</summary>
        private static float[] LoadTemplates()
        {
            var raw = new byte[10 * Pixels];
            for (int i = 0; i < raw.Length; i++)
                raw[i] = (byte)((TemplateData[i >> 1] >> ((i & 1) == 0 ? 4 : 0) & 15) * 17);
            var t = new float[raw.Length];
            for (int d = 0; d < 10; d++)
            {
                int o = d * Pixels;
                double mean = 0;
                for (int i = 0; i < Pixels; i++)
                    mean += raw[o + i];
                mean /= Pixels;
                double norm = 0;
                for (int i = 0; i < Pixels; i++)
                    norm += (raw[o + i] - mean) * (raw[o + i] - mean);
                norm = Math.Sqrt(norm);
                for (int i = 0; i < Pixels; i++)
                    t[o + i] = (float)((raw[o + i] - mean) / norm);
            }
            return t;
        }
    }
}
