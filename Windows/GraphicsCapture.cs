using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;
using Size = System.Drawing.Size;

namespace BgaSpnfdr
{
    /// <summary>
    /// Captures one window with Windows.Graphics.Capture (Windows 10 version
    /// 1903 and later). Windows itself keeps the window's newest picture in a
    /// texture on the graphics card; Copy brings back only the part asked for
    /// (the score strip on every read, the whole frame only for the preview
    /// and auto-crop), so the cost of a read doesn't grow with the window's
    /// size. Frame coordinates start at the
    /// window's visible frame (DWMWA_EXTENDED_FRAME_BOUNDS); FrameSource maps
    /// them to PrintWindow's.
    ///
    /// Direct3D 11 is called through its function tables (no wrapper
    /// library, so the exe stays a single file); WinRT through .NET
    /// Framework's built-in support. Only used once FrameSource has checked
    /// the Windows version: older Windows lacks the WinRT types. For the same
    /// reason no field has a WinRT value type (loading the class would need it).
    /// </summary>
    internal sealed unsafe class GraphicsCapture : IDisposable
    {
        private const int Buffers = 2;            // one frame held for reading, one for Windows to fill
        private const int FirstFramePolls = 30;   // give up if no frame comes in this many polls
        private const uint Bgra = 87;             // DXGI_FORMAT_B8G8R8A8_UNORM

        private static readonly Guid IidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid IidTexture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly Guid IidCaptureItem = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");

        // turning the yellow capture border off (Windows 11): asked once per run
        private static IAsyncOperation<AppCapabilityAccessStatus> _borderRequest;
        private static bool _borderUnavailable;
        private bool _borderDone;

        private IntPtr _device, _context;     // ID3D11Device and its immediate context
        private IDirect3DDevice _winrtDevice; // the same device, for WinRT
        private Direct3D11CaptureFramePool _pool;
        private GraphicsCaptureSession _session;
        private int _poolWidth, _poolHeight;
        private Direct3D11CaptureFrame _frame; // newest frame, held until a newer one arrives
        private IntPtr _texture;               // its ID3D11Texture2D
        private readonly IntPtr[] _staging = new IntPtr[2]; // CPU-readable copies: [0] score strip, [1] full frame
        private readonly Size[] _stagingSize = new Size[2];
        private int _polls;

        /// <summary>The newest frame's size.</summary>
        public Size Size;

        /// <summary>Counts the frames received (changes when a new one arrives).</summary>
        public int FrameNumber;

        public bool HasFrame => _texture != IntPtr.Zero;

