using System;
using System.Globalization;
using BgaSpnfdr.Native;
using static BgaSpnfdr.Native.Gtk;

namespace BgaSpnfdr.UI
{
    /// <summary>One dialog for how the notes window looks: font (family,
    /// size, bold/italic via the system font picker), text color, background
    /// color, keep above other windows. Every change is applied to the live
    /// notes window straight away (it is its own preview); Save keeps it,
    /// Cancel lets the caller revert.</summary>
    internal static unsafe class StyleDialog
    {
        /// <summary>Returns true if the user pressed Save; the out values
        /// hold the final choices either way.</summary>
        public static bool Run(IntPtr parent, ref string font, ref string textColor, ref string backColor,
            ref bool keepAbove, Action<string, string, string, bool> liveApply)
        {
            IntPtr dialog = gtk_dialog_new();
            gtk_window_set_title(dialog, "Notes appearance");
            gtk_window_set_transient_for(dialog, parent);
            gtk_window_set_modal(dialog, 1);
            gtk_window_set_resizable(dialog, 0);
            gtk_dialog_add_button(dialog, "_Cancel", RESPONSE_CANCEL);
            gtk_dialog_add_button(dialog, "_Save", RESPONSE_OK);
            gtk_dialog_set_default_response(dialog, RESPONSE_OK);

            IntPtr grid = gtk_grid_new();
            gtk_grid_set_row_spacing(grid, 8);
            gtk_grid_set_column_spacing(grid, 12);
            gtk_container_set_border_width(grid, 12);
            gtk_container_add(gtk_dialog_get_content_area(dialog), grid);

            IntPtr fontButton = gtk_font_button_new_with_font(font);
            var text = ToRgba(textColor);
            var back = ToRgba(backColor);
            IntPtr textButton = gtk_color_button_new_with_rgba(&text);
            IntPtr backButton = gtk_color_button_new_with_rgba(&back);
            IntPtr above = gtk_check_button_new_with_label("Keep the notes window above other windows");
            gtk_toggle_button_set_active(above, keepAbove ? 1 : 0);

            Row(grid, 0, "Font:", fontButton);
            Row(grid, 1, "Text color:", textButton);
            Row(grid, 2, "Background:", backButton);
            gtk_grid_attach(grid, above, 1, 3, 1, 1);

            string f = font, t = textColor, b = backColor;
            bool k = keepAbove;
            void Push()
            {
                IntPtr chosen = gtk_font_chooser_get_font(fontButton);
                if (chosen != IntPtr.Zero)
                {
                    f = GLib.Utf8(chosen);
                    GLib.g_free(chosen);
                }
                RGBA c;
                gtk_color_chooser_get_rgba(textButton, &c); t = ToHex(c);
                gtk_color_chooser_get_rgba(backButton, &c); b = ToHex(c);
                k = gtk_toggle_button_get_active(above) != 0;
                liveApply(f, t, b, k);
            }
            Callbacks.Connect(fontButton, "font-set", Push);
            Callbacks.Connect(textButton, "color-set", Push);
            Callbacks.Connect(backButton, "color-set", Push);
            Callbacks.Connect(above, "toggled", Push);

            gtk_widget_show_all(dialog);
            bool saved = gtk_dialog_run(dialog) == RESPONSE_OK;
            gtk_widget_destroy(dialog);
            font = f; textColor = t; backColor = b; keepAbove = k;
            return saved;
        }

        private static void Row(IntPtr grid, int row, string caption, IntPtr widget)
        {
            IntPtr label = gtk_label_new(caption);
            gtk_label_set_xalign(label, 0);
            gtk_grid_attach(grid, label, 0, row, 1, 1);
            gtk_grid_attach(grid, widget, 1, row, 1, 1);
        }

        public static RGBA ToRgba(string hex)
        {
            int v = int.Parse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new RGBA { Red = (v >> 16 & 255) / 255.0, Green = (v >> 8 & 255) / 255.0, Blue = (v & 255) / 255.0, Alpha = 1 };
        }

        public static string ToHex(RGBA c) =>
            "#" + ((int)Math.Round(c.Red * 255)).ToString("x2") + ((int)Math.Round(c.Green * 255)).ToString("x2")
                + ((int)Math.Round(c.Blue * 255)).ToString("x2");
    }
}
