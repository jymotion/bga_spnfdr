using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using BgaSpnfdr.Capture;
using BgaSpnfdr.Native;
using static BgaSpnfdr.Native.Gtk;

namespace BgaSpnfdr.UI
{
    /// <summary>
    /// The setup window plus everything that connects capture, reading and
    /// the notes window (the Linux counterpart of Windows/MainForm.cs, with
    /// the same buttons and flow). Runs on the GTK main thread.
    /// </summary>
    internal sealed unsafe class App
    {
        private const int AutoCropFramesInitial = 40; // ~4 s of 10/s snapshots
        private const int AutoCropFramesRetry = 20;   // ~2 s for "Re-try Automatic Crop"

        private readonly LinuxSettings _settings = LinuxSettings.Load();
        private readonly FrameProcessor _processor = new FrameProcessor();
        private readonly ScreenCastPortal _portal = new ScreenCastPortal();
        private readonly NotesWindow _notes = new NotesWindow();
        private PipeWireStream _stream;
        private Dictionary<long, string> _route = new Dictionary<long, string>();

        private IntPtr _setup, _preview, _status, _pickButton, _cropButton, _csvButton;
        private bool _capturing;
        private bool _autoCropOnStart;
        private string _captureStatus;
        private string _sticky;
        private long _stickyUntil; // Environment.TickCount64

        // Core/GameFinder at work: _detector while auto-crop runs (the user
        // waits for it), _checker in the background while the crop hasn't
        // been confirmed by a score (set up on the menu, there is none yet)
        private GameFinder _detector, _checker;
        private bool _reported; // the finder at work has given a result already
        private Size _sizeChecked;          // frame size the crop was last checked against
        private bool _manualCrop, _dragging;
        private Point _dragStart;
        private Rectangle _cropBeforeDrag;

        private byte[] _snapshot;
        private int _snapW, _snapH;
        private int _snapshotPosted;

        public App()
        {
            BuildSetupWindow();
            _notes.ShowSetup = ShowSetup;
            _notes.Exit = Quit;

            if (_settings.CsvPath.Length > 0 && File.Exists(_settings.CsvPath))
                LoadCsv(_settings.CsvPath, quiet: true);
            ApplyNotesStyle(_settings.NotesFont, _settings.NotesTextColor, _settings.NotesBackColor, _settings.NotesTopMost);
            _processor.Configure(_settings.Crop, _route);
            _processor.NoteReady = note => Callbacks.Post(() => _notes.SetNote(note));
            _processor.CropMoved = crop => Callbacks.Post(() =>
            {
                // a score was read starting left of the crop's edge
                if (_detector == null && !_manualCrop && _capturing)
                    SetCrop(crop, _settings.CropVerified);
            });
            _processor.SnapshotReady = () =>
            {
                if (Interlocked.Exchange(ref _snapshotPosted, 1) == 0)
                    Callbacks.Post(OnSnapshot);
            };
            _portal.Started = (fd, node, token) =>
            {
                if (!string.IsNullOrEmpty(token))
                {
                    _settings.RestoreToken = token;
                    SaveSettings();
                }
                StartStream(fd, node);
            };
            _portal.Failed = message =>
            {
                _captureStatus = null;
                ShowSticky(message + " — click \"Select Capture Window\" to try again.", 10);
                UpdateButtonLabels();
            };

            // the notes window IS the program: always visible
            gtk_window_move(_notes.Window, _settings.NotesLocation.X, _settings.NotesLocation.Y);
            gtk_widget_show_all(_notes.Window);
            ShowSetup();
            UpdateButtonLabels();
            Callbacks.Timeout(200, Tick);

            // resume capture: test hook, or the window shared last time
            if (Environment.GetEnvironmentVariable("BGA_SPNFDR_PIPEWIRE_NODE") != null)
                StartCapture(prompt: false);
            else if (_settings.RestoreToken.Length > 0)
                StartCapture(prompt: false);
        }

        // ---------- setup window ----------

