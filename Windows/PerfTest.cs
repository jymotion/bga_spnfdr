#if PERF_TEST
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace BgaSpnfdr
{
    /// <summary>
    /// 30-reads-a-second test build only (dotnet build ... -p:PerfTest=true;
    /// compiled out of the normal build). Reads 30 times a second with the
    /// 3-read rule, like the Linux build, and switches to 10 a second every
    /// other minute so both rates are measured on the same PC in the same
    /// session. Logs to %APPDATA%\bga_spnfdr\perftest\(date-time)\:
    ///  - perf.csv: one line per minute: reads per second achieved, capture
    ///    and read time, CPU of this app, of the window compositor (dwm.exe,
    ///    which does part of each capture's work) and of the whole system
    ///  - scores.csv: every confirmed score, with the rate it was read at
    /// </summary>
    internal sealed class PerfTest
    {
        private const int SegmentSeconds = 60;

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        public readonly string Dir;
        private readonly StreamWriter _perf, _scores;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _fast = true;
        private double _segmentStart, _lastSideTasks = -1;
        private long _frames, _failed;
        private double _captureMs, _captureMax, _readMs;
        private TimeSpan _cpu0, _dwm0;
        private long _idle0, _kernel0, _user0;
        private long _lastScore = -1;
        private Size _size;
        private string _last = "measuring the first minute…";

        public PerfTest()
        {
            Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "bga_spnfdr", "perftest", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(Dir);
            _perf = new StreamWriter(Path.Combine(Dir, "perf.csv")) { AutoFlush = true };
            _scores = new StreamWriter(Path.Combine(Dir, "scores.csv")) { AutoFlush = true };
            _perf.WriteLine("seconds,target_reads_per_s,reads_per_s,capture_ms_avg,capture_ms_max,read_ms_avg," +
                            "app_cpu_pct_of_one_core,dwm_cpu_pct_of_one_core,system_cpu_pct_of_all_cores," +
                            "cores,capture_size,setup_window_open,failed_captures");
            _scores.WriteLine("seconds,target_reads_per_s,confirmed_score");
            StartSegment();
        }

        /// <summary>Timer interval for the current rate (ms).</summary>
        public int Interval => _fast ? 30 : 100;

        private double Now => _clock.Elapsed.TotalSeconds;

        private static string F(double v, string format = "F2") => v.ToString(format, CultureInfo.InvariantCulture);

        private void StartSegment()
        {
            _segmentStart = Now;
            _frames = _failed = 0;
            _captureMs = _captureMax = _readMs = 0;
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

        public void OnCapture(double ms, Bitmap bmp)
        {
            _captureMs += ms;
            _captureMax = Math.Max(_captureMax, ms);
            if (bmp == null) _failed++;
            else _size = bmp.Size;
        }

        public void OnRead(double ms, long confirmedScore)
        {
            _frames++;
            _readMs += ms;
            if (confirmedScore != _lastScore)
            {
                _lastScore = confirmedScore;
                _scores.WriteLine($"{F(Now)},{(_fast ? 30 : 10)},{confirmedScore}");
            }
        }

        /// <summary>Preview, auto-crop and status stay at ~10 a second, as in
        /// the normal build.</summary>
        public bool SideTasksDue()
        {
            if (Now - _lastSideTasks < 0.095)
                return false;
            _lastSideTasks = Now;
            return true;
        }

        /// <summary>Every timer tick: ends a minute's measurement when due
        /// and switches rate. Returns true when the interval changed.</summary>
        public bool Tick(bool setupOpen)
        {
            double seconds = Now - _segmentStart;
            if (seconds < SegmentSeconds)
                return false;

            double app = (Process.GetCurrentProcess().TotalProcessorTime - _cpu0).TotalSeconds / seconds * 100;
            var dwmNow = DwmCpu();
            string dwm = _dwm0 == TimeSpan.MinValue || dwmNow == TimeSpan.MinValue
                ? "n/a" : F((dwmNow - _dwm0).TotalSeconds / seconds * 100);
            GetSystemTimes(out long idle, out long kernel, out long user);
            long busy = (kernel - _kernel0) + (user - _user0); // kernel time includes idle
            double system = busy > 0 ? (1 - (idle - _idle0) / (double)busy) * 100 : 0;
            double captures = Math.Max(1, _frames + _failed);
            double rate = _frames / seconds;
            _perf.WriteLine($"{F(Now, "F0")},{(_fast ? 30 : 10)},{F(rate, "F1")},{F(_captureMs / captures)},{F(_captureMax)}," +
                            $"{F(_readMs / Math.Max(1, _frames))},{F(app)},{dwm},{F(system)},{Environment.ProcessorCount}," +
                            $"{_size.Width}x{_size.Height},{(setupOpen ? "yes" : "no")},{_failed}");
            _last = $"{(_fast ? 30 : 10)}/s target got {F(rate, "F1")}/s, capture {F(_captureMs / captures, "F1")} ms, " +
                    $"read {F(_readMs / Math.Max(1, _frames))} ms, app CPU {F(app, "F1")}%, dwm {dwm}%";
            _fast = !_fast;
            StartSegment();
            return true;
        }

        public string Summary => $"TEST reading {(_fast ? 30 : 10)}/s (switches each minute) · last minute: {_last} · log: {Dir}";
    }
}
#endif
