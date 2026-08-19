using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace PISMO.Native
{
    /// <summary>
    /// Захват системного звука в обход СВОЕГО процесса — WASAPI process loopback
    /// (ActivateAudioInterfaceAsync). Режим «исключить дерево процесса PISMO»:
    /// в звук демонстрации попадёт всё, что играет система (игра/музыка/браузер),
    /// КРОМЕ того, что выводит сам PISMO (голоса собеседников). Поэтому у друзей
    /// нет эха собственного голоса, а микрофон в демку и так не попадает.
    ///
    /// ВАЖНО (.NET 8 + CsWinRT): проект использует WinRT-проекции (WGC), а CsWinRT
    /// регистрирует ГЛОБАЛЬНЫЙ ComWrappers. После этого весь встроенный COM-маршалинг
    /// (P/Invoke интерфейсов, RCW, приведения [ComImport]) идёт через него, и
    /// классические [ComImport]-интерфейсы падают «Specified cast is not valid».
    /// Поэтому здесь COM делается ВРУЧНУЮ: вызовы методов — по vtable через указатели
    /// функций, а колбэк завершения — самодельный CCW (своя vtable), без RCW/ComImport.
    ///
    /// Отдаёт кадры 48 кГц / 16 бит / стерео. Если активация не поддержана —
    /// Start() кидает исключение, и вызывающий откатывается на обычный
    /// WasapiLoopbackCapture (со звуком, но с возможным эхом).
    /// </summary>
    internal sealed class ProcessLoopbackCapture : IDisposable
    {
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public WaveFormat WaveFormat { get; } = new WaveFormat(48000, 16, 2);

        private readonly int _targetPid;
        private readonly bool _excludeTree;
        private IntPtr _audioClient;    // IAudioClient*
        private IntPtr _capture;        // IAudioCaptureClient*
        private EventWaitHandle _bufferReady;
        private Thread _thread;
        private volatile bool _run;
        private int _blockAlign;
        private ManualCompletionHandler _handler;   // живёт до Dispose (native может дёрнуть Release позже)

        public ProcessLoopbackCapture(int targetPid, bool excludeTargetTree)
        {
            _targetPid = targetPid;
            _excludeTree = excludeTargetTree;
        }

        private readonly ManualResetEvent _setupDone = new(false);
        private Exception _setupError;

        public void Start()
        {
            // ВСЁ (активация + захват) делаем на ОДНОМ выделенном MTA-потоке: так
            // апартамент гарантирован и объекты IAudioClient используются там же,
            // где созданы. Ждём результат настройки и пробрасываем ошибку наверх.
            _thread = new Thread(SetupAndRun) { IsBackground = true, Name = "pismo-proc-loopback" };
            try { _thread.SetApartmentState(ApartmentState.MTA); } catch { }
            _thread.Start();
            if (!_setupDone.WaitOne(6000))
            {
                _run = false;
                throw new TimeoutException("process loopback: настройка не завершилась");
            }
            if (_setupError != null) throw _setupError;
        }

        private void SetupAndRun()
        {
            try { Setup(); }
            catch (Exception e) { _setupError = e; _setupDone.Set(); return; }
            _setupDone.Set();
            CaptureLoop();
        }

        private void Setup()
        {
            // ActivateAudioInterfaceAsync — обычный DllImport (не вызов COM-метода),
            // поэтому .NET НЕ инициализирует COM на этом потоке сам. Инициализируем
            // явно MTA (S_FALSE/уже-инициализирован — не ошибка).
            int coHr = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);

            var actParams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = 1,                          // PROCESS_LOOPBACK
                TargetProcessId = _targetPid,
                ProcessLoopbackMode = _excludeTree ? 1 : 0   // 1 = EXCLUDE_TARGET_PROCESS_TREE
            };
            IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
            IntPtr pProp = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
            var handler = _handler = new ManualCompletionHandler();
            try
            {
                Marshal.StructureToPtr(actParams, pParams, false);
                var prop = new PROPVARIANT_BLOB
                {
                    vt = 0x41, // VT_BLOB
                    cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                    pBlobData = pParams
                };
                Marshal.StructureToPtr(prop, pProp, false);

                Guid iidAudioClient = IID_IAudioClient;
                int hr = ActivateAudioInterfaceAsync(VirtualDevicePath, ref iidAudioClient,
                    pProp, handler.NativePtr, out IntPtr opPtr);
                if (hr < 0)
                {
                    if (opPtr != IntPtr.Zero) Release(opPtr);
                    // Диагностика: апартамент потока (M/S/U) + код act + сборка ОС.
                    var apt = Thread.CurrentThread.GetApartmentState();
                    char a = apt == ApartmentState.MTA ? 'M' : apt == ApartmentState.STA ? 'S' : 'U';
                    // process-loopback требует Windows build >= 20348.
                    throw new InvalidOperationException($"act{a}:{hr:X8}:b{Environment.OSVersion.Version.Build}");
                }

                bool answered = handler.Done.WaitOne(3000);
                // Операцию освобождаем ТОЛЬКО после колбэка: раньше Release шёл сразу
                // после вызова, т.е. объект мог быть снесён до/во время колбэка.
                if (opPtr != IntPtr.Zero) Release(opPtr);
                if (!answered) throw new TimeoutException("process loopback: активация не ответила");

                // Разделяем два разных HRESULT: сам вызов GetActivateResult (resC) и
                // результат активации, который он вернул (resA) — причины разные.
                Check("resC", handler.CallHr);
                if (handler.ActivateHr < 0)
                {
                    // + сборка ОС: process-loopback поддерживается только с build >= 20348.
                    // E_NOINTERFACE тут почти наверняка = ОС не понимает process-loopback.
                    throw new InvalidOperationException(
                        $"resA:{handler.ActivateHr:X8}:b{Environment.OSVersion.Version.Build}");
                }
                _audioClient = handler.Interface;
                if (_audioClient == IntPtr.Zero) throw new InvalidOperationException("process loopback: нет IAudioClient");
            }
            finally
            {
                // handler НЕ освобождаем здесь: native может дёрнуть Release позже —
                // держим его native-память живой до Dispose всего захвата.
                Marshal.FreeHGlobal(pParams);
                Marshal.FreeHGlobal(pProp);
            }

            // Формат задаём сами (GetMixFormat для process loopback = E_NOTIMPL).
            var wf = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 2, nSamplesPerSec = 48000,
                wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = 48000 * 4, cbSize = 0
            };
            _blockAlign = wf.nBlockAlign;

            const int SHARED = 0;
            const uint LOOPBACK = 0x00020000;   // без EVENTCALLBACK: капчур-луп опрашивает буфер
            IntPtr pWf = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            try
            {
                Marshal.StructureToPtr(wf, pWf, false);
                Check("init", AC_Initialize(_audioClient, SHARED, LOOPBACK,
                    2_000_000, 0, pWf, IntPtr.Zero));
            }
            finally { Marshal.FreeHGlobal(pWf); }

            _bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);

            Guid iidCapture = IID_IAudioCaptureClient;
            Check("svc", AC_GetService(_audioClient, ref iidCapture, out _capture));
            if (_capture == IntPtr.Zero) throw new InvalidOperationException("process loopback: нет IAudioCaptureClient");

            Check("start", AC_Start(_audioClient));
            _run = true;
        }

        private void CaptureLoop()
        {
            while (_run)
            {
                // Событийный режим у process-loopback часто не срабатывает — опрашиваем всегда.
                _bufferReady.WaitOne(20);
                try
                {
                    while (true)
                    {
                        int hr = CC_GetBuffer(_capture, out IntPtr pData, out uint frames,
                            out uint flags, out _, out _);
                        if (hr != 0 || frames == 0) break;
                        int bytes = (int)frames * _blockAlign;
                        var buf = new byte[bytes];
                        if ((flags & 0x2) == 0 && pData != IntPtr.Zero)   // 0x2 = SILENT
                            Marshal.Copy(pData, buf, 0, bytes);
                        CC_ReleaseBuffer(_capture, frames);
                        try { DataAvailable?.Invoke(this, new WaveInEventArgs(buf, bytes)); } catch { }
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _run = false;
            try { _thread?.Join(400); } catch { }
            _thread = null;
            try { if (_audioClient != IntPtr.Zero) AC_Stop(_audioClient); } catch { }
            if (_capture != IntPtr.Zero) { try { Release(_capture); } catch { } _capture = IntPtr.Zero; }
            if (_audioClient != IntPtr.Zero) { try { Release(_audioClient); } catch { } _audioClient = IntPtr.Zero; }
            try { _bufferReady?.Dispose(); } catch { }
            _bufferReady = null;
            try { _handler?.Dispose(); } catch { }
            _handler = null;
        }

        // На ошибке кидаем с меткой шага и hex-кодом HRESULT — чтобы бейдж
        // показал ГДЕ и ЧТО (например «start:0x88890004»), а не только текст.
        private static void Check(string step, int hr)
        {
            if (hr >= 0) return;
            throw new InvalidOperationException($"{step}:0x{hr:X8}");
        }

        // ── Вызовы методов COM по vtable (без RCW/ComImport) ──────────────
        private static T Fn<T>(IntPtr obj, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        // IUnknown::Release (slot 2)
        private delegate uint FnRelease(IntPtr self);
        private static void Release(IntPtr obj) { try { Fn<FnRelease>(obj, 2)(obj); } catch { } }

        // IAudioClient (после IUnknown 0..2)
        private delegate int FnInitialize(IntPtr self, int share, uint flags, long dur, long period, IntPtr fmt, IntPtr guid);
        private delegate int FnStart(IntPtr self);
        private delegate int FnStop(IntPtr self);
        private delegate int FnSetEventHandle(IntPtr self, IntPtr h);
        private delegate int FnGetService(IntPtr self, ref Guid iid, out IntPtr svc);
        private static int AC_Initialize(IntPtr o, int s, uint f, long d, long p, IntPtr fmt, IntPtr g)
            => Fn<FnInitialize>(o, 3)(o, s, f, d, p, fmt, g);
        private static int AC_Start(IntPtr o) => Fn<FnStart>(o, 10)(o);
        private static int AC_Stop(IntPtr o) => Fn<FnStop>(o, 11)(o);
        private static int AC_SetEventHandle(IntPtr o, IntPtr h) => Fn<FnSetEventHandle>(o, 13)(o, h);
        private static int AC_GetService(IntPtr o, ref Guid iid, out IntPtr svc) => Fn<FnGetService>(o, 14)(o, ref iid, out svc);

        // IAudioCaptureClient (после IUnknown 0..2)
        private delegate int FnGetBuffer(IntPtr self, out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpc);
        private delegate int FnReleaseBuffer(IntPtr self, uint frames);
        private static int CC_GetBuffer(IntPtr o, out IntPtr d, out uint fr, out uint fl, out ulong dp, out ulong qp)
            => Fn<FnGetBuffer>(o, 3)(o, out d, out fr, out fl, out dp, out qp);
        private static int CC_ReleaseBuffer(IntPtr o, uint fr) => Fn<FnReleaseBuffer>(o, 4)(o, fr);

        // ── Самодельный CCW колбэка IActivateAudioInterfaceCompletionHandler ──
        // Native вызывает наш объект: vtable из 4 указателей (QI, AddRef, Release,
        // ActivateCompleted). Внутри ActivateCompleted берём результат у operation
        // тоже по vtable (slot 3 = GetActivateResult), без ComImport.
        private sealed class ManualCompletionHandler : IDisposable
        {
            public readonly EventWaitHandle Done = new(false, EventResetMode.ManualReset);
            public int CallHr;       // HRESULT самого вызова GetActivateResult
            public int ActivateHr;   // HRESULT активации, который он вернул
            public IntPtr Interface;
            public IntPtr NativePtr { get; }   // указатель на COM-объект (для передачи в native)

            private readonly IntPtr _vtbl;
            private IntPtr _ftm;        // агрегированный Free-Threaded Marshaler (IMarshal)
            private GCHandle _self;
            // Делегаты держим живыми, иначе GC соберёт трамплины.
            private readonly FnQI _qi; private readonly FnAddRefRel _ar; private readonly FnAddRefRel _rel;
            private readonly FnActivateCompleted _done;

            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int FnQI(IntPtr self, IntPtr riid, out IntPtr ppv);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate uint FnAddRefRel(IntPtr self);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int FnActivateCompleted(IntPtr self, IntPtr operation);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)]
            private delegate int FnGetActivateResult(IntPtr self, out int hr, out IntPtr iface);

            public ManualCompletionHandler()
            {
                _qi = QI; _ar = AddRef; _rel = Rel; _done = Completed;
                _vtbl = Marshal.AllocHGlobal(IntPtr.Size * 4);
                Marshal.WriteIntPtr(_vtbl, 0 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_qi));
                Marshal.WriteIntPtr(_vtbl, 1 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_ar));
                Marshal.WriteIntPtr(_vtbl, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_rel));
                Marshal.WriteIntPtr(_vtbl, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(_done));
                // COM-объект: [vtable*][GCHandle этого класса] — чтобы из статик-колбэков
                // добраться до экземпляра.
                NativePtr = Marshal.AllocHGlobal(IntPtr.Size * 2);
                _self = GCHandle.Alloc(this);
                Marshal.WriteIntPtr(NativePtr, 0, _vtbl);
                Marshal.WriteIntPtr(NativePtr, IntPtr.Size, GCHandle.ToIntPtr(_self));

                // Настоящая agile-обёртка = агрегируем Free-Threaded Marshaler (как FtmBase
                // в C++-примере MS). Без него ответа на один IAgileObject мало: COM при
                // маршалинге спрашивает IMarshal, не находит FTM, строит стандартный прокси
                // в другой апартамент — и результат активации (IAudioClient) не может
                // вернуться → E_NOINTERFACE. С FTM объект по-настоящему свободнопоточный,
                // колбэк идёт напрямую, и GetActivateResult отдаёт живой IAudioClient.
                CoCreateFreeThreadedMarshaler(NativePtr, out _ftm);
            }

            private static ManualCompletionHandler From(IntPtr self)
                => (ManualCompletionHandler)GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, IntPtr.Size)).Target;

            private static int QI(IntPtr self, IntPtr riid, out IntPtr ppv)
            {
                Guid iid = Marshal.PtrToStructure<Guid>(riid);
                if (iid == IID_IUnknown || iid == IID_CompletionHandler || iid == IID_IAgileObject)
                { ppv = self; return 0; }
                // IMarshal перенаправляем в агрегированный FTM — так объект становится
                // по-настоящему свободнопоточным и колбэк не маршалится в чужой апартамент.
                if (iid == IID_IMarshal)
                {
                    var h = From(self);
                    if (h._ftm != IntPtr.Zero)
                    {
                        Guid g = iid;
                        return Marshal.QueryInterface(h._ftm, ref g, out ppv);
                    }
                }
                ppv = IntPtr.Zero; return unchecked((int)0x80004002); // E_NOINTERFACE
            }
            private static uint AddRef(IntPtr self) => 1;
            private static uint Rel(IntPtr self) => 1;

            private static int Completed(IntPtr self, IntPtr operation)
            {
                var h = From(self);
                try
                {
                    IntPtr vtbl = Marshal.ReadIntPtr(operation);
                    IntPtr fn = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);   // GetActivateResult
                    var get = Marshal.GetDelegateForFunctionPointer<FnGetActivateResult>(fn);
                    h.CallHr = get(operation, out int ar, out IntPtr iface);
                    h.ActivateHr = ar;
                    h.Interface = iface;
                }
                catch (Exception ex) { h.CallHr = Marshal.GetHRForException(ex); }
                finally { h.Done.Set(); }
                return 0;
            }

            public void Dispose()
            {
                try { if (_ftm != IntPtr.Zero) { Marshal.Release(_ftm); _ftm = IntPtr.Zero; } } catch { }
                try { if (NativePtr != IntPtr.Zero) Marshal.FreeHGlobal(NativePtr); } catch { }
                try { if (_vtbl != IntPtr.Zero) Marshal.FreeHGlobal(_vtbl); } catch { }
                try { if (_self.IsAllocated) _self.Free(); } catch { }
                try { Done.Dispose(); } catch { }
            }
        }

        // ── COM / Win32 ───────────────────────────────────────────────────
        private const string VirtualDevicePath = "VAD\\Process_Loopback";
        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IMarshal = new("00000003-0000-0000-C000-000000000046");
        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        private static readonly Guid IID_CompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");
        private static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern int CoCreateFreeThreadedMarshaler(IntPtr pUnkOuter, out IntPtr ppunkMarshal);

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            // REFIID = GUID*. `ref Guid` уже даёт указатель на GUID; добавлять
            // LPStruct НЕЛЬЗЯ — с ним получается GUID** (двойная косвенность), Windows
            // читает мусорный IID, активирует, а потом отдаёт E_NOINTERFACE на запрос
            // интерфейса. Без LPStruct — корректный REFIID.
            ref Guid riid,
            IntPtr activationParams,
            IntPtr completionHandler,
            out IntPtr operation);

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIOCLIENT_ACTIVATION_PARAMS
        {
            public int ActivationType;
            public int TargetProcessId;
            public int ProcessLoopbackMode;
        }

        // PROPVARIANT под VT_BLOB (x64-раскладка: union с 8-байтным выравниванием).
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PROPVARIANT_BLOB
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public uint cbSize;
            [FieldOffset(16)] public IntPtr pBlobData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEX
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }
    }
}
