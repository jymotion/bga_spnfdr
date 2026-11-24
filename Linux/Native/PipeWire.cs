using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BgaSpnfdr.Native
{
    /// <summary>libpipewire-0.3 functions, constants and struct offsets the
    /// app uses (checked against the PipeWire 1.0 headers; these ABIs are
    /// stable across 0.3.x/1.x).</summary>
    internal static unsafe partial class PipeWire
    {
        private const string Lib = "libpipewire-0.3.so.0";

        [LibraryImport(Lib)] public static partial void pw_init(IntPtr argc, IntPtr argv);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr pw_thread_loop_new(string name, IntPtr props);
        [LibraryImport(Lib)] public static partial IntPtr pw_thread_loop_get_loop(IntPtr loop);
        [LibraryImport(Lib)] public static partial int pw_thread_loop_start(IntPtr loop);
        [LibraryImport(Lib)] public static partial void pw_thread_loop_stop(IntPtr loop);
        [LibraryImport(Lib)] public static partial void pw_thread_loop_lock(IntPtr loop);
        [LibraryImport(Lib)] public static partial void pw_thread_loop_unlock(IntPtr loop);
        [LibraryImport(Lib)] public static partial void pw_thread_loop_destroy(IntPtr loop);
        [LibraryImport(Lib)] public static partial IntPtr pw_context_new(IntPtr loop, IntPtr props, nuint userDataSize);
        [LibraryImport(Lib)] public static partial void pw_context_destroy(IntPtr context);
        [LibraryImport(Lib)] public static partial IntPtr pw_context_connect(IntPtr context, IntPtr props, nuint userDataSize);
        [LibraryImport(Lib)] public static partial IntPtr pw_context_connect_fd(IntPtr context, int fd, IntPtr props, nuint userDataSize);
        [LibraryImport(Lib)] public static partial int pw_core_disconnect(IntPtr core);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr pw_properties_new_string(string args);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr pw_stream_new(IntPtr core, string name, IntPtr props);
        [LibraryImport(Lib)] public static partial void pw_stream_add_listener(IntPtr stream, IntPtr hook, IntPtr events, IntPtr data);
        [LibraryImport(Lib)] public static partial int pw_stream_connect(IntPtr stream, int direction, uint targetId,
            int flags, IntPtr* parameters, uint nParams);
        [LibraryImport(Lib)] public static partial int pw_stream_update_params(IntPtr stream, IntPtr* parameters, uint nParams);
        [LibraryImport(Lib)] public static partial int pw_stream_disconnect(IntPtr stream);
        [LibraryImport(Lib)] public static partial void pw_stream_destroy(IntPtr stream);
        [LibraryImport(Lib)] public static partial IntPtr pw_stream_dequeue_buffer(IntPtr stream);
        [LibraryImport(Lib)] public static partial int pw_stream_queue_buffer(IntPtr stream, IntPtr buffer);

        public const int PW_DIRECTION_INPUT = 0;
        public const int PW_STREAM_FLAG_AUTOCONNECT = 1, PW_STREAM_FLAG_MAP_BUFFERS = 4;
        public const int PW_STREAM_STATE_ERROR = -1, PW_STREAM_STATE_STREAMING = 3;
        public const uint PW_ID_ANY = 0xffffffff;

        // struct pw_stream_events (version 0): uint32 version, then pointers
        public const int EventsSize = 96;
        public const int EvStateChanged = 16, EvParamChanged = 40, EvProcess = 64;
        public const int HookSize = 48; // struct spa_hook

        // struct pw_buffer { struct spa_buffer *buffer; ... }
        // struct spa_buffer { u32 n_metas; u32 n_datas; spa_meta *metas; spa_data *datas; }
        public const int SpaBufferNDatas = 4, SpaBufferDatas = 16;
        // struct spa_data { u32 type; u32 flags; i64 fd; u32 mapoffset; u32 maxsize; void *data; spa_chunk *chunk; }
        public const int SpaDataData = 24, SpaDataChunk = 32;
        // struct spa_chunk { u32 offset; u32 size; i32 stride; i32 flags; }

        // SPA ids
        public const uint SPA_PARAM_EnumFormat = 3, SPA_PARAM_Format = 4, SPA_PARAM_Buffers = 5;
        public const uint SPA_TYPE_Id = 3, SPA_TYPE_Int = 4, SPA_TYPE_Rectangle = 10, SPA_TYPE_Fraction = 11,
                          SPA_TYPE_Object = 15, SPA_TYPE_Choice = 19;
        public const uint SPA_TYPE_OBJECT_Format = 0x40003, SPA_TYPE_OBJECT_ParamBuffers = 0x40004;
        public const uint SPA_CHOICE_Range = 1, SPA_CHOICE_Enum = 3;
        public const uint SPA_FORMAT_mediaType = 1, SPA_FORMAT_mediaSubtype = 2;
        public const uint SPA_FORMAT_VIDEO_format = 0x20001, SPA_FORMAT_VIDEO_size = 0x20003,
                          SPA_FORMAT_VIDEO_framerate = 0x20004, SPA_FORMAT_VIDEO_maxFramerate = 0x20005;
        public const uint SPA_MEDIA_TYPE_video = 2, SPA_MEDIA_SUBTYPE_raw = 1;
        public const uint SPA_VIDEO_FORMAT_RGBx = 7, SPA_VIDEO_FORMAT_BGRx = 8,
                          SPA_VIDEO_FORMAT_RGBA = 11, SPA_VIDEO_FORMAT_BGRA = 12;
        public const uint SPA_PARAM_BUFFERS_dataType = 6;
        public const int SPA_DATA_MemPtr = 1, SPA_DATA_MemFd = 2;
    }

    /// <summary>
    /// Writes SPA "POD" values, PipeWire's binary parameter format (normally
    /// built with C macros, which can't be called from C#). Every value is a
    /// (size, type) header plus a body padded to 8 bytes. Output was checked
    /// byte for byte against spa_pod_builder.
    /// </summary>
    internal sealed class PodWriter
    {
        private readonly List<byte> _b = new List<byte>();

        private void U32(uint v)
        {
            _b.Add((byte)v); _b.Add((byte)(v >> 8)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 24));
        }

        private void Pad()
        {
            while (_b.Count % 8 != 0) _b.Add(0);
        }

        private int BeginPod(uint type)
        {
            int at = _b.Count;
            U32(0); // size, patched in End
            U32(type);
            return at;
        }

        private void End(int at)
        {
            uint size = (uint)(_b.Count - at - 8);
            _b[at] = (byte)size; _b[at + 1] = (byte)(size >> 8); _b[at + 2] = (byte)(size >> 16); _b[at + 3] = (byte)(size >> 24);
            Pad();
        }

        public int BeginObject(uint objectType, uint id)
        {
            int at = BeginPod(PipeWire.SPA_TYPE_Object);
            U32(objectType);
            U32(id);
            return at;
        }

        public void EndObject(int at) => End(at);

        public void Prop(uint key) { U32(key); U32(0); }

        public void Id(uint v) { U32(4); U32(PipeWire.SPA_TYPE_Id); U32(v); Pad(); }

        public void Int(int v) { U32(4); U32(PipeWire.SPA_TYPE_Int); U32((uint)v); Pad(); }

        /// <summary>Choice of ids: the first value is the preferred one.</summary>
        public void IdEnum(params uint[] values)
        {
            int at = BeginPod(PipeWire.SPA_TYPE_Choice);
            U32(PipeWire.SPA_CHOICE_Enum); U32(0);
            U32(4); U32(PipeWire.SPA_TYPE_Id);
            foreach (uint v in values) U32(v);
            End(at);
        }

        /// <summary>Range choice of 2-int values (Rectangle or Fraction):
        /// default, min, max.</summary>
        public void PairRange(uint type, (uint, uint) def, (uint, uint) min, (uint, uint) max)
        {
            int at = BeginPod(PipeWire.SPA_TYPE_Choice);
            U32(PipeWire.SPA_CHOICE_Range); U32(0);
            U32(8); U32(type);
            foreach (var (a, b) in new[] { def, min, max }) { U32(a); U32(b); }
            End(at);
        }

        public byte[] ToArray() => _b.ToArray();
    }

    /// <summary>Reads the few values the app needs from a negotiated video
    /// format POD (an object of key/value properties).</summary>
    internal static unsafe class PodReader
    {
        public static bool ReadVideoFormat(IntPtr pod, out uint format, out int width, out int height)
        {
            format = 0; width = height = 0;
            byte* p = (byte*)pod;
            uint size = *(uint*)p, type = *(uint*)(p + 4);
            if (type != PipeWire.SPA_TYPE_Object || size < 8)
                return false;
            byte* end = p + 8 + size;
            byte* q = p + 16; // skip header + object type + id
            while (q + 16 <= end)
            {
                uint key = *(uint*)q;
                uint vsize = *(uint*)(q + 8), vtype = *(uint*)(q + 12);
                byte* body = q + 16;
                // a fixed value may still arrive wrapped in a Choice: take its first (default) value
                if (vtype == PipeWire.SPA_TYPE_Choice && vsize >= 16)
                {
                    vtype = *(uint*)(body + 12);
                    body += 16;
                }
                if (key == PipeWire.SPA_FORMAT_VIDEO_format && vtype == PipeWire.SPA_TYPE_Id)
                    format = *(uint*)body;
                else if (key == PipeWire.SPA_FORMAT_VIDEO_size && vtype == PipeWire.SPA_TYPE_Rectangle)
                {
                    width = *(int*)body;
                    height = *(int*)(body + 4);
                }
                q += 16 + ((vsize + 7) & ~7u);
            }
            return format != 0 && width > 0 && height > 0;
        }
    }
}
