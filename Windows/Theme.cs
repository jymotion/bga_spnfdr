using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BgaSpnfdr
{
    /// <summary>
    /// Follows the Windows light/dark app theme. Classic desktop apps don't
    /// get themed automatically: the dark title bar must be requested from
    /// DWM, and the client area colors are ours to set. Detected once at
    /// startup (like most Win32 apps, a theme change needs a restart).
    /// </summary>
    internal static class Theme
    {
        public static readonly bool Dark = DetectDark();

        private static readonly Color FormBack = Color.FromArgb(32, 32, 32);
        private static readonly Color ControlBack = Color.FromArgb(55, 55, 55);
        private static readonly Color Fore = Color.FromArgb(240, 240, 240);
        private static readonly Color Border = Color.FromArgb(100, 100, 100);

        private static bool DetectDark()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Dark title bar only — for windows whose client area has
        /// its own colors (the notes window). Must not force handle creation
        /// (that would lock in StartPosition before saved bounds are applied),
        /// so it hooks HandleCreated instead of touching Handle directly.</summary>
        public static void ApplyTitleBar(Form form)
        {
            if (!Dark)
                return;
            if (form.IsHandleCreated)
                Native.UseDarkTitleBar(form.Handle);
            form.HandleCreated += (s, e) => Native.UseDarkTitleBar(form.Handle);
        }

        /// <summary>Dark title bar + dark client colors, recursively.</summary>
        public static void Apply(Form form)
        {
            if (!Dark)
                return;
            ApplyTitleBar(form);
            form.BackColor = FormBack;
            ApplyToChildren(form);
        }

        private static void ApplyToChildren(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                switch (c)
                {
                    case Button button:
                        button.FlatStyle = FlatStyle.Flat;
                        button.BackColor = ControlBack;
                        button.ForeColor = Fore;
                        button.FlatAppearance.BorderColor = Border;
                        break;
                    case ComboBox combo:
                        combo.FlatStyle = FlatStyle.Flat;
                        combo.BackColor = ControlBack;
                        combo.ForeColor = Fore;
                        break;
                    case NumericUpDown numeric:
                        numeric.BackColor = ControlBack;
                        numeric.ForeColor = Fore;
                        break;
                    case CheckBox check:
                        check.ForeColor = Fore;
                        break;
                    case Label label:
                        label.ForeColor = Fore;
                        break;
                    case FlowLayoutPanel flow:
                        flow.BackColor = FormBack;
                        break;
                    case PictureBox _:
                    case Panel _:
                        // the preview keeps its own dark color; swatch panels
                        // show a chosen color that must not be overwritten
                        break;
                }
                if (c.HasChildren)
                    ApplyToChildren(c);
            }
        }
    }
}