        /// <summary>Starts capturing hWnd. Throws if Windows can't.</summary>
        public GraphicsCapture(IntPtr hWnd)
        {
            try
            {
                Start(hWnd);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Start(IntPtr hWnd)
        {
            if (!GraphicsCaptureSession.IsSupported())
                throw new NotSupportedException("Windows.Graphics.Capture isn't supported on this PC");

            const int hardware = 1, bgraSupport = 0x20, sdkVersion = 7;
            Check(D3D11CreateDevice(IntPtr.Zero, hardware, IntPtr.Zero, bgraSupport, IntPtr.Zero, 0,
                sdkVersion, out _device, out _, out _context));
            Guid iid = IidDxgiDevice;
            Check(Marshal.QueryInterface(_device, ref iid, out IntPtr dxgi));
            try
            {
                Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out IntPtr inspectable));
                try { _winrtDevice = (IDirect3DDevice)Marshal.GetObjectForIUnknown(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally
            {
                Marshal.Release(dxgi);
            }

            var interop = (IGraphicsCaptureItemInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(GraphicsCaptureItem));
            iid = IidCaptureItem;
            IntPtr itemPtr = interop.CreateForWindow(hWnd, ref iid);
            GraphicsCaptureItem item;
            try { item = (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(itemPtr); }
            finally { Marshal.Release(itemPtr); }

            var size = item.Size;
            _poolWidth = size.Width;
            _poolHeight = size.Height;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, Buffers, size);
            _session = _pool.CreateCaptureSession(item);
            // PrintWindow never shows the mouse pointer; keep it off the score
            try { HideCursor(_session); }
            catch (Exception) { } // before Windows 10 version 2004: no setting
            UpdateBorder();
            _session.StartCapture();
        }

        /// <summary>Takes the newest frame Windows has captured, if one came
        /// since the last call (none comes while the window is unchanged).
        /// Throws if capturing has stopped working.</summary>
        public void Poll()
        {
            UpdateBorder();
            for (int i = 0; i < 4; i++) // anything older than the newest is skipped
            {
                var frame = _pool.TryGetNextFrame();
                if (frame == null)
                    break;
                Take(frame);
            }
            if (!HasFrame && ++_polls > FirstFramePolls)
                throw new TimeoutException("Windows.Graphics.Capture sent no picture");
        }

        private void Take(Direct3D11CaptureFrame frame)
        {
            IntPtr texture;
            Desc desc;
            try
            {
                var surface = frame.Surface;
                try
                {
                    Guid iid = IidTexture2D;
                    texture = ((IDirect3DDxgiInterfaceAccess)surface).GetInterface(ref iid);
                }
                finally
                {
                    Marshal.ReleaseComObject(surface);
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, Desc*, void>)Vtbl(texture)[10])(texture, &desc); // ID3D11Texture2D::GetDesc
            }
            catch
            {
                CloseFrame(frame);
                throw;
            }

            // the frame is held until a newer one replaces it, so its texture
            // can be read at any time, even if the window doesn't change
            Release(ref _texture);
            CloseFrame(_frame);
            _frame = frame;
            _texture = texture;
            FrameNumber++;
            if (desc.Format != Bgra)
                throw new NotSupportedException("unexpected pixel format " + desc.Format);

            // ContentSize is the window's size now; the texture is the size
            // the pool was made for, until it's remade for the new size
            var content = frame.ContentSize;
            Size = new Size(Math.Min(content.Width, (int)desc.Width), Math.Min(content.Height, (int)desc.Height));
            if ((content.Width != _poolWidth || content.Height != _poolHeight)
                && content.Width > 0 && content.Height > 0)
            {
                _poolWidth = content.Width;
                _poolHeight = content.Height;
                _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, Buffers, content);
            }
        }

        /// <summary>
        /// Copies area (frame coordinates, inside Size) of the newest frame to
        /// dst, dstStride bytes per row, 4 bytes per pixel (B,G,R,A). Only that
        /// area comes back from the graphics card. slot 0 is for the score
        /// strip, 1 for full frames: each keeps its own CPU-readable texture,
        /// so neither is remade on every call.
        /// </summary>
        public void Copy(Rectangle area, byte* dst, int dstStride, int slot)
        {
            if (_stagingSize[slot] != area.Size)
            {
                Release(ref _staging[slot]);
                _stagingSize[slot] = Size.Empty;
                var desc = new Desc
                {
                    Width = (uint)area.Width,
                    Height = (uint)area.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Bgra,
                    SampleCount = 1,
                    Usage = 3,                // D3D11_USAGE_STAGING
                    CpuAccessFlags = 0x20000, // D3D11_CPU_ACCESS_READ
                };
                IntPtr staging;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, Desc*, IntPtr, IntPtr*, int>)Vtbl(_device)[5])(
                    _device, &desc, IntPtr.Zero, &staging)); // ID3D11Device::CreateTexture2D
                _staging[slot] = staging;
                _stagingSize[slot] = area.Size;
            }

