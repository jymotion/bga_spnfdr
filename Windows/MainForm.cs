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

        // ----- auto-crop -----
        private const int AutoCropFramesInitial = 40; // ~4s after selecting a window
        private const int AutoCropFramesRetry = 20;   // ~2s for "Re-try Automatic Crop"

        private readonly AppSettings _settings;
        private readonly WindowCapture _windowCapture = new WindowCapture();
        private readonly DigitReader _reader = new DigitReader();
        private WindowPicker _picker;
        private NotesForm _notesForm;

        private Button _pickButton;
        private Button _cropButton;
        private Button _csvButton;
        private PictureBox _previewBox;
        private Label _statusLabel;
        private Timer _timer;

        private IntPtr _targetWindow = IntPtr.Zero;
        private Bitmap _capture; // shared buffer owned by _windowCapture

        private bool _manualCropMode;
        private bool _dragging;
        private Point _dragStart;          // in capture-pixel coordinates
        private Rectangle _cropBeforeDrag; // restored on stray clicks

        private GameFeedDetector _detector; // non-null while auto-crop runs
        private string _stickyStatus;       // status message that survives a few ticks
        private DateTime _stickyUntil;

        private Dictionary<long, string> _scoreTable = new Dictionary<long, string>();

        private readonly ScoreTracker _tracker = new ScoreTracker();
        private readonly byte[] _band = new byte[ScoreBand.Width * ScoreBand.Rows];
        private byte[] _bandRows = new byte[0]; // capture rows under the band
        private Rectangle _readCrop;             // crop the reader is calibrated for

        public MainForm()
        {
            _settings = AppSettings.Load();
            Text = "bga_spnfdr - setup";
            MinimumSize = new Size(700, 450);
            Size = new Size(1000, 600);

            // restore this window's size/position from last time (before
            // anything can create the window handle, or it won't stick)
            if (_settings.SetupBounds.Width >= 200 && _settings.SetupBounds.Height >= 150
                && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(_settings.SetupBounds)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = _settings.SetupBounds;
            }
            if (_settings.SetupMaximized)
                WindowState = FormWindowState.Maximized;

            BuildUi();
            Theme.Apply(this);

            _notesForm = new NotesForm(this);
            Theme.ApplyTitleBar(_notesForm); // its client colors belong to the user

            _picker = new WindowPicker(this, hWnd =>
                hWnd == Handle || (_notesForm != null && hWnd == _notesForm.Handle));
            _picker.Started += () =>
                _statusLabel.Text = "Now click the window showing the game feed…";
            _picker.Finished += OnWindowPicked;

            FindSavedWindow();
            if (_targetWindow != IntPtr.Zero && _settings.Crop.IsEmpty)
                StartAutoCrop(AutoCropFramesInitial); // window known but never cropped

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
            var topRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8, 8, 8, 0),
                WrapContents = false,
            };
            _pickButton = AddButton(topRow, "Select Capture Window", (s, e) =>
            {
                ExitManualCrop(); // picking a window supersedes crop mode
                _picker.Start();
            });
            AddButton(topRow, "Re-try Automatic Crop", RetryAutoCrop);
            _cropButton = AddButton(topRow, "Manually Crop Gamefeed", ToggleManualCrop);

            var secondRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(8, 0, 8, 0),
                WrapContents = false,
            };
            _csvButton = AddButton(secondRow, "Select Route CSV", ChooseCsv);
            AddButton(secondRow, "Change Notes Formatting", ChooseNotesStyle);

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
            _previewBox.MouseDown += PreviewMouseDown;
            _previewBox.MouseMove += PreviewMouseMove;
            _previewBox.MouseUp += PreviewMouseUp;

            // dock order: last added ends up on top
            Controls.Add(_previewBox);
            Controls.Add(_statusLabel);
            Controls.Add(secondRow);
            Controls.Add(topRow);
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
            SaveSettings();
            UpdateButtonLabels();
            StartAutoCrop(AutoCropFramesInitial); // pre-select the game picture
        }

        // ---------- automatic game-feed detection ----------

        private void StartAutoCrop(int frameBudget)
        {
            _detector = new GameFeedDetector(frameBudget);
        }

        private void RetryAutoCrop(object sender, EventArgs e)
        {
            if (_targetWindow == IntPtr.Zero)
            {
                ShowStickyStatus("Capture Window not yet selected");
                return;
            }
            if (_detector != null)
                return; // already running; the countdown is in the status bar
            ExitManualCrop();
            StartAutoCrop(AutoCropFramesRetry);
        }

        private void FeedAutoCrop()
        {
            var client = WindowCapture.ClientArea(_targetWindow, _capture.Size);
            if (!_detector.Feed(_capture, client))
                return;

            Rectangle box = _detector.Result;
            _detector = null;
            if (box.IsEmpty)
            {
                ShowStickyStatus("Couldn't auto-detect the game feed — use \"Manually Crop Gamefeed\".");
                return;
            }
            _settings.Crop = box;
            SaveSettings();
            _previewBox.Invalidate();
            ShowStickyStatus(
                "Gameplay feed auto-detected — if the red box looks wrong, use " +
                "\"Re-try Automatic Crop\" or \"Manually Crop Gamefeed\".",
                seconds: 12);
        }

        private void ShowStickyStatus(string text, int seconds = 6)
        {
            _stickyStatus = text;
            _stickyUntil = DateTime.Now.AddSeconds(seconds);
            _statusLabel.Text = text;
        }

        // ---------- manual cropping ----------

        private void ToggleManualCrop(object sender, EventArgs e)
        {
            if (_manualCropMode)
            {
                ExitManualCrop();
                return;
            }
            if (_targetWindow == IntPtr.Zero)
            {
                ShowStickyStatus("Capture Window not yet selected");
                return;
            }
            if (_detector != null)
            {
                ShowStickyStatus("Auto-detection is still running — wait for it to finish.");
                return;
            }
            _manualCropMode = true;
            _cropButton.Text = "Cancel Manual Crop";
            _previewBox.Cursor = Cursors.Cross;
            ShowStickyStatus("Drag a box on the preview around the full game picture.");
        }

        private void ExitManualCrop()
        {
            _manualCropMode = false;
            _dragging = false;
            _cropButton.Text = "Manually Crop Gamefeed";
            _previewBox.Cursor = Cursors.Default;
        }

        private void PreviewMouseDown(object sender, MouseEventArgs e)
        {
            if (!_manualCropMode || _capture == null || e.Button != MouseButtons.Left)
                return;
            _dragging = true;
            _dragStart = PreviewToImage(e.Location);
            _cropBeforeDrag = _settings.Crop; // restored if this is a stray click
            _settings.Crop = new Rectangle(_dragStart, Size.Empty);
        }

        private void PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _capture == null)
                return;
            var p = PreviewToImage(e.Location);
            _settings.Crop = Rectangle.FromLTRB(
                Math.Min(_dragStart.X, p.X), Math.Min(_dragStart.Y, p.Y),
                Math.Max(_dragStart.X, p.X), Math.Max(_dragStart.Y, p.Y));
            _previewBox.Invalidate();
        }

        private void PreviewMouseUp(object sender, MouseEventArgs e)
        {
            if (!_dragging)
                return;
            _dragging = false;
            if (_settings.Crop.Width < 4 || _settings.Crop.Height < 4)
            {
                // stray click: keep the previous crop and stay in crop mode
                _settings.Crop = _cropBeforeDrag;
                _previewBox.Invalidate();
                return;
            }
            SaveSettings();
            _previewBox.Invalidate();
            ExitManualCrop(); // one crop per button press
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
            if (_targetWindow == IntPtr.Zero)
                return;

            var bmp = _windowCapture.Capture(_targetWindow);
            if (bmp == null)
            {
                if (!_picker.Active)
                    _statusLabel.Text = Native.IsWindow(_targetWindow)
                        ? "Capture failed — is the window minimized? (Restore it; covering it is fine.)"
                        : "That window was closed. Click \"Select Capture Window\" to pick another.";
                return;
            }
            _capture = bmp;
            if (_previewBox.Visible && Visible)
                _previewBox.Invalidate();

            if (_detector != null)
                FeedAutoCrop();

            ReadScore();

            // while picking a window, the status bar shows the pick prompt —
            // don't stomp it every tick (sticky messages get a few seconds)
            if (_detector != null)
            {
                int seconds = (_detector.FramesLeft * _timer.Interval + 999) / 1000;
                _statusLabel.Text = $"Auto-detecting the game feed… {seconds}s";
            }
            else if (!_picker.Active)
            {
                _statusLabel.Text = DateTime.Now < _stickyUntil ? _stickyStatus : _tracker.Status;
            }
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
        /// reader and feeds the safeguards (Core/ScoreTracker). About 1 ms.</summary>
        private void ReadScore()
        {
            var crop = _settings.Crop;
            if (_capture == null || crop.Width <= 0 || crop.Height <= 0)
                return;
            if (crop != _readCrop)
            {
                _reader.Reset(); // re-find the band's exact rows for the new crop
                _readCrop = crop;
            }
            ScoreBand.RowsNeeded(crop, _capture.Height, out int first, out int last);
            if (last < first)
                return;
            var data = _capture.LockBits(new Rectangle(0, first, _capture.Width, last - first + 1),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int n = data.Stride * (last - first + 1);
                if (_bandRows.Length < n)
                    _bandRows = new byte[n];
                Marshal.Copy(data.Scan0, _bandRows, 0, n);
                ScoreBand.Fill(_bandRows, data.Stride, first, last, _capture.Width, crop, false, _band);
            }
            finally
            {
                _capture.UnlockBits(data);
            }
            string read = _reader.Read(_band, ScoreBand.Width, ScoreBand.Rows);
            string note = _tracker.HandleRead(read, _scoreTable);
            if (note != null)
                _notesForm.SetNote(note);
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

        private Point PreviewToImage(Point p)
        {
            var disp = DisplayRect(_previewBox, _capture.Size);
            if (disp.Width == 0) return Point.Empty;
            int x = (p.X - disp.X) * _capture.Width / disp.Width;
            int y = (p.Y - disp.Y) * _capture.Height / disp.Height;
            return new Point(
                Math.Max(0, Math.Min(_capture.Width - 1, x)),
                Math.Max(0, Math.Min(_capture.Height - 1, y)));
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
                TextRenderer.DrawText(e.Graphics, "game feed",
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
                _settings.SetupBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                _settings.SetupMaximized = WindowState == FormWindowState.Maximized;
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
            _windowCapture.Dispose();
            base.OnFormClosed(e);
        }
    }
}
