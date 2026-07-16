using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace PISMO.Native
{
    /// <summary>
    /// Захват системного звука в обход СВОЕГО процесса — WASAPI process loopback
    /// (ActivateAudioInterfaceAsync, Windows 10 20348+). Режим «исключить дерево
    /// процесса PISMO» означает: в звук демонстрации попадёт всё, что играет
    /// система (игра/музыка/браузер), КРОМЕ того, что выводит сам PISMO
    /// (голоса собеседников). Поэтому у друзей нет эха собственного голоса, а
    /// микрофон в демку и так не попадает.
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
        private IAudioClient _audioClient;
        private IAudioCaptureClient _capture;
        private EventWaitHandle _bufferReady;
        private Thread _thread;
        private volatile bool _run;
        private int _blockAlign;

        public ProcessLoopbackCapture(int targetPid, bool excludeTargetTree)
        {
            _targetPid = targetPid;
            _excludeTree = excludeTargetTree;
        }

        public void Start()
        {
            // 1) Параметры активации: process loopback с исключением/включением дерева.
            var actParams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                TargetProcessId = _targetPid,
                ProcessLoopbackMode = _excludeTree ? 1 : 0 // 1 = EXCLUDE_TARGET_PROCESS_TREE
            };
            IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
            IntPtr pProp = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
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

                var handler = new ActivateHandler();
                Guid iidAudioClient = IID_IAudioClient;
                ActivateAudioInterfaceAsync(VirtualDevicePath, ref iidAudioClient, pProp, handler, out _);

                if (!handler.Done.WaitOne(3000)) throw new TimeoutException("process loopback: активация не ответила");
                Marshal.ThrowExceptionForHR(handler.ActivateResult);
                _audioClient = (IAudioClient)handler.Interface
                    ?? throw new InvalidOperationException("process loopback: нет IAudioClient");
            }
            finally
            {
                Marshal.FreeHGlobal(pParams);
                Marshal.FreeHGlobal(pProp);
            }

            // 2) Формат — задаём сами (GetMixFormat для process loopback = E_NOTIMPL).
            var wf = new WAVEFORMATEX
            {
                wFormatTag = 1, // WAVE_FORMAT_PCM
                nChannels = 2,
                nSamplesPerSec = 48000,
                wBitsPerSample = 16,
                nBlockAlign = 4,           // 2 канала * 16 бит / 8
                nAvgBytesPerSec = 48000 * 4,
                cbSize = 0
            };
            _blockAlign = wf.nBlockAlign;

            const int AUDCLNT_SHAREMODE_SHARED = 0;
            const uint LOOPBACK = 0x00020000;
            const uint EVENTCALLBACK = 0x00040000;
            IntPtr pWf = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            try
            {
                Marshal.StructureToPtr(wf, pWf, false);
                _audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, LOOPBACK | EVENTCALLBACK,
                    2_000_000, 0, pWf, IntPtr.Zero);
            }
            finally { Marshal.FreeHGlobal(pWf); }

            _bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
            _audioClient.SetEventHandle(_bufferReady.SafeWaitHandle.DangerousGetHandle());

            Guid iidCapture = IID_IAudioCaptureClient;
            _audioClient.GetService(ref iidCapture, out object capObj);
            _capture = (IAudioCaptureClient)capObj;

            _audioClient.Start();
            _run = true;
            _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "pismo-proc-loopback" };
            _thread.Start();
        }

        private void CaptureLoop()
        {
            while (_run)
            {
                // Ждём событие, но GetBuffer пробуем ВСЕГДА: у process-loopback
                // (VAD\Process_Loopback) событийный режим часто не срабатывает —
                // тогда без опроса захват «жив», но данных нет вообще.
                _bufferReady.WaitOne(20);
                try
                {
                    while (true)
                    {
                        int hr = _capture.GetBuffer(out IntPtr pData, out uint frames, out uint flags, out _, out _);
                        if (hr != 0 || frames == 0) break;            // S_OK==0; иначе (в т.ч. buffer empty) выходим
                        int bytes = (int)frames * _blockAlign;
                        var buf = new byte[bytes];
                        // AUDCLNT_BUFFERFLAGS_SILENT = 0x2 — тишина: отдаём нули.
                        if ((flags & 0x2) == 0 && pData != IntPtr.Zero)
                            Marshal.Copy(pData, buf, 0, bytes);
                        _capture.ReleaseBuffer(frames);
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
            try { _audioClient?.Stop(); } catch { }
            try { if (_capture != null) Marshal.ReleaseComObject(_capture); } catch { }
            try { if (_audioClient != null) Marshal.ReleaseComObject(_audioClient); } catch { }
            _capture = null; _audioClient = null;
            try { _bufferReady?.Dispose(); } catch { }
            _bufferReady = null;
        }

        // ── COM / Win32 ───────────────────────────────────────────────────
        private const string VirtualDevicePath = "VAD\\Process_Loopback";
        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        private static extern void ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] ref Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation operation);

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

        [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int activateResult,
                [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        private sealed class ActivateHandler : IActivateAudioInterfaceCompletionHandler
        {
            public readonly EventWaitHandle Done = new(false, EventResetMode.ManualReset);
            public int ActivateResult;
            public object Interface;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try { operation.GetActivateResult(out ActivateResult, out Interface); }
                catch (Exception ex) { ActivateResult = Marshal.GetHRForException(ex); }
                finally { Done.Set(); }
            }
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            void Initialize(int shareMode, uint streamFlags, long hnsBufferDuration,
                long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
            void GetBufferSize(out uint bufferFrames);
            void GetStreamLatency(out long latency);
            void GetCurrentPadding(out uint padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
            void GetMixFormat(out IntPtr format);
            void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            void Start();
            void Stop();
            void Reset();
            void SetEventHandle(IntPtr eventHandle);
            void GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead,
                out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint numFramesRead);
            [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
        }
    }
}