            IntPtr target = _staging[slot];
            var box = new Box
            {
                Left = (uint)area.Left, Top = (uint)area.Top, Front = 0,
                Right = (uint)area.Right, Bottom = (uint)area.Bottom, Back = 1,
            };
            void** context = Vtbl(_context);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)context[46])(
                _context, target, 0, 0, 0, 0, _texture, 0, &box); // ID3D11DeviceContext::CopySubresourceRegion
            Mapped mapped;
            const int read = 1; // D3D11_MAP_READ: waits for the copy to finish
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, Mapped*, int>)context[14])(
                _context, target, 0, read, 0, &mapped)); // ID3D11DeviceContext::Map
            try
            {
                long rowBytes = area.Width * 4L;
                for (int y = 0; y < area.Height; y++)
                    Buffer.MemoryCopy(mapped.Data + (long)y * mapped.RowPitch, dst + (long)y * dstStride, rowBytes, rowBytes);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)context[15])(_context, target, 0); // ::Unmap
            }
        }

        // ----- the yellow border around a captured window -----

        /// <summary>Windows 11 lets apps turn the border off once they've
        /// asked (desktop apps are allowed without a prompt). Asked once per
        /// run without waiting for the answer; applied when it arrives.</summary>
        private void UpdateBorder()
        {
            if (_borderDone || _borderUnavailable)
                return;
            try
            {
                if (_borderRequest == null)
                    _borderRequest = RequestBorderless();
                var status = _borderRequest.Status;
                if (status == AsyncStatus.Started)
                    return;
                if (status == AsyncStatus.Completed && _borderRequest.GetResults() == AppCapabilityAccessStatus.Allowed)
                    HideBorder(_session);
                else
                    _borderUnavailable = true;
            }
            catch (Exception)
            {
                _borderUnavailable = true; // Windows 10: no setting
            }
            _borderDone = true;
        }

        // The settings below were added after Windows 10 version 1903. Each
        // is its own method so that on Windows without it, only the call to
        // it fails (caught), not the method that calls it.

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IAsyncOperation<AppCapabilityAccessStatus> RequestBorderless() =>
            GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HideBorder(GraphicsCaptureSession session) => session.IsBorderRequired = false;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void HideCursor(GraphicsCaptureSession session) => session.IsCursorCaptureEnabled = false;

        // ----- cleanup -----

        public void Dispose()
        {
            CloseFrame(_frame);
            _frame = null;
            Release(ref _texture);
            Close(_session);
            _session = null;
            Close(_pool);
            _pool = null;
            Release(ref _staging[0]);
            Release(ref _staging[1]);
            Close(_winrtDevice);
            _winrtDevice = null;
            Release(ref _context);
            Release(ref _device);
        }

        private static void Close(IDisposable winrtObject)
        {
            try { winrtObject?.Dispose(); }
            catch (Exception) { } // already broken: nothing left to free
        }

        /// <summary>Hands the frame's buffer back to Windows and drops our
        /// wrapper at once (one comes per frame, up to 30 a second).</summary>
        private static void CloseFrame(Direct3D11CaptureFrame frame)
        {
            if (frame == null)
                return;
            Close(frame);
            Marshal.ReleaseComObject(frame);
        }

        private static void Release(ref IntPtr com)
        {
            if (com != IntPtr.Zero)
                Marshal.Release(com);
            com = IntPtr.Zero;
        }

        private static void Check(int hr)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }

        private static void** Vtbl(IntPtr com) => *(void***)com;

        // ----- interop declarations -----

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, int flags,
            IntPtr featureLevels, int featureLevelCount, int sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr context);

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        }

        [ComImport, Guid("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface(ref Guid iid);
        }

        private struct Desc // D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality,
                Usage, BindFlags, CpuAccessFlags, MiscFlags;
        }

        private struct Box // D3D11_BOX
        {
            public uint Left, Top, Front, Right, Bottom, Back;
        }

        private struct Mapped // D3D11_MAPPED_SUBRESOURCE
        {
            public byte* Data;
            public uint RowPitch, DepthPitch;
        }
    }
}
