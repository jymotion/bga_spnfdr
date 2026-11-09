using System;
using System.Runtime.InteropServices;

namespace BgaSpnfdr.Native
{
    /// <summary>The GTK 3 (+ GDK, Pango, Cairo) functions the app uses. GTK 3
    /// is installed on practically every Linux desktop, KDE included (Firefox
    /// and most other apps depend on it), so nothing is bundled.</summary>
    internal static unsafe partial class Gtk
    {
        private const string Lib = "libgtk-3.so.0";
        private const string Gdk = "libgdk-3.so.0";
        private const string Pango = "libpango-1.0.so.0";
        private const string Cairo = "libcairo.so.2";

        // ----- startup / main loop -----
        [LibraryImport(Gdk, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void gdk_set_allowed_backends(string backends);
        [LibraryImport(Lib)] public static partial int gtk_init_check(IntPtr argc, IntPtr argv);
        [LibraryImport(Lib)] public static partial void gtk_main();
        [LibraryImport(Lib)] public static partial void gtk_main_quit();

        // ----- widgets -----
        [LibraryImport(Lib)] public static partial void gtk_widget_show_all(IntPtr widget);
        [LibraryImport(Lib)] public static partial void gtk_widget_show(IntPtr widget);
        [LibraryImport(Lib)] public static partial void gtk_widget_hide(IntPtr widget);
        [LibraryImport(Lib)] public static partial void gtk_widget_destroy(IntPtr widget);
        [LibraryImport(Lib)] public static partial int gtk_widget_get_visible(IntPtr widget);
        [LibraryImport(Lib)] public static partial void gtk_widget_queue_draw(IntPtr widget);
        [LibraryImport(Lib)] public static partial void gtk_widget_add_events(IntPtr widget, int events);
        [LibraryImport(Lib)] public static partial int gtk_widget_get_allocated_width(IntPtr widget);
        [LibraryImport(Lib)] public static partial int gtk_widget_get_allocated_height(IntPtr widget);
        [LibraryImport(Lib)] public static partial void gtk_widget_set_size_request(IntPtr widget, int w, int h);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void gtk_widget_set_name(IntPtr widget, string name);
        [LibraryImport(Lib)] public static partial void gtk_widget_set_margin_start(IntPtr widget, int margin);
        [LibraryImport(Lib)] public static partial void gtk_widget_set_margin_end(IntPtr widget, int margin);
        [LibraryImport(Lib)] public static partial void gtk_widget_set_margin_top(IntPtr widget, int margin);
        [LibraryImport(Lib)] public static partial void gtk_widget_set_margin_bottom(IntPtr widget, int margin);
        [LibraryImport(Lib)] public static partial void gtk_widget_set_halign(IntPtr widget, int align);
        [LibraryImport(Lib)] public static partial void gtk_widget_set_valign(IntPtr widget, int align);
        [LibraryImport(Lib)] public static partial IntPtr gtk_widget_get_style_context(IntPtr widget);
        [LibraryImport(Lib)] public static partial IntPtr gtk_widget_get_window(IntPtr widget);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr gtk_widget_create_pango_layout(IntPtr widget, string text);
        [LibraryImport(Lib)] public static partial void gtk_widget_set_sensitive(IntPtr widget, int sensitive);

        [LibraryImport(Lib)] public static partial IntPtr gtk_window_new(int type);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void gtk_window_set_title(IntPtr window, string title);
        [LibraryImport(Lib)] public static partial void gtk_window_set_default_size(IntPtr window, int w, int h);
        [LibraryImport(Lib)] public static partial void gtk_window_resize(IntPtr window, int w, int h);
        [LibraryImport(Lib)] public static partial void gtk_window_move(IntPtr window, int x, int y);
        [LibraryImport(Lib)] public static partial void gtk_window_get_position(IntPtr window, out int x, out int y);
        [LibraryImport(Lib)] public static partial void gtk_window_get_size(IntPtr window, out int w, out int h);
        [LibraryImport(Lib)] public static partial void gtk_window_set_keep_above(IntPtr window, int setting);
        [LibraryImport(Lib)] public static partial void gtk_window_present(IntPtr window);
        [LibraryImport(Lib)] public static partial void gtk_window_maximize(IntPtr window);
        [LibraryImport(Lib)] public static partial int gtk_window_is_maximized(IntPtr window);
        [LibraryImport(Lib)] public static partial void gtk_window_set_transient_for(IntPtr window, IntPtr parent);
        [LibraryImport(Lib)] public static partial void gtk_window_set_modal(IntPtr window, int modal);
        [LibraryImport(Lib)] public static partial void gtk_window_set_resizable(IntPtr window, int resizable);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void gtk_window_set_icon_name(IntPtr window, string name);

        [LibraryImport(Lib)] public static partial IntPtr gtk_box_new(int orientation, int spacing);
        [LibraryImport(Lib)] public static partial void gtk_box_pack_start(IntPtr box, IntPtr child, int expand, int fill, uint padding);
        [LibraryImport(Lib)] public static partial void gtk_box_pack_end(IntPtr box, IntPtr child, int expand, int fill, uint padding);
        [LibraryImport(Lib)] public static partial void gtk_container_add(IntPtr container, IntPtr child);
        [LibraryImport(Lib)] public static partial void gtk_container_set_border_width(IntPtr container, uint width);
        [LibraryImport(Lib)] public static partial IntPtr gtk_grid_new();
        [LibraryImport(Lib)] public static partial void gtk_grid_attach(IntPtr grid, IntPtr child, int left, int top, int width, int height);
        [LibraryImport(Lib)] public static partial void gtk_grid_set_row_spacing(IntPtr grid, uint spacing);
        [LibraryImport(Lib)] public static partial void gtk_grid_set_column_spacing(IntPtr grid, uint spacing);

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr gtk_button_new_with_label(string label);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void gtk_button_set_label(IntPtr button, string label);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr gtk_label_new(string text);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void gtk_label_set_text(IntPtr label, string text);
        [LibraryImport(Lib)] public static partial void gtk_label_set_xalign(IntPtr label, float xalign);
        [LibraryImport(Lib)] public static partial void gtk_label_set_yalign(IntPtr label, float yalign);
        [LibraryImport(Lib)] public static partial void gtk_label_set_ellipsize(IntPtr label, int mode);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr gtk_check_button_new_with_label(string label);
        [LibraryImport(Lib)] public static partial int gtk_toggle_button_get_active(IntPtr button);
        [LibraryImport(Lib)] public static partial void gtk_toggle_button_set_active(IntPtr button, int active);
        [LibraryImport(Lib)] public static partial IntPtr gtk_drawing_area_new();
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr gtk_font_button_new_with_font(string font);
        [LibraryImport(Lib)] public static partial IntPtr gtk_font_chooser_get_font(IntPtr chooser); // g_free
        [LibraryImport(Lib)] public static partial IntPtr gtk_color_button_new_with_rgba(RGBA* color);
        [LibraryImport(Lib)] public static partial void gtk_color_chooser_get_rgba(IntPtr chooser, RGBA* color);

        // ----- menus -----
        [LibraryImport(Lib)] public static partial IntPtr gtk_menu_new();
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr gtk_menu_item_new_with_label(string label);
        [LibraryImport(Lib)] public static partial IntPtr gtk_separator_menu_item_new();
        [LibraryImport(Lib)] public static partial void gtk_menu_shell_append(IntPtr menu, IntPtr item);
        [LibraryImport(Lib)] public static partial void gtk_menu_popup_at_pointer(IntPtr menu, IntPtr triggerEvent);

        // ----- dialogs -----
        [LibraryImport(Lib)] public static partial IntPtr gtk_dialog_new();
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr gtk_dialog_add_button(IntPtr dialog, string text, int responseId);
        [LibraryImport(Lib)] public static partial IntPtr gtk_dialog_get_content_area(IntPtr dialog);
        [LibraryImport(Lib)] public static partial int gtk_dialog_run(IntPtr dialog);
        [LibraryImport(Lib)] public static partial void gtk_dialog_set_default_response(IntPtr dialog, int responseId);
        public const int RESPONSE_OK = -5, RESPONSE_CANCEL = -6, RESPONSE_ACCEPT = -3;

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr gtk_file_chooser_native_new(string title, IntPtr parent, int action,
            string acceptLabel, string cancelLabel);
        [LibraryImport(Lib)] public static partial int gtk_native_dialog_run(IntPtr dialog);
        [LibraryImport(Lib)] public static partial IntPtr gtk_file_chooser_get_filename(IntPtr chooser); // g_free
        [LibraryImport(Lib)] public static partial void gtk_file_chooser_add_filter(IntPtr chooser, IntPtr filter);
        [LibraryImport(Lib)] public static partial IntPtr gtk_file_filter_new();
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void gtk_file_filter_set_name(IntPtr filter, string name);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void gtk_file_filter_add_pattern(IntPtr filter, string pattern);

        // ----- CSS styling -----
        [LibraryImport(Lib)] public static partial IntPtr gtk_css_provider_new();
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int gtk_css_provider_load_from_data(IntPtr provider, string data, nint length, IntPtr* error);
        [LibraryImport(Lib)] public static partial void gtk_style_context_add_provider(IntPtr context, IntPtr provider, uint priority);
        public const uint STYLE_PROVIDER_PRIORITY_APPLICATION = 600;

        // ----- events (accessors avoid depending on GdkEvent struct layout) -----
        [LibraryImport(Gdk)] public static partial int gdk_event_get_event_type(IntPtr ev);
        [LibraryImport(Gdk)] public static partial int gdk_event_get_coords(IntPtr ev, out double x, out double y);
        [LibraryImport(Gdk)] public static partial int gdk_event_get_button(IntPtr ev, out uint button);
        [LibraryImport(Gdk)] public static partial IntPtr gdk_x11_window_get_xid(IntPtr window);
        [LibraryImport(Gdk)] public static partial IntPtr gdk_display_get_default();
        [LibraryImport(Gdk)] public static partial IntPtr gdk_display_get_name(IntPtr display);
        public const int GDK_BUTTON_PRESS = 4, GDK_2BUTTON_PRESS = 5;
        public const int POINTER_MOTION_MASK = 1 << 2, BUTTON_PRESS_MASK = 1 << 8, BUTTON_RELEASE_MASK = 1 << 9;
        public const int ORIENTATION_HORIZONTAL = 0, ORIENTATION_VERTICAL = 1;
        public const int ALIGN_FILL = 0, ALIGN_START = 1, ALIGN_END = 2, ALIGN_CENTER = 3;

        // ----- Pango (text measuring, font descriptions) -----
        [LibraryImport(Pango, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr pango_font_description_from_string(string text);
        [LibraryImport(Pango)] public static partial void pango_font_description_free(IntPtr desc);
        [LibraryImport(Pango)] public static partial IntPtr pango_font_description_get_family(IntPtr desc);
        [LibraryImport(Pango)] public static partial int pango_font_description_get_size(IntPtr desc);
        [LibraryImport(Pango)] public static partial int pango_font_description_get_size_is_absolute(IntPtr desc);
        [LibraryImport(Pango)] public static partial int pango_font_description_get_weight(IntPtr desc);
        [LibraryImport(Pango)] public static partial int pango_font_description_get_style(IntPtr desc);
        [LibraryImport(Pango)] public static partial void pango_layout_set_font_description(IntPtr layout, IntPtr desc);
        [LibraryImport(Pango)] public static partial void pango_layout_get_pixel_size(IntPtr layout, out int w, out int h);
        public const int PANGO_SCALE = 1024;

        // ----- Cairo (drawing the preview) -----
        public const int CAIRO_FORMAT_RGB24 = 1;
        [LibraryImport(Cairo)] public static partial IntPtr cairo_image_surface_create_for_data(byte* data, int format, int w, int h, int stride);
        [LibraryImport(Cairo)] public static partial void cairo_surface_destroy(IntPtr surface);
        [LibraryImport(Cairo)] public static partial void cairo_save(IntPtr cr);
        [LibraryImport(Cairo)] public static partial void cairo_restore(IntPtr cr);
        [LibraryImport(Cairo)] public static partial void cairo_translate(IntPtr cr, double x, double y);
        [LibraryImport(Cairo)] public static partial void cairo_scale(IntPtr cr, double sx, double sy);
        [LibraryImport(Cairo)] public static partial void cairo_set_source_surface(IntPtr cr, IntPtr surface, double x, double y);
        [LibraryImport(Cairo)] public static partial IntPtr cairo_get_source(IntPtr cr);
        [LibraryImport(Cairo)] public static partial void cairo_pattern_set_filter(IntPtr pattern, int filter);
        [LibraryImport(Cairo)] public static partial void cairo_paint(IntPtr cr);
        [LibraryImport(Cairo)] public static partial void cairo_set_source_rgb(IntPtr cr, double r, double g, double b);
        [LibraryImport(Cairo)] public static partial void cairo_set_source_rgba(IntPtr cr, double r, double g, double b, double a);
        [LibraryImport(Cairo)] public static partial void cairo_rectangle(IntPtr cr, double x, double y, double w, double h);
        [LibraryImport(Cairo)] public static partial void cairo_fill(IntPtr cr);
        [LibraryImport(Cairo)] public static partial void cairo_stroke(IntPtr cr);
        [LibraryImport(Cairo)] public static partial void cairo_set_line_width(IntPtr cr, double width);
        [LibraryImport(Cairo)] public static partial void cairo_set_fill_rule(IntPtr cr, int rule);
        [LibraryImport(Cairo)] public static partial void cairo_move_to(IntPtr cr, double x, double y);
        [LibraryImport(Cairo, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void cairo_show_text(IntPtr cr, string text);
        [LibraryImport(Cairo)] public static partial void cairo_set_font_size(IntPtr cr, double size);
        public const int CAIRO_FILTER_FAST = 0, CAIRO_FILTER_GOOD = 1, CAIRO_FILTER_BILINEAR = 4;
        public const int CAIRO_FILL_RULE_EVEN_ODD = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct RGBA
        {
            public double Red, Green, Blue, Alpha;
        }

        /// <summary>Applies a CSS snippet to one widget.</summary>
        public static IntPtr ApplyCss(IntPtr widget, string css, IntPtr previous)
        {
            IntPtr provider = previous != IntPtr.Zero ? previous : gtk_css_provider_new();
            IntPtr error = IntPtr.Zero;
            if (gtk_css_provider_load_from_data(provider, css, -1, &error) == 0)
                Console.Error.WriteLine("bga_spnfdr: CSS error: " + GLib.TakeError(error));
            if (previous == IntPtr.Zero)
                gtk_style_context_add_provider(gtk_widget_get_style_context(widget), provider,
                    STYLE_PROVIDER_PRIORITY_APPLICATION);
            return provider;
        }
    }
}
