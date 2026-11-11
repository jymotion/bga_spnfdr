using System;
using BgaSpnfdr.Native;
using BgaSpnfdr.UI;
using static BgaSpnfdr.Native.Gtk;

namespace BgaSpnfdr
{
    internal static class Program
    {
        // keep in step with <Version> in bga_spnfdr.csproj (asking the
        // program itself for it would add reflection code to it)
        private const string Version = "1.2.0";

        private static App _app; // keeps the app (and its callbacks) alive

        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
            {
                GLib.Print(1, "bga_spnfdr " + Version);
                return 0;
            }
            // Prefer X11 (XWayland on Wayland desktops): only there may an app
            // keep its window above others and put it back where it was, and
            // the notes window needs both. Falls back to anything else.
            if (Environment.GetEnvironmentVariable("GDK_BACKEND") == null)
                gdk_set_allowed_backends("x11,*");
            if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
            {
                GLib.Print(2, "bga_spnfdr: can't open a display (run it from your desktop session)");
                return 1;
            }
            _app = new App();
            gtk_main();
            return 0;
        }
    }
}
