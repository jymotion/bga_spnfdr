using System;
#if !NETFRAMEWORK
using System.Numerics;
#endif

namespace BgaSpnfdr
{
    /// <summary>
    /// Reads the BattleTanx score straight from pixels by matching the game's
    /// own digit shapes; no OCR engine or extra download. Learned from a real
    /// full-game run (footage branch) and checked against a hand-verified
    /// ground truth there (see Tools/ReaderBench).
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
    ///    same offset wins LockVotes reads running, only that row is read,
    ///    with a full search every RecheckEvery frames in case it changed.
    ///    Call Reset() when the crop changes.
    /// Validation (multiples of 25, confirmation reads, ...) is not done
    /// here; reads go through Core/ScoreTracker like the Windows OCR's do.
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
        private const int RecheckEvery = 30;

        // 10 digits x 18 rows x 14 columns, brightness 0-255, row-major.
        private const string TemplateData =
            "LCssLC4vLi8tKysqKigqKio0T2hxZEEoKioqMyorKDZTanFoQigqKys0JTlyi2RNRlyUoVAiJy0kPHWNYkhCWJelTyAnLxg+" +
            "gZA0AQYwhZk0AxgvGD6EkjICBi6GnDABGC8aPoSRMAIWQnp/KQEaMBs/g5AwAhhDeoEpARowGj+EkS4CGEiJkzQDGzAaQIOQ" +
            "MAIZR4mWNAUbMRU8dYhOKCdFiZQxAhowEzxwg08qK0aFjiwBGjEXKC1Eep6hi08eBAAcLRkqKT15oKGMTRgDAR4uJS0tLCYY" +
            "CgEAAg4gKC0nLS0rJRgKAQECECMpLCssLS0tLSopKyosKyssNDQyMjI0NDM0NDY3ODozNDIxMTQzMjM0NjhoozQ4NDY2NjQy" +
            "MjQ1OW6iOEaCtrykTCwuMjZDbmI4SYOyvqdKJCwyN0VqXjc6QFCWoCoGHjQ2RGleNzY+SZSfKwYgNDZAaF42ODxIkp4sBR80" +
            "Njk/Sjk6PUuSniwFHzQ2OD5HLjg+S5ScLAUiNDY2ODIsNzpOlpwrBiA0NjY2MCY2PkyYnioHIzQ1Njg6Jjg+UJadMAgkNDAy" +
            "PEEpRoi+xcbAoGg+OUBaayhFisDEv8CiaEA2PlhsOj4+OA0AAQEAAB40Oiw4NzQwDAAAAAAAHDQ4Kjs6Ojw7Ozo6Ojo5Ojk5" +
            "GBkdICAfHhoVExAODQsWE0ebwbyNQBsVExMUFxMQRp3AvY9DIBkWFRYYFhYkNzo0WpiELxUXFxcZGiQwNC9VmYUzFBMSFRUX" +
            "Eg4HBS95fEYkEg4UFRYUDwYFMHt9SyUPCxQRFhQTEhdEjHMXAQQLERIVFRYWHEmLbRUAAw4TFBUUGEaPgTQJAAIKExQVFBQc" +
            "SZKDMgYCBA0WERQgTpaMQSEZDw0MEhIRFiNPmYxGJB0WDQwPEREYLE+Dl5yYjW5GKRIPEhYmS32RmpWLcEMmDw4QExQNBAEB" +
            "AQAAAAEBAg4QDwoDAQABAAAAAAEDDREREhEQDw8PDw8PDw8QIB8hJCQlIyIgISEhISEeIThxq8CmWSQcHh0dHiEhOXStwqhg" +
            "Jh8fHB4eIzI9OjQ4UpWsWiAdLDcjMjk1MDRRl7BbIBstOyAgFw8NETB3lD4KEyk1ICAZEA4VN3uSOAsTKTUfISIoQnikjFYh" +
            "BhMnMyAhIylEd6CHWSAIEygzICAhIRsYLHqYRRAYKjUgICIgGRkqe5pGERgqNSAnKyMZGzeJokcOEyg1IisvKB0fN4WeRAwT" +
            "JzUlMVJ2l56JVB8FBhIfLSQuU3iYn4hQGQMHFBwoICEbDwQBAQECCREYHR8gHxoNAwEBAQIKEhgcGyAfHx4dHBoaGxsdHR0c" +
            "LSwsLC0uLCwsLSwsLC4uLi8vLi4yMiwqKywsOC8uMC8uMDU3LCosLCw4LS0uMTZVlrpzNSklKi4tLC40OViYunI5JiMoLywq" +
            "LEF6lZyOaD8iHCk0KiosQ32WnI5nPyIcKDcoOImQUEZzkWg8IRwpQio7ipRTR3WWaz4gGytDMkWUtLG4yNDHokcgKD0xRY6v" +
            "rrTK0cioRx4pOzEsHxAEF16UbTwJBiAyMSwcDwQUWpFqOAkHHjAsLi0sLDE8PSwdExYqMi4wMDEyMjY4KRgXHC0xLi8uMDIx" +
            "Mi4dFR4lLDIwMDAwMTAxKx4WICYtMTExMTExMTAvLi4wMDEyFRUXGBocHB0bGhgYGBgYHTZjmLy/q3ZGKRcYGBggPGefvsGs" +
            "d0cnGBkaFyd7l0ogHh0eFQcIEhoWKHqVShwYGRgTBwgRHBQneqBqQCwaExIREhghFCR5nWxEMB8VFBQVGSETIDBMZH6Xm39Y" +
            "LhkaJBYcLUhgfJSaf14xGhojGBobFw4IBh56ojoHFCAWGBsZDAYFHXimOgQSIBQVJCwiIjNMcXEfARIcERQoMSUnNExqahwB" +
            "EhsUJE96lJiEXyYDAAITGxUgTXiVmoBcIgIBAxYaHCAhFwcBAQEAAxUgICIbHx4WBwEBAgEFFiEhIiAgHyAeHx8fHyAhIiIj" +
            "KissLC0uLjAuLi0sKywrLCwsKTBHYXpzPycqKiwsLiwqMklie3I9JSknIiorQoGdbEMyIg4LGyMhLC5Hgp1qQzAgDQwaIyQx" +
            "QVJocFlAMicdHCAnJjVDVGpyW0Q0KCEeIiopQG+QnZyCeJWYdVY8MCpBcI+dm394kZV6Wj8xJz5hcmRHGgIEMYiSOxYmPF9w" +
            "YkYXAQIwi5Q7Eic0Nkl+kU0kO1x7bB8DJjM2R3+PTiw+W3hlHQIpMjE3RVyMn4ldKQcABSozMjVBWYmfh1ojAgEHMTEwMDEt" +
            "HxAEAAEGHSsxMTEvLyscDwMAAAYcKjAwMC8uLi0tLi4tLi8vJSQnJykpKCgkIyMhHx8iJ2+4vsDArHc8KiIgIyAmcLnCwsOx" +
            "eT8tIRwmHSArOERLW2+ap1wTFiYdICgyPkNWb5ejWBATJx8hIBwWFliNcD8WAgglHyIhHRYUWo9uOhICCyQhJCUhICduk00G" +
            "BQ8UJB8mJiQkLWyPSAYGEBYjGyMlKF+OZjIPAQwgIyIYIiQrY5BiLAsACyQiIhAhJi5nkUoJBBEaJSUjDyEpMWaLRwcEExsk" +
            "JCIXKT1INiQNAgsiJykoLxkqOkYzHgwBDyQoKSktKTI1MSIWFyMoLjEyMjMpMTIwJBgZJSovMTIxMjQ0NDMyMjIxMTIzMzIx" +
            "CAgGCAgKDAgICAUCAgQICAgVNVpzTx4MBgMCAgkKDBg4VXZUIA0GAgICBjBiYlxCKmqrahQHBAIEMGRoYD0maa5tGQoGAgQ4" +
            "eHx0PgpEll4WCAQEBTZ3fHg+CkSSXhQFBAYGBg9esMTAr5RIAAQEBgYHF1qrwLyulEYBAAQHElqcYRQAAkGUgFYpBggRWKRk" +
            "EAAAQJaBWSYGBgtSk4RkNBZIknI8GQUGDUyRgGY2GkyMajgYAAIKDBBKiJ2dekcaAAACBwwKCkWGmptyQhIBBAEGJigoJSAS" +
            "BQICBgwYJyYoKCcmHRAEAgIHDhkoKCwrKCcmJigpKSgmJygoQkE8RUQ+PDo5OTonFRBAQTpLX2dtVj8zFgwJAkBCPUtga25Y" +
            "PDEWDxAOPWWdkE5OVYKkgiAQFgs2YKOOSFBRgqeEJBAaEC9foGoCAhpZlHUhAQsKL16fZgIHFVaXeiQHDQwxY6CfdF80dKqJ" +
            "LQkLCzRfoqB8YT50sY4xDAUKOjoyarjEu8K+chEACQo9NS1luMLCwsJzDwAOCz85OCAVQ3CHkWMMABEQQT81HhVAd4aNXwAA" +
            "FhBJSFJ0n6+4bSUEAhc2KUtMUHCfrcJsHwkBFTspSkxJPSEWAgEEDDpBQ0NJSUI2HhECAgQLO0FCQ0VEQ0NCQUBBQUE+QEFE";

        private static readonly float[] Templates = LoadTemplates();

        private float[] _best = Array.Empty<float>();
        private int[] _digit = Array.Empty<int>();
        private float[] _acc = Array.Empty<float>();
        private int[] _prev = Array.Empty<int>();
        private float[] _colSum = Array.Empty<float>();
        private float[] _colSq = Array.Empty<float>();
        private readonly float[] _window = new float[Pixels];
        private readonly char[] _text = new char[32];

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
            if (bestOffset >= 0)
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
                // .NET Framework (Windows side-by-side test build): no in-box SIMD
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
            int n = 0;
            for (int x = end; x >= 0 && n < _text.Length; x = _prev[x])
                _text[n++] = (char)('0' + _digit[x]);
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
            byte[] raw = Convert.FromBase64String(TemplateData);
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
