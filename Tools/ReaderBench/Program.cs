using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;

namespace BgaSpnfdr.Bench
{
    /// <summary>
    /// Plays the reference run through ScoreBand + DigitReader + ScoreTracker
    /// exactly as the apps do, and compares against ground-truth.txt (one line per
    /// stretch where the score is on screen: part, start s, end s, score;
    /// sampled at 10 fps and checked by eye frame-group by frame-group).
    ///
    /// Usage (needs ffmpeg; the recording is on the footage branch):
    ///   git worktree add ../footage origin/footage
    ///   dotnet run -c Release --project Tools/ReaderBench -- [fps] [footage dir] [scale] [misalignment]
    /// e.g. "-- 30 ../footage/footage 1 0". Scale resizes the video first to
    /// simulate other capture resolutions (2 = 1280x960); misalignment moves
    /// the game-picture crop down by that many pixels, as if auto-crop were
    /// a little off. Build with -p:WindowsRules=true for the Windows app's
    /// confirmation rule (and run it at 10 fps).
    /// </summary>
    internal static class Program
    {
        // the game picture in the reference 640x480 capture
        private static readonly Rectangle ReferenceCrop = new Rectangle(14, 2, 617, 474);

        private static int Main(string[] args)
        {
            double fps = args.Length > 0 ? double.Parse(args[0]) : 30;
            string footage = args.Length > 1 ? args[1] : "footage";
            double scale = args.Length > 2 ? double.Parse(args[2]) : 1;
            int shift = args.Length > 3 ? int.Parse(args[3]) : 0;
            int frameW = (int)Math.Round(640 * scale / 2) * 2, frameH = (int)Math.Round(480 * scale / 2) * 2;
            var crop = new Rectangle((int)Math.Round(ReferenceCrop.X * scale), (int)Math.Round(ReferenceCrop.Y * scale) + shift,
                (int)Math.Round(ReferenceCrop.Width * scale), (int)Math.Round(ReferenceCrop.Height * scale));
            ScoreBand.RowsNeeded(crop, frameH, out int first, out int last);
            int rowCount = last - first + 1;
            string[] parts = Directory.GetFiles(footage, "part*.mp4").OrderBy(p => p).ToArray();
            if (parts.Length == 0)
            {
                Console.Error.WriteLine($"no footage/part*.mp4 found under '{footage}' (check out the footage branch)");
                return 1;
            }
            var truth = LoadTruth(Path.Combine(AppContext.BaseDirectory, "ground-truth.txt"));
            var trueScores = new HashSet<long>(truth.Select(t => t.Score));

            var reader = new DigitReader();
            var tracker = new ScoreTracker();
            // a "note" for every possible score, so HandleRead reports each
            // newly confirmed score
            var route = new Dictionary<long, string>();
            for (long s = 0; s < 1000000; s += 25) route[s] = s.ToString();

            var rows = new byte[frameW * 4 * rowCount];
            var band = new byte[ScoreBand.Width * ScoreBand.Rows];
            long frames = 0, visible = 0, correct = 0, wrongPlausible = 0, hidden = 0, falseReads = 0;
            var wrongConfirmed = new SortedSet<long>();
            var firstSeen = new Dictionary<long, double>();  // global seconds
            var confirmedAt = new Dictionary<long, double>();
            var readTime = new Stopwatch();
            double offset = 0;
            for (int p = 0; p < parts.Length; p++)
            {
                var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                    $"-v error -i \"{parts[p]}\" -vf fps={fps},scale={frameW}:{frameH}:flags=bicubic,crop={frameW}:{rowCount}:0:{first}:exact=1 -f rawvideo -pix_fmt bgra -")
                { RedirectStandardOutput = true, UseShellExecute = false });
                var stdout = ffmpeg.StandardOutput.BaseStream;
                double t = 0, duration = 0;
                for (long k = 0; ReadExactly(stdout, rows); k++, frames++)
                {
                    t = k / fps;
                    readTime.Start();
                    ScoreBand.Fill(rows, frameW * 4, first, last, frameW, crop, false, band);
                    string read = reader.Read(band, ScoreBand.Width, ScoreBand.Rows);
                    readTime.Stop();
                    string note = tracker.HandleRead(read, route, reader.LeftMatch);
                    long expected = Expected(truth, p, t);
                    if (note != null)
                    {
                        long score = long.Parse(note);
                        if (!trueScores.Contains(score)) wrongConfirmed.Add(score);
                        else if (!confirmedAt.ContainsKey(score)) confirmedAt[score] = offset + t;
                    }
                    if (expected >= 0)
                    {
                        visible++;
                        if (!firstSeen.ContainsKey(expected)) firstSeen[expected] = offset + t;
                        if (read == expected.ToString()) correct++;
                        else if (Plausible(read)) wrongPlausible++;
                    }
                    else if (expected == -1)
                    {
                        hidden++;
                        if (Plausible(read)) falseReads++;
                    }
                    duration = t;
                }
                ffmpeg.WaitForExit();
                offset += duration + 1 / fps;
            }