        private void BuildSetupWindow()
        {
            _setup = gtk_window_new(0);
            gtk_window_set_title(_setup, "bga_spnfdr - setup");
            gtk_window_set_default_size(_setup, 1000, 600);
            if (_settings.SetupBounds.Width >= 200 && _settings.SetupBounds.Height >= 150)
            {
                gtk_window_move(_setup, _settings.SetupBounds.X, _settings.SetupBounds.Y);
                gtk_window_resize(_setup, _settings.SetupBounds.Width, _settings.SetupBounds.Height);
            }
            if (_settings.SetupMaximized)
                gtk_window_maximize(_setup);

            IntPtr vbox = gtk_box_new(ORIENTATION_VERTICAL, 6);
            gtk_container_set_border_width(vbox, 8);
            gtk_container_add(_setup, vbox);

            IntPtr row1 = gtk_box_new(ORIENTATION_HORIZONTAL, 8);
            _pickButton = AddButton(row1, "Select Capture Window", () =>
            {
                ExitManualCrop(); // picking a window supersedes crop mode
                StartCapture(prompt: true);
            });
            AddButton(row1, "Re-try Automatic Crop", RetryAutoCrop);
            _cropButton = AddButton(row1, "Manually Crop Gamefeed", ToggleManualCrop);
            gtk_box_pack_start(vbox, row1, 0, 0, 0);

            IntPtr row2 = gtk_box_new(ORIENTATION_HORIZONTAL, 8);
            _csvButton = AddButton(row2, "Select Route CSV", ChooseCsv);
            AddButton(row2, "Change Notes Formatting", ChooseNotesStyle);
            gtk_box_pack_start(vbox, row2, 0, 0, 0);

            _preview = gtk_drawing_area_new();
            gtk_widget_set_size_request(_preview, 320, 240);
            gtk_widget_add_events(_preview, BUTTON_PRESS_MASK | BUTTON_RELEASE_MASK | POINTER_MOTION_MASK);
            Callbacks.Connect(_preview, "draw", DrawPreview);
            Callbacks.Connect(_preview, "button-press-event", PreviewPress);
            Callbacks.Connect(_preview, "motion-notify-event", PreviewMotion);
            Callbacks.Connect(_preview, "button-release-event", PreviewRelease);
            gtk_box_pack_start(vbox, _preview, 1, 1, 0);

            _status = gtk_label_new("Select a window to capture.");
            gtk_label_set_xalign(_status, 0);
            gtk_label_set_ellipsize(_status, 3); // end
            gtk_box_pack_start(vbox, _status, 0, 0, 0);

            // once configured the setup window isn't needed: closing it just
            // hides it (right-click the notes window to bring it back).
            // Capture and reading keep running.
            Callbacks.Connect(_setup, "delete-event", _ =>
            {
                RememberSetupBounds();
                SaveSettings();
                gtk_widget_hide(_setup);
                return true;
            });
        }

        private static IntPtr AddButton(IntPtr box, string label, Action onClick)
        {
            IntPtr button = gtk_button_new_with_label(label);
            Callbacks.Connect(button, "clicked", onClick);
            gtk_box_pack_start(box, button, 0, 0, 0);
            return button;
        }

        private bool SetupVisible => gtk_widget_get_visible(_setup) != 0;

        private void ShowSetup()
        {
            gtk_widget_show_all(_setup);
            gtk_window_present(_setup);
        }

        /// <summary>"Select" before a choice has been made, "Change" after.</summary>
        private void UpdateButtonLabels()
        {
            gtk_button_set_label(_pickButton, _capturing ? "Change Capture Window" : "Select Capture Window");
            gtk_button_set_label(_csvButton, _route.Count > 0 ? "Change Route CSV" : "Select Route CSV");
        }

        private void ShowSticky(string text, int seconds = 6)
        {
            _sticky = text;
            _stickyUntil = Environment.TickCount64 + seconds * 1000L;
            gtk_label_set_text(_status, text);
        }

