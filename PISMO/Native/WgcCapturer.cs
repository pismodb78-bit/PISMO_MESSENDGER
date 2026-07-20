using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PISMO.Native
{
    /// <summary>
    /// Живой захват КОНКРЕТНОГО окна через Windows.Graphics.Capture (WGC) — тот
    /// же путь, что у Discord/OBS. В отличие от PrintWindow (замороженный кадр для
    /// GPU-приложений) и захвата экранной области (показывает то, что физически
    /// поверх), WGC отдаёт живую картинку ИМЕННО этого окна, даже когда оно
    /// перекрыто. Требует Windows 10 1809+ (CreateFreeThreaded — опрос из любого
    /// потока). Кадр кладётся в Buffer (BGRA, stride = Width*4) — интерфейс тот же,
    /// что у DxgiDuplicator, чтобы петля захвата обращалась одинаково.
    /// </summary>
    internal sealed class WgcCapturer : IDisposable
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public IntPtr Buffer { get; private set; }
        public bool HasFrame { get; private set; }

        private IntPtr _device;      // ID3D11Device*
        private IntPtr _context;     // ID3D11DeviceContext*
        private IntPtr _staging;     // ID3D11Texture2D* (CPU-читаемая)
        private IDirect3DDevice _rtDevice;
        private GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool _pool;
        private GraphicsCaptureSession _session;
        private int _poolW, _poolH;
        private bool _disposed;

        public static bool IsSupported()
        {
            try { return GraphicsCaptureSession.IsSupported(); } catch { return false; }
        }

        public WgcCapturer(IntPtr hwnd) : this(hwnd, false) { }

        /// <summary>isMonitor=false → захват окна (HWND); true → захват монитора
        /// (HMONITOR). Для монитора WGC берёт композицию DWM — на Optimus картинка
        /// НЕ чёрная (в отличие от сырой DXGI-дупликации).</summary>
        public WgcCapturer(IntPtr handle, bool isMonitor)
        {
            try
            {
                if (!IsSupported()) throw new NotSupportedException("WGC не поддерживается");

                // 1) D3D11-устройство (BGRA_SUPPORT) + WinRT-обёртка IDirect3DDevice.
                int[] levels = { 0xb000, 0xa100, 0xa000 };
                var pLevels = Marshal.AllocHGlobal(levels.Length * 4);
                Marshal.Copy(levels, 0, pLevels, levels.Length);
                try
                {
                    Check(D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0x20 /*BGRA_SUPPORT*/,
                        pLevels, (uint)levels.Length, 7, out _device, out _, out _context), "D3D11CreateDevice");
                }
                finally { Marshal.FreeHGlobal(pLevels); }

                IntPtr dxgiDev = QueryInterface(_device, IID_IDXGIDevice);
                try
                {
                    Check(CreateDirect3D11DeviceFromDXGIDevice(dxgiDev, out IntPtr inspectable), "CreateD3DDevice");
                    try { _rtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                    finally { if (inspectable != IntPtr.Zero) Marshal.Release(inspectable); }
                }
                finally { Marshal.Release(dxgiDev); }

                // 2) Элемент захвата (окно/монитор) через interop-фабрику активации.
                IntPtr factoryPtr = IntPtr.Zero, hstr = IntPtr.Zero;
                object factoryObj;
                try
                {
                    string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
                    Check(WindowsCreateString(cls, cls.Length, out hstr), "WindowsCreateString");
                    var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
                    Check(RoGetActivationFactory(hstr, ref interopIid, out factoryPtr), "RoGetActivationFactory");
                    factoryObj = Marshal.GetObjectForIUnknown(factoryPtr);
                }
                finally
                {
                    if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
                    if (hstr != IntPtr.Zero) WindowsDeleteString(hstr);
                }
                var factory = (IGraphicsCaptureItemInterop)factoryObj;
                var itemIid = GuidOf_IGraphicsCaptureItem;
                IntPtr itemPtr = isMonitor
                    ? factory.CreateForMonitor(handle, ref itemIid)
                    : factory.CreateForWindow(handle, ref itemIid);
                try { _item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr); }
                finally { if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr); }
                if (_item == null) throw new InvalidOperationException("CreateFor* вернул null");

                var size = _item.Size;
                CreatePoolAndStaging(size);

                _session = _pool.CreateCaptureSession(_item);
                TryDisableBorder(_session);
                _session.StartCapture();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void CreatePoolAndStaging(SizeInt32 size)
        {
            int w = Math.Max(2, size.Width) & ~1;
            int h = Math.Max(2, size.Height) & ~1;

            if (_pool == null)
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32 { Width = w, Height = h });
            else
                _pool.Recreate(_rtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32 { Width = w, Height = h });
            _poolW = w; _poolH = h;

            if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
            var td = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = 87 /*B8G8R8A8_UNORM*/,
                SampleCount = 1,
                SampleQuality = 0,
                Usage = 3 /*STAGING*/,
                BindFlags = 0,
                CPUAccessFlags = 0x20000 /*READ*/,
                MiscFlags = 0
            };
            Check(CreateTexture2D(_device, ref td, out _staging), "CreateTexture2D");

            if (Buffer != IntPtr.Zero) Marshal.FreeHGlobal(Buffer);
            Buffer = Marshal.AllocHGlobal(w * h * 4);
            Width = w; Height = h;
        }

        /// <summary>Забрать свежий кадр окна. true — Buffer обновлён; false — нового нет.</summary>
        public bool TryAcquireFrame(int timeoutMs)
        {
            if (_disposed || _pool == null) return false;
            Direct3D11CaptureFrame frame = null;
            try { frame = _pool.TryGetNextFrame(); }
            catch { return false; }
            if (frame == null) return false;
            try
            {
                var cs = frame.ContentSize;
                int w = Math.Max(2, cs.Width) & ~1, h = Math.Max(2, cs.Height) & ~1;
                if (w != _poolW || h != _poolH)
                {
                    // Окно изменило размер — пересоздаём пул/стейджинг и ждём след. кадр.
                    CreatePoolAndStaging(new SizeInt32 { Width = w, Height = h });
                    return false;
                }

                IntPtr texPtr = IntPtr.Zero;
                var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                var texIid = IID_ID3D11Texture2D;
                texPtr = access.GetInterface(ref texIid);
                try
                {
                    CopyResource(_context, _staging, texPtr);
                    var mapped = new D3D11_MAPPED_SUBRESOURCE();
                    Check(MapResource(_context, _staging, 0, 1 /*READ*/, 0, ref mapped), "Map");
                    try
                    {
                        int rowBytes = Width * 4;
                        for (int y = 0; y < Height; y++)
                            memcpy(Buffer + y * rowBytes, mapped.pData + y * (int)mapped.RowPitch, (UIntPtr)rowBytes);
                    }
                    finally { UnmapResource(_context, _staging, 0); }
                    HasFrame = true;
                    return true;
                }
                finally { if (texPtr != IntPtr.Zero) Marshal.Release(texPtr); }
            }
            catch { return false; }
            finally { frame.Dispose(); }
        }

        private static void TryDisableBorder(GraphicsCaptureSession session)
        {
            // На Windows 11 у захвата рамка-подсветка — уберём, если API есть.
            try
            {
                var p = session.GetType().GetProperty("IsBorderRequired");
                if (p != null && p.CanWrite) p.SetValue(session, false);
            }
            catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            _session = null; _pool = null; _item = null; _rtDevice = null;
            if (_staging != IntPtr.Zero) { try { Marshal.Release(_staging); } catch { } _staging = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { try { Marshal.Release(_context); } catch { } _context = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { try { Marshal.Release(_device); } catch { } _device = IntPtr.Zero; }
            if (Buffer != IntPtr.Zero) { try { Marshal.FreeHGlobal(Buffer); } catch { } Buffer = IntPtr.Zero; }
            HasFrame = false;
        }

        // ── D3D11 vtable-вызовы (как в DxgiDuplicator) ──
        private delegate int FnCreateTex(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);
        private delegate void FnCopyRes(IntPtr self, IntPtr dst, IntPtr src);
        private delegate int FnMap(IntPtr self, IntPtr res, uint sub, uint mapType, uint flags, ref D3D11_MAPPED_SUBRESOURCE mapped);
        private delegate void FnUnmap(IntPtr self, IntPtr res, uint sub);

        private static T GetFn<T>(IntPtr comObj, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(comObj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return (T)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        private static int CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc, out IntPtr tex)
            => GetFn<FnCreateTex>(device, 5)(device, ref desc, IntPtr.Zero, out tex);
        private static int MapResource(IntPtr ctx, IntPtr res, uint sub, uint type, uint flags, ref D3D11_MAPPED_SUBRESOURCE m)
            => GetFn<FnMap>(ctx, 14)(ctx, res, sub, type, flags, ref m);
        private static void UnmapResource(IntPtr ctx, IntPtr res, uint sub)
            => GetFn<FnUnmap>(ctx, 15)(ctx, res, sub);
        private static void CopyResource(IntPtr ctx, IntPtr dst, IntPtr src)
            => GetFn<FnCopyRes>(ctx, 47)(ctx, dst, src);

        private static IntPtr QueryInterface(IntPtr obj, Guid iid)
        {
            Marshal.QueryInterface(obj, ref iid, out IntPtr result);
            if (result == IntPtr.Zero) throw new InvalidCastException("QueryInterface " + iid);
            return result;
        }

        private static void Check(int hr, string what)
        {
            if (hr != 0) throw new COMException($"{what} 0x{(uint)hr:X8}", hr);
        }

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
            uint flags, IntPtr featureLevels, uint numLevels, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr context);

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);
        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr memcpy(IntPtr dst, IntPtr src, UIntPtr count);

        private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        private static readonly Guid GuidOf_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public uint Format;
            public uint SampleCount, SampleQuality;
            public uint Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch;
            public uint DepthPitch;
        }
    }

    // Interop-фабрика WinRT: создаёт GraphicsCaptureItem из HWND/HMONITOR.
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    // Доступ к нативному DXGI-интерфейсу из IDirect3DSurface (текстура кадра).
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
