using System;
using System.Runtime.InteropServices;

namespace PISMO.Native
{
    /// <summary>
    /// Низкоуровневый биндинг к нативной библиотеке LiveKit `livekit_ffi.dll`
    /// (Rust client-sdk-ffi, обёртка над libwebrtc). Это фундамент перехода звонка
    /// с WebView2/Chromium на НАТИВНЫЙ путь — чтобы обойти 0x8007139F, который VR
    /// вызывает у любого Chromium.
    ///
    /// C-ABI библиотеки (из livekit_ffi.h) минимален — 4 функции:
    ///   FfiHandleId livekit_ffi_request(const uint8_t* data, size_t len,
    ///                                   const uint8_t** res_ptr, size_t* res_len);
    ///   bool        livekit_ffi_drop_handle(FfiHandleId handle);
    ///   void        livekit_ffi_initialize(FfiCallback cb, bool captureLogs,
    ///                                      const char* sdk, const char* version);
    ///   void        livekit_ffi_dispose();
    /// где FfiCallback = void(*)(const uint8_t* data, size_t len) — асинхронные
    /// события (FfiEvent, protobuf): комната подключилась, участник вошёл, трек
    /// подписан, аудио/видео-кадры и т.д.
    ///
    /// Протокол — protobuf: в request сериализуем FfiRequest, из ответа парсим
    /// FfiResponse; в колбэке приходит FfiEvent. C#-классы protobuf генерируются из
    /// .proto LiveKit (livekit-ffi/protocol/*.proto) — СЛЕДУЮЩИЙ шаг. Пока слой
    /// работает на «сырых» байтах, чтобы фундамент компилировался без зависимостей.
    ///
    /// Нативную dll (24 МБ, win-x64) кладём рядом с PISMO.exe. Как достать:
    ///   pip download livekit --only-binary=:all: --platform win_amd64 \
    ///       --python-version 3.11 -d wheels
    /// затем из колеса livekit-*.whl взять livekit/rtc/resources/livekit_ffi.dll.
    /// (Либо собрать client-sdk-ffi через cargo.)
    /// </summary>
    internal static class LiveKitFfi
    {
        private const string Dll = "livekit_ffi";   // livekit_ffi.dll рядом с exe

        /// <summary>Асинхронное событие FfiEvent (protobuf-байты). Разбор — слоем выше.</summary>
        public static event Action<byte[]> Event;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FfiCallback(IntPtr data, UIntPtr len);

        // Держим делегат живым (иначе GC соберёт, и нативный колбэк упадёт).
        private static FfiCallback _cb;
        private static readonly object _lock = new object();
        private static bool _initialized;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void livekit_ffi_initialize(
            FfiCallback callback, [MarshalAs(UnmanagedType.I1)] bool captureLogs,
            [MarshalAs(UnmanagedType.LPStr)] string sdk,
            [MarshalAs(UnmanagedType.LPStr)] string version);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong livekit_ffi_request(
            byte[] data, UIntPtr len, out IntPtr resPtr, out UIntPtr resLen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool livekit_ffi_drop_handle(ulong handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void livekit_ffi_dispose();

        /// <summary>Инициализация FFI: регистрируем колбэк событий. Вызывать один раз.</summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _cb = OnFfiEvent;   // сохраняем ссылку — защита от GC
                livekit_ffi_initialize(_cb, false, "pismo-dotnet", "1.0.0");
                _initialized = true;
            }
        }

        /// <summary>Синхронный запрос: сериализованный FfiRequest → сериализованный
        /// FfiResponse. (protobuf-обёртки навешиваем слоем выше.)</summary>
        public static byte[] Request(byte[] ffiRequest)
        {
            if (ffiRequest == null) throw new ArgumentNullException(nameof(ffiRequest));
            ulong handle = livekit_ffi_request(
                ffiRequest, (UIntPtr)ffiRequest.Length, out IntPtr resPtr, out UIntPtr resLen);

            int len = checked((int)resLen);
            var resp = new byte[len];
            if (len > 0 && resPtr != IntPtr.Zero)
                Marshal.Copy(resPtr, resp, 0, len);

            if (handle != 0) livekit_ffi_drop_handle(handle);
            return resp;
        }

        /// <summary>Освобождение нативного хэндла (video/audio buffer, room и т.д.).</summary>
        public static void DropHandle(ulong handle)
        {
            if (handle != 0) livekit_ffi_drop_handle(handle);
        }

        public static void Dispose()
        {
            lock (_lock)
            {
                if (!_initialized) return;
                try { livekit_ffi_dispose(); } catch { }
                _initialized = false;
                _cb = null;
            }
        }

        private static void OnFfiEvent(IntPtr data, UIntPtr len)
        {
            try
            {
                int n = checked((int)len);
                if (n <= 0 || data == IntPtr.Zero) return;
                var buf = new byte[n];
                Marshal.Copy(data, buf, 0, n);
                Event?.Invoke(buf);
            }
            catch { /* колбэк из натива — не даём исключению пересечь границу */ }
        }
    }
}
