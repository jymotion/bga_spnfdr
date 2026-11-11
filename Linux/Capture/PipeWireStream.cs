using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BgaSpnfdr.Native;
using static BgaSpnfdr.Native.PipeWire;

namespace BgaSpnfdr.Capture
{
    /// <summary>One captured frame, valid only during the FrameReady call.
    /// 4 bytes per pixel; RgbOrder = R,G,B,x in memory, else B,G,R,x.</summary>
    internal unsafe readonly struct Frame
    {
        public readonly byte* Data;
        public readonly int Width, Height, Stride;
        public readonly bool RgbOrder;

        public Frame(byte* data, int width, int height, int stride, bool rgbOrder)
        {
            Data = data; Width = width; Height = height; Stride = stride; RgbOrder = rgbOrder;
        }
    }

    /// <summary>
    /// Receives video frames from a PipeWire node: the window the user picked
    /// in the screen-sharing prompt (the portal hands over a PipeWire
    /// connection and node id). Frames arrive on PipeWire's own thread.
    /// Only CPU-readable (shared memory) buffers in 32-bit RGB formats are
    /// requested, so no GPU interop is needed.
    /// </summary>
    internal sealed unsafe class PipeWireStream : IDisposable
    {
        private static bool _initialized;

        /// <summary>Called on PipeWire's thread for every frame.</summary>
        public Action<Frame> FrameReady;
        /// <summary>Called on PipeWire's thread when the stream fails or ends.</summary>
        public Action<string> Failed;

        private IntPtr _loop, _context, _core, _stream;
        private IntPtr _hook, _events;
        private GCHandle _self;
        private int _width, _height;
        private bool _rgbOrder;
        private bool _failedReported;

        /// <summary>Connects to a node. fd: PipeWire connection from the
        /// portal, or -1 for the user's PipeWire daemon (testing only).</summary>
        public void Start(int fd, uint nodeId)
        {
            if (!_initialized)
            {
                pw_init(IntPtr.Zero, IntPtr.Zero);
                _initialized = true;
            }
            _self = GCHandle.Alloc(this);
            _loop = pw_thread_loop_new("bga_spnfdr-capture", IntPtr.Zero);
            _context = pw_context_new(pw_thread_loop_get_loop(_loop), IntPtr.Zero, 0);
            if (pw_thread_loop_start(_loop) < 0)
                throw new InvalidOperationException("could not start the PipeWire thread");

            pw_thread_loop_lock(_loop);
            try
            {
                _core = fd >= 0
                    ? pw_context_connect_fd(_context, fd, IntPtr.Zero, 0)
                    : pw_context_connect(_context, IntPtr.Zero, 0);
                if (_core == IntPtr.Zero)
                    throw new InvalidOperationException("could not connect to PipeWire");

                _stream = pw_stream_new(_core, "bga_spnfdr",
                    pw_properties_new_string("media.type=Video media.category=Capture media.role=Screen"));
                _events = (IntPtr)NativeMemory.AllocZeroed(EventsSize);
                _hook = (IntPtr)NativeMemory.AllocZeroed(HookSize);
                byte* ev = (byte*)_events;
                *(uint*)ev = 0; // events version 0: destroy .. drained
                *(IntPtr*)(ev + EvStateChanged) = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr, void>)&OnStateChanged;
                *(IntPtr*)(ev + EvParamChanged) = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr, void>)&OnParamChanged;
                *(IntPtr*)(ev + EvProcess) = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&OnProcess;
                pw_stream_add_listener(_stream, _hook, _events, GCHandle.ToIntPtr(_self));