        private bool Tick()
        {
            bool visible = SetupVisible;
            _processor.WantStatus = visible;

            // the shared window changed size: the old crop no longer fits
            // (once per size, so a failed detection doesn't loop)
            int fw = _processor.FrameWidth, fh = _processor.FrameHeight;
            var size = new Size(fw, fh);
            if (_capturing && _detector == null && !_manualCrop && fw > 0 && size != _sizeChecked)
            {
                _sizeChecked = size;
                if (_settings.Crop.IsEmpty || _settings.CropFrameSize != size)
                {
                    if (!_settings.Crop.IsEmpty)
                        ShowSticky("The shared window changed size — re-detecting the game feed.");
                    _settings.CropVerified = false; // it was confirmed for the other size
                    // the old crop is for another size: reading through it
                    // would only produce misreads
                    _processor.Configure(Rectangle.Empty, _route);
                    StartAutoCrop(AutoCropFramesInitial);
                }
            }

            // a crop no score has confirmed yet is checked until one does
            bool unconfirmed = !_settings.Crop.IsEmpty && !_settings.CropVerified;
            if (_capturing && _detector == null && _checker == null && !_manualCrop && fw > 0 && unconfirmed)
            {
                _checker = GameFinder.Check(_settings.Crop);
                _reported = false;
            }
            _processor.WantSnapshots = visible || _detector != null || _checker != null;

            string text;
            if (_detector != null)
                text = _detector.FramesLeft > 0
                    ? $"Auto-detecting the game feed… {(_detector.FramesLeft + 9) / 10}s"
                    : "Auto-detecting the game feed… checking it against the score";
            else if (Environment.TickCount64 < _stickyUntil)
                text = _sticky;
            else if (_captureStatus != null)
                text = _captureStatus;
            else if (!_capturing)
                text = "Select a window to capture.";
            else if (fw == 0)
                text = "Waiting for the first frame from the shared window…";
            else
                text = unconfirmed
                    ? _processor.Status + "  ·  crop not confirmed yet: it adjusts itself when a score appears"
                    : _processor.Status;
            if (visible)
                gtk_label_set_text(_status, text);
            return true;
        }

        // ---------- capture ----------

        private void StartCapture(bool prompt)
        {
            StopStream();
            _autoCropOnStart = prompt;
            string node = Environment.GetEnvironmentVariable("BGA_SPNFDR_PIPEWIRE_NODE");
            if (!string.IsNullOrEmpty(node) && uint.TryParse(node, out uint id))
            {
                StartStream(-1, id); // testing: a node on the user's own PipeWire
                return;
            }
            _captureStatus = prompt
                ? "Choose the window showing the game feed in the screen-sharing prompt…"
                : "Reconnecting to the window shared last time…";
            gtk_label_set_text(_status, _captureStatus);
            _portal.Start(prompt ? null : _settings.RestoreToken, ParentWindowId());
        }

        private void StartStream(int fd, uint node)
        {
            StopStream();
            _captureStatus = null;
            _processor.FrameWidth = _processor.FrameHeight = 0;
            _sizeChecked = Size.Empty;
            _stream = new PipeWireStream
            {
                FrameReady = _processor.OnFrame,
                Failed = message => Callbacks.Post(() => OnStreamFailed(message)),
            };
            try
            {
                _stream.Start(fd, node);
                _capturing = true;
                if (_autoCropOnStart)
                    _settings.CropVerified = false; // the crop was for another window
                if (_autoCropOnStart)
                    StartAutoCrop(AutoCropFramesInitial); // pre-select the game picture
            }
            catch (Exception e)
            {
                StopStream();
                ShowSticky("Capture failed: " + e.Message, 10);
            }
            UpdateButtonLabels();
        }

        private void OnStreamFailed(string message)
        {
            StopStream();
            _portal.Close();
            ShowSticky(message + " — click \"Select Capture Window\" to pick it again.", 15);
            UpdateButtonLabels();
        }

        private void StopStream()
        {
            _stream?.Dispose();
            _stream = null;
            _capturing = false;
            if (_detector != null) // it may have set the crop aside (Tick)
                _processor.Configure(_settings.Crop, _route);
            _detector = _checker = null;
        }

        /// <summary>Lets the portal place its prompt over our window (X11).</summary>
        private string ParentWindowId()
        {
            try
            {
                string display = GLib.Utf8(gdk_display_get_name(gdk_display_get_default())) ?? "";
                IntPtr window = gtk_widget_get_window(_setup);
                if (window == IntPtr.Zero || !display.Contains(':') || display.StartsWith("wayland"))
                    return "";
                return "x11:" + gdk_x11_window_get_xid(window).ToString("x");
            }
            catch (Exception)
            {
                return "";
            }
        }

        // ---------- automatic game-feed detection ----------

        private void StartAutoCrop(int frames)
        {
            ExitManualCrop();
            _detector = GameFinder.Detect(frames);
            _checker = null;
            _reported = false;
            _processor.WantSnapshots = true;
        }

