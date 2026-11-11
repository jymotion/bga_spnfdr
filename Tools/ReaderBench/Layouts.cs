using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace BgaSpnfdr.Bench
{
    /// <summary>
    /// Puts the reference run into the kinds of picture the apps get
    /// captured (with ffmpeg), feeds each to Core/GameFinder the way the apps
    /// do (10 frames a second: Detect, or a wrong crop to start from, then
    /// Check until the score confirms it), and says how close the crop is to
    /// the truth at setup and once the score has been found.
    ///
    /// Usage: dotnet run -c Release --project Tools/ReaderBench -- layouts [footage dir]
    ///
    /// "menu" cases sit on the game's menu for 9 s (its first 1.5 s,
    /// repeated), are set up there, and then the run starts: its score first
    /// has two digits 16.1 s in. A crop counts as OK when the score band is
    /// within 4 reference rows, the height within 3.5%, the width between
    /// 3.5% narrower and 9% wider, and the left edge 28 px left to 24 px
    /// right of the truth: what the reader takes without loss (see README).
    /// </summary>
    internal static class Layouts
    {
        private const string Menu = "[0:v]fps=10,split[a][b];[a]trim=end_frame=15,loop=loop=5:size=15:start=0,setpts=N/10/TB[m];" +
                                    "[b]setpts=N/10/TB[r];[m][r]concat=n=2:v=1";
        private const string Backdrop = "color=c=0x303848:s=1920x1080:r=10[bg]";
        // beside the game: a moving "webcam", a looping animation, a running timer
        private const string Beside = "mandelbrot=s=480x360:r=10[cam];[x1][cam]overlay=1380:60[x2];testsrc2=s=200x150:r=10[gif];" +
                                      "[x2][gif]overlay=1400:800,drawtext=text='%{pts\\:hms}':x=1380:y=500:fontsize=64:fontcolor=white";

        // The capturing program's own window, as a tester had it: grey
        // panels, and in its black preview a still image and a moving webcam
        // 20 px left of the game (stretched to 16:9), with moving meters below
        private const string Window = "color=c=0x1e1e1e:s=1920x1080:r=10,drawbox=x=300:y=100:w=1300:h=700:color=black:t=fill," +
                                      "drawbox=x=310:y=110:w=250:h=190:color=white:t=fill[bg];";
        private const string InWindow = "[bg][g]overlay=580:110[x1];mandelbrot=s=250x190:r=10[cam];[x1][cam]overlay=310:320[x2];" +
                                        "testsrc2=s=300x60:r=10[m];[x2][m]overlay=320:900";

        public static int Run(string footage)
        {
            string part = System.IO.Path.Combine(footage, "part00.mp4");
            if (!System.IO.File.Exists(part))
            {
                Console.Error.WriteLine($"no part00.mp4 under '{footage}' (check out the footage branch)");
                return 1;
            }
            string menu = $"-i \"{part}\" -filter_complex \"{Menu}";
            string At(int t) => $"-ss {t} -i \"{part}\"";
            string stream = $"{Backdrop};[0:v]fps=10,scale=1280:960[g];[bg][g]overlay=40:60[x1];{Beside}";
            string streamMenu = $"{Backdrop};{Menu},scale=1280:960[g];[bg][g]overlay=40:60[x1];{Beside}";
            var raw = new RectangleF(14, 2, 617, 474);
            var projector = new RectangleF(271.5f, 4.5f, 1388.25f, 1066.5f);
            var wide = new RectangleF(42, 4.5f, 1851, 1066.5f);
            var inStream = new RectangleF(68, 64, 1234, 948);
            var inWindow = new RectangleF(601, 112.25f, 925.5f, 533.25f);
            string window = $"{Window}[0:v]fps=10,scale=960:540[g];{InWindow}";

            Console.WriteLine("-- set up on the menu, then the run starts");
            One("capture as is (640x480)", 640, 480, raw, null, 500, menu + "\"");
            One("projector, black around (1080p)", 1920, 1080, projector, null, 500, menu + ",scale=1440:1080,pad=1920:1080:240:0:black\"");
            One("stretched to 16:9 (1080p)", 1920, 1080, wide, null, 500, menu + ",scale=1920:1080\"");
            One("stream layout", 1920, 1080, inStream, null, 500, $"-i \"{part}\" -filter_complex \"{streamMenu}\"");
            One("capturing program's own window", 1920, 1080, inWindow, null, 500,
                $"-i \"{part}\" -filter_complex \"{Window}{Menu},scale=960:540[g];{InWindow}\"");
            Console.WriteLine("-- set up during play");
            One("capturing program's own window", 1920, 1080, inWindow, null, 400, At(147) + $" -filter_complex \"{window}\"");
            foreach (int t in new[] { 40, 100, 165, 237 })
            {
                One($"capture as is, {t} s in", 640, 480, raw, null, 300, At(t) + " -vf fps=10");
                One($"stretched to 16:9, {t} s in", 1920, 1080, wide, null, 300, At(t) + " -vf fps=10,scale=1920:1080");
                One($"stream layout, {t} s in", 1920, 1080, inStream, null, 300, At(t) + $" -filter_complex \"{stream}\"");
            }
            Console.WriteLine("-- starting from a wrong crop, during play (capture as is; the picture is 14,2 617x474)");
            foreach (string box in new[] { "5,0,630,478", "30,20,590,440", "60,10,570,460", "0,0,640,480", "100,80,400,300", "14,2,300,474" })
                One("crop " + box, 640, 480, raw, box, 600, At(8) + " -vf fps=10");
            Console.WriteLine("-- starting from a wrong crop, during play, stream layout (the picture is 68,64 1234x948)");
            foreach (string box in new[] { "30,50,1300,980", "120,100,1150,880", "60,60,1700,960", "0,0,1920,1080" })
                One("crop " + box, 1920, 1080, inStream, box, 600, At(147) + $" -filter_complex \"{stream}\"");
            One("whole window (capturing program's)", 1920, 1080, inWindow, "0,0,1920,1080", 600, At(147) + $" -filter_complex \"{window}\"");
            Console.WriteLine("-- no game in the frame at all: must stay unconfirmed");
            One("webcam, animation and timer only", 1920, 1080, inStream, null, 600,
                $"-f lavfi -i \"{Backdrop};mandelbrot=s=1280x960:r=10[g];[bg][g]overlay=40:60[x1];{Beside}\"");
            return 0;
        }

        private static void One(string name, int w, int h, RectangleF truth, string box, int maxFrames, string input)
        {
            var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", $"-v error {input} -f rawvideo -pix_fmt bgra -")
            { RedirectStandardOutput = true, UseShellExecute = false });
            var stream = ffmpeg.StandardOutput.BaseStream;
            var px = new byte[w * h * 4];
            var area = new Rectangle(0, 0, w, h);
            GameFinder finder = GameFinder.Detect(40);
            bool detecting = true, found = false;
            Rectangle crop = Rectangle.Empty;
            var clock = new Stopwatch();
            double worst = 0, total = 0;
            int checks = 0;
            string setup = "", after = "not confirmed by the score";
            for (int k = 0; k < maxFrames; k++)
            {
                int got = 0;
                while (got < px.Length)
                {
                    int n = stream.Read(px, got, px.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < px.Length)
                    break;
                if (k == 0 && box != null)
                {
                    int[] b = box.Split(',').Select(int.Parse).ToArray();
                    crop = new Rectangle(b[0], b[1], b[2], b[3]);
                    setup = "to start with: " + Compare(crop, truth);
                    detecting = false;
                    finder = GameFinder.Check(crop);
                }
                clock.Restart();
                bool done = finder.Feed(px, w, h, w * 4, false, area);
                double ms = clock.Elapsed.TotalMilliseconds;
                if (!detecting)
                {
                    checks++;
                    total += ms;
                    worst = Math.Max(worst, ms);
                }
                if (!done)
                    continue;
                crop = finder.Result;
                if (detecting)
                {
                    setup = $"at setup, after {(k + 1) / 10.0:F1} s{(finder.Recognized ? ", by the menu" : "")}: " + Compare(crop, truth);
                    detecting = false;
                }
                else if (finder.Recognized)
                    after = $"menu recognised at {(k + 1) / 10.0:F1} s; " + after;
                if (finder.Verified)
                {
                    if (!found)
                        after = $"score found at {(k + 1) / 10.0:F1} s";
                    found = true;
                    if (!finder.Refining)
                    {
                        after += ": " + Compare(crop, truth);
                        break;
                    }
                }
                else
                    finder = GameFinder.Check(crop);
            }
            try { ffmpeg.Kill(); } catch (Exception) { }
            Console.WriteLine($"{name,-34} {setup}");
            Console.WriteLine($"{"",-34} {after}" + (checks > 0 ? $"  (checking: {total / checks:F0} ms a frame, {worst:F0} at most)" : ""));
        }

        private static string Compare(Rectangle c, RectangleF t)
        {
            if (c.IsEmpty)
                return "BAD  nothing found";
            double sy = c.Height / 474.0, sx = c.Width / 617.0, tsy = t.Height / 474.0, tsx = t.Width / 617.0;
            double band = (c.Y + 423 * sy - (t.Top + 423 * tsy)) / tsy, height = (sy / tsy - 1) * 100, width = (sx / tsx - 1) * 100;
            double left = (c.X - t.Left) / tsx;
            bool ok = Math.Abs(band) <= 4 && Math.Abs(height) <= 3.5 && width >= -3.5 && width <= 9 && left <= 24 && left >= -28;
            return string.Format(CultureInfo.InvariantCulture,
                "{0}  band {1:+0.0;-0.0} rows, height {2:+0.0;-0.0}%, width {3:+0.0;-0.0}%, left {4:+0;-0} px",
                ok ? "OK " : "BAD", band, height, width, left);
        }
    }
}
