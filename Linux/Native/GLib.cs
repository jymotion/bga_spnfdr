using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace BgaSpnfdr.Native
{
    /// <summary>GLib, GObject and GIO (D-Bus) functions the app uses. These
    /// libraries ship with GTK, so they're on every desktop that has it.</summary>
    internal static unsafe partial class GLib
    {
        private const string Glib = "libglib-2.0.so.0";
        private const string Gobject = "libgobject-2.0.so.0";
        private const string Gio = "libgio-2.0.so.0";

        [LibraryImport(Glib)] public static partial uint g_idle_add(IntPtr function, IntPtr data);
        [LibraryImport(Glib)] public static partial uint g_timeout_add(uint interval, IntPtr function, IntPtr data);
        [LibraryImport(Glib)] public static partial int g_source_remove(uint tag);
        [LibraryImport(Glib)] public static partial void g_free(IntPtr mem);
        [LibraryImport(Glib)] public static partial void g_error_free(IntPtr error);

        [LibraryImport(Gobject, StringMarshalling = StringMarshalling.Utf8)]
        public static partial ulong g_signal_connect_data(IntPtr instance, string signal, IntPtr handler,
            IntPtr data, IntPtr destroy, int flags);
        [LibraryImport(Gobject)] public static partial void g_object_unref(IntPtr obj);

        // ----- GVariant (D-Bus values) -----
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr g_variant_new_string(string s);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr g_variant_new_object_path(string path);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_new_uint32(uint value);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_new_boolean(int value);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_new_variant(IntPtr value);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_new_dict_entry(IntPtr key, IntPtr value);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_new_array(IntPtr childType, IntPtr* children, nuint n);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_new_tuple(IntPtr* children, nuint n);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr g_variant_type_new(string type);
        [LibraryImport(Glib)] public static partial void g_variant_type_free(IntPtr type);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_get_child_value(IntPtr value, nuint index);
        [LibraryImport(Glib)] public static partial nuint g_variant_n_children(IntPtr value);
        [LibraryImport(Glib)] public static partial uint g_variant_get_uint32(IntPtr value);
        [LibraryImport(Glib)] public static partial int g_variant_get_handle(IntPtr value);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_get_string(IntPtr value, IntPtr length);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_get_variant(IntPtr value);
        [LibraryImport(Glib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr g_variant_lookup_value(IntPtr dict, string key, IntPtr expectedType);
        [LibraryImport(Glib)] public static partial IntPtr g_variant_get_type_string(IntPtr value);
        [LibraryImport(Glib)] public static partial void g_variant_unref(IntPtr value);

        // ----- D-Bus -----
        public const int G_BUS_TYPE_SESSION = 2;
        [LibraryImport(Gio)] public static partial IntPtr g_bus_get_sync(int busType, IntPtr cancellable, IntPtr* error);
        [LibraryImport(Gio)] public static partial IntPtr g_dbus_connection_get_unique_name(IntPtr connection);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr g_dbus_connection_call_sync(IntPtr connection, string busName, string objectPath,
            string interfaceName, string methodName, IntPtr parameters, IntPtr replyType, int flags, int timeoutMs,
            IntPtr cancellable, IntPtr* error);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr g_dbus_connection_call_with_unix_fd_list_sync(IntPtr connection, string busName,
            string objectPath, string interfaceName, string methodName, IntPtr parameters, IntPtr replyType, int flags,
            int timeoutMs, IntPtr fdList, IntPtr* outFdList, IntPtr cancellable, IntPtr* error);
        [LibraryImport(Gio, StringMarshalling = StringMarshalling.Utf8)]
        public static partial uint g_dbus_connection_signal_subscribe(IntPtr connection, string sender,
            string interfaceName, string member, string objectPath, string arg0, int flags, IntPtr callback,
            IntPtr userData, IntPtr destroy);
        [LibraryImport(Gio)] public static partial void g_dbus_connection_signal_unsubscribe(IntPtr connection, uint id);
        [LibraryImport(Gio)] public static partial int g_unix_fd_list_get(IntPtr list, int index, IntPtr* error);

        public static string Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        /// <summary>Message of a GError*, freeing it.</summary>
        public static string TakeError(IntPtr error)
        {
            if (error == IntPtr.Zero)
                return "unknown error";
            // struct GError { GQuark domain; gint code; gchar *message; }
            string message = Utf8(*(IntPtr*)((byte*)error + 8));
            g_error_free(error);
            return message;
        }

        /// <summary>Builds an a{sv} dictionary (the portal's option format).</summary>
        public static IntPtr Options(params (string Key, IntPtr Value)[] entries)
        {
            var children = stackalloc IntPtr[Math.Max(1, entries.Length)];
            for (int i = 0; i < entries.Length; i++)
                children[i] = g_variant_new_dict_entry(g_variant_new_string(entries[i].Key),
                    g_variant_new_variant(entries[i].Value));
            IntPtr type = g_variant_type_new("{sv}");
            IntPtr dict = g_variant_new_array(type, children, (nuint)entries.Length);
            g_variant_type_free(type);
            return dict;
        }

        public static IntPtr Tuple(params IntPtr[] children)
        {
            fixed (IntPtr* p = children)
                return g_variant_new_tuple(p, (nuint)children.Length);
        }
    }

    /// <summary>
    /// Routes GTK/GLib callbacks to C# code. Native code can only call static
    /// [UnmanagedCallersOnly] methods, so each connection stores its C#
    /// handler under an id that travels as the callback's user data.
    /// Everything here runs on the GTK main thread, except Post(), which any
    /// thread may call.
    /// </summary>
    internal static unsafe class Callbacks
    {
        private static readonly Dictionary<int, Delegate> Handlers = new Dictionary<int, Delegate>();
        private static int _next;

        private static IntPtr Register(Delegate d)
        {
            int id = ++_next;
            Handlers[id] = d;
            return (IntPtr)id;
        }

        /// <summary>Signals shaped void(GtkWidget*, gpointer): clicked,
        /// toggled, activate, color-set, font-set, ...</summary>
        public static void Connect(IntPtr instance, string signal, Action handler) =>
            GLib.g_signal_connect_data(instance, signal,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnVoid, Register(handler), IntPtr.Zero, 0);

        /// <summary>Signals shaped gboolean(GtkWidget*, X*, gpointer): draw
        /// (X = cairo_t), button/motion/configure/delete events (X =
        /// GdkEvent). Return true to stop further handling.</summary>
        public static void Connect(IntPtr instance, string signal, Func<IntPtr, bool> handler) =>
            GLib.g_signal_connect_data(instance, signal,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnBool, Register(handler), IntPtr.Zero, 0);

        /// <summary>Repeating timer on the main thread; return false to stop.</summary>
        public static uint Timeout(uint milliseconds, Func<bool> tick) =>
            GLib.g_timeout_add(milliseconds, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&OnSource, Register(tick));

        /// <summary>D-Bus signal subscription (handler gets the signal's
        /// parameters GVariant).</summary>
        public static uint Subscribe(IntPtr connection, string sender, string iface, string member, string path,
            Action<IntPtr> handler) =>
            GLib.g_dbus_connection_signal_subscribe(connection, sender, iface, member, path, null, 0,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDbusSignal,
                Register(handler), IntPtr.Zero);

        // ----- cross-thread: queue work for the main thread -----
        private static readonly ConcurrentQueue<Action> Posted = new ConcurrentQueue<Action>();
        private static int _drainScheduled;

        public static void Post(Action action)
        {
            Posted.Enqueue(action);
            if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
                GLib.g_idle_add((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int>)&OnDrain, IntPtr.Zero);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int OnDrain(IntPtr data)
        {
            Volatile.Write(ref _drainScheduled, 0);
            while (Posted.TryDequeue(out var action))
                Guard(action);
            return 0; // one-shot
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnVoid(IntPtr instance, IntPtr data)
        {
            if (Handlers.TryGetValue((int)data, out var d))
                Guard((Action)d);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int OnBool(IntPtr instance, IntPtr arg, IntPtr data)
        {
            if (!Handlers.TryGetValue((int)data, out var d))
                return 0;
            try { return ((Func<IntPtr, bool>)d)(arg) ? 1 : 0; }
            catch (Exception e) { Report(e); return 0; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static int OnSource(IntPtr data)
        {
            if (!Handlers.TryGetValue((int)data, out var d))
                return 0;
            bool again;
            try { again = ((Func<bool>)d)(); }
            catch (Exception e) { Report(e); again = true; }
            if (!again)
                Handlers.Remove((int)data);
            return again ? 1 : 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDbusSignal(IntPtr connection, IntPtr sender, IntPtr path, IntPtr iface, IntPtr member,
            IntPtr parameters, IntPtr data)
        {
            if (Handlers.TryGetValue((int)data, out var d))
            {
                try { ((Action<IntPtr>)d)(parameters); }
                catch (Exception e) { Report(e); }
            }
        }

        private static void Guard(Action action)
        {
            try { action(); }
            catch (Exception e) { Report(e); }
        }

        /// <summary>Exceptions must never unwind into native code.</summary>
        public static void Report(Exception e) => Console.Error.WriteLine("bga_spnfdr: " + e);
    }
}