        private void RetryAutoCrop()
        {
            if (!_capturing)
            {
                ShowSticky("Capture Window not yet selected");
                return;
            }
            if (_detector != null)
                return; // already running; the countdown is in the status bar
            StartAutoCrop(AutoCropFramesRetry);
        }

        private void OnSnapshot()
        {
            Volatile.Write(ref _snapshotPosted, 0);
            if (!_processor.TakeSnapshot(ref _snapshot, out _snapW, out _snapH))
                return;
            var frame = new Rectangle(0, 0, _snapW, _snapH);
            GameFinder finder = _detector ?? _checker;
            if (finder != null && finder.Feed(_snapshot, _snapW, _snapH, _snapW * 4, false, frame))
            {
                bool first = !_reported;
                _reported = true;
                if (finder == _detector && finder.Result.IsEmpty)
                {
                    _processor.Configure(_settings.Crop, _route); // back to the crop there was
                    ShowSticky("Couldn't auto-detect the game feed — use \"Manually Crop Gamefeed\".");
                }
                else if (finder == _detector)
                {
                    // on the game's menu there is no score to go by yet: the
                    // box is from what moves and the picture's edges
                    SetCrop(finder.Result, finder.Verified);
                    if (first)
                        ShowSticky(finder.Verified
                            ? "Gameplay feed auto-detected and confirmed by the score."
                            : "Gameplay feed auto-detected — if the red box looks wrong, use \"Re-try Automatic " +
                              "Crop\" or \"Manually Crop Gamefeed\". It adjusts itself when a score appears.", 12);
                }
                else if (finder.Verified)
                {
                    SetCrop(finder.Result, true);
                    if (first)
                        ShowSticky("Score found — the crop now fits it exactly.", 8);
                }
                // a finder that has just found the score goes on to steady
                // its result; one that hasn't is started afresh by Tick
                if (!finder.Refining)
                    _detector = _checker = null;
            }
            if (SetupVisible)
                gtk_widget_queue_draw(_preview);
        }

        private void SetCrop(Rectangle crop, bool verified)
        {
            _settings.Crop = crop;
            _settings.CropVerified = verified;
            _settings.CropFrameSize = _processor.FrameWidth > 0
                ? new Size(_processor.FrameWidth, _processor.FrameHeight)
                : new Size(_snapW, _snapH);
            _processor.Configure(crop, _route);
            SaveSettings();
            gtk_widget_queue_draw(_preview);
        }

        // ---------- manual cropping ----------

        private void ToggleManualCrop()
        {
            if (_manualCrop)
            {
                ExitManualCrop();
                return;
            }
            if (!_capturing || _snapW == 0)
            {
                ShowSticky("Capture Window not yet selected");
                return;
            }
            if (_detector != null)
            {
                ShowSticky("Auto-detection is still running — wait for it to finish.");
                return;
            }
            _manualCrop = true;
            _checker = null;
            gtk_button_set_label(_cropButton, "Cancel Manual Crop");
            ShowSticky("Drag a box on the preview around the full game picture.");
        }

        private void ExitManualCrop()
        {
            _manualCrop = false;
            _dragging = false;
            gtk_button_set_label(_cropButton, "Manually Crop Gamefeed");
        }

        private bool PreviewPress(IntPtr ev)
        {
            gdk_event_get_button(ev, out uint button);
            if (!_manualCrop || _snapW == 0 || button != 1)
                return false;
            gdk_event_get_coords(ev, out double x, out double y);
            _dragging = true;
            _dragStart = PreviewToImage(x, y);
            _cropBeforeDrag = _settings.Crop; // restored if this is a stray click
            _settings.Crop = new Rectangle(_dragStart, Size.Empty);
            return true;
        }

        private bool PreviewMotion(IntPtr ev)
        {
            if (!_dragging)
                return false;
            gdk_event_get_coords(ev, out double x, out double y);
            var p = PreviewToImage(x, y);
            _settings.Crop = Rectangle.FromLTRB(Math.Min(_dragStart.X, p.X), Math.Min(_dragStart.Y, p.Y),
                Math.Max(_dragStart.X, p.X), Math.Max(_dragStart.Y, p.Y));
            gtk_widget_queue_draw(_preview);
            return true;
        }

