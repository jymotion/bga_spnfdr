using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace BgaSpnfdr
{
    /// <summary>
    /// Captures a window's full contents (even while covered by other windows)
    /// via PrintWindow. Reuses one bitmap buffer across frames — at 10 fps the
    /// old allocate-per-frame approach churned a full-window bitmap through
    /// the GC ten times a second for nothing.
    /// </summary>
    internal sealed class WindowCapture : IDisposable
    {
        private Bitmap _buffer;

        /// <summary>Returns the shared capture buffer (do not dispose), or
        /// null if the window is gone, minimized, or the capture failed.</summary>
        public Bitmap Capture(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !Native.IsWindow(hWnd) || Native.IsIconic(hWnd))
                return null;
            if (!Native.GetWindowRect(hWnd, out var rect))
                return null;
            int w = rect.Width, h = rect.Height;
            if (w <= 0 || h <= 0)
                return null;

            if (_buffer == null || _buffer.Width != w || _buffer.Height != h)
            {
                _buffer?.Dispose();
                _buffer = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            }

            try
            {
                using (var g = Graphics.FromImage(_buffer))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok;
                    try
                    {
                        ok = Native.PrintWindow(hWnd, hdc, Native.PW_RENDERFULLCONTENT);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                    return ok ? _buffer : null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The window's client area (no title bar/borders), in
        /// capture-bitmap coordinates.</summary>
        public static Rectangle ClientArea(IntPtr hWnd, Size captureSize)
        {
            var full = new Rectangle(Point.Empty, captureSize);
            if (!Native.GetWindowRect(hWnd, out var windowRect)
                || !Native.GetClientRect(hWnd, out var clientRect))
                return full;
            var origin = new Native.POINT { X = 0, Y = 0 };
            if (!Native.ClientToScreen(hWnd, ref origin))
                return full;
            var client = new Rectangle(
                origin.X - windowRect.Left, origin.Y - windowRect.Top,
                clientRect.Width, clientRect.Height);
            client = Rectangle.Intersect(client, full);
            return client.Width > 0 && client.Height > 0 ? client : full;
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            _buffer = null;
        }
    }
}
