using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace PISMO.Native
{
    /// <summary>
    /// Захват монитора через DXGI Desktop Duplication: кадры отдаёт сам
    /// видеодрайвер (GPU), без GDI BitBlt. Единственный по-настоящему быстрый
    /// путь к 60fps на любых разрешениях.
    ///
    /// Устройство D3D11 создаётся на адаптере, которому принадлежит нужный
    /// монитор (важно для мульти-GPU: ноутбуки, VR). Любая ошибка — исключение;
    /// вызывающий откатывается на GDI-путь.
    /// </summary>
    internal sealed class DxgiDuplicator : IDisposable
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>Постоянный буфер последнего кадра (BGRA, stride = Width*4).</summary>
        public IntPtr Buffer { get; private set; }
        public bool HasFrame { get; private set; }

        private IntPtr _device;      // ID3D11Device*
        private IntPtr _context;     // ID3D11DeviceContext*
        private IntPtr _dupl;        // IDXGIOutputDuplication*
        private IntPtr _staging;     // ID3D11Texture2D* (CPU-читаемая)

        public DxgiDuplicator(Rectangle monitorBounds)
        {
            IntPtr factory = IntPtr.Zero;
            int[] levels = { 0xb000 /*11_0*/, 0xa100 /*10_1*/, 0xa000 /*10_0*/, 0x9300 /*9_3*/ };
            var pLevels = Marshal.AllocHGlobal(levels.Length * 4);
            Marshal.Copy(levels, 0, pLevels, levels.Length);
            int lastHr = unchecked((int)0x887A0004);   // UNSUPPORTED по умолчанию
            try
            {
                Check(CreateDXGIFactory1(IID_IDXGIFactory1, out factory), "CreateDXGIFactory1");

                // Optimus/мульти-GPU: НЕСКОЛЬКО адаптеров могут дуплицировать один и
                // тот же монитор, но кадр отдаёт непустым ТОЛЬКО тот, что реально
                // рисует рабочий стол (Intel); NVIDIA-кандидат «успешно» даёт ЧЁРНОЕ.
                // Поэтому для каждого кандидата делаем ТЕСТ-захват кадра и выбираем
                // тот, чей кадр НЕ чёрный. Если непустого нет — берём первый рабочий.
                bool committedContent = false;   // выбран кандидат с непустым кадром
                bool committedWanted = false;    // текущий кандидат — «нужный» монитор
                for (uint a = 0; !committedContent; a++)
                {
                    if (VtblCall2(factory, 12, a, out IntPtr ad) != 0) break;   // EnumAdapters1
                    IntPtr dev = IntPtr.Zero, ctx = IntPtr.Zero;
                    try
                    {
                        int hr = D3D11CreateDevice(ad, 0, IntPtr.Zero, 0x20 /*BGRA_SUPPORT*/,
                                                   pLevels, (uint)levels.Length, 7, out dev, out _, out ctx);
                        if (hr != 0) { lastHr = hr; continue; }

                        for (uint o = 0; !committedContent; o++)
                        {
                            if (VtblCall2(ad, 7, o, out IntPtr op) != 0) break;  // EnumOutputs
                            try
                            {
                                var desc = new DXGI_OUTPUT_DESC();
                                if (GetOutputDesc(op, ref desc) != 0 || desc.AttachedToDesktop == 0) continue;
                                var r = Rectangle.FromLTRB(desc.Left, desc.Top, desc.Right, desc.Bottom);
                                bool wanted = r == monitorBounds;
                                IntPtr o1 = QueryInterface(op, IID_IDXGIOutput1);
                                try
                                {
                                    int dhr = VtblCall2Ptr(o1, 22, dev, out IntPtr dup);   // DuplicateOutput
                                    if (dhr != 0) { lastHr = dhr; continue; }

                                    // Тест-захват: этот адаптер отдаёт НЕпустой кадр нужного монитора?
                                    bool content = wanted && DuplYieldsContent(dev, ctx, dup);
                                    if (content)
                                    {
                                        // идеал: нужный монитор + живая картинка → фиксируем и выходим
                                        if (_dupl != IntPtr.Zero) Marshal.Release(_dupl);
                                        if (_device != IntPtr.Zero) Marshal.Release(_device);
                                        if (_context != IntPtr.Zero) Marshal.Release(_context);
                                        _dupl = dup; _device = dev; _context = ctx;
                                        Marshal.AddRef(dev); Marshal.AddRef(ctx);
                                        committedContent = true; committedWanted = true;
                                    }
                                    else if (_dupl == IntPtr.Zero || (wanted && !committedWanted))
                                    {
                                        // запасной кандидат (первый рабочий / первый «нужный»),
                                        // пока не нашли адаптер с непустым кадром
                                        if (_dupl != IntPtr.Zero) Marshal.Release(_dupl);
                                        if (_device != IntPtr.Zero) Marshal.Release(_device);
                                        if (_context != IntPtr.Zero) Marshal.Release(_context);
                                        _dupl = dup; _device = dev; _context = ctx;
                                        Marshal.AddRef(dev); Marshal.AddRef(ctx);
                                        committedWanted = wanted;
                                    }
                                    else Marshal.Release(dup);
                                }
                                finally { Marshal.Release(o1); }
                            }
                            finally { Marshal.Release(op); }
                        }
                    }
                    finally
                    {
                        if (dev != IntPtr.Zero) Marshal.Release(dev);
                        if (ctx != IntPtr.Zero) Marshal.Release(ctx);
                        Marshal.Release(ad);
                    }
                }
                if (_dupl == IntPtr.Zero) throw new COMException($"DuplicateOutput 0x{(uint)lastHr:X8}", lastHr);

                var dd = new DXGI_OUTDUPL_DESC();
                GetDuplDesc(_dupl, ref dd);
                Width = (int)dd.ModeWidth & ~1;
                Height = (int)dd.ModeHeight & ~1;
                if (Width <= 0 || Height <= 0) throw new InvalidOperationException("нулевой размер дубликации");

                // Staging-текстура для чтения кадра CPU.
                var td = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)dd.ModeWidth,
                    Height = (uint)dd.ModeHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = 87 /*DXGI_FORMAT_B8G8R8A8_UNORM*/,
                    SampleCount = 1,
                    SampleQuality = 0,
                    Usage = 3 /*D3D11_USAGE_STAGING*/,
                    BindFlags = 0,
                    CPUAccessFlags = 0x20000 /*D3D11_CPU_ACCESS_READ*/,
                    MiscFlags = 0
                };
                Check(CreateTexture2D(_device, ref td, out _staging), "CreateTexture2D");

                Buffer = Marshal.AllocHGlobal(Width * Height * 4);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(pLevels);
                if (factory != IntPtr.Zero) Marshal.Release(factory);
            }
        }

        // Тест-захват: пробует получить кадр с этого (dev/ctx/dupl) и проверяет, что
        // он НЕ полностью чёрный. Нужен для Optimus: NVIDIA-кандидат дуплицируется
        // «успешно», но отдаёт чёрное; Intel — реальную картинку. Оставляет
        // дупликацию в чистом состоянии (кадр освобождён) для дальнейшего захвата.
        private static bool DuplYieldsContent(IntPtr dev, IntPtr ctx, IntPtr dupl)
        {
            var dd = new DXGI_OUTDUPL_DESC();
            GetDuplDesc(dupl, ref dd);
            int w = (int)dd.ModeWidth, h = (int)dd.ModeHeight;
            if (w <= 0 || h <= 0) return false;

            var td = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = 87, SampleCount = 1, SampleQuality = 0, Usage = 3,
                BindFlags = 0, CPUAccessFlags = 0x20000, MiscFlags = 0
            };
            if (CreateTexture2D(dev, ref td, out IntPtr staging) != 0 || staging == IntPtr.Zero) return false;

            bool content = false;
            try
            {
                // Первый AcquireNextFrame обычно сразу отдаёт текущий рабочий стол;
                // до ~12 попыток по 50мс на случай статичного экрана.
                for (int attempt = 0; attempt < 12 && !content; attempt++)
                {
                    var fi = new DXGI_OUTDUPL_FRAME_INFO();
                    int hr = AcquireNextFrame(dupl, 50, ref fi, out IntPtr res);
                    if (hr != 0) continue;   // таймаут/ошибка — res невалиден
                    try
                    {
                        IntPtr tex = QueryInterface(res, IID_ID3D11Texture2D);
                        try
                        {
                            CopyResource(ctx, staging, tex);
                            var m = new D3D11_MAPPED_SUBRESOURCE();
                            if (MapResource(ctx, staging, 0, 1, 0, ref m) == 0)
                            {
                                try { content = MappedHasContent(m.pData, w, h, (int)m.RowPitch); }
                                finally { UnmapResource(ctx, staging, 0); }
                            }
                        }
                        finally { Marshal.Release(tex); }
                    }
                    catch { }
                    finally { ReleaseFrame(dupl); if (res != IntPtr.Zero) Marshal.Release(res); }
                }
            }
            catch { }
            finally { Marshal.Release(staging); }
            return content;
        }

        // Кадр не полностью чёрный: сэмплируем сетку ~64×64, «есть картинка» = хотя
        // бы ~1% точек не чёрные (одиночный яркий пиксель не считается).
        private static bool MappedHasContent(IntPtr data, int w, int h, int rowPitch)
        {
            if (data == IntPtr.Zero || rowPitch <= 0) return false;
            int total = 0, nonBlack = 0;
            int stepY = Math.Max(1, h / 64), stepX = Math.Max(1, w / 64);
            for (int y = 0; y < h; y += stepY)
                for (int x = 0; x < w; x += stepX)
                {
                    int off = y * rowPitch + x * 4;
                    total++;
                    if (Marshal.ReadByte(data, off) > 12 || Marshal.ReadByte(data, off + 1) > 12 || Marshal.ReadByte(data, off + 2) > 12)
                        nonBlack++;
                }
            return total > 0 && nonBlack * 100 >= total;
        }

        /// <summary>Пытается забрать новый кадр (timeoutMs). true — Buffer обновлён;
        /// false — кадр не менялся (Buffer хранит предыдущий). Исключение — дубликация
        /// потеряна (смена режима/выход из сессии): пересоздать или откатиться на GDI.</summary>
        public bool TryAcquireFrame(int timeoutMs)
        {
            var fi = new DXGI_OUTDUPL_FRAME_INFO();
            int hr = AcquireNextFrame(_dupl, (uint)timeoutMs, ref fi, out IntPtr resource);
            if (hr == unchecked((int)0x887A0027)) return false;   // DXGI_ERROR_WAIT_TIMEOUT
            if (hr != 0) throw new COMException($"AcquireNextFrame 0x{(uint)hr:X8}", hr);
            try
            {
                if (fi.LastPresentTime == 0 && !HasFrame && fi.AccumulatedFrames == 0)
                    return false;   // только курсор двигался, содержимое прежнее
                IntPtr tex = QueryInterface(resource, IID_ID3D11Texture2D);
                try
                {
                    CopyResource(_context, _staging, tex);
                    var mapped = new D3D11_MAPPED_SUBRESOURCE();
                    Check(MapResource(_context, _staging, 0, 1 /*D3D11_MAP_READ*/, 0, ref mapped), "Map");
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
                finally { Marshal.Release(tex); }
            }
            finally
            {
                ReleaseFrame(_dupl);
                if (resource != IntPtr.Zero) Marshal.Release(resource);
            }
        }

        public void Dispose()
        {
            if (_dupl != IntPtr.Zero) { try { Marshal.Release(_dupl); } catch { } _dupl = IntPtr.Zero; }
            if (_staging != IntPtr.Zero) { try { Marshal.Release(_staging); } catch { } _staging = IntPtr.Zero; }
            if (_context != IntPtr.Zero) { try { Marshal.Release(_context); } catch { } _context = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { try { Marshal.Release(_device); } catch { } _device = IntPtr.Zero; }
            if (Buffer != IntPtr.Zero) { try { Marshal.FreeHGlobal(Buffer); } catch { } Buffer = IntPtr.Zero; }
            HasFrame = false;
        }

        // ── Низкоуровневые COM-вызовы через vtable (без огромных ComImport-интерфейсов) ──
        private delegate int Fn2(IntPtr self, uint index, out IntPtr result);
        private delegate int Fn2Ptr(IntPtr self, IntPtr arg, out IntPtr result);
        private delegate int FnOutputDesc(IntPtr self, ref DXGI_OUTPUT_DESC desc);
        private delegate void FnDuplDesc(IntPtr self, ref DXGI_OUTDUPL_DESC desc);
        private delegate int FnCreateTex(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);
        private delegate void FnCopyRes(IntPtr self, IntPtr dst, IntPtr src);
        private delegate int FnMap(IntPtr self, IntPtr res, uint sub, uint mapType, uint flags, ref D3D11_MAPPED_SUBRESOURCE mapped);
        private delegate void FnUnmap(IntPtr self, IntPtr res, uint sub);
        private delegate int FnAcquire(IntPtr self, uint timeout, ref DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource);
        private delegate int FnRelease(IntPtr self);

        private static T GetFn<T>(IntPtr comObj, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(comObj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return (T)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        // IDXGIFactory1.EnumAdapters1 = slot 12; IDXGIAdapter.EnumOutputs = slot 7.
        private static int VtblCall2(IntPtr obj, int slot, uint index, out IntPtr result)
            => GetFn<Fn2>(obj, slot)(obj, index, out result);

        // IDXGIOutput1.DuplicateOutput = slot 22.
        private static int VtblCall2Ptr(IntPtr obj, int slot, IntPtr arg, out IntPtr result)
            => GetFn<Fn2Ptr>(obj, slot)(obj, arg, out result);

        // IDXGIOutput.GetDesc = slot 7.
        private static int GetOutputDesc(IntPtr output, ref DXGI_OUTPUT_DESC desc)
            => GetFn<FnOutputDesc>(output, 7)(output, ref desc);

        // IDXGIOutputDuplication.GetDesc = slot 7.
        private static void GetDuplDesc(IntPtr dupl, ref DXGI_OUTDUPL_DESC desc)
            => GetFn<FnDuplDesc>(dupl, 7)(dupl, ref desc);

        // ID3D11Device.CreateTexture2D = slot 5.
        private static int CreateTexture2D(IntPtr device, ref D3D11_TEXTURE2D_DESC desc, out IntPtr tex)
            => GetFn<FnCreateTex>(device, 5)(device, ref desc, IntPtr.Zero, out tex);

        // ID3D11DeviceContext: Map = 14, Unmap = 15, CopyResource = 47.
        private static int MapResource(IntPtr ctx, IntPtr res, uint sub, uint type, uint flags, ref D3D11_MAPPED_SUBRESOURCE m)
            => GetFn<FnMap>(ctx, 14)(ctx, res, sub, type, flags, ref m);
        private static void UnmapResource(IntPtr ctx, IntPtr res, uint sub)
            => GetFn<FnUnmap>(ctx, 15)(ctx, res, sub);
        private static void CopyResource(IntPtr ctx, IntPtr dst, IntPtr src)
            => GetFn<FnCopyRes>(ctx, 47)(ctx, dst, src);

        // IDXGIOutputDuplication: AcquireNextFrame = 8, ReleaseFrame = 14.
        private static int AcquireNextFrame(IntPtr dupl, uint timeout, ref DXGI_OUTDUPL_FRAME_INFO info, out IntPtr res)
            => GetFn<FnAcquire>(dupl, 8)(dupl, timeout, ref info, out res);
        private static void ReleaseFrame(IntPtr dupl) => GetFn<FnRelease>(dupl, 14)(dupl);

        private static IntPtr QueryInterface(IntPtr obj, Guid iid)
        {
            Marshal.QueryInterface(obj, ref iid, out IntPtr result);
            if (result == IntPtr.Zero) throw new InvalidCastException("QueryInterface " + iid);
            return result;
        }

        private static void Check(int hr, string what = "call")
        {
            if (hr != 0) throw new COMException($"{what} 0x{(uint)hr:X8}", hr);
        }

        // ── P/Invoke / структуры ──────────────────────────────────────────
        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
            uint flags, IntPtr featureLevels, uint numLevels, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr context);

        [DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr memcpy(IntPtr dst, IntPtr src, UIntPtr count);

        private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public int Left, Top, Right, Bottom;   // DesktopCoordinates (RECT)
            public int AttachedToDesktop;          // BOOL
            public int Rotation;
            public IntPtr Monitor;                 // HMONITOR
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_DESC
        {
            public uint ModeWidth, ModeHeight, RefreshRateNum, RefreshRateDen;
            public uint Format;
            public uint ScanlineOrdering;
            public uint Scaling;
            public int Rotation;
            public int DesktopImageInSystemMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced;
            public int ProtectedContentMaskedOut;
            public POINTER_POSITION PointerPosition;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_POSITION
        {
            public int X, Y;
            public int Visible;
        }

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
}
