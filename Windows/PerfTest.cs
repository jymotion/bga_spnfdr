#if PERF_TEST
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BgaSpnfdr
{
    /// <summary>
    /// 30-reads-a-second test build only (dotnet build ... -p:PerfTest=true;
    /// compiled out of the other builds). Rotates each minute between three
    /// ways of reading, so all are measured on the same PC in the same session:
    ///  1. PrintWindow, 30 reads a second (3-read rule, like the Linux build)
    ///  2. Windows.Graphics.Capture, 30 reads a second (3-read rule), copying
    ///     only the score strip back from the graphics card
    ///  3. PrintWindow, 10 reads a second (2-read rule): today's app
    /// Preview, auto-crop and status stay at ~10 a second throughout. Logs to
    /// %APPDATA%\bga_spnfdr\perftest\(date-time)\:
    ///  - perf.csv: one line per minute: method, reads per second achieved,
    ///    capture time (getting the score rows to the CPU, plus the whole
    ///    picture when the preview or auto-crop needs it) and read time, CPU
    ///    of this app, of the window compositor (dwm.exe, which does part of
    ///    each capture's work) and of the whole system, scores confirmed
    ///  - scores.csv: every confirmed score, with the method it was read with
    ///  - summary.csv: averages and totals per method so far, rewritten each
    ///    minute
    /// </summary>
    internal sealed class PerfTest
    {
        private const int SegmentSeconds = 60;

        private static readonly string[] MethodNames = { "PrintWindow", "GraphicsCapture", "PrintWindow" };
        private static readonly int[] MethodRates = { 30, 30, 10 };

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        public readonly string Dir;
        private readonly StreamWriter _perf, _scores;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _method; // index into MethodNames/MethodRates
        private double _segmentStart;
        private long _frames, _failed, _graphicsReads;
        private int _confirmed;
        private double _captureMs, _captureMax, _readMs;
        private TimeSpan _cpu0, _dwm0;
        private long _idle0, _kernel0, _user0;
        private long _lastScore = -1;
        private Size _size;
        private string _error;
        private string _last = "measuring the first minute…";

        // per-method totals for summary.csv
        private readonly int[] _minutes = new int[3], _minutesOpen = new int[3], _dwmMinutes = new int[3], _scoreTotal = new int[3];
        private readonly double[] _rateSum = new double[3], _captureSum = new double[3], _readSum = new double[3],
            _appSum = new double[3], _dwmSum = new double[3], _systemSum = new double[3];

        public PerfTest()
        {
            Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "bga_spnfdr", "perftest", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(Dir);
            _perf = new StreamWriter(Path.Combine(Dir, "perf.csv")) { AutoFlush = true };
            _scores = new StreamWriter(Path.Combine(Dir, "scores.csv")) { AutoFlush = true };
            _perf.WriteLine("seconds,method,target_reads_per_s,reads_per_s,graphics_capture_reads_pct," +
                            "capture_ms_avg,capture_ms_max,read_ms_avg," +
                            "app_cpu_pct_of_one_core,dwm_cpu_pct_of_one_core,system_cpu_pct_of_all_cores," +
                            "cores,capture_size,setup_window_open,failed_captures,scores_confirmed,graphics_capture_error");
            _scores.WriteLine("seconds,method,target_reads_per_s,confirmed_score");
            StartSegment();
        }

        /// <summary>Whether this minute captures with Windows.Graphics.Capture.</summary>
        public bool Graphics => _method == 1;

        /// <summary>Whether this minute reads 30 a second (else 10).</summary>
        public bool Fast => MethodRates[_method] == 30;

        private string Method => MethodNames[_method];

        private double Now => _clock.Elapsed.TotalSeconds;

        private static string F(double v, string format = "F2") => v.ToString(format, CultureInfo.InvariantCulture);

        private void StartSegment()
        {
            _segmentStart = Now;
            _frames = _failed = _graphicsReads = 0;
            _confirmed = 0;
            _captureMs = _captureMax = _readMs = 0;
            _error = null;
            _cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            _dwm0 = DwmCpu();
            GetSystemTimes(out _idle0, out _kernel0, out _user0);
        }

        private static TimeSpan DwmCpu()
        {
            try
            {
                var total = TimeSpan.Zero;
                foreach (var p in Process.GetProcessesByName("dwm"))
                    using (p) total += p.TotalProcessorTime;
                return total;
            }
            catch (Exception)
            {
                return TimeSpan.MinValue; // not allowed to read it on this PC
            }
        }

        public void OnCapture(double ms, bool ok, FrameSource source)
        {
            _captureMs += ms;
            _captureMax = Math.Max(_captureMax, ms);
            if (!ok)
                _failed++;
            else
                _size = source.Size;
            if (source.UsedGraphicsCapture)
                _graphicsReads++;
            else if (Graphics && source.Error != null)
                _error = source.Error;
        }

        public void OnRead(double ms, long confirmedScore)
        {
            _frames++;
            _readMs += ms;
            if (confirmedScore != _lastScore)
            {
                _lastScore = confirmedScore;
                _confirmed++;
                _scores.WriteLine($"{F(Now)},{Method},{MethodRates[_method]},{confirmedScore}");
            }
        }

        /// <summary>Every timer tick: ends a minute's measurement when due
        /// and moves on to the next method. Returns true when it did.</summary>
        public bool Tick(bool setupOpen)
        {
            double seconds = Now - _segmentStart;
            if (seconds < SegmentSeconds)
                return false;

            double app = (Process.GetCurrentProcess().TotalProcessorTime - _cpu0).TotalSeconds / seconds * 100;
            var dwmNow = DwmCpu();
            bool dwmKnown = _dwm0 != TimeSpan.MinValue && dwmNow != TimeSpan.MinValue;
            double dwmPct = dwmKnown ? (dwmNow - _dwm0).TotalSeconds / seconds * 100 : 0;
            string dwm = dwmKnown ? F(dwmPct) : "n/a";
            GetSystemTimes(out long idle, out long kernel, out long user);
            long busy = (kernel - _kernel0) + (user - _user0); // kernel time includes idle
            double system = busy > 0 ? (1 - (idle - _idle0) / (double)busy) * 100 : 0;
            double captures = Math.Max(1, _frames + _failed);
            double rate = _frames / seconds;
            double captureAvg = _captureMs / captures, readAvg = _readMs / Math.Max(1, _frames);
            string error = (_error ?? "").Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ');
            _perf.WriteLine($"{F(Now, "F0")},{Method},{MethodRates[_method]},{F(rate, "F1")},{F(_graphicsReads * 100.0 / captures, "F0")}," +
                            $"{F(captureAvg)},{F(_captureMax)},{F(readAvg)},{F(app)},{dwm},{F(system)},{Environment.ProcessorCount}," +
                            $"{_size.Width}x{_size.Height},{(setupOpen ? "yes" : "no")},{_failed},{_confirmed},{error}");
            _last = $"{Method} {MethodRates[_method]}/s got {F(rate, "F1")}/s, capture {F(captureAvg, "F1")} ms, " +
                    $"read {F(readAvg)} ms, app CPU {F(app, "F1")}%, dwm {dwm}%, {_confirmed} scores" +
                    (Graphics && _graphicsReads < _frames ? $", fell back to PrintWindow: {error}" : "");

            int m = _method;
            _minutes[m]++;
            if (setupOpen) _minutesOpen[m]++;
            _rateSum[m] += rate;
            _captureSum[m] += captureAvg;
            _readSum[m] += readAvg;
            _appSum[m] += app;
            _systemSum[m] += system;
            if (dwmKnown)
            {
                _dwmMinutes[m]++;
                _dwmSum[m] += dwmPct;
            }
            _scoreTotal[m] += _confirmed;
            WriteSummary();

            _method = (_method + 1) % MethodNames.Length;
            StartSegment();
            return true;
        }

        private void WriteSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("method,target_reads_per_s,minutes,minutes_with_setup_window_open,reads_per_s,capture_ms_avg,read_ms_avg," +
                          "app_cpu_pct_of_one_core,dwm_cpu_pct_of_one_core,system_cpu_pct_of_all_cores,scores_confirmed,scores_per_minute");
            for (int m = 0; m < MethodNames.Length; m++)
            {
                int n = _minutes[m];
                if (n == 0)
                    continue;
                string dwm = _dwmMinutes[m] > 0 ? F(_dwmSum[m] / _dwmMinutes[m]) : "n/a";
                sb.AppendLine($"{MethodNames[m]},{MethodRates[m]},{n},{_minutesOpen[m]},{F(_rateSum[m] / n, "F1")}," +
                              $"{F(_captureSum[m] / n)},{F(_readSum[m] / n)},{F(_appSum[m] / n)},{dwm},{F(_systemSum[m] / n)}," +
                              $"{_scoreTotal[m]},{F(_scoreTotal[m] / (double)n, "F1")}");
            }
            try { File.WriteAllText(Path.Combine(Dir, "summary.csv"), sb.ToString()); }
            catch (IOException) { } // open in a spreadsheet: next minute's write will do
            catch (UnauthorizedAccessException) { }
        }

        public string Summary => $"TEST {Method} {MethodRates[_method]}/s (switches each minute) · last minute: {_last} · log: {Dir}";
    }
}
#endif