        private bool PreviewRelease(IntPtr ev)
        {
            if (!_dragging)
                return false;
            _dragging = false;
            if (_settings.Crop.Width < 4 || _settings.Crop.Height < 4)
            {
                // stray click: keep the previous crop and stay in crop mode
                _settings.Crop = _cropBeforeDrag;
                gtk_widget_queue_draw(_preview);
                return true;
            }
            // onto the picture's edges, where they can be seen close by
            SetCrop(GameFinder.Snap(_snapshot, _snapW, _snapH, _snapW * 4, false, _settings.Crop), false);
            ShowSticky("Crop set. It adjusts itself to the score when one appears.", 8);
            ExitManualCrop(); // one crop per button press
            return true;
        }

        // ---------- preview painting ----------

        private (double X, double Y, double W, double H) DisplayRect()
        {
            double bw = gtk_widget_get_allocated_width(_preview), bh = gtk_widget_get_allocated_height(_preview);
            if (_snapW == 0 || bw <= 0 || bh <= 0) return (0, 0, 0, 0);
            double scale = Math.Min(bw / _snapW, bh / _snapH);
            double w = _snapW * scale, h = _snapH * scale;
            return ((bw - w) / 2, (bh - h) / 2, w, h);
        }

        private Point PreviewToImage(double x, double y)
        {
            var d = DisplayRect();
            if (d.W <= 0) return Point.Empty;
            int ix = (int)((x - d.X) * _snapW / d.W), iy = (int)((y - d.Y) * _snapH / d.H);
            return new Point(Math.Clamp(ix, 0, _snapW - 1), Math.Clamp(iy, 0, _snapH - 1));
        }

        private bool DrawPreview(IntPtr cr)
        {
            double bw = gtk_widget_get_allocated_width(_preview), bh = gtk_widget_get_allocated_height(_preview);
            cairo_set_source_rgb(cr, 0.12, 0.12, 0.12);
            cairo_rectangle(cr, 0, 0, bw, bh);
            cairo_fill(cr);
            if (_snapshot == null || _snapW == 0)
            {
                cairo_set_source_rgb(cr, 0.86, 0.86, 0.86);
                cairo_set_font_size(cr, 14);
                cairo_move_to(cr, 16, bh / 2);
                cairo_show_text(cr, _capturing ? "Waiting for the first frame…" : "No capture yet — select a window above.");
                return true;
            }

            var d = DisplayRect();
            fixed (byte* pixels = _snapshot)
            {
                IntPtr surface = cairo_image_surface_create_for_data(pixels, CAIRO_FORMAT_RGB24, _snapW, _snapH, _snapW * 4);
                cairo_save(cr);
                cairo_translate(cr, d.X, d.Y);
                cairo_scale(cr, d.W / _snapW, d.H / _snapH);
                cairo_set_source_surface(cr, surface, 0, 0);
                cairo_pattern_set_filter(cairo_get_source(cr), CAIRO_FILTER_GOOD);
                cairo_paint(cr);
                cairo_restore(cr);
                cairo_surface_destroy(surface);
            }

            var crop = _settings.Crop;
            if (crop.Width <= 0 || crop.Height <= 0)
                return true;
            double s = d.W / _snapW;
            double cx = d.X + crop.X * s, cy = d.Y + crop.Y * s, cw = crop.Width * s, ch = crop.Height * s;
            // dim everything outside the crop so it stands out
            cairo_set_fill_rule(cr, CAIRO_FILL_RULE_EVEN_ODD);
            cairo_rectangle(cr, d.X, d.Y, d.W, d.H);
            cairo_rectangle(cr, cx, cy, cw, ch);
            cairo_set_source_rgba(cr, 0, 0, 0, 0.35);
            cairo_fill(cr);
            cairo_set_line_width(cr, 2);
            cairo_set_font_size(cr, 13);
            cairo_set_source_rgb(cr, 1, 0, 0);
            cairo_rectangle(cr, cx, cy, cw, ch);
            cairo_stroke(cr);
            cairo_move_to(cr, cx + 4, cy + 15);
            cairo_show_text(cr, _settings.CropVerified ? "game feed (confirmed by the score)" : "game feed (not confirmed yet)");

            var f = BattleTanx.ScoreFraction;
            double sx = cx + f.X * cw, sy = cy + f.Y * ch, sw = f.Width * cw, sh = f.Height * ch;
            cairo_set_source_rgb(cr, 0, 1, 0);
            cairo_rectangle(cr, sx, sy, sw, sh);
            cairo_stroke(cr);
            cairo_move_to(cr, sx, Math.Max(12, sy - 5));
            cairo_show_text(cr, "score");
            return true;
        }

