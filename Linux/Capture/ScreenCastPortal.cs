using System;
using BgaSpnfdr.Native;
using static BgaSpnfdr.Native.GLib;

namespace BgaSpnfdr.Capture
{
    /// <summary>
    /// Asks the desktop for a window to capture through the standard
    /// screen-sharing prompt (xdg-desktop-portal ScreenCast; the same one OBS,
    /// browsers and Discord use). Works on KDE, GNOME and most other desktops,
    /// Wayland or X11. The portal hands back a PipeWire connection + node id,
    /// and a restore token that lets the next launch reuse the same window
    /// without prompting (where the desktop supports it).
    ///
    /// Each step is a D-Bus call answered later by a Response signal on a
    /// request object; everything runs on the GTK main thread.
    /// </summary>
    internal sealed unsafe class ScreenCastPortal
    {
        private const string Bus = "org.freedesktop.portal.Desktop";
        private const string Path = "/org/freedesktop/portal/desktop";
        private const string Iface = "org.freedesktop.portal.ScreenCast";
        private const uint SourceMonitor = 1, SourceWindow = 2;
        private const uint CursorHidden = 1;
        private const uint PersistUntilRevoked = 2;

        /// <summary>(PipeWire fd, node id, restore token or null)</summary>
        public Action<int, uint, string> Started;
        public Action<string> Failed;

        private IntPtr _bus;
        private string _sender;
        private string _session;
        private int _tokenCounter;
        private uint _subscription;

        public bool Active => _session != null;

        public void Start(string restoreToken, string parentWindow)
        {
            Close();
            if (_bus == IntPtr.Zero)
            {
                IntPtr error = IntPtr.Zero;
                _bus = g_bus_get_sync(G_BUS_TYPE_SESSION, IntPtr.Zero, &error);
                if (_bus == IntPtr.Zero)
                {
                    Failed?.Invoke("no D-Bus session: " + TakeError(error));
                    return;
                }
                // ":1.42" -> "1_42", used in request object paths
                _sender = Utf8(g_dbus_connection_get_unique_name(_bus)).TrimStart(':').Replace('.', '_');
            }

            uint version = PropertyUint("version");
            uint sources = PropertyUint("AvailableSourceTypes");
            uint cursors = PropertyUint("AvailableCursorModes");
            if (sources == 0)
            {
                Failed?.Invoke("this desktop's screen-sharing portal isn't available " +
                               "(install xdg-desktop-portal and your desktop's portal backend)");
                return;
            }
            uint types = (sources & SourceWindow) != 0 ? SourceWindow | (sources & SourceMonitor) : sources;

            // 1. CreateSession
            string sessionToken = "bga_spnfdr_" + Environment.ProcessId;
            Request("CreateSession", t => Tuple(Options(
                    ("handle_token", g_variant_new_string(t)),
                    ("session_handle_token", g_variant_new_string(sessionToken)))),
                results =>
                {
                    _session = LookupString(results, "session_handle");
                    if (_session == null) { Fail("the portal didn't create a session"); return; }

                    // 2. SelectSources: a window (or screen); persist if supported
                    Request("SelectSources", t =>
                        {
                            var opts = new System.Collections.Generic.List<(string, IntPtr)>
                            {
                                ("handle_token", g_variant_new_string(t)),
                                ("types", g_variant_new_uint32(types)),
                                ("multiple", g_variant_new_boolean(0)),
                            };
                            if ((cursors & CursorHidden) != 0)
                                opts.Add(("cursor_mode", g_variant_new_uint32(CursorHidden)));
                            if (version >= 4)
                            {
                                opts.Add(("persist_mode", g_variant_new_uint32(PersistUntilRevoked)));
                                if (!string.IsNullOrEmpty(restoreToken))
                                    opts.Add(("restore_token", g_variant_new_string(restoreToken)));
                            }
                            return Tuple(g_variant_new_object_path(_session), Options(opts.ToArray()));
                        },
                        _ =>
                        {
                            // 3. Start: shows the picker (unless restored)
                            Request("Start", t => Tuple(g_variant_new_object_path(_session),
                                    g_variant_new_string(parentWindow ?? ""),
                                    Options(("handle_token", g_variant_new_string(t)))),
                                started =>
                                {
                                    uint node = FirstStreamNode(started);
                                    if (node == 0) { Fail("no window was shared"); return; }
                                    string token = LookupString(started, "restore_token");
                                    int fd = OpenPipeWireRemote();
                                    if (fd < 0) return;
                                    Started?.Invoke(fd, node, token);
                                });
                        });
                });
        }

        /// <summary>Ends the screen-sharing session (stops the capture).</summary>
        public void Close()
        {
            if (_subscription != 0)
            {
                g_dbus_connection_signal_unsubscribe(_bus, _subscription);
                _subscription = 0;
            }
            if (_session == null)
                return;
            IntPtr error = IntPtr.Zero;
            IntPtr reply = g_dbus_connection_call_sync(_bus, Bus, _session, "org.freedesktop.portal.Session", "Close",
                IntPtr.Zero, IntPtr.Zero, 0, 2000, IntPtr.Zero, &error);
            if (reply != IntPtr.Zero) g_variant_unref(reply); else TakeError(error);
            _session = null;
        }

