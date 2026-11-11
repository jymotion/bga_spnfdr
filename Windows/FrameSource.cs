using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BgaSpnfdr
{
    /// <summary>
    /// Gets each tick's pixels from the capture window: with
    /// Windows.Graphics.Capture where Windows has it (10 version 1903 and
    /// later), with PrintWindow before that or if it fails.
    /// Everything comes out in PrintWindow's coordinates (the window
    /// rectangle, invisible resize borders included), so crops saved by
    /// earlier versions still fit:
    ///  - Rows: the frame rows under the score band, every tick. With
    ///    Windows.Graphics.Capture only the score strip within them is
    ///    copied back from the graphics card; the rest stays unused.
    ///  - Frame: the whole picture, when asked (preview and auto-crop).
    ///    PrintWindow always renders the whole window anyway.
    /// </summary>
    internal sealed unsafe class FrameSource : IDisposable
    {
        // Windows 10 version 1903 (build 18362) added capturing a window by
        // its handle. Checked here, before anything loads GraphicsCapture:
        // older Windows lacks the WinRT types it uses.
        private static readonly bool Supported = Environment.OSVersion.Version >= new Version(10, 0, 18362);

        private readonly WindowCapture _printWindow = new WindowCapture();
        private GraphicsCapture _graphics;
        private IntPtr _graphicsWindow; // window _graphics captures
        private IntPtr _failedWindow;   // Windows.Graphics.Capture failed on it: PrintWindow from then on
        private Bitmap _bitmap;         // full frames from Windows.Graphics.Capture
        private int _bitmapFrame;       // _graphics.FrameNumber copied into it
        private Rectangle _bitmapArea;  // where that frame sits in it

        /// <summary>False: PrintWindow only (the test build's PrintWindow minutes).</summary>
        public bool AllowGraphicsCapture = true;

        /// <summary>Whether the last Capture used Windows.Graphics.Capture.</summary>
        public bool UsedGraphicsCapture;

        /// <summary>Why Windows.Graphics.Capture was given up on, if it was.</summary>
        public string Error;

        /// <summary>Window size: the size of PrintWindow's picture.</summary>
        public Size Size;

        /// <summary>The newest whole picture (do not dispose); null before the
        /// first. Only refreshed when asked for, so it can be older than Rows.</summary>
        public Bitmap Frame;

        /// <summary>Frame rows First..Last (Stride bytes each, B,G,R,x) under
        /// the score band; Last &lt; First when there is nothing to read.</summary>
        public byte[] Rows = new byte[0];
        public int Stride, First, Last = -1;

        /// <summary>Captures what this tick needs: the rows under the score
        /// band for crop, and the whole picture if fullFrame. False if the
        /// window is gone or minimized, or the capture failed.</summary>
        public bool Capture(IntPtr hWnd, Rectangle crop, bool fullFrame)
        {
            Last = -1;
            UsedGraphicsCapture = false;
            if (hWnd == IntPtr.Zero || !Native.IsWindow(hWnd) || Native.IsIconic(hWnd))
                return false;

            if (AllowGraphicsCapture && Supported && hWnd != _failedWindow)
            {
                try
                {
                    if (CaptureGraphics(hWnd, crop, fullFrame))
                    {
                        UsedGraphicsCapture = true;
                        return true;
                    }
                    // no picture yet (just started): PrintWindow this time
                }
                catch (Exception e)
                {
                    Error = $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message}";
                    StopGraphics();
                    _failedWindow = hWnd;
                }
            }
            else
            {
                StopGraphics();
            }
            return CapturePrintWindow(hWnd, crop);
        }

#if PERF_TEST
        /// <summary>Test build: try Windows.Graphics.Capture again even on a
        /// window it failed on.</summary>
        public void Retry()
        {
            _failedWindow = IntPtr.Zero;
            Error = null;
        }
#endif

        private bool CapturePrintWindow(IntPtr hWnd, Rectangle crop)
        {
            var bmp = _printWindow.Capture(hWnd);
            if (bmp == null)
                return false;
            Frame = bmp;
            Size = bmp.Size;
            if (crop.Width <= 0 || crop.Height <= 0)
                return true;
            ScoreBand.RowsNeeded(crop, bmp.Height, out int first, out int last);
            if (last < first)
                return true;
            var data = bmp.LockBits(new Rectangle(0, first, bmp.Width, last - first + 1),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int n = data.Stride * (last - first + 1);
                if (Rows.Length < n)
                    Rows = new byte[n];
                Marshal.Copy(data.Scan0, Rows, 0, n);
                Stride = data.Stride;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            First = first;
            Last = last;
            return true;
        }

        private bool CaptureGraphics(IntPtr hWnd, Rectangle crop, bool fullFrame)
        {
            if (_graphics == null || _graphicsWindow != hWnd)
            {
                StopGraphics();
                _graphics = new GraphicsCapture(hWnd);
                _graphicsWindow = hWnd;
            }
            _graphics.Poll();
            if (!_graphics.HasFrame || !Native.GetWindowRect(hWnd, out var window))
                return false;

            // The picture starts at the window's visible frame; PrintWindow's
            // at its full rectangle, which also takes in the invisible resize
            // borders (usually 7 px left, right and bottom on Windows 10/11).
            var origin = Point.Empty;
            if (Native.GetExtendedFrameBounds(hWnd, out var visible))
                origin = new Point(visible.Left - window.Left, visible.Top - window.Top);
            Size = new Size(window.Width, window.Height);
            if (Size.Width <= 0 || Size.Height <= 0)
                return false;
            // the picture's place in PrintWindow coordinates, cut to the window
            var picture = Rectangle.Intersect(new Rectangle(origin, _graphics.Size), new Rectangle(Point.Empty, Size));

            if (crop.Width > 0 && crop.Height > 0)
            {
                ScoreBand.RowsNeeded(crop, Size.Height, out int first, out int last);
                ScoreBand.ColumnsNeeded(crop, Size.Width, out int left, out int right);
                if (last >= first && right >= left)
                {
                    Stride = Size.Width * 4;
                    int n = Stride * (last - first + 1);
                    if (Rows.Length < n)
                        Rows = new byte[n];
                    var strip = Rectangle.FromLTRB(left, first, right + 1, last + 1);
                    var part = Rectangle.Intersect(strip, picture);
                    if (part != strip) // partly off the picture: leave black there
                        for (int y = first; y <= last; y++)
                            Array.Clear(Rows, (y - first) * Stride + left * 4, strip.Width * 4);
                    if (part.Width > 0 && part.Height > 0)
                    {
                        fixed (byte* rows = Rows)
                            _graphics.Copy(new Rectangle(part.X - origin.X, part.Y - origin.Y, part.Width, part.Height),
                                rows + (part.Y - first) * Stride + part.X * 4, Stride, 0);
                    }
                    First = first;
                    Last = last;
                }
            }

            if (fullFrame)
                CopyFullFrame(origin, picture);
            return true;
        }

        /// <summary>Copies the whole picture into Frame, unless it already
        /// holds this frame.</summary>
        private void CopyFullFrame(Point origin, Rectangle picture)
        {
            if (_bitmap != null && _bitmap.Size == Size && _bitmapFrame == _graphics.FrameNumber
                && _bitmapArea == picture && Frame == _bitmap)
                return;
            bool clear = _bitmapArea != picture;
            if (_bitmap == null || _bitmap.Size != Size)
            {
                // not disposed: the preview may still show the old one if this
                // tick fails; the garbage collector frees it (resizes are rare)
                _bitmap = new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppRgb);
                clear = true;
            }
            if (clear)
            {
                // outside the picture (the invisible borders) stays black
                using (var g = Graphics.FromImage(_bitmap))
                    g.Clear(Color.Black);
                _bitmapArea = picture;
            }
            if (picture.Width > 0 && picture.Height > 0)
            {
                var data = _bitmap.LockBits(picture, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    _graphics.Copy(new Rectangle(picture.X - origin.X, picture.Y - origin.Y, picture.Width, picture.Height),
                        (byte*)data.Scan0, data.Stride, 1);
                }
                finally
                {
                    _bitmap.UnlockBits(data);
                }
            }
            _bitmapFrame = _graphics.FrameNumber;
            Frame = _bitmap;
        }

        private void StopGraphics()
        {
            _graphics?.Dispose();
            _graphics = null;
            _graphicsWindow = IntPtr.Zero;
            _bitmapFrame = 0; // a new session counts its frames from 1 again
        }

        public void Dispose()
        {
            StopGraphics();
            _printWindow.Dispose();
            _bitmap?.Dispose();
            _bitmap = null;
            Frame = null;
        }
    }
}