        // ---------- notes ----------

        private void ChooseCsv()
        {
            IntPtr dialog = gtk_file_chooser_native_new("Choose route CSV (column 1 = score, column 2 = note)",
                _setup, 0, "_Open", "_Cancel");
            IntPtr csv = gtk_file_filter_new();
            gtk_file_filter_set_name(csv, "CSV files");
            gtk_file_filter_add_pattern(csv, "*.csv");
            gtk_file_filter_add_pattern(csv, "*.CSV");
            gtk_file_chooser_add_filter(dialog, csv);
            IntPtr all = gtk_file_filter_new();
            gtk_file_filter_set_name(all, "All files");
            gtk_file_filter_add_pattern(all, "*");
            gtk_file_chooser_add_filter(dialog, all);
            if (gtk_native_dialog_run(dialog) == RESPONSE_ACCEPT)
            {
                IntPtr name = gtk_file_chooser_get_filename(dialog);
                string path = GLib.Utf8(name);
                GLib.g_free(name);
                if (path != null)
                    LoadCsv(path, quiet: false);
            }
            GLib.g_object_unref(dialog);
        }

        private void LoadCsv(string path, bool quiet)
        {
            Dictionary<long, string> table;
            try
            {
                table = RouteCsv.Load(path);
            }
            catch (Exception ex)
            {
                if (!quiet)
                    Message("Could not load the route CSV:\n" + ex.Message);
                return;
            }
            _route = table;
            _settings.CsvPath = path;
            _processor.Configure(_settings.Crop, _route);
            _notes.SizeToFit(table.Values, _settings.NotesFont);
            SaveSettings();
            UpdateButtonLabels();
        }

        private void ChooseNotesStyle()
        {
            string font = _settings.NotesFont, text = _settings.NotesTextColor, back = _settings.NotesBackColor;
            bool above = _settings.NotesTopMost;
            if (StyleDialog.Run(_setup, ref font, ref text, ref back, ref above, ApplyNotesStyle))
            {
                _settings.NotesFont = font;
                _settings.NotesTextColor = text;
                _settings.NotesBackColor = back;
                _settings.NotesTopMost = above;
                SaveSettings();
            }
            // settings change only on Save, so this commits or reverts the preview
            ApplyNotesStyle(_settings.NotesFont, _settings.NotesTextColor, _settings.NotesBackColor, _settings.NotesTopMost);
        }

        private void ApplyNotesStyle(string font, string text, string back, bool above)
        {
            _notes.ApplyStyle(font, text, back, above);
            if (_route.Count > 0)
                _notes.SizeToFit(_route.Values, font);
        }

        private void Message(string text)
        {
            IntPtr dialog = gtk_dialog_new();
            gtk_window_set_title(dialog, "bga_spnfdr");
            gtk_window_set_transient_for(dialog, _setup);
            gtk_window_set_modal(dialog, 1);
            gtk_dialog_add_button(dialog, "_OK", RESPONSE_OK);
            IntPtr label = gtk_label_new(text);
            gtk_container_set_border_width(label, 12);
            gtk_container_add(gtk_dialog_get_content_area(dialog), label);
            gtk_widget_show_all(dialog);
            gtk_dialog_run(dialog);
            gtk_widget_destroy(dialog);
        }

        // ---------- lifecycle ----------

        private void RememberSetupBounds()
        {
            if (!SetupVisible)
                return;
            _settings.SetupMaximized = gtk_window_is_maximized(_setup) != 0;
            if (_settings.SetupMaximized)
                return;
            gtk_window_get_position(_setup, out int x, out int y);
            gtk_window_get_size(_setup, out int w, out int h);
            _settings.SetupBounds = new Rectangle(x, y, w, h);
        }

        private void SaveSettings()
        {
            if (gtk_widget_get_visible(_notes.Window) != 0)
            {
                gtk_window_get_position(_notes.Window, out int x, out int y);
                _settings.NotesLocation = new Point(x, y);
            }
            _settings.Save();
        }

        private void Quit()
        {
            RememberSetupBounds();
            SaveSettings();
            StopStream();
            _portal.Close();
            gtk_main_quit();
        }
    }
}