                // preferred first: at most 30 frames a second, then any rate,
                // in case a desktop can't agree to the limit
                byte[] limited = BuildEnumFormat(true), any = BuildEnumFormat(false);
                fixed (byte* a = limited, b = any)
                {
                    IntPtr* formats = stackalloc IntPtr[2];
                    formats[0] = (IntPtr)a;
                    formats[1] = (IntPtr)b;
                    int rc = pw_stream_connect(_stream, PW_DIRECTION_INPUT, nodeId,
                        PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS, formats, 2);
                    if (rc < 0)
                        throw new InvalidOperationException($"could not connect the PipeWire stream ({rc})");
                }
            }
            finally
            {
                pw_thread_loop_unlock(_loop);
            }
        }

        /// <summary>The formats we accept: 32-bit RGB in either byte order,
        /// any size, any frame rate (we pick 30 a second ourselves). With
        /// limitRate the desktop is also asked to send at most 30 frames a
        /// second (GNOME and KDE honor it), so it doesn't copy out frames
        /// of a 60-a-second window that FrameProcessor would only drop.</summary>
        internal static byte[] BuildEnumFormat(bool limitRate)
        {
            var w = new PodWriter();
            int obj = w.BeginObject(SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat);
            w.Prop(SPA_FORMAT_mediaType); w.Id(SPA_MEDIA_TYPE_video);
            w.Prop(SPA_FORMAT_mediaSubtype); w.Id(SPA_MEDIA_SUBTYPE_raw);
            w.Prop(SPA_FORMAT_VIDEO_format);
            w.IdEnum(SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_BGRx, SPA_VIDEO_FORMAT_RGBx,
                SPA_VIDEO_FORMAT_BGRA, SPA_VIDEO_FORMAT_RGBA);
            w.Prop(SPA_FORMAT_VIDEO_size);
            w.PairRange(SPA_TYPE_Rectangle, (640, 480), (1, 1), (8192, 8192));
            w.Prop(SPA_FORMAT_VIDEO_framerate);
            w.PairRange(SPA_TYPE_Fraction, (30, 1), (0, 1), (360, 1));
            if (limitRate)
            {
                w.Prop(SPA_FORMAT_VIDEO_maxFramerate);
                w.PairRange(SPA_TYPE_Fraction, (30, 1), (1, 1), (30, 1));
            }
            w.EndObject(obj);
            return w.ToArray();
        }

        /// <summary>Asks for shared-memory buffers (no GPU buffers).</summary>
        internal static byte[] BuildBuffers()
        {
            var w = new PodWriter();
            int obj = w.BeginObject(SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers);
            w.Prop(SPA_PARAM_BUFFERS_dataType); w.Int((1 << SPA_DATA_MemPtr) | (1 << SPA_DATA_MemFd));
            w.EndObject(obj);
            return w.ToArray();
        }

        private static PipeWireStream From(IntPtr data) => (PipeWireStream)GCHandle.FromIntPtr(data).Target;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnStateChanged(IntPtr data, int old, int state, IntPtr error)
        {
            try
            {
                var s = From(data);
                if (state == PW_STREAM_STATE_ERROR)
                    s.ReportFailure("capture stream error: " + (GLib.Utf8(error) ?? "unknown"));
                else if (state == 0 /* unconnected */ && old != 0)
                    s.ReportFailure("the shared window stopped (closed, or sharing was ended)");
            }
            catch (Exception e) { Callbacks.Report(e); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnParamChanged(IntPtr data, uint id, IntPtr param)
        {
            try
            {
                if (id != SPA_PARAM_Format || param == IntPtr.Zero)
                    return;
                var s = From(data);
                if (!PodReader.ReadVideoFormat(param, out uint format, out s._width, out s._height))
                    return;
                s._rgbOrder = format == SPA_VIDEO_FORMAT_RGBx || format == SPA_VIDEO_FORMAT_RGBA;
                byte[] buffers = BuildBuffers();
                fixed (byte* b = buffers)
                {
                    IntPtr p = (IntPtr)b;
                    pw_stream_update_params(s._stream, &p, 1);
                }
            }
            catch (Exception e) { Callbacks.Report(e); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnProcess(IntPtr data)
        {
            try
            {
                var s = From(data);
                IntPtr pwBuffer = pw_stream_dequeue_buffer(s._stream);
                if (pwBuffer == IntPtr.Zero)
                    return;
                try
                {
                    byte* spaBuffer = *(byte**)pwBuffer;
                    if (spaBuffer == null || *(uint*)(spaBuffer + SpaBufferNDatas) == 0 || s._width <= 0)
                        return;
                    byte* spaData = *(byte**)(spaBuffer + SpaBufferDatas);
                    byte* pixels = *(byte**)(spaData + SpaDataData);
                    uint* chunk = *(uint**)(spaData + SpaDataChunk);
                    if (pixels == null || chunk == null || chunk[1] == 0)
                        return; // e.g. a cursor-only update
                    int stride = (int)chunk[2];
                    if (stride <= 0) stride = s._width * 4;
                    if ((long)stride * s._height > chunk[1] + (long)stride)
                        return; // short buffer
                    s.FrameReady?.Invoke(new Frame(pixels + chunk[0], s._width, s._height, stride, s._rgbOrder));
                }
                finally
                {
                    pw_stream_queue_buffer(s._stream, pwBuffer);
                }
            }
            catch (Exception e) { Callbacks.Report(e); }
        }

        private void ReportFailure(string message)
        {
            if (_failedReported) return;
            _failedReported = true;
            Failed?.Invoke(message);
        }

        public void Dispose()
        {
            if (_loop == IntPtr.Zero)
                return;
            pw_thread_loop_lock(_loop);
            if (_stream != IntPtr.Zero)
            {
                pw_stream_disconnect(_stream);
                pw_stream_destroy(_stream);
                _stream = IntPtr.Zero;
            }
            if (_core != IntPtr.Zero)
            {
                pw_core_disconnect(_core);
                _core = IntPtr.Zero;
            }
            pw_thread_loop_unlock(_loop);
            pw_thread_loop_stop(_loop);
            if (_context != IntPtr.Zero)
                pw_context_destroy(_context);
            pw_thread_loop_destroy(_loop);
            _loop = _context = IntPtr.Zero;
            NativeMemory.Free((void*)_events);
            NativeMemory.Free((void*)_hook);
            _events = _hook = IntPtr.Zero;
            if (_self.IsAllocated)
                _self.Free();
        }
    }
}