        /// <summary>Calls a portal method whose answer arrives as a Response
        /// signal. Subscribes before calling so the answer can't be missed.</summary>
        private void Request(string method, Func<string, IntPtr> parameters, Action<IntPtr> onSuccess)
        {
            string token = "bga_spnfdr_" + (++_tokenCounter);
            string requestPath = $"{Path}/request/{_sender}/{token}";
            if (_subscription != 0)
                g_dbus_connection_signal_unsubscribe(_bus, _subscription);
            _subscription = Callbacks.Subscribe(_bus, Bus, "org.freedesktop.portal.Request", "Response", requestPath,
                response =>
                {
                    g_dbus_connection_signal_unsubscribe(_bus, _subscription);
                    _subscription = 0;
                    // (u response, a{sv} results): 0 = success, 1 = cancelled
                    IntPtr code = g_variant_get_child_value(response, 0);
                    IntPtr results = g_variant_get_child_value(response, 1);
                    uint answer = g_variant_get_uint32(code);
                    g_variant_unref(code);
                    try
                    {
                        if (answer == 0) onSuccess(results);
                        else Fail(answer == 1 ? "window selection cancelled" : $"the portal refused ({method})");
                    }
                    finally
                    {
                        g_variant_unref(results);
                    }
                });
            IntPtr error = IntPtr.Zero;
            IntPtr reply = g_dbus_connection_call_sync(_bus, Bus, Path, Iface, method, parameters(token),
                IntPtr.Zero, 0, -1, IntPtr.Zero, &error);
            if (reply == IntPtr.Zero)
                Fail($"screen-sharing portal error ({method}): " + TakeError(error));
            else
                g_variant_unref(reply);
        }

        private int OpenPipeWireRemote()
        {
            IntPtr error = IntPtr.Zero, fds = IntPtr.Zero;
            IntPtr reply = g_dbus_connection_call_with_unix_fd_list_sync(_bus, Bus, Path, Iface, "OpenPipeWireRemote",
                Tuple(g_variant_new_object_path(_session), Options()), IntPtr.Zero, 0, -1, IntPtr.Zero, &fds,
                IntPtr.Zero, &error);
            if (reply == IntPtr.Zero)
            {
                Fail("could not open the capture connection: " + TakeError(error));
                return -1;
            }
            IntPtr handle = g_variant_get_child_value(reply, 0);
            int index = g_variant_get_handle(handle);
            g_variant_unref(handle);
            g_variant_unref(reply);
            int fd = g_unix_fd_list_get(fds, index, &error);
            g_object_unref(fds);
            if (fd < 0)
                Fail("could not open the capture connection: " + TakeError(error));
            return fd;
        }

        /// <summary>Reads a uint property of the ScreenCast interface; 0 if
        /// unavailable.</summary>
        private uint PropertyUint(string name)
        {
            IntPtr error = IntPtr.Zero;
            IntPtr reply = g_dbus_connection_call_sync(_bus, Bus, Path, "org.freedesktop.DBus.Properties", "Get",
                Tuple(g_variant_new_string(Iface), g_variant_new_string(name)), IntPtr.Zero, 0, 5000, IntPtr.Zero, &error);
            if (reply == IntPtr.Zero)
            {
                TakeError(error);
                return 0;
            }
            IntPtr boxed = g_variant_get_child_value(reply, 0);   // v
            IntPtr value = g_variant_get_variant(boxed);           // u
            uint result = Utf8(g_variant_get_type_string(value)) == "u" ? g_variant_get_uint32(value) : 0;
            g_variant_unref(value);
            g_variant_unref(boxed);
            g_variant_unref(reply);
            return result;
        }

        private static string LookupString(IntPtr dict, string key)
        {
            IntPtr v = g_variant_lookup_value(dict, key, IntPtr.Zero);
            if (v == IntPtr.Zero) return null;
            string type = Utf8(g_variant_get_type_string(v));
            string s = type == "s" || type == "o" ? Utf8(g_variant_get_string(v, IntPtr.Zero)) : null;
            g_variant_unref(v);
            return s;
        }

        /// <summary>results["streams"] is a(ua{sv}); returns the first node
        /// id (0 if none).</summary>
        private static uint FirstStreamNode(IntPtr results)
        {
            IntPtr streams = g_variant_lookup_value(results, "streams", IntPtr.Zero);
            if (streams == IntPtr.Zero) return 0;
            uint node = 0;
            if (g_variant_n_children(streams) > 0)
            {
                IntPtr first = g_variant_get_child_value(streams, 0);
                IntPtr id = g_variant_get_child_value(first, 0);
                node = g_variant_get_uint32(id);
                g_variant_unref(id);
                g_variant_unref(first);
            }
            g_variant_unref(streams);
            return node;
        }

        private void Fail(string message)
        {
            Close();
            Failed?.Invoke(message);
        }
    }
}
