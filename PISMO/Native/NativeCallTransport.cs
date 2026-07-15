using System;
using LiveKit.Proto;

namespace PISMO.Native
{
    /// <summary>
    /// НАТИВНЫЙ транспорт звонков на LiveKit — через livekit_ffi.dll (Rust/libwebrtc),
    /// БЕЗ WebView2/Chromium. Цель: обойти 0x8007139F, который активный VR вызывает
    /// у любого Chromium. Подключается к тому же LiveKit-серверу теми же JWT-токенами.
    ///
    /// Ф0 (этот файл): подключение к комнате + события участников/дисконнекта.
    /// Дальше — аудио (WASAPI ↔ FFI audio source/sink), затем камера/демка.
    /// Публичный контракт по мере готовности приводится к тому же, что у
    /// WebRtcTransport, чтобы CallForm переключился с минимальными правками.
    /// </summary>
    public sealed class NativeCallTransport : IDisposable
    {
        public event Action Connected;
        public event Action Disconnected;
        public event Action<string, string> ParticipantJoined;   // (identity, name)
        public event Action<string> ParticipantLeftById;          // (identity)
        public event Action<string> ConnectError;                 // текст ошибки подключения

        private ulong _connectAsyncId;
        private ulong _roomHandle;
        private bool _disposed;

        /// <summary>Подключение к комнате LiveKit. Асинхронно: FFI вернёт async_id,
        /// а результат придёт событием ConnectCallback.</summary>
        public void Connect(string url, string token)
        {
            LiveKitFfi.Initialize();
            LiveKitFfi.FfiEventReceived += OnFfiEvent;

            var req = new FfiRequest
            {
                Connect = new ConnectRequest
                {
                    Url = url,
                    Token = token,
                    Options = new RoomOptions
                    {
                        AutoSubscribe = true,
                        Dynacast = true,
                        AdaptiveStream = false
                    }
                }
            };
            FfiResponse resp = LiveKitFfi.Request(req);
            _connectAsyncId = resp.Connect.AsyncId;
        }

        public void DisconnectCall()
        {
            if (_roomHandle == 0) return;
            try
            {
                LiveKitFfi.Request(new FfiRequest
                {
                    Disconnect = new DisconnectRequest { RoomHandle = _roomHandle }
                });
            }
            catch { }
        }

        private void OnFfiEvent(FfiEvent ev)
        {
            try
            {
                switch (ev.MessageCase)
                {
                    case FfiEvent.MessageOneofCase.Connect:
                        HandleConnect(ev.Connect);
                        break;
                    case FfiEvent.MessageOneofCase.RoomEvent:
                        HandleRoomEvent(ev.RoomEvent);
                        break;
                }
            }
            catch { /* не даём исключению пересечь границу нативного колбэка */ }
        }

        private void HandleConnect(ConnectCallback cb)
        {
            if (cb.AsyncId != _connectAsyncId) return;
            if (cb.MessageCase == ConnectCallback.MessageOneofCase.Error)
            {
                ConnectError?.Invoke(cb.Error);
                return;
            }
            var res = cb.Result;
            _roomHandle = res.Room.Handle.Id;
            Connected?.Invoke();
            // Уже присутствующие участники.
            foreach (var pwt in res.Participants)
            {
                var info = pwt.Participant.Info;
                ParticipantJoined?.Invoke(info.Identity, info.Name);
            }
        }

        private void HandleRoomEvent(RoomEvent re)
        {
            if (_roomHandle != 0 && re.RoomHandle != _roomHandle) return;
            switch (re.MessageCase)
            {
                case RoomEvent.MessageOneofCase.ParticipantConnected:
                {
                    var info = re.ParticipantConnected.Info.Info;
                    ParticipantJoined?.Invoke(info.Identity, info.Name);
                    break;
                }
                case RoomEvent.MessageOneofCase.ParticipantDisconnected:
                    ParticipantLeftById?.Invoke(re.ParticipantDisconnected.ParticipantIdentity);
                    break;
                case RoomEvent.MessageOneofCase.Disconnected:
                    Disconnected?.Invoke();
                    break;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { LiveKitFfi.FfiEventReceived -= OnFfiEvent; } catch { }
            try { DisconnectCall(); } catch { }
        }
    }
}
