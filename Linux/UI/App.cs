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

        private readonly LinuxSettings _settings = LinuxSettings.Load();
        private readonly FrameProcessor _processor = new FrameProcessor();
        private readonly ScreenCastPortal _portal = new ScreenCastPortal();
        private readonly NotesWindow _notes = new NotesWindow();
        private PipeWireStream _stream;
        private Dictionary<long, string> _route = new Dictionary<long, string>();

        private IntPtr _setup, _preview, _status, _pickButton, _csvButton, _debugButton;
        private bool _debug;     // "Show Debug Info" is on: preview and reading details
        private Size _debugSize; // the setup window's size with them showing
        private bool _capturing;
        private bool _autoCropOnStart;
        private string _captureStatus;
        private string _sticky;
        private long _stickyUntil; // Environment.TickCount64

        // Where the game picture is, and looking for it (shared with the
        // Windows app)
        private readonly GameWatch _watch = new GameWatch();

        private byte[] _snapshot;
        private int _snapW, _snapH;
        private int _snapshotPosted;
        private int _picturesUnused; // ticks since anything last looked at whole pictures

        public App()
        {
            BuildSetupWindow();
            _notes.ShowSetup = ShowSetup;
            _notes.Exit = Quit;

            if (_settings.CsvPath.Length > 0 && File.Exists(_settings.CsvPath))
                LoadCsv(_settings.CsvPath, quiet: true);
            ApplyNotesStyle(_settings.NotesFont, _settings.NotesTextColor, _settings.NotesBackColor, _settings.NotesTopMost);
            _watch.Crop = _settings.Crop;
            _watch.Verified = _settings.CropVerified;
            _watch.FrameSize = _settings.CropFrameSize;
            _watch.Changed = OnCropChanged;
            _watch.Message = ShowSticky;
            Reconfigure();
            _processor.NoteReady = note => Callbacks.Post(() => _notes.SetNote(note));
            _processor.CropMoved = crop => Callbacks.Post(() =>
            {
                // a score was read starting left of the crop's edge, or
                // well off the middle of its band
                if (_capturing)
                    _watch.MoveCrop(crop, new Size(_processor.FrameWidth, _processor.FrameHeight));
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
            // it opens small: buttons and a status line. "Show Debug Info"
            // adds the preview, at the size the window last had with it.
            bool saved = _settings.SetupBounds.Width >= 200 && _settings.SetupBounds.Height >= 150;
            _debugSize = saved ? _settings.SetupBounds.Size : new Size(1000, 600);
            if (saved)
                gtk_window_move(_setup, _settings.SetupBounds.X, _settings.SetupBounds.Y);

            IntPtr vbox = gtk_box_new(ORIENTATION_VERTICAL, 6);
            gtk_container_set_border_width(vbox, 8);
            gtk_container_add(_setup, vbox);

            IntPtr row1 = gtk_box_new(ORIENTATION_HORIZONTAL, 8);
            _pickButton = AddButton(row1, "Select Capture Window", () =>
            {
                StartCapture(prompt: true);
            });
            _csvButton = AddButton(row1, "Select Route CSV", ChooseCsv);
            AddButton(row1, "Change Notes Formatting", ChooseNotesStyle);
            _debugButton = AddButton(row1, "Show Debug Info", ToggleDebug);
            gtk_box_pack_start(vbox, row1, 0, 0, 0);

            _preview = gtk_drawing_area_new();
            gtk_widget_set_size_request(_preview, 320, 240);
            Callbacks.Connect(_preview, "draw", DrawPreview);
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
            if (!_debug)
            {
                gtk_widget_hide(_preview);
                gtk_window_resize(_setup, 1, 1); // as small as the buttons allow
            }
            gtk_window_present(_setup);
        }

        /// <summary>"Show Debug Info" / "Hide Debug Info": the preview, and
        /// reading details in the status line. Off, nothing is copied or
        /// worked out for them.</summary>
        private void ToggleDebug()
        {
            if (_debug)
                RememberSetupBounds(); // the size to come back to
            _debug = !_debug;
            gtk_button_set_label(_debugButton, _debug ? "Hide Debug Info" : "Show Debug Info");
            if (_debug)
            {
                gtk_widget_show(_preview);
                gtk_window_resize(_setup, _debugSize.Width, _debugSize.Height);
            }
            else
            {
                gtk_widget_hide(_preview);
                gtk_window_resize(_setup, 1, 1);
            }
            Tick();
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
            _processor.WantStatus = visible && _debug;

            int fw = _processor.FrameWidth, fh = _processor.FrameHeight;
            long now = Environment.TickCount64;
            if (_capturing && fw > 0)
            {
                _watch.ScoreSeen(_processor.LastScoreSeen);
                _watch.Tick(now, new Size(fw, fh));
            }
            bool pictures = (visible && _debug) || _watch.Busy;
            _processor.WantSnapshots = pictures;
            // Once the score is being read, nothing looks at whole pictures:
            // 5 s on, the frame-sized buffers kept for them (25 MB at 1080p:
            // this one, FrameProcessor's, MenuFinder's) go back to the
            // system. They only do when the collector is told to run:
            // nothing else here makes it.
            if (pictures)
                _picturesUnused = 0;
            else if (_snapshot != null && ++_picturesUnused >= 25)
            {
                _snapshot = null;
                MenuFinder.Release();
                GC.Collect();
            }

            string text;
            if (_watch.Detecting)
                text = _watch.Status(now, -1, false, null, null);
            else if (now < _stickyUntil)
                text = _sticky;
            else if (_captureStatus != null)
                text = _captureStatus;
            else if (!_capturing)
                text = "Select a window to capture.";
            else if (fw == 0)
                text = "Waiting for the first frame from the shared window…";
            else
                text = _watch.Status(now, _processor.Score, _debug, _processor.Status, $"frame {fw}x{fh}");
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
            _stream = new PipeWireStream
            {
                FrameReady = _processor.OnFrame,
                Failed = message => Callbacks.Post(() => OnStreamFailed(message)),
            };
            try
            {
                _stream.Start(fd, node);
                _capturing = true;
                // (a window just picked: the crop was for another, so the
                // game picture is looked for)
                _watch.Start(Environment.TickCount64, _autoCropOnStart);
                _processor.WantSnapshots = _watch.Busy;
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
            _watch.Stop();
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

        private void OnSnapshot()
        {
            Volatile.Write(ref _snapshotPosted, 0);
            if (!_processor.TakeSnapshot(ref _snapshot, out int width, out int height))
                return;
            _snapW = width;
            _snapH = height;
            _watch.Feed(_snapshot, _snapW, _snapH, _snapW * 4, false, new Rectangle(0, 0, _snapW, _snapH), Environment.TickCount64);
            if (SetupVisible)
                gtk_widget_queue_draw(_preview);
        }

        /// <summary>The crop, or whether it can be read through, has
        /// changed: saved, and the reader told.</summary>
        private void OnCropChanged()
        {
            _settings.Crop = _watch.Crop;
            _settings.CropVerified = _watch.Verified;
            _settings.CropFrameSize = _watch.FrameSize;
            Reconfigure();
            SaveSettings();
            gtk_widget_queue_draw(_preview);
        }

        /// <summary>Gives the reader the crop (none while one for another
        /// frame size is being replaced) and the route.</summary>
        private void Reconfigure() =>
            _processor.Configure(_watch.Reading ? _watch.Crop : Rectangle.Empty, _route);

        // ---------- preview painting ----------

        private (double X, double Y, double W, double H) DisplayRect()
        {
            double bw = gtk_widget_get_allocated_width(_preview), bh = gtk_widget_get_allocated_height(_preview);
            if (_snapW == 0 || bw <= 0 || bh <= 0) return (0, 0, 0, 0);
            double scale = Math.Min(bw / _snapW, bh / _snapH);
            double w = _snapW * scale, h = _snapH * scale;
            return ((bw - w) / 2, (bh - h) / 2, w, h);
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
            cairo_show_text(cr, _watch.Label(_processor.Score));

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
            Reconfigure();
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

        /// <summary>The setup window's place, and its size while the debug
        /// info is showing (without it, it is as small as it can be).</summary>
        private void RememberSetupBounds()
        {
            if (!SetupVisible || gtk_window_is_maximized(_setup) != 0)
                return;
            gtk_window_get_position(_setup, out int x, out int y);
            if (_debug)
            {
                gtk_window_get_size(_setup, out int w, out int h);
                _debugSize = new Size(w, h);
            }
            _settings.SetupBounds = new Rectangle(x, y, _debugSize.Width, _debugSize.Height);
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
