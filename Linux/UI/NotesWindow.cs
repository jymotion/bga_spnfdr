using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BgaSpnfdr.Native;
using static BgaSpnfdr.Native.Gtk;

namespace BgaSpnfdr.UI
{
    /// <summary>
    /// The window the player uses during a run: big text in the chosen font
    /// and colors, optionally kept above other windows. Right-click for
    /// "Capture setup…" and "Exit"; double-click opens the setup. This
    /// window IS the program: closing it exits.
    /// </summary>
    internal sealed class NotesWindow
    {
        private const int Padding = 12;
        public readonly IntPtr Window;
        private readonly IntPtr _label;
        private readonly IntPtr _menu;
        private IntPtr _labelCss, _windowCss;

        public Action ShowSetup;
        public Action Exit;

        public NotesWindow()
        {
            Window = gtk_window_new(0);
            gtk_window_set_title(Window, "bga_spnfdr");
            gtk_widget_set_name(Window, "notes-window");
            _label = gtk_label_new("Waiting for a score…");
            gtk_widget_set_name(_label, "notes");
            gtk_label_set_xalign(_label, 0);
            gtk_label_set_yalign(_label, 0);
            gtk_widget_set_margin_start(_label, Padding);
            gtk_widget_set_margin_end(_label, Padding);
            gtk_widget_set_margin_top(_label, Padding);
            gtk_widget_set_margin_bottom(_label, Padding);
            gtk_container_add(Window, _label);

            _menu = gtk_menu_new();
            IntPtr setup = gtk_menu_item_new_with_label("Capture setup…");
            IntPtr exit = gtk_menu_item_new_with_label("Exit bga_spnfdr");
            gtk_menu_shell_append(_menu, setup);
            gtk_menu_shell_append(_menu, gtk_separator_menu_item_new());
            gtk_menu_shell_append(_menu, exit);
            gtk_widget_show_all(_menu);
            Callbacks.Connect(setup, "activate", () => ShowSetup?.Invoke());
            Callbacks.Connect(exit, "activate", () => Exit?.Invoke());

            gtk_widget_add_events(Window, BUTTON_PRESS_MASK);
            Callbacks.Connect(Window, "button-press-event", ev =>
            {
                gdk_event_get_button(ev, out uint button);
                int type = gdk_event_get_event_type(ev);
                if (button == 3 && type == GDK_BUTTON_PRESS)
                {
                    gtk_menu_popup_at_pointer(_menu, ev);
                    return true;
                }
                if (button == 1 && type == GDK_2BUTTON_PRESS)
                {
                    ShowSetup?.Invoke();
                    return true;
                }
                return false;
            });
            // closing the notes window always closes the whole program
            Callbacks.Connect(Window, "delete-event", _ =>
            {
                Exit?.Invoke();
                return true;
            });
        }

        public void SetNote(string text) => gtk_label_set_text(_label, text);

        public void ApplyStyle(string font, string textColor, string backColor, bool keepAbove)
        {
            _labelCss = ApplyCss(_label, "#notes { " + FontCss(font) + $" color: {textColor}; }}", _labelCss);
            _windowCss = ApplyCss(Window, $"#notes-window {{ background-color: {backColor}; }}", _windowCss);
            gtk_window_set_keep_above(Window, keepAbove ? 1 : 0);
        }

        /// <summary>Sizes the window to the largest note so it doesn't jump
        /// around as notes change mid-run.</summary>
        public void SizeToFit(IEnumerable<string> notes, string font)
        {
            IntPtr desc = pango_font_description_from_string(font);
            int w = 200, h = 60;
            foreach (string note in notes)
            {
                IntPtr layout = gtk_widget_create_pango_layout(_label, note);
                pango_layout_set_font_description(layout, desc);
                pango_layout_get_pixel_size(layout, out int lw, out int lh);
                GLib.g_object_unref(layout);
                w = Math.Max(w, lw + 2 * Padding);
                h = Math.Max(h, lh + 2 * Padding);
            }
            pango_font_description_free(desc);
            gtk_window_resize(Window, w, h);
        }

        /// <summary>CSS font properties from a Pango description ("Sans Bold
        /// Italic 48").</summary>
        private static string FontCss(string font)
        {
            IntPtr desc = pango_font_description_from_string(font);
            string family = GLib.Utf8(pango_font_description_get_family(desc)) ?? "Sans";
            double size = pango_font_description_get_size(desc) / (double)PANGO_SCALE;
            string unit = pango_font_description_get_size_is_absolute(desc) != 0 ? "px" : "pt";
            int weight = pango_font_description_get_weight(desc);
            int style = pango_font_description_get_style(desc); // 0 normal, 1 oblique, 2 italic
            pango_font_description_free(desc);
            var css = new StringBuilder();
            css.Append("font-family: \"").Append(family.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"; ");
            if (size > 0)
                css.Append("font-size: ").Append(size.ToString("0.#", CultureInfo.InvariantCulture)).Append(unit).Append("; ");
            css.Append("font-weight: ").Append(weight.ToString(CultureInfo.InvariantCulture)).Append("; ");
            css.Append("font-style: ").Append(style == 2 ? "italic" : style == 1 ? "oblique" : "normal").Append(";");
            return css.ToString();
        }
    }
}
