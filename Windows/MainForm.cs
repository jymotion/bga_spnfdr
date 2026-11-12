using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BgaSpnfdr
{
    public class MainForm : Form
    {
        // The score reader and its safeguards live in Core/ (shared with the
        // Linux build): DigitReader.cs, ScoreBand.cs, ScoreTracker.cs,
        // BattleTanx.cs.

        // Once nothing has looked at whole pictures for this many ticks of
        // the side tasks (~5 s), what is kept for them is let go of
        private const int ReleaseAfter = 50;

        private readonly AppSettings _settings;
        private readonly FrameSource _source = new FrameSource();
        private int _reads; // reads at 30 a second, for the ~10-a-second side tasks
        private readonly DigitReader _reader = new DigitReader();
        private WindowPicker _picker;
        private NotesForm _notesForm;

        private Button _pickButton;
        private Button _csvButton;
        private PictureBox _previewBox;
        private FlowLayoutPanel _topRow;
        private Button _debugButton;
        private bool _debug;     // "Show Debug Info" is on: preview and reading details
        private Size _debugSize; // the setup window's size with them showing
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew(); // the time, for _watch
        private Label _statusLabel;
        private Timer _timer;

        private IntPtr _targetWindow = IntPtr.Zero;
        private Bitmap _capture; // shared buffer owned by _source

        // Where the game picture is, and looking for it (shared with the
        // Linux app)
        private readonly GameWatch _watch = new GameWatch();
        private byte[] _pixels = new byte[0]; // the newest whole picture, for the finders
        private int _picturesUnused;          // side-task ticks since anything looked at whole pictures
        private int _sinceSearch;             // ms since the saved window was last looked for
        private string _stickyStatus;       // status message that survives a few ticks
        private DateTime _stickyUntil;

        private Dictionary<long, string> _scoreTable = new Dictionary<long, string>();

        private readonly ScoreTracker _tracker = new ScoreTracker();
        private readonly byte[] _band = new byte[ScoreBand.Width * ScoreBand.Rows];
        private Rectangle _readCrop;             // crop the reader is calibrated for

        // With Windows.Graphics.Capture a tick can find the picture it read
        // last time (the window didn't change in between). Reading it again
        // would count one picture twice towards a score's confirmation, so
        // such ticks are skipped; but every StaleReadEvery-th in a row is
        // read, so a score still confirms on a picture that stays still
        // (a paused game).
        private const int StaleReadEvery = 3;
        private int _staleTicks;


        public MainForm()
        {
            _settings = AppSettings.Load();
            Text = "bga_spnfdr - setup";

            // It opens small: buttons and a status line, where it was last
            // time. "Show Debug Info" adds the preview, at the size the
            // window last had with it. (Before anything can create the
            // window handle, or the position won't stick.)
            bool saved = _settings.SetupBounds.Width >= 200 && _settings.SetupBounds.Height >= 150
                && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(_settings.SetupBounds));
            _debugSize = saved ? _settings.SetupBounds.Size : new Size(1000, 600);
            if (saved)
            {
                StartPosition = FormStartPosition.Manual;
                Location = _settings.SetupBounds.Location;
            }

            BuildUi();
            Theme.Apply(this);
            ApplyDebug();

            _notesForm = new NotesForm(this);
            Theme.ApplyTitleBar(_notesForm); // its client colors belong to the user

            _picker = new WindowPicker(this, hWnd =>
                hWnd == Handle || (_notesForm != null && hWnd == _notesForm.Handle));
            _picker.Started += () =>
                _statusLabel.Text = "Now click the window showing the game feed…";
            _picker.Finished += OnWindowPicked;

            _watch.Crop = _settings.Crop;
            _watch.Verified = _settings.CropVerified;
            _watch.FrameSize = _settings.CropWindowSize;
            _watch.Changed = OnCropChanged;
            _watch.Message = (text, seconds) => ShowStickyStatus(text, seconds);
            FindSavedWindow();
            _watch.Start(_clock.ElapsedMilliseconds, false); // (with no crop yet, the game is looked for)
            if (_targetWindow == IntPtr.Zero && _settings.WindowTitle.Length > 0)
                _statusLabel.Text = "Waiting for last time's window to open — or select another."; // (CaptureFrame looks)

            // restore the notes window as it was last time
            bool onScreen = Screen.AllScreens.Any(s =>
                s.WorkingArea.Contains(new Rectangle(_settings.NotesLocation, new Size(60, 60))));
            _notesForm.Location = onScreen ? _settings.NotesLocation : new Point(80, 80);
            ApplyNotesStyle();
            if (_settings.CsvPath.Length > 0 && File.Exists(_settings.CsvPath))
                LoadCsv(_settings.CsvPath);
            // the notes window IS the program — always visible
            _notesForm.Show();
            UpdateButtonLabels();

            _timer = new Timer { Interval = 100 };
            _timer.Tick += (s, e) => CaptureFrame();
            _timer.Start();
        }

        private void BuildUi()
        {
            var topRow = _topRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8, 8, 8, 0),
                WrapContents = false,
            };
            _pickButton = AddButton(topRow, "Select Capture Window", (s, e) =>
            {
                _picker.Start();
            });
            _csvButton = AddButton(topRow, "Select Route CSV", ChooseCsv);
            AddButton(topRow, "Change Notes Formatting", ChooseNotesStyle);
            _debugButton = AddButton(topRow, "Show Debug Info", (s, e) =>
            {
                if (_debug && WindowState == FormWindowState.Normal)
                    _debugSize = Size; // the size to come back to
                _debug = !_debug;
                ApplyDebug();
            });

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Text = "Select a window to capture.",
                AutoSize = false,
                Height = 24,
                Padding = new Padding(8, 4, 8, 0),
            };

            _previewBox = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30) };
            _previewBox.Paint += PaintPreview;

            // dock order: last added ends up on top
            Controls.Add(_previewBox);
            Controls.Add(_statusLabel);
            Controls.Add(topRow);
        }

        /// <summary>"Show Debug Info" / "Hide Debug Info": the preview, and
        /// reading details in the status bar. Off, the window is just the
        /// buttons and the status bar, and nothing is captured or worked
        /// out for the rest.</summary>
        private void ApplyDebug()
        {
            _debugButton.Text = _debug ? "Hide Debug Info" : "Show Debug Info";
            _previewBox.Visible = _debug;
            if (WindowState != FormWindowState.Normal)
                WindowState = FormWindowState.Normal;
            if (_debug)
            {
                MinimumSize = new Size(700, 450);
                Size = _debugSize;
            }
            else
            {
                MinimumSize = Size.Empty;
                Size row = _topRow.PreferredSize;
                ClientSize = new Size(Math.Max(row.Width, 560), row.Height + _statusLabel.Height);
            }
        }

        private static Button AddButton(Control parent, string text, EventHandler onClick)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(0, 4, 8, 4),
            };
            button.Click += onClick;
            parent.Controls.Add(button);
            return button;
        }

        /// <summary>"Select" before a choice has been made, "Change" after.</summary>
        private void UpdateButtonLabels()
        {
            _pickButton.Text = _targetWindow != IntPtr.Zero
                ? "Change Capture Window"
                : "Select Capture Window";
            _csvButton.Text = _scoreTable.Count > 0
                ? "Change Route CSV"
                : "Select Route CSV";
        }

        // ---------- capture window selection ----------

        /// <summary>Re-find the window saved from the previous session.</summary>
        private void FindSavedWindow()
        {
            if (_settings.WindowTitle.Length == 0)
                return;
            Native.EnumWindows((hWnd, _) =>
            {
                if (Native.IsWindowVisible(hWnd)
                    && !Native.IsCloaked(hWnd)
                    && Native.GetWindowTitle(hWnd) == _settings.WindowTitle)
                {
                    _targetWindow = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        private void OnWindowPicked(IntPtr target)
        {
            if (target == IntPtr.Zero)
            {
                _statusLabel.Text = "Selection cancelled.";
                return;
            }
            _targetWindow = target;
            _settings.WindowTitle = Native.GetWindowTitle(target);
            // the crop was for another window: the game picture is looked for
            _watch.Start(_clock.ElapsedMilliseconds, true);
            SaveSettings();
            UpdateButtonLabels();
        }

        // ---------- the game picture (Core/GameWatch) ----------

        /// <summary>The crop has changed: saved.</summary>
        private void OnCropChanged()
        {
            _settings.Crop = _watch.Crop;
            _settings.CropVerified = _watch.Verified;
            _settings.CropWindowSize = _watch.FrameSize;
            SaveSettings();
            _previewBox.Invalidate();
        }

        /// <summary>The newest whole picture as bytes (B,G,R,x, all rows),
        /// which is what Core/GameFinder works on.</summary>
        private byte[] Pixels(out int stride)
        {
            var data = _capture.LockBits(new Rectangle(Point.Empty, _capture.Size),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                stride = data.Stride;
                int n = stride * data.Height;
                if (_pixels.Length < n)
                    _pixels = new byte[n];
                Marshal.Copy(data.Scan0, _pixels, 0, n);
            }
            finally
            {
                _capture.UnlockBits(data);
            }
            return _pixels;
        }

        private void ShowStickyStatus(string text, int seconds = 6)
        {
            _stickyStatus = text;
            _stickyUntil = DateTime.Now.AddSeconds(seconds);
            _statusLabel.Text = text;
        }

        // ---------- notes ----------

        private void ChooseCsv(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "Choose route CSV (column 1 = score, column 2 = note)",
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    LoadCsv(dlg.FileName);
            }
        }

        private void LoadCsv(string path)
        {
            Dictionary<long, string> table;
            try
            {
                table = RouteCsv.Load(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not load the route CSV:\n" + ex.Message,
                    "bga_spnfdr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _scoreTable = table;
            _settings.CsvPath = path;
            _notesForm.SizeToFit(table.Values);
            SaveSettings();
            UpdateButtonLabels();
        }

        private void ChooseNotesStyle(object sender, EventArgs e)
        {
            using (var dlg = new StyleDialog(
                _settings.NotesFontFamily, _settings.NotesFontSize, _settings.NotesFontStyle,
                _settings.NotesTextColor, _settings.NotesBackColor, _settings.NotesTopMost,
                ApplyStyleToNotes)) // every dialog change previews on the real notes window
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _settings.NotesFontFamily = dlg.FamilyName;
                    _settings.NotesFontStyle = dlg.FontStyleValue;
                    _settings.NotesFontSize = dlg.FontSizeValue;
                    _settings.NotesTextColor = dlg.TextColor;
                    _settings.NotesBackColor = dlg.BackgroundColor;
                    _settings.NotesTopMost = dlg.TopMostChoice;
                }
                // settings are only updated on Save, so re-applying them either
                // commits the new style or reverts the live preview
                ApplyNotesStyle();
            }
        }

        /// <summary>Applies an arbitrary style to the notes window without
        /// touching the saved settings — the style dialog uses this to make
        /// the notes window its own live preview.</summary>
        private void ApplyStyleToNotes(string family, float size, FontStyle style,
            Color text, Color back, bool topMost)
        {
            Font font;
            try { font = new Font(family, Math.Max(6f, size), style); }
            catch (ArgumentException) { font = new Font("Segoe UI", Math.Max(6f, size), FontStyle.Bold); }
            _notesForm.ApplyStyle(font, text, back, topMost);
            if (_scoreTable.Count > 0)
                _notesForm.SizeToFit(_scoreTable.Values);
        }

        private void ApplyNotesStyle()
        {
            ApplyStyleToNotes(_settings.NotesFontFamily, _settings.NotesFontSize,
                _settings.NotesFontStyle, _settings.NotesTextColor,
                _settings.NotesBackColor, _settings.NotesTopMost);
            SaveSettings();
        }

        // ---------- capture loop ----------

        private void CaptureFrame()
        {
            // The window chosen last time may not have been open yet when
            // the program started, or has been closed and opened again: it
            // is looked for about once a second until it is there
            if ((_targetWindow == IntPtr.Zero || !Native.IsWindow(_targetWindow)) && !_picker.Active
                && (_sinceSearch += _timer.Interval) >= 1000)
            {
                _sinceSearch = 0;
                IntPtr before = _targetWindow;
                FindSavedWindow();
                if (_targetWindow != before)
                {
                    _watch.Start(_clock.ElapsedMilliseconds, false);
                    UpdateButtonLabels();
                }
            }
            if (_targetWindow == IntPtr.Zero)
                return;

            // The score is read every tick. The preview, auto-crop and status
            // bar stay at ~10 updates a second, and only they need the whole
            // picture, which Windows.Graphics.Capture then copies back too.
            bool sideTasks = _timer.Interval >= 100 || ++_reads % 3 == 0;
            bool fullFrame = sideTasks
                && (_watch.Busy || (_debug && Visible && WindowState != FormWindowState.Minimized));
            bool ok = _source.Capture(_targetWindow, _settings.Crop, fullFrame);
            // 30 reads a second with Windows.Graphics.Capture; PrintWindow
            // (its fallback) costs much more per read, so that reads 10
            SetReadRate(_source.UsedGraphicsCapture);
            if (!ok)
            {
                if (!_picker.Active)
                    _statusLabel.Text = Native.IsWindow(_targetWindow)
                        ? "Capture failed — is the window minimized? (Restore it; covering it is fine.)"
                        : "That window was closed — waiting for it to open again. (Or select another.)";
                return;
            }
            if (_source.Frame != null)
                _capture = _source.Frame;
            // (a window that has changed size, a crop to be checked: see GameWatch)
            long now = _clock.ElapsedMilliseconds;
            _watch.Tick(now, _source.Size);
            _tracker.WantStatus = Visible && _debug; // nobody sees it otherwise
            if (_source.Fresh)
                _staleTicks = 0;
            if (_watch.Reading && (_source.Fresh || ++_staleTicks % StaleReadEvery == 0))
                ReadScore();
            if (!sideTasks)
                return;

            if (_previewBox.Visible && Visible)
                _previewBox.Invalidate();

            // the newest whole picture, for the finder at work
            if (_watch.Busy && _capture != null)
            {
                byte[] px = Pixels(out int stride);
                _watch.Feed(px, _capture.Width, _capture.Height, stride, false,
                    WindowCapture.ClientArea(_targetWindow, _capture.Size), now);
            }

            // Once the score is being read, nothing looks at whole pictures:
            // 5 s on, what is kept for them (about 30 MB at 1080p) goes back
            // to the system. The arrays only do when the collector is told
            // to run: nothing else here makes it.
            if (_watch.Busy || (_debug && Visible && WindowState != FormWindowState.Minimized))
                _picturesUnused = 0;
            else if (_picturesUnused < ReleaseAfter && ++_picturesUnused == ReleaseAfter)
            {
                _pixels = new byte[0];
                _source.DropFrame();
                _capture = _source.Frame;
                MenuFinder.Release();
                GC.Collect();
            }
            if (!Visible || _picker.Active)
                return; // (while picking a window, the status bar shows the pick prompt)

            _statusLabel.Text = !_watch.Detecting && DateTime.Now < _stickyUntil
                ? _stickyStatus
                : _watch.Status(now, _tracker.Score, _debug, _tracker.Status,
                    $"window {_source.Size.Width}x{_source.Size.Height}, "
                    + (_source.UsedGraphicsCapture ? "Windows.Graphics.Capture" : "PrintWindow")
                    + (_source.Error != null ? " (" + _source.Error + ")" : ""));
        }

        /// <summary>Reads 30 (fast) or 10 times a second, with the matching
        /// confirmation rule.</summary>
        private void SetReadRate(bool fast)
        {
            int interval = fast ? 30 : 100; // 30 ms: Windows' timer steps make it ~32 a second
            if (_timer.Interval == interval)
                return;
            _timer.Interval = interval;
            _tracker.ReadEveryFrame(fast);
        }

        /// <summary>The score area in capture-pixel coordinates: the BattleTanx
        /// fractional rectangle applied to the crop.</summary>
        private Rectangle ScoreRegion()
        {
            var crop = _settings.Crop;
            if (crop.Width <= 0 || crop.Height <= 0)
                return Rectangle.Empty;
            return new Rectangle(
                crop.X + (int)(BattleTanx.ScoreFraction.X * crop.Width),
                crop.Y + (int)(BattleTanx.ScoreFraction.Y * crop.Height),
                Math.Max(1, (int)(BattleTanx.ScoreFraction.Width * crop.Width)),
                Math.Max(1, (int)(BattleTanx.ScoreFraction.Height * crop.Height)));
        }

        // ---------- reading the score ----------

        /// <summary>Reads the score from the newest capture with the digit
        /// reader and feeds the safeguards (Core/ScoreTracker).</summary>
        private void ReadScore()
        {
            var crop = _settings.Crop;
            // the rows under the band, which _source captured for this crop
            if (crop.Width <= 0 || crop.Height <= 0 || _source.Last < _source.First)
                return;
            if (crop != _readCrop)
            {
                _reader.Reset(); // re-find the band's exact rows for the new crop
                _readCrop = crop;
            }
            ScoreBand.Fill(_source.Rows, _source.Stride, _source.First, _source.Last, _source.Size.Width,
                crop, false, _band);
            string read = _reader.Read(_band, ScoreBand.Width, ScoreBand.Rows);
            string note = _tracker.HandleRead(read, _scoreTable, _reader.LeftMatch);
            if (note != null)
                _notesForm.SetNote(note);
            if (_tracker.ReadStands)
            {
                _watch.ScoreSeen(_clock.ElapsedMilliseconds);
                // the score read starting left of the crop's edge: the crop
                // belongs further left (ScoreBand.Recenter)
                _watch.MoveCrop(ScoreBand.Recenter(crop, _reader.FoundFirst, _reader.FoundOffset), _source.Size);
            }
        }

        // ---------- painting & coordinate mapping ----------

        /// <summary>Where the capture image lands inside the preview
        /// (letterboxed, aspect kept).</summary>
        private static Rectangle DisplayRect(Control box, Size image)
        {
            if (image.Width <= 0 || image.Height <= 0
                || box.ClientSize.Width <= 0 || box.ClientSize.Height <= 0)
                return Rectangle.Empty;
            float scale = Math.Min(
                (float)box.ClientSize.Width / image.Width,
                (float)box.ClientSize.Height / image.Height);
            int w = Math.Max(1, (int)(image.Width * scale));
            int h = Math.Max(1, (int)(image.Height * scale));
            return new Rectangle((box.ClientSize.Width - w) / 2, (box.ClientSize.Height - h) / 2, w, h);
        }

        private Rectangle ImageToPreview(Rectangle r)
        {
            var disp = DisplayRect(_previewBox, _capture.Size);
            if (disp.Width == 0) return Rectangle.Empty;
            return new Rectangle(
                disp.X + r.X * disp.Width / _capture.Width,
                disp.Y + r.Y * disp.Height / _capture.Height,
                Math.Max(1, r.Width * disp.Width / _capture.Width),
                Math.Max(1, r.Height * disp.Height / _capture.Height));
        }

        private void PaintPreview(object sender, PaintEventArgs e)
        {
            if (_capture == null)
            {
                TextRenderer.DrawText(e.Graphics, "No capture yet — select a window above.",
                    Font, _previewBox.ClientRectangle, Color.Gainsboro,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            var disp = DisplayRect(_previewBox, _capture.Size);
            e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
            e.Graphics.DrawImage(_capture, disp);

            var crop = _settings.Crop;
            if (crop.Width > 0 && crop.Height > 0)
            {
                var r = ImageToPreview(crop);
                using (var dim = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                {
                    // dim everything outside the crop so it stands out
                    var outside = new Region(disp);
                    outside.Exclude(r);
                    e.Graphics.FillRegion(dim, outside);
                }
                using (var pen = new Pen(Color.Red, 2))
                    e.Graphics.DrawRectangle(pen, r);
                TextRenderer.DrawText(e.Graphics,
                    _watch.Label(_tracker.Score),
                    Font, new Point(r.X + 4, r.Y + 3), Color.Red);

                var scoreDisp = ImageToPreview(ScoreRegion());
                using (var pen = new Pen(Color.Lime, 2))
                    e.Graphics.DrawRectangle(pen, scoreDisp);
                TextRenderer.DrawText(e.Graphics, "score",
                    Font, new Point(scoreDisp.X, Math.Max(0, scoreDisp.Y - 18)), Color.Lime);
            }
        }

        // ---------- lifecycle ----------

        private void SaveSettings()
        {
            if (_notesForm != null && _notesForm.Visible)
                _settings.NotesLocation = _notesForm.Location;
            if (IsHandleCreated)
            {
                // its place, and its size while the debug info is showing
                // (without it, it is as small as it can be)
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (_debug)
                    _debugSize = bounds.Size;
                _settings.SetupBounds = new Rectangle(bounds.Location, _debugSize);
            }
            _settings.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // once configured, the setup window isn't needed: closing it just
            // hides it while the notes window is up (right-click the notes
            // window to bring it back or exit). Capture and reading keep running.
            if (e.CloseReason == CloseReason.UserClosing && _notesForm != null && _notesForm.Visible)
            {
                e.Cancel = true;
                SaveSettings();
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer?.Stop();
            SaveSettings(); // capture the notes window's final position
            _picker.Dispose();
            _source.Dispose();
            base.OnFormClosed(e);
        }
    }
}
