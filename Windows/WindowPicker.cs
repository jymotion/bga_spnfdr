using System;
using System.Drawing;
using System.Windows.Forms;

namespace BgaSpnfdr
{
    /// <summary>
    /// Click-to-pick window selection (Spy++ style). After Start(), the host
    /// form holds the mouse capture so the next click anywhere on screen comes
    /// to us; the window under the cursor is tinted red meanwhile. Left-click
    /// picks; right-click or clicking one of the app's own windows cancels.
    ///
    /// Hard-won details baked in here:
    ///  - Capture must engage AFTER the triggering button finishes its click
    ///    processing (a Button resets its own capture), hence BeginInvoke.
    ///  - Mouse-move events don't arrive from every screen region while
    ///    capture is held, so a 30ms timer polls Cursor.Position — the
    ///    events, when they do arrive, just add smoothness.
    ///  - Hit-testing walks the z-order manually instead of WindowFromPoint,
    ///    skipping click-through overlays (screen recorders, GPU overlays),
    ///    cloaked/minimized windows, and the desktop.
    ///  - The overlay positions itself with raw SetWindowPos pixels and
    ///    swallows WM_DPICHANGED; WinForms' DPI handling would otherwise
    ///    rescale the bounds it was just given.
    /// </summary>
    internal sealed class WindowPicker : IDisposable
    {
        private readonly Form _host;
        private readonly Func<IntPtr, bool> _isOwnWindow;
        private readonly Timer _pollTimer = new Timer { Interval = 30 };
        private HighlightOverlay _overlay;

        public bool Active { get; private set; }

        /// <summary>Raised (on the UI thread) once pick mode is engaged.</summary>
        public event Action Started;

        /// <summary>Raised with the chosen window, or IntPtr.Zero if cancelled.</summary>
        public event Action<IntPtr> Finished;

        public WindowPicker(Form host, Func<IntPtr, bool> isOwnWindow)
        {
            _host = host;
            _isOwnWindow = isOwnWindow;
            _pollTimer.Tick += (s, e) => UpdateHighlight();
            _host.MouseMove += (s, e) => UpdateHighlight();
            _host.MouseDown += HostMouseDown;
            _host.MouseCaptureChanged += (s, e) =>
            {
                if (Active && !_host.Capture)
                {
                    End(); // capture stolen (alt-tab etc.) = cancel
                    Finished?.Invoke(IntPtr.Zero);
                }
            };
        }

        public void Start()
        {
            if (Active)
                return;
            // defer until the triggering button finishes its click processing —
            // engaging mouse capture inside a Click handler gets undone
            _host.BeginInvoke(new Action(() =>
            {
                Active = true;
                _overlay = new HighlightOverlay();
                _host.Capture = true; // all mouse input now comes to the host
                _pollTimer.Start();
                Started?.Invoke();
            }));
        }

        private void End()
        {
            Active = false;
            _pollTimer.Stop();
            _host.Capture = false;
            _overlay?.Dispose();
            _overlay = null;
        }

        private void HostMouseDown(object sender, MouseEventArgs e)
        {
            if (!Active)
                return;
            IntPtr target = e.Button == MouseButtons.Left
                ? FindTargetAt(Cursor.Position)
                : IntPtr.Zero;
            End();
            Finished?.Invoke(target);
        }

        private void UpdateHighlight()
        {
            if (!Active)
                return;
            IntPtr target = FindTargetAt(Cursor.Position);
            if (target == IntPtr.Zero)
            {
                _overlay.Hide();
                return;
            }
            Native.GetWindowRect(target, out var r);
            _overlay.ShowAt(r);
        }

        /// <summary>Finds the pickable window under a screen point by walking
        /// the z-order top-down, skipping things the user can't see and didn't
        /// mean: click-through overlay windows, cloaked windows, the desktop,
        /// and this app's own windows.</summary>
        private IntPtr FindTargetAt(Point screen)
        {
            for (IntPtr h = Native.GetTopWindow(IntPtr.Zero);
                 h != IntPtr.Zero;
                 h = Native.GetWindow(h, Native.GW_HWNDNEXT))
            {
                if (_isOwnWindow(h) || (_overlay != null && h == _overlay.Handle))
                    continue;
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h))
                    continue;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TRANSPARENT) != 0)
                    continue; // click-through overlay, invisible to clicks by design
                string cls = Native.GetWindowClass(h);
                if (cls == "Progman" || cls == "WorkerW")
                    continue; // the desktop — highlighting it turns the whole screen red
                if (!Native.GetWindowRect(h, out var r))
                    continue;
                if (screen.X < r.Left || screen.X >= r.Right
                    || screen.Y < r.Top || screen.Y >= r.Bottom)
                    continue;
                return h;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            End();
            _pollTimer.Dispose();
        }

        /// <summary>Semi-transparent red sheet shown over the window under the
        /// cursor. Transparent to mouse hit-testing so it never picks itself,
        /// and never steals activation (that would break the mouse capture).</summary>
        private sealed class HighlightOverlay : Form
        {
            public HighlightOverlay()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.Red;
                Opacity = 0.30;
                TopMost = true;
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x20        // WS_EX_TRANSPARENT: skipped by hit-testing
                                | 0x80000     // WS_EX_LAYERED
                                | 0x08000000; // WS_EX_NOACTIVATE
                    return cp;
                }
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_DPICHANGED = 0x02E0;
                if (m.Msg == WM_DPICHANGED)
                {
                    // ignore: WinForms would rescale the raw-pixel bounds we
                    // just set, ballooning the overlay across the screen
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
            }

            /// <summary>Positions with raw physical pixels (bypassing WinForms
            /// DPI scaling) and shows without stealing activation.</summary>
            public void ShowAt(Native.RECT r)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST,
                    r.Left, r.Top, r.Width, r.Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
        }
    }
}
