using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiveKit.Proto;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PISMO.Native
{
    /// <summary>
    /// НАТИВНЫЙ транспорт звонков на LiveKit — через livekit_ffi.dll (Rust/libwebrtc),
    /// БЕЗ WebView2/Chromium. Обходит 0x8007139F, который активный VR вызывает у
    /// любого Chromium. Подключается к тому же LiveKit-серверу теми же JWT-токенами.
    ///
    /// Готово: подключение к комнате + голос (микрофон → LiveKit, приём голоса
    /// собеседников → колонки через NAudio-микшер). Дальше — камера/демка.
    /// </summary>
    public sealed class NativeCallTransport : IDisposable
    {
        public event Action Connected;
        public event Action Disconnected;
        public event Action<string, string> ParticipantJoined;   // (identity, name)
        public event Action<string> ParticipantLeftById;          // (identity)
        public event Action<string> ConnectError;

        private ulong _connectAsyncId;
        private ulong _roomHandle;
        private ulong _localHandle;      // OwnedParticipant локального участника
        private bool _disposed;

        // Аудио 48 кГц моно — общий формат FFI-источника/стрима.
        private const int SR = 48000;
        private const int CH = 1;

        // Приём: микшер всех входящих голосов → один WaveOut.
        private WaveOutEvent _out;
        private MixingSampleProvider _mixer;
        private readonly Dictionary<ulong, BufferedWaveProvider> _remoteByStream = new();
        private readonly object _audioLock = new();

        // Отправка: микрофон → FFI audio source.
        private WaveInEvent _micIn;
        private ulong _micSource, _micTrack;
        private ulong _publishAsyncId;
        private bool _micStarted;

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
                    Options = new RoomOptions { AutoSubscribe = true, Dynacast = true, AdaptiveStream = false }
                }
            };
            _connectAsyncId = LiveKitFfi.Request(req).Connect.AsyncId;
        }

        public void DisconnectCall()
        {
            if (_roomHandle == 0) return;
            try { LiveKitFfi.Request(new FfiRequest { Disconnect = new DisconnectRequest { RoomHandle = _roomHandle } }); }
            catch { }
        }

        // ── Микрофон ──────────────────────────────────────────────────────
        public void StartMicrophone(bool echoCancel = true, bool noiseSuppress = true, bool agc = true)
        {
            if (_micStarted || _localHandle == 0) return;
            _micStarted = true;
            try
            {
                // 1) Нативный push-источник аудио с обработкой (EC/NS/AGC в libwebrtc APM).
                var srcResp = LiveKitFfi.Request(new FfiRequest
                {
                    NewAudioSource = new NewAudioSourceRequest
                    {
                        Type = AudioSourceType.AudioSourceNative,
                        SampleRate = SR,
                        NumChannels = CH,
                        QueueSizeMs = 100,
                        Options = new AudioSourceOptions
                        {
                            EchoCancellation = echoCancel,
                            NoiseSuppression = noiseSuppress,
                            AutoGainControl = agc
                        }
                    }
                });
                _micSource = srcResp.NewAudioSource.Source.Handle.Id;

                // 2) Трек из источника.
                var trkResp = LiveKitFfi.Request(new FfiRequest
                {
                    CreateAudioTrack = new CreateAudioTrackRequest { Name = "microphone", SourceHandle = _micSource }
                });
                _micTrack = trkResp.CreateAudioTrack.Track.Handle.Id;

                // 3) Публикация трека от локального участника.
                var pubResp = LiveKitFfi.Request(new FfiRequest
                {
                    PublishTrack = new PublishTrackRequest
                    {
                        LocalParticipantHandle = _localHandle,
                        TrackHandle = _micTrack,
                        Options = new TrackPublishOptions { Source = TrackSource.SourceMicrophone, Dtx = true, Red = true }
                    }
                });
                _publishAsyncId = pubResp.PublishTrack.AsyncId;

                // 4) Захват микрофона: 48 кГц / 16 бит / моно → CaptureAudioFrame.
                _micIn = new WaveInEvent { WaveFormat = new WaveFormat(SR, 16, CH), BufferMilliseconds = 20 };
                _micIn.DataAvailable += OnMicData;
                _micIn.StartRecording();
            }
            catch (Exception ex) { ConnectError?.Invoke("микрофон: " + ex.Message); }
        }

        public void StopMicrophone()
        {
            try { _micIn?.StopRecording(); _micIn?.Dispose(); } catch { }
            _micIn = null;
            _micStarted = false;
        }

        private void OnMicData(object sender, WaveInEventArgs e)
        {
            if (_micSource == 0 || e.BytesRecorded <= 0) return;
            int samples = e.BytesRecorded / 2;   // 16-бит моно
            IntPtr buf = Marshal.AllocHGlobal(e.BytesRecorded);
            try
            {
                Marshal.Copy(e.Buffer, 0, buf, e.BytesRecorded);
                LiveKitFfi.Request(new FfiRequest
                {
                    CaptureAudioFrame = new CaptureAudioFrameRequest
                    {
                        SourceHandle = _micSource,
                        Buffer = new AudioFrameBufferInfo
                        {
                            DataPtr = (ulong)buf.ToInt64(),
                            NumChannels = CH,
                            SampleRate = SR,
                            SamplesPerChannel = (uint)samples
                        }
                    }
                });
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ── События FFI ───────────────────────────────────────────────────
        private void OnFfiEvent(FfiEvent ev)
        {
            try
            {
                switch (ev.MessageCase)
                {
                    case FfiEvent.MessageOneofCase.Connect: HandleConnect(ev.Connect); break;
                    case FfiEvent.MessageOneofCase.RoomEvent: HandleRoomEvent(ev.RoomEvent); break;
                    case FfiEvent.MessageOneofCase.AudioStreamEvent: HandleAudioStream(ev.AudioStreamEvent); break;
                }
            }
            catch { }
        }

        private void HandleConnect(ConnectCallback cb)
        {
            if (cb.AsyncId != _connectAsyncId) return;
            if (cb.MessageCase == ConnectCallback.MessageOneofCase.Error) { ConnectError?.Invoke(cb.Error); return; }
            var res = cb.Result;
            _roomHandle = res.Room.Handle.Id;
            _localHandle = res.LocalParticipant.Handle.Id;
            EnsurePlayback();
            Connected?.Invoke();
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
                case RoomEvent.MessageOneofCase.TrackSubscribed:
                    OnTrackSubscribed(re.TrackSubscribed.Track);
                    break;
                case RoomEvent.MessageOneofCase.Disconnected:
                    Disconnected?.Invoke();
                    break;
            }
        }

        // Подписались на удалённый трек: аудио → открываем стрим и играем.
        private void OnTrackSubscribed(OwnedTrack track)
        {
            if (track.Info.Kind != TrackKind.KindAudio) return;   // видео/демка — отдельно
            var resp = LiveKitFfi.Request(new FfiRequest
            {
                NewAudioStream = new NewAudioStreamRequest
                {
                    TrackHandle = track.Handle.Id,
                    Type = AudioStreamType.AudioStreamNative,
                    SampleRate = SR,
                    NumChannels = CH
                }
            });
            ulong streamHandle = resp.NewAudioStream.Stream.Handle.Id;
            lock (_audioLock)
            {
                EnsurePlayback();
                var prov = new BufferedWaveProvider(new WaveFormat(SR, 16, CH))
                { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true };
                _remoteByStream[streamHandle] = prov;
                _mixer.AddMixerInput(prov.ToSampleProvider());
            }
        }

        private void HandleAudioStream(AudioStreamEvent ase)
        {
            if (ase.MessageCase != AudioStreamEvent.MessageOneofCase.FrameReceived) return;
            BufferedWaveProvider prov;
            lock (_audioLock) { _remoteByStream.TryGetValue(ase.StreamHandle, out prov); }
            if (prov == null) return;

            var frame = ase.FrameReceived.Frame;
            var info = frame.Info;
            int samples = (int)(info.SamplesPerChannel * info.NumChannels);
            int bytes = samples * 2;   // i16
            if (bytes > 0 && info.DataPtr != 0)
            {
                var pcm = new byte[bytes];
                Marshal.Copy(new IntPtr((long)info.DataPtr), pcm, 0, bytes);
                prov.AddSamples(pcm, 0, bytes);
            }
            LiveKitFfi.DropHandle(frame.Handle.Id);
        }

        private void EnsurePlayback()
        {
            if (_out != null) return;
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SR, CH)) { ReadFully = true };
            _out = new WaveOutEvent { DesiredLatency = 120 };
            _out.Init(_mixer);
            _out.Play();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { LiveKitFfi.FfiEventReceived -= OnFfiEvent; } catch { }
            try { StopMicrophone(); } catch { }
            try { DisconnectCall(); } catch { }
            try { _out?.Stop(); _out?.Dispose(); } catch { }
            _out = null;
        }
    }
}
