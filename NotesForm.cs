using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BgaSpnfdr
{
    /// <summary>
    /// The window the player actually uses during a run: big text on the
    /// user's chosen colors, optionally always on top. Right-click for
    /// "Capture setup…" and "Exit". This window IS the program: it is always
    /// visible, and closing it closes everything. The setup window can be
    /// closed freely once configured.
    /// </summary>
    internal sealed class NotesForm : Form
    {
        private readonly Label _label;
        private readonly Form _setup;
        private Font _ownedFont;

        public NotesForm(Form setup)
        {
            _setup = setup;
            Text = "bga_spnfdr";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(80, 80);
            _label = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Padding = new Padding(12),
                Text = "Waiting for a score…",
            };
            Controls.Add(_label);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Capture setup…", null, (s, e) => ShowSetup());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit bga_spnfdr", null, (s, e) => Application.Exit());
            ContextMenuStrip = menu;
            _label.ContextMenuStrip = menu;
            _label.DoubleClick += (s, e) => ShowSetup();
        }

        private void ShowSetup()
        {
            _setup.Show();
            if (_setup.WindowState == FormWindowState.Minimized)
                _setup.WindowState = FormWindowState.Normal;
            _setup.BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // closing the notes window always closes the whole program
            if (e.CloseReason == CloseReason.UserClosing
                || e.CloseReason == CloseReason.TaskManagerClosing)
                Application.Exit();
            base.OnFormClosing(e);
        }

        public void SetNote(string text)
        {
            _label.Text = text;
        }

        /// <summary>Takes ownership of <paramref name="font"/>.</summary>
        public void ApplyStyle(Font font, Color textColor, Color backColor, bool topMost)
        {
            _label.Font = font;
            if (_ownedFont != null && !ReferenceEquals(_ownedFont, font))
                _ownedFont.Dispose();
            _ownedFont = font;
            _label.ForeColor = textColor;
            _label.BackColor = backColor;
            BackColor = backColor;
            TopMost = topMost;
        }

        /// <summary>Sizes the window to fit the largest note so it doesn't
        /// jump around as notes change mid-run.</summary>
        public void SizeToFit(IEnumerable<string> notes)
        {
            var max = new Size(200, 60);
            foreach (string note in notes)
            {
                var s = TextRenderer.MeasureText(note, _label.Font);
                max.Width = Math.Max(max.Width, s.Width);
                max.Height = Math.Max(max.Height, s.Height);
            }
            ClientSize = new Size(
                max.Width + _label.Padding.Horizontal,
                max.Height + _label.Padding.Vertical);
        }
    }
}
