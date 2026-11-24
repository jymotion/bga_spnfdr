using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BgaSpnfdr
{
    /// <summary>One dialog for everything about how the notes window looks:
    /// font family, size, bold/italic, text color, background color, always
    /// on top. Every change is pushed to the live notes window immediately —
    /// the notes window is its own preview (fonts and sizes change its
    /// dimensions, so an in-dialog preview would lie). Save keeps the result;
    /// Cancel lets the caller revert.</summary>
    internal sealed class StyleDialog : Form
    {
        private readonly ComboBox _family;
        private readonly NumericUpDown _size;
        private readonly CheckBox _bold;
        private readonly CheckBox _italic;
        private readonly Panel _textSwatch;
        private readonly Panel _backSwatch;
        private readonly CheckBox _topMost;
        private readonly Action<string, float, FontStyle, Color, Color, bool> _liveApply;

        public string FamilyName => _family.SelectedItem as string ?? "Segoe UI";
        public float FontSizeValue => (float)_size.Value;
        public FontStyle FontStyleValue =>
            (_bold.Checked ? FontStyle.Bold : FontStyle.Regular)
            | (_italic.Checked ? FontStyle.Italic : FontStyle.Regular);
        public Color TextColor { get; private set; }
        public Color BackgroundColor { get; private set; }
        public bool TopMostChoice => _topMost.Checked;

        public StyleDialog(string family, float size, FontStyle style, Color text, Color back,
            bool topMost, Action<string, float, FontStyle, Color, Color, bool> liveApply)
        {
            _liveApply = liveApply;
            Text = "Notes appearance";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(430, 230);
            TextColor = text;
            BackgroundColor = back;

            Controls.Add(new Label { Text = "Font:", Location = new Point(12, 15), AutoSize = true });
            _family = new ComboBox
            {
                Location = new Point(110, 11),
                Width = 220,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            _family.Items.AddRange(FontFamily.Families
                .Select(f => f.Name)
                .Distinct()
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .Cast<object>()
                .ToArray());
            _family.SelectedItem = _family.Items.Contains(family) ? family : "Segoe UI";
            _family.SelectedIndexChanged += (s, e) => PushLive();
            Controls.Add(_family);

            Controls.Add(new Label { Text = "Size:", Location = new Point(12, 49), AutoSize = true });
            _size = new NumericUpDown
            {
                Location = new Point(110, 45),
                Width = 70,
                Minimum = 8,
                Maximum = 200,
                Value = Math.Max(8, Math.Min(200, (decimal)size)),
            };
            _size.ValueChanged += (s, e) => PushLive();
            Controls.Add(_size);
            _bold = new CheckBox
            {
                Text = "Bold",
                Location = new Point(200, 46),
                AutoSize = true,
                Checked = (style & FontStyle.Bold) != 0,
            };
            _bold.CheckedChanged += (s, e) => PushLive();
            Controls.Add(_bold);
            _italic = new CheckBox
            {
                Text = "Italic",
                Location = new Point(265, 46),
                AutoSize = true,
                Checked = (style & FontStyle.Italic) != 0,
            };
            _italic.CheckedChanged += (s, e) => PushLive();
            Controls.Add(_italic);

            Controls.Add(new Label { Text = "Text color:", Location = new Point(12, 83), AutoSize = true });
            var textButton = new Button { Text = "Choose…", Location = new Point(110, 78), AutoSize = true };
            textButton.Click += (s, e) => PickColor(isText: true);
            Controls.Add(textButton);
            _textSwatch = MakeSwatch(new Point(195, 81));
            _textSwatch.BackColor = TextColor;
            Controls.Add(_textSwatch);

            Controls.Add(new Label { Text = "Background:", Location = new Point(12, 116), AutoSize = true });
            var backButton = new Button { Text = "Choose…", Location = new Point(110, 111), AutoSize = true };
            backButton.Click += (s, e) => PickColor(isText: false);
            Controls.Add(backButton);
            _backSwatch = MakeSwatch(new Point(195, 114));
            _backSwatch.BackColor = BackgroundColor;
            Controls.Add(_backSwatch);

            _topMost = new CheckBox
            {
                Text = "Always on top",
                Location = new Point(110, 145),
                AutoSize = true,
                Checked = topMost,
            };
            _topMost.CheckedChanged += (s, e) => PushLive();
            Controls.Add(_topMost);

            var save = new Button
            {
                Text = "Save",
                DialogResult = DialogResult.OK,
                Location = new Point(262, 192),
                Size = new Size(75, 26),
            };
            Controls.Add(save);
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(343, 192),
                Size = new Size(75, 26),
            };
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;

            Theme.Apply(this);
        }

        private static Panel MakeSwatch(Point location) => new Panel
        {
            Location = location,
            Size = new Size(22, 22),
            BorderStyle = BorderStyle.FixedSingle,
        };

        private void PickColor(bool isText)
        {
            using (var dlg = new ColorDialog
            {
                Color = isText ? TextColor : BackgroundColor,
                FullOpen = true,
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                if (isText)
                {
                    TextColor = dlg.Color;
                    _textSwatch.BackColor = dlg.Color;
                }
                else
                {
                    BackgroundColor = dlg.Color;
                    _backSwatch.BackColor = dlg.Color;
                }
                PushLive();
            }
        }

        private void PushLive()
        {
            _liveApply?.Invoke(FamilyName, FontSizeValue, FontStyleValue,
                TextColor, BackgroundColor, TopMostChoice);
        }
    }
}