            var missed = firstSeen.Keys.Where(s => !confirmedAt.ContainsKey(s)).OrderBy(s => s).ToList();
            var delays = firstSeen.Where(kv => confirmedAt.ContainsKey(kv.Key))
                .Select(kv => Math.Max(0, confirmedAt[kv.Key] - kv.Value)).OrderBy(d => d).ToList();
            Console.WriteLine($"frames read: {frames} at {fps} fps ({offset / 60:F1} min of footage), " +
                              $"capture {frameW}x{frameH}, crop {crop}, rules: confirm after " +
#if READ_EVERY_FRAME
                              "3 reads (Linux)");
#else
                              "2 reads (Windows)");
#endif
            Console.WriteLine($"score visible: {visible} frames, read exactly right {100.0 * correct / visible:F1}%, wrong but plausible {wrongPlausible}");
            Console.WriteLine($"score hidden: {hidden} frames, plausible false reads {falseReads}");
            Console.WriteLine($"scores on screen: {firstSeen.Count}, confirmed {firstSeen.Count - missed.Count}, missed [{string.Join(", ", missed)}]");
            Console.WriteLine($"WRONG scores confirmed: [{string.Join(", ", wrongConfirmed)}]");
            Console.WriteLine($"delay to confirm: median {delays[delays.Count / 2]:F2}s, 90th pct {delays[(int)(delays.Count * 0.9)]:F2}s, max {delays.Last():F2}s");
            Console.WriteLine($"band + reader CPU: {readTime.Elapsed.TotalMilliseconds / frames:F3} ms per frame " +
                              $"({100 * readTime.Elapsed.TotalSeconds / frames * fps:F2}% of one core at {fps} fps)");
            return 0;
        }

        private static bool Plausible(string read) =>
            read.Length > 0 && read.Length <= 6 && !(read.Length > 1 && read[0] == '0')
            && long.Parse(read) % 25 == 0;

        private struct Segment { public int Part; public double Start, End; public long Score; }

        private static List<Segment> LoadTruth(string path) =>
            File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Select(l =>
            {
                var f = l.Split(' ');
                return new Segment
                {
                    Part = int.Parse(f[0]), Start = double.Parse(f[1]),
                    End = double.Parse(f[2]), Score = long.Parse(f[3]),
                };
            }).ToList();

        /// <summary>The true score at time t: the 10 fps samples on either
        /// side must agree. -1 = hidden on both, -2 = a change happens
        /// between them (frame excluded from per-frame stats).</summary>
        private static long Expected(List<Segment> truth, int part, double t)
        {
            double k0 = Math.Floor(t * 10 + 1e-6) / 10, k1 = k0 + 0.1;
            long a = At(truth, part, k0), b = At(truth, part, k1);
            if (Math.Abs(t - k0) < 1e-6) return a;
            return a == b ? a : -2;
        }

        private static long At(List<Segment> truth, int part, double t)
        {
            foreach (var s in truth)
                if (s.Part == part && t >= s.Start - 1e-6 && t <= s.End + 1e-6)
                    return s.Score;
            return -1;
        }

        private static bool ReadExactly(Stream s, byte[] buffer)
        {
            int got = 0;
            while (got < buffer.Length)
            {
                int n = s.Read(buffer, got, buffer.Length - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }
}
