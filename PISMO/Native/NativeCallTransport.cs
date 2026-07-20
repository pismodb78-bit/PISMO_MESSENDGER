using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using AForge.Video;
using AForge.Video.DirectShow;
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
    /// Готово: подключение к комнате, голос (микрофон → LiveKit, приём голоса →
    /// колонки через NAudio-микшер), КАМЕРА и ДЕМОНСТРАЦИЯ экрана/окна (публикация
    /// BGRA-кадров) + приём удалённого видео (камера/демка собеседников) кадрами BGRA.
    /// Один и тот же движок используется и в звонках серверных голосовых каналов, и в
    /// личных/групповых звонках мессенджера.
    /// </summary>
    public sealed class NativeCallTransport : IDisposable
    {
        public event Action Connected;
        public event Action Disconnected;
        public event Action<string, string> ParticipantJoined;   // (identity, name)
        public event Action<string> ParticipantLeftById;          // (identity)
        public event Action<string> ConnectError;
        public event Action<string[]> ActiveSpeakersChanged;          // список говорящих identity
        public event Action<string, bool> ParticipantMicMuted;       // (identity, muted) — мьют МИКРОФОНА
        public event Action<string, bool> ParticipantDeafened;       // (identity, deafened) — «наушники»
        public event Action<int> RttUpdated;                          // реальный RTT медиа-канала, мс

        // Видео: кадры BGRA (packed, stride = width*4). CallForm рисует плитки.
        public event Action<string, bool, byte[], int, int> RemoteVideoFrame; // (identity, isScreen, bgra, w, h)
        public event Action<string, bool> RemoteVideoRemoved;                 // (identity, isScreen)
        public event Action<byte[], int, int> LocalCameraFrame;               // локальное превью камеры
        public event Action<byte[], int, int> LocalScreenFrame;               // локальное превью демки
        public event Action<string> ScreenCaptureStats;                       // "48 fps · DXGI 1920x1080" для плашки

        private ulong _connectAsyncId;
        private ulong _statsAsyncId;
        private System.Threading.Timer _statsTimer;
        private ulong _roomHandle;
        private ulong _localHandle;      // OwnedParticipant локального участника
        private bool _disposed;

        // Аудио 48 кГц моно — общий формат FFI-источника/стрима.
        private const int SR = 48000;
        private const int CH = 1;

        // Монотонные микросекундные метки для кадров.
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static long NowUs() => _clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

        // Приём: микшер всех входящих голосов → один WaveOut.
        private WaveOutEvent _out;
        private MixingSampleProvider _mixer;
        private readonly Dictionary<ulong, BufferedWaveProvider> _remoteByStream = new();

        // Пер-поточная громкость входящего звука (голос/демка каждого участника).
        private sealed class RemoteAudioCtx
        {
            public string Pid;
            public bool IsScreen;         // звук демки (SCREENSHARE_AUDIO), не голос
            public VolumeSampleProvider Vol;
            public float User = 1f;       // пользовательская громкость (0..3)
            public bool Muted;
        }
        private readonly Dictionary<ulong, RemoteAudioCtx> _audioCtxByStream = new();
        private float _globalScreenVol = 1f;   // общий ползунок «Громкость демонстрации»
        private readonly HashSet<string> _watchedScreenAudio = new();   // чью демку СМОТРИМ

        private void ApplyRemoteAudioVolume(RemoteAudioCtx c)
        {
            // Звук демки слышен ТОЛЬКО когда её смотрят (нажали «Смотреть стрим»).
            if (c.IsScreen && !_watchedScreenAudio.Contains(c.Pid)) { c.Vol.Volume = 0f; return; }
            c.Vol.Volume = c.Muted ? 0f : Math.Clamp(c.User * (c.IsScreen ? _globalScreenVol : 1f), 0f, 4f);
        }

        /// <summary>Смотрим/не смотрим демку участника → включаем/глушим её звук.</summary>
        public void SetScreenAudioWatched(string pid, bool watched)
        {
            lock (_audioLock)
            {
                if (watched) _watchedScreenAudio.Add(pid); else _watchedScreenAudio.Remove(pid);
                foreach (var c in _audioCtxByStream.Values)
                    if (c.IsScreen && c.Pid == pid) ApplyRemoteAudioVolume(c);
            }
        }

        /// <summary>Громкость ГОЛОСА конкретного участника (ПКМ по плитке).</summary>
        public void SetParticipantVolume(string pid, float volume)
        {
            lock (_audioLock)
                foreach (var c in _audioCtxByStream.Values)
                    if (!c.IsScreen && c.Pid == pid) { c.User = volume; ApplyRemoteAudioVolume(c); }
        }

        /// <summary>Заглушить голос конкретного участника.</summary>
        public void SetParticipantMuted(string pid, bool muted)
        {
            lock (_audioLock)
                foreach (var c in _audioCtxByStream.Values)
                    if (!c.IsScreen && c.Pid == pid) { c.Muted = muted; ApplyRemoteAudioVolume(c); }
        }

        /// <summary>Громкость ДЕМКИ конкретного участника (0 = заглушить).</summary>
        public void SetScreenShareVolume(string pid, float volume)
        {
            lock (_audioLock)
                foreach (var c in _audioCtxByStream.Values)
                    if (c.IsScreen && c.Pid == pid) { c.User = volume; ApplyRemoteAudioVolume(c); }
        }

        /// <summary>Общая громкость всех демок (ползунок в панели ⚙).</summary>
        public void SetScreenAudioVolumeAll(float volume)
        {
            lock (_audioLock)
            {
                _globalScreenVol = volume;
                foreach (var c in _audioCtxByStream.Values)
                    if (c.IsScreen) ApplyRemoteAudioVolume(c);
            }
        }
        private readonly object _audioLock = new();
        private int _outputDeviceIndex = -1;      // -1 = устройство по умолчанию
        private float _playbackVolume = 1.0f;
        private bool _playbackMuted;

        // Отправка: микрофон → FFI audio source.
        private WaveInEvent _micIn;
        private ulong _micSource, _micTrack;
        private ulong _publishAsyncId;
        private bool _micStarted;
        private volatile bool _micMuted;          // мьют = не отправляем кадры
        private int _inputDeviceIndex = -1;       // -1 = устройство по умолчанию
        private MicDenoiser _denoiser;            // клик-гейт (транзиенты) — откат
        private SpectralDenoiser _spectral;       // частотный шумодав (постоянный фон) — откат
        private RnnoiseDenoiser _rnnoise;         // НАСТОЯЩИЙ RNNoise (как в тесте) — основной
        private volatile bool _nsEnabled;

        // Нативный libwebrtc APM: шумодав + эхоподавление + ВЧ-фильтр + AGC.
        // ProcessStream = ближний конец (микрофон), ProcessReverseStream = дальний
        // конец (то, что играем в колонки) — референс для AEC.
        private ulong _apmHandle;
        private volatile bool _apmEnabled;
        private bool _apmEc = true, _apmAgc = true;   // флаги APM для пересоздания
        private readonly object _apmLock = new();
        private byte[] _micAccum = new byte[0];   // накопитель микрофона для 10мс-кадров
        private int _micAccumLen;
        private const int ApmFrameBytes = SR / 100 * CH * 2;   // 10 мс i16 = 960 байт

        // ── Видео (камера/демка) ──────────────────────────────────────────
        private ulong _camSource, _camTrack, _camPublishAsyncId;
        private VideoCaptureDevice _camDevice;
        private string _camTrackSid;
        private bool _camStarted;
        private bool _camPublished;
        private int _camW = 1280, _camH = 720;   // реальное разрешение захвата камеры

        private ulong _scrSource, _scrTrack, _scrPublishAsyncId;
        private Thread _scrThread;
        private volatile bool _scrRun;
        private Rectangle _scrBounds;
        private IntPtr _scrWindow;
        private int _scrFps = 15;
        private volatile int _scrTargetHeight;   // 0 = родное разрешение, иначе даунскейл до этой высоты
        private string _scrCodec = "h264";       // предпочтительный кодек демки
        private string _scrGpuPref = "auto";     // hint энкодера (auto/high/integrated)
        private string _scrTrackSid;
        private bool _scrStarted;

        // Звук демонстрации (системный звук через WASAPI-loopback). Публикуется
        // ОТДЕЛЬНЫМ треком source=SCREENSHARE_AUDIO, с ВЫКЛЮЧЕННЫМИ EC/NS/AGC —
        // чтобы шумодав/эхоподавление не гасили звуки игры/музыки в демке.
        private NAudio.Wave.WasapiLoopbackCapture _loopback;
        private ProcessLoopbackCapture _procLoop;   // захват без своего процесса (без эха)
        private bool _scrAudioFloat;                // (устар.) — см. _scrCaptureFloat
        private int _scrCaptureRate = 48000, _scrCaptureCh = 2;
        private bool _scrCaptureFloat = true;
        private ulong _scrAudioSource, _scrAudioTrack;
        private ulong _scrAudioPublishAsyncId;
        private string _scrAudioTrackSid;
        private int _scrAudioRate = 48000, _scrAudioCh = 2;

        // sid трека → источник (камера/демка), чтобы различать входящее видео.
        private readonly Dictionary<string, TrackSource> _sourceBySid = new();
        // handle видеострима → (участник, это ли демка).
        // Контекст входящего видеопотока: кто/что + пул буферов кадров.
        // Пул из 3 буферов вместо new byte[8МБ] на КАЖДЫЙ кадр: на 60fps это
        // ~500МБ/с мусора — паузы GC дёргали и превью, и входящие стримы.
        private sealed class VideoStreamCtx
        {
            public string Identity;
            public bool IsScreen;
            public readonly byte[][] Pool = new byte[3][];
            public int Idx;
            public byte[] Rent(int size)
            {
                int i = (Idx = (Idx + 1) % 3);
                var b = Pool[i];
                if (b == null || b.Length != size) Pool[i] = b = new byte[size];
                return b;
            }
        }
        private readonly Dictionary<ulong, VideoStreamCtx> _videoStreamMeta = new();
        private readonly object _videoLock = new();

        public void Connect(string url, string token)
        {
            LiveKitFfi.Initialize();
            LiveKitFfi.FfiEventReceived += OnFfiEvent;

            // На время звонка — GC с минимальными паузами: сборки мусора не должны
            // дёргать 60fps-видео и аудио-кадры по 10мс.
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; } catch { }

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

                // 4) Нативный libwebrtc APM: настоящий шумодав + эхоподавление.
                //    Работает на i16-кадрах по 10 мс: ближний конец в OnMicData,
                //    дальний (референс эха) — из микшера воспроизведения.
                _apmEc = echoCancel; _apmAgc = agc;
                CreateApm(noiseSuppress);

                // Шумодав. Основной — НАСТОЯЩИЙ RNNoise (тот же wasm, что в тесте
                // микрофона), крутится в процессе через Wasmtime. Если он не поднялся
                // (нет wasm/рантайма) — откат на программный spectral+gate.
                _nsEnabled = noiseSuppress;
                _gateEnabled = noiseSuppress;
                _rnnoise = RnnoiseDenoiser.TryCreate();
                _denoiser = new MicDenoiser(SR) { TransientGuard = noiseSuppress };
                _spectral = new SpectralDenoiser();

                // 5) Захват микрофона: 48 кГц / 16 бит / моно → CaptureAudioFrame.
                StartMicCapture();
            }
            catch (Exception ex) { ConnectError?.Invoke("микрофон: " + ex.Message); }
        }

        // Создаёт (или пересоздаёт) APM с текущими EC/AGC и заданным шумодавом.
        // NewApmRequest фиксирует флаги на всю жизнь APM, поэтому живое
        // переключение шумодава = пересоздание модуля (звук не прерывается:
        // OnMicData просто перейдёт на новый handle со следующего 10мс-кадра).
        private void CreateApm(bool noiseSuppress)
        {
            ulong old = _apmHandle;
            try
            {
                var apmResp = LiveKitFfi.Request(new FfiRequest
                {
                    NewApm = new NewApmRequest
                    {
                        EchoCancellerEnabled = _apmEc,
                        GainControllerEnabled = _apmAgc,
                        HighPassFilterEnabled = true,
                        // NS делает наш SpectralDenoiser (детерминированно, не зависит
                        // от того, работает ли NS внутри этой сборки FFI).
                        NoiseSuppressionEnabled = false
                    }
                });
                _apmHandle = apmResp.NewApm.Apm.Handle.Id;
                _apmEnabled = true;
                // Типичная задержка тракта воспроизведения (буфер WaveOut ~120мс).
                try { LiveKitFfi.Request(new FfiRequest { ApmSetStreamDelay = new ApmSetStreamDelayRequest { ApmHandle = _apmHandle, DelayMs = 140 } }); } catch { }
            }
            catch { _apmHandle = 0; _apmEnabled = false; }
            if (old != 0) { try { LiveKitFfi.DropHandle(old); } catch { } }
        }

        private void StartMicCapture()
        {
            _micIn = new WaveInEvent { WaveFormat = new WaveFormat(SR, 16, CH), BufferMilliseconds = 20 };
            if (_inputDeviceIndex >= 0 && _inputDeviceIndex < WaveInEvent.DeviceCount)
                _micIn.DeviceNumber = _inputDeviceIndex;
            _micIn.DataAvailable += OnMicData;
            _micIn.StartRecording();
        }

        public void StopMicrophone()
        {
            try { if (_micIn != null) _micIn.DataAvailable -= OnMicData; } catch { }
            try { _micIn?.StopRecording(); _micIn?.Dispose(); } catch { }
            _micIn = null;
            _micStarted = false;
            _apmEnabled = false;
            if (_apmHandle != 0) { try { LiveKitFfi.DropHandle(_apmHandle); } catch { } _apmHandle = 0; }
            _micAccumLen = 0;
            try { _rnnoise?.Dispose(); } catch { }
            _rnnoise = null;
        }

        /// <summary>Вкл/выкл шумодав на лету (совместимость: bool → режим).</summary>
        public void SetNoiseSuppression(bool on) => SetNoiseMode(on ? "standard" : "off");

        /// <summary>Шумодав на лету: "off" либо включён. «Включён» = WebRTC APM NS
        /// + мягкий транзиент-гейт (давит и клики клавиатуры) — один режим, как
        /// был RNNoise во времена WebView2. APM пересоздаётся на лету.</summary>
        public void SetNoiseMode(string mode)
        {
            bool ns = !string.Equals(mode ?? "off", "off", StringComparison.OrdinalIgnoreCase);
            _gateEnabled = ns;
            _nsEnabled = ns;
            if (_denoiser != null) _denoiser.TransientGuard = ns;
            // Если включают шумодав, а RNNoise ещё не поднят (звонок начали с off) —
            // поднимаем его на лету.
            if (ns && _micStarted && _rnnoise == null)
            {
                try { _rnnoise = RnnoiseDenoiser.TryCreate(); } catch { }
            }
            if (_micStarted) { try { CreateApm(ns); } catch { } }
        }

        private volatile bool _gateEnabled;   // программный гейт ПОВЕРХ APM (aggressive)

        /// <summary>Мьют микрофона: трек остаётся опубликованным, кадры не шлём.</summary>
        public void SetMicMuted(bool muted) => _micMuted = muted;

        private bool _selfMicMuted, _selfDeafened;

        /// <summary>Транслировать своё голосовое состояние (микрофон/наушники)
        /// остальным через атрибуты "mic"/"deaf" — они рисуют значки на нашей плитке.</summary>
        public void PublishVoiceState(bool micMuted, bool deafened)
        {
            _selfMicMuted = micMuted; _selfDeafened = deafened;
            if (_localHandle == 0) return;
            try
            {
                var req = new SetLocalAttributesRequest { LocalParticipantHandle = _localHandle };
                req.Attributes.Add(new AttributesEntry { Key = "mic", Value = micMuted ? "0" : "1" });
                req.Attributes.Add(new AttributesEntry { Key = "deaf", Value = deafened ? "1" : "0" });
                LiveKitFfi.Request(new FfiRequest { SetLocalAttributes = req });
            }
            catch { }
        }

        /// <summary>Сменить устройство ввода на лету (перезапуск захвата).</summary>
        public void SetInputDeviceIndex(int index)
        {
            _inputDeviceIndex = index;
            if (!_micStarted || _micIn == null) return;
            try { _micIn.DataAvailable -= OnMicData; _micIn.StopRecording(); _micIn.Dispose(); } catch { }
            _micIn = null;
            try { StartMicCapture(); } catch (Exception ex) { ConnectError?.Invoke("микрофон: " + ex.Message); }
        }

        private void OnMicData(object sender, WaveInEventArgs e)
        {
            if (_micSource == 0 || _micMuted || e.BytesRecorded <= 0) return;

            // APM обрабатывает строго 10-мс кадры — копим и режем на куски по 960 байт.
            if (_apmEnabled && _apmHandle != 0)
            {
                int need = _micAccumLen + e.BytesRecorded;
                if (_micAccum.Length < need) Array.Resize(ref _micAccum, need);
                Buffer.BlockCopy(e.Buffer, 0, _micAccum, _micAccumLen, e.BytesRecorded);
                _micAccumLen = need;

                int off = 0;
                while (_micAccumLen - off >= ApmFrameBytes)
                {
                    ProcessAndSendFrame(_micAccum, off, ApmFrameBytes);
                    off += ApmFrameBytes;
                }
                // Остаток переносим в начало.
                int rem = _micAccumLen - off;
                if (rem > 0) Buffer.BlockCopy(_micAccum, off, _micAccum, 0, rem);
                _micAccumLen = rem;
                return;
            }

            // Резервный путь без APM: шумодав → отправка.
            if (_nsEnabled) DenoiseInPlace(e.Buffer, 0, e.BytesRecorded);
            SendCapturedAudio(e.Buffer, 0, e.BytesRecorded);
        }

        // Единый шумодав in-place. Если поднялся нативный RNNoise (как в тесте) —
        // используем ТОЛЬКО его (он давит и фон, и клавиатуру). Иначе — программный
        // откат: частотный spectral (фон) + клик-гейт (транзиенты).
        private void DenoiseInPlace(byte[] data, int offset, int len)
        {
            var rn = _rnnoise;
            if (rn != null && rn.IsReady)
            {
                try { rn.Process(data, offset, len); return; } catch { }
            }
            try { _spectral?.Process(data, offset, len); } catch { }
            try { _denoiser?.Process(data, offset, len); } catch { }
        }

        // Один 10-мс кадр: APM ProcessStream (шумодав/AEC/HPF/AGC) → отправка в FFI.
        private void ProcessAndSendFrame(byte[] data, int offset, int len)
        {
            if (_nsEnabled) DenoiseInPlace(data, offset, len);
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.Copy(data, offset, buf, len);
                try
                {
                    LiveKitFfi.Request(new FfiRequest
                    {
                        ApmProcessStream = new ApmProcessStreamRequest
                        {
                            ApmHandle = _apmHandle,
                            DataPtr = (ulong)buf.ToInt64(),
                            Size = (uint)len,
                            SampleRate = SR,
                            NumChannels = CH
                        }
                    });
                }
                catch { }
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
                            SamplesPerChannel = (uint)(len / 2)
                        }
                    }
                });
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void SendCapturedAudio(byte[] data, int offset, int len)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.Copy(data, offset, buf, len);
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
                            SamplesPerChannel = (uint)(len / 2)
                        }
                    }
                });
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // Дальний конец (референс эха): 10-мс i16-кадр из микшера воспроизведения.
        // Кормим ОБА эхоподавителя: микрофонный и звука демки.
        internal void ApmReverseFrame(byte[] pcm, int len)
        {
            ulong mic = (_apmEnabled ? _apmHandle : 0), scr = _scrApmHandle;
            if (mic == 0 && scr == 0) return;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.Copy(pcm, 0, buf, len);
                if (mic != 0)
                    try
                    {
                        LiveKitFfi.Request(new FfiRequest
                        {
                            ApmProcessReverseStream = new ApmProcessReverseStreamRequest
                            {
                                ApmHandle = mic,
                                DataPtr = (ulong)buf.ToInt64(),
                                Size = (uint)len,
                                SampleRate = SR,
                                NumChannels = CH
                            }
                        });
                    }
                    catch { }
                if (scr != 0)
                    try
                    {
                        LiveKitFfi.Request(new FfiRequest
                        {
                            ApmProcessReverseStream = new ApmProcessReverseStreamRequest
                            {
                                ApmHandle = scr,
                                DataPtr = (ulong)buf.ToInt64(),
                                Size = (uint)len,
                                SampleRate = SR,
                                NumChannels = CH
                            }
                        });
                    }
                    catch { }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ── Камера ────────────────────────────────────────────────────────
        // Разделено на превью (захват без публикации) и публикацию — чтобы окно
        // «Включить камеру» показывало картинку до того, как собеседники увидят трек.
        // moniker — DirectShow-идентификатор устройства; null/пусто → первая камера.
        public void StartCameraPreview(string moniker = null, int width = 1280, int height = 720)
        {
            if (_camStarted || _localHandle == 0) return;
            _camStarted = true;
            try
            {
                if (string.IsNullOrEmpty(moniker)) moniker = FirstCameraMoniker();
                if (string.IsNullOrEmpty(moniker)) { _camStarted = false; ConnectError?.Invoke("камера: устройство не найдено"); return; }

                // Создаём устройство и выбираем лучшее разрешение захвата (до 720p),
                // иначе камера часто отдаёт дефолтные 640×480.
                _camDevice = new VideoCaptureDevice(moniker);
                _camW = width; _camH = height;
                PickCameraResolution(_camDevice);

                var srcResp = LiveKitFfi.Request(new FfiRequest
                {
                    NewVideoSource = new NewVideoSourceRequest
                    {
                        Type = VideoSourceType.VideoSourceNative,
                        Resolution = new VideoSourceResolution { Width = (uint)_camW, Height = (uint)_camH },
                        IsScreencast = false
                    }
                });
                _camSource = srcResp.NewVideoSource.Source.Handle.Id;

                var trkResp = LiveKitFfi.Request(new FfiRequest
                {
                    CreateVideoTrack = new CreateVideoTrackRequest { Name = "camera", SourceHandle = _camSource }
                });
                _camTrack = trkResp.CreateVideoTrack.Track.Handle.Id;

                _camDevice.NewFrame += OnCameraFrame;
                _camDevice.Start();
            }
            catch (Exception ex) { ConnectError?.Invoke("камера: " + ex.Message); _camStarted = false; }
        }

        // Ставит устройству наилучшее разрешение с высотой ≤ 720 (баланс качество/
        // нагрузка); если таких нет — наименьшее. Обновляет _camW/_camH.
        private void PickCameraResolution(VideoCaptureDevice dev)
        {
            try
            {
                var caps = dev.VideoCapabilities;
                if (caps == null || caps.Length == 0) return;
                AForge.Video.DirectShow.VideoCapabilities best = null;
                foreach (var c in caps)
                    if (c.FrameSize.Height <= 720 &&
                        (best == null || (long)c.FrameSize.Width * c.FrameSize.Height >
                                          (long)best.FrameSize.Width * best.FrameSize.Height))
                        best = c;
                if (best == null)
                    foreach (var c in caps)
                        if (best == null || (long)c.FrameSize.Width * c.FrameSize.Height <
                                             (long)best.FrameSize.Width * best.FrameSize.Height)
                            best = c;
                if (best != null)
                {
                    dev.VideoResolution = best;
                    _camW = best.FrameSize.Width;
                    _camH = best.FrameSize.Height;
                }
            }
            catch { }
        }

        /// <summary>Публикация уже захватываемой камеры (после подтверждения превью).</summary>
        public void PublishCamera()
        {
            if (_camTrack == 0 || _camPublished || _localHandle == 0) return;
            try
            {
                var pubResp = LiveKitFfi.Request(new FfiRequest
                {
                    PublishTrack = new PublishTrackRequest
                    {
                        LocalParticipantHandle = _localHandle,
                        TrackHandle = _camTrack,
                        Options = new TrackPublishOptions
                        {
                            Source = TrackSource.SourceCamera,
                            VideoCodec = (VideoCodec)1,                              // H264
                            // Кодируем на выбранной в настройках видеокарте — тот же
                            // хинт (NVENC/HW/SW/auto), что и у демонстрации экрана.
                            VideoEncoder = (VideoEncoderBackend)MapGpu(DeviceSettings.GpuEncodePref),
                            DegradationPreference = (LiveKit.Proto.DegradationPreference)1, // MAINTAIN_FRAMERATE (лицо: плавность важнее)
                            VideoEncoding = new VideoEncoding
                            {
                                MaxBitrate = CamBitrateFor(_camH),
                                MaxFramerate = 30
                            }
                        }
                    }
                });
                _camPublishAsyncId = pubResp.PublishTrack.AsyncId;
                _camPublished = true;
            }
            catch (Exception ex) { ConnectError?.Invoke("камера: " + ex.Message); }
        }

        /// <summary>Захват + публикация одним вызовом (когда превью не нужно).</summary>
        public void StartCamera(string moniker = null, int width = 1280, int height = 720)
        {
            StartCameraPreview(moniker, width, height);
            PublishCamera();
        }

        /// <summary>Сменить устройство камеры на лету, не пересоздавая трек.</summary>
        public void SwitchCameraDevice(string moniker)
        {
            if (!_camStarted) { StartCameraPreview(moniker); return; }
            try { if (_camDevice != null) { _camDevice.NewFrame -= OnCameraFrame; _camDevice.SignalToStop(); } } catch { }
            _camDevice = null;
            try
            {
                if (string.IsNullOrEmpty(moniker)) moniker = FirstCameraMoniker();
                if (string.IsNullOrEmpty(moniker)) return;
                _camDevice = new VideoCaptureDevice(moniker);
                PickCameraResolution(_camDevice);
                _camDevice.NewFrame += OnCameraFrame;
                _camDevice.Start();
            }
            catch (Exception ex) { ConnectError?.Invoke("камера: " + ex.Message); }
        }

        public void StopCamera()
        {
            try
            {
                if (_camDevice != null)
                {
                    _camDevice.NewFrame -= OnCameraFrame;
                    _camDevice.SignalToStop();
                }
            }
            catch { }
            _camDevice = null;

            try
            {
                if (_camPublished && !string.IsNullOrEmpty(_camTrackSid) && _localHandle != 0)
                    LiveKitFfi.Request(new FfiRequest
                    {
                        UnpublishTrack = new UnpublishTrackRequest
                        {
                            LocalParticipantHandle = _localHandle,
                            TrackSid = _camTrackSid,
                            StopOnUnpublish = true
                        }
                    });
            }
            catch { }

            _camSource = 0; _camTrack = 0; _camTrackSid = null; _camStarted = false; _camPublished = false;
        }

        private void OnCameraFrame(object sender, NewFrameEventArgs e)
        {
            if (_camSource == 0) return;
            try { PushBitmap(_camSource, e.Frame, LocalCameraFrame != null ? EmitLocalCam : null); }
            catch { }
        }

        private void EmitLocalCam(byte[] bgra, int w, int h) => LocalCameraFrame?.Invoke(bgra, w, h);

        private static string FirstCameraMoniker()
        {
            try
            {
                var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (devices.Count > 0) return devices[0].MonikerString;
            }
            catch { }
            return null;
        }

        // ── Демонстрация экрана / окна ────────────────────────────────────
        // Настройки качества/кодека/энкодера — до старта демки.
        public void SetScreenCodec(string codec) { if (!string.IsNullOrWhiteSpace(codec)) _scrCodec = codec; }
        public void SetScreenEncoderPref(string gpu) { if (!string.IsNullOrWhiteSpace(gpu)) _scrGpuPref = gpu; }

        /// <summary>Сменить разрешение/FPS демки «на лету» (петля захвата подхватит).</summary>
        public void SetScreenQualityLive(int resHeight, int fps)
        {
            _scrTargetHeight = Math.Max(0, resHeight);
            if (fps > 0) _scrFps = Math.Max(1, Math.Min(60, fps));
        }

        /// <summary>Сменить ИСТОЧНИК демки на лету БЕЗ перепубликации трека: петля
        /// захвата читает _scrWindow/_scrBounds на каждом кадре, зрители видят
        /// мгновенное переключение без «стрим завершён/начался».</summary>
        private readonly object _scrSrcLock = new();   // источник читается из потока захвата

        public void SwitchShareToMonitor(Rectangle bounds)
        {
            if (!_scrStarted) return;
            lock (_scrSrcLock) { _scrWindow = IntPtr.Zero; _scrBounds = bounds; }
        }

        public void SwitchShareToWindow(IntPtr window)
        {
            if (!_scrStarted || window == IntPtr.Zero) return;
            lock (_scrSrcLock) { _scrWindow = window; }
        }

        public void StartScreenShare(Rectangle bounds, int fps = 15, int resHeight = 0, bool withAudio = false)
            => StartScreenInternal(bounds, IntPtr.Zero, fps, resHeight, withAudio);

        public void StartScreenShareWindow(IntPtr window, int fps = 15, int resHeight = 0, bool withAudio = false)
        {
            if (!GetWindowRect(window, out RECT r)) return;
            StartScreenInternal(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), window, fps, resHeight, withAudio);
        }

        private void StartScreenInternal(Rectangle bounds, IntPtr window, int fps, int resHeight, bool withAudio)
        {
            if (_scrStarted || _localHandle == 0) return;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            _scrStarted = true;
            _scrBounds = bounds;
            _scrWindow = window;
            _scrFps = Math.Max(1, Math.Min(60, fps));
            _scrTargetHeight = Math.Max(0, resHeight);

            // Публикуемое разрешение: даунскейл до целевой высоты (сохраняя пропорции),
            // иначе — родное. Чётные размеры (требование кодеков).
            int pubH = (_scrTargetHeight > 0 && _scrTargetHeight < bounds.Height) ? _scrTargetHeight : bounds.Height;
            int pubW = (int)Math.Round(bounds.Width * (pubH / (double)bounds.Height));
            pubW &= ~1; pubH &= ~1;
            if (pubW <= 0) pubW = 2; if (pubH <= 0) pubH = 2;

            try
            {
                var srcResp = LiveKitFfi.Request(new FfiRequest
                {
                    NewVideoSource = new NewVideoSourceRequest
                    {
                        Type = VideoSourceType.VideoSourceNative,
                        Resolution = new VideoSourceResolution { Width = (uint)pubW, Height = (uint)pubH },
                        IsScreencast = true
                    }
                });
                _scrSource = srcResp.NewVideoSource.Source.Handle.Id;

                var trkResp = LiveKitFfi.Request(new FfiRequest
                {
                    CreateVideoTrack = new CreateVideoTrackRequest { Name = "screen", SourceHandle = _scrSource }
                });
                _scrTrack = trkResp.CreateVideoTrack.Track.Handle.Id;

                // Опции публикации: кодек, hint энкодера (аппаратный), битрейт под
                // разрешение, и «держать разрешение» — для демки важнее чёткость
                // текста, чем плавность (при нехватке канала падает FPS, а не резкость).
                var opts = new TrackPublishOptions
                {
                    Source = TrackSource.SourceScreenshare,
                    VideoCodec = (VideoCodec)MapCodec(_scrCodec),
                    VideoEncoder = (VideoEncoderBackend)MapGpu(_scrGpuPref),
                    DegradationPreference = (LiveKit.Proto.DegradationPreference)2, // MAINTAIN_RESOLUTION
                    VideoEncoding = new VideoEncoding
                    {
                        MaxBitrate = BitrateFor(pubH),
                        MaxFramerate = _scrFps
                    }
                };

                var pubResp = LiveKitFfi.Request(new FfiRequest
                {
                    PublishTrack = new PublishTrackRequest
                    {
                        LocalParticipantHandle = _localHandle,
                        TrackHandle = _scrTrack,
                        Options = opts
                    }
                });
                _scrPublishAsyncId = pubResp.PublishTrack.AsyncId;

                _scrRun = true;
                _scrThread = new Thread(ScreenLoop) { IsBackground = true, Name = "pismo-screen-capture" };
                _scrThread.Start();

                // ВАЖНО: с UI-потока (STA) активация process-loopback дедлочится —
                // COM-колбэк ActivateAudioInterfaceAsync приходит на тот же STA-поток,
                // который заблокирован ожиданием → таймаут → откат на device-loopback
                // (а это эхо голосов в демке). Стартуем звук в фоновом MTA-потоке.
                if (withAudio)
                    new Thread(StartScreenAudio) { IsBackground = true, Name = "pismo-screen-audio-init" }.Start();
            }
            catch (Exception ex) { ConnectError?.Invoke("демонстрация: " + ex.Message); _scrStarted = false; }
        }

        // Захват системного звука (WASAPI-loopback устройства воспроизведения) и
        // публикация отдельным треком демки. EC/NS/AGC = OFF: звук игры/музыки
        // идёт как есть, шумодав его не режет. Голос микрофона в loopback НЕ
        // попадает (микрофон не выводится на колонки) — сам себя не задублируешь.
        private void StartScreenAudio()
        {
            try
            {
                // ПРАВИЛЬНОЕ решение эха: захватываем системный звук БЕЗ своего
                // процесса (process-loopback, exclude-self). Звук PISMO (голоса
                // звонка) физически НЕ попадает в захват → эхо невозможно в
                // принципе, без всякого AEC. Игра/музыка/браузер — попадают.
                // Активация в фоновом MTA-потоке (мы уже в отдельном потоке).
                bool useProc = false;
                try
                {
                    _procLoop = new ProcessLoopbackCapture(
                        System.Diagnostics.Process.GetCurrentProcess().Id, excludeTargetTree: true);
                    _procLoop.Start();
                    _scrCaptureRate = _procLoop.WaveFormat.SampleRate;   // 48000
                    _scrCaptureCh = _procLoop.WaveFormat.Channels;       // 2
                    _scrCaptureFloat = false;                            // i16 PCM
                    useProc = true;
                }
                catch
                {
                    try { _procLoop?.Dispose(); } catch { }
                    _procLoop = null;
                    // Фолбэк: device-loopback (весь звук). Здесь голоса звонка ТОЖЕ
                    // попадут — их гасит эхоподавитель ниже (моно-референс).
                    _loopback = new NAudio.Wave.WasapiLoopbackCapture();
                    _scrCaptureRate = _loopback.WaveFormat.SampleRate;
                    _scrCaptureCh = Math.Max(1, _loopback.WaveFormat.Channels);
                    _scrCaptureFloat = _loopback.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat;
                }
                // Публикуем/обрабатываем МОНО (единый формат для источника и AEC).
                _scrAudioCh = 1;
                _scrAudioRate = _scrCaptureRate;

                var srcResp = LiveKitFfi.Request(new FfiRequest
                {
                    NewAudioSource = new NewAudioSourceRequest
                    {
                        Type = AudioSourceType.AudioSourceNative,
                        SampleRate = (uint)_scrAudioRate,
                        NumChannels = (uint)_scrAudioCh,
                        QueueSizeMs = 100,
                        Options = new AudioSourceOptions
                        {
                            EchoCancellation = false,
                            NoiseSuppression = false,
                            AutoGainControl = false
                        }
                    }
                });
                _scrAudioSource = srcResp.NewAudioSource.Source.Handle.Id;

                // APM демки нужен ТОЛЬКО на фолбэке (device-loopback ловит голоса).
                // При process-loopback голосов в захвате нет — AEC не нужен.
                _scrApmFrameBytes = _scrAudioRate / 100 * _scrAudioCh * 2;
                _scrApmAccumLen = 0;
                _scrApmHandle = 0;
                if (!useProc && _scrAudioRate == SR)
                {
                    try
                    {
                        var scrApm = LiveKitFfi.Request(new FfiRequest
                        {
                            NewApm = new NewApmRequest
                            {
                                EchoCancellerEnabled = true,
                                GainControllerEnabled = false,
                                HighPassFilterEnabled = false,
                                NoiseSuppressionEnabled = false
                            }
                        });
                        _scrApmHandle = scrApm.NewApm.Apm.Handle.Id;
                        try { LiveKitFfi.Request(new FfiRequest { ApmSetStreamDelay = new ApmSetStreamDelayRequest { ApmHandle = _scrApmHandle, DelayMs = 140 } }); } catch { }
                    }
                    catch { _scrApmHandle = 0; }
                }

                var trkResp = LiveKitFfi.Request(new FfiRequest
                {
                    CreateAudioTrack = new CreateAudioTrackRequest { Name = "screen_audio", SourceHandle = _scrAudioSource }
                });
                _scrAudioTrack = trkResp.CreateAudioTrack.Track.Handle.Id;

                var pubResp = LiveKitFfi.Request(new FfiRequest
                {
                    PublishTrack = new PublishTrackRequest
                    {
                        LocalParticipantHandle = _localHandle,
                        TrackHandle = _scrAudioTrack,
                        Options = new TrackPublishOptions { Source = TrackSource.SourceScreenshareAudio }
                    }
                });
                _scrAudioPublishAsyncId = pubResp.PublishTrack.AsyncId;

                if (_procLoop != null)
                {
                    _procLoop.DataAvailable += OnScreenAudioData;
                    ArmScreenAudioWatchdog();   // если данных нет за 2с — откат на device
                }
                else
                {
                    _loopback.DataAvailable += OnScreenAudioData;
                    _loopback.StartRecording();
                }
            }
            catch (Exception ex) { ConnectError?.Invoke("звук демки: " + ex.Message); }
        }

        // Сторож: process-loopback может «успешно» стартовать и молча не давать
        // данных (COM-путь зависит от сборки Windows). Если за 2 с не пришло ни
        // одного кадра — пересоздаём захват через обычный device-loopback.
        private System.Threading.Timer _scrAudioWatchdog;
        private volatile bool _scrAudioGotData;

        // Эхоподавление ЗВУКА ДЕМКИ: свой APM (EC on) с тем же референсом
        // воспроизведения, что и у микрофона. Убирает голоса собеседников из
        // демки, даже если сработал откат на device-loopback (захват всего
        // системного звука, включая воспроизведение PISMO).
        private ulong _scrApmHandle;
        private byte[] _scrApmAccum = Array.Empty<byte>();
        private int _scrApmAccumLen;
        private int _scrApmFrameBytes;   // 10 мс = rate/100 * ch * 2

        private void ArmScreenAudioWatchdog()
        {
            _scrAudioGotData = false;
            _scrAudioWatchdog?.Dispose();
            _scrAudioWatchdog = new System.Threading.Timer(_ =>
            {
                _scrAudioWatchdog?.Dispose(); _scrAudioWatchdog = null;
                if (_scrAudioGotData || _procLoop == null || _scrAudioSource == 0) return;
                try
                {
                    try { _procLoop.DataAvailable -= OnScreenAudioData; _procLoop.Dispose(); } catch { }
                    _procLoop = null;
                    _loopback = new NAudio.Wave.WasapiLoopbackCapture();
                    // Захват меняем на устройство, но публикуем ПО-ПРЕЖНЕМУ моно под
                    // тем же источником (OnScreenAudioData даунмиксит любой формат).
                    _scrCaptureRate = _loopback.WaveFormat.SampleRate;
                    _scrCaptureCh = Math.Max(1, _loopback.WaveFormat.Channels);
                    _scrCaptureFloat = _loopback.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat;
                    _loopback.DataAvailable += OnScreenAudioData;
                    _loopback.StartRecording();
                }
                catch { }
            }, null, 2000, System.Threading.Timeout.Infinite);
        }

        // Кадр системного звука → i16 PCM → CaptureAudioFrame. Источник даёт либо
        // 32-бит float (device loopback), либо уже i16 (process loopback).
        private void OnScreenAudioData(object sender, WaveInEventArgs e)
        {
            if (_scrAudioSource == 0 || e.BytesRecorded <= 0) return;
            _scrAudioGotData = true;   // сторож: захват жив, откат не нужен
            int ch = _scrCaptureCh;
            int bytesPerSample = _scrCaptureFloat ? 4 : 2;
            int frames = e.BytesRecorded / (bytesPerSample * ch);   // сэмплов на канал
            if (frames <= 0) return;

            // Даунмикс в МОНО (среднее по каналам) → i16.
            short[] pcm = new short[frames];
            for (int f = 0; f < frames; f++)
            {
                float acc = 0f;
                int baseIdx = f * ch * bytesPerSample;
                for (int c = 0; c < ch; c++)
                {
                    int idx = baseIdx + c * bytesPerSample;
                    float v = _scrCaptureFloat
                        ? BitConverter.ToSingle(e.Buffer, idx)
                        : (short)(e.Buffer[idx] | (e.Buffer[idx + 1] << 8)) / 32768f;
                    acc += v;
                }
                acc /= ch;
                if (acc > 1f) acc = 1f; else if (acc < -1f) acc = -1f;
                pcm[f] = (short)(acc * 32767f);
            }
            int n = frames;

            // Через APM (эхоподавление) — строго 10-мс кусками; без APM — сразу.
            if (_scrApmHandle != 0 && _scrApmFrameBytes > 0)
            {
                int add = n * 2;
                if (_scrApmAccum.Length < _scrApmAccumLen + add)
                    Array.Resize(ref _scrApmAccum, _scrApmAccumLen + add);
                Buffer.BlockCopy(pcm, 0, _scrApmAccum, _scrApmAccumLen, add);
                _scrApmAccumLen += add;
                int off = 0;
                while (_scrApmAccumLen - off >= _scrApmFrameBytes)
                {
                    SendScreenAudioChunk(_scrApmAccum, off, _scrApmFrameBytes, true);
                    off += _scrApmFrameBytes;
                }
                int rem = _scrApmAccumLen - off;
                if (rem > 0) Buffer.BlockCopy(_scrApmAccum, off, _scrApmAccum, 0, rem);
                _scrApmAccumLen = rem;
                return;
            }

            var raw = new byte[n * 2];
            Buffer.BlockCopy(pcm, 0, raw, 0, raw.Length);
            SendScreenAudioChunk(raw, 0, raw.Length, false);
        }

        private void SendScreenAudioChunk(byte[] data, int offset, int len, bool viaApm)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.Copy(data, offset, buf, len);
                if (viaApm)
                {
                    try
                    {
                        LiveKitFfi.Request(new FfiRequest
                        {
                            ApmProcessStream = new ApmProcessStreamRequest
                            {
                                ApmHandle = _scrApmHandle,
                                DataPtr = (ulong)buf.ToInt64(),
                                Size = (uint)len,
                                SampleRate = (uint)_scrAudioRate,
                                NumChannels = (uint)_scrAudioCh
                            }
                        });
                    }
                    catch { }
                }
                LiveKitFfi.Request(new FfiRequest
                {
                    CaptureAudioFrame = new CaptureAudioFrameRequest
                    {
                        SourceHandle = _scrAudioSource,
                        Buffer = new AudioFrameBufferInfo
                        {
                            DataPtr = (ulong)buf.ToInt64(),
                            NumChannels = (uint)_scrAudioCh,
                            SampleRate = (uint)_scrAudioRate,
                            SamplesPerChannel = (uint)(len / 2 / _scrAudioCh)
                        }
                    }
                });
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void StopScreenAudio()
        {
            try { _scrAudioWatchdog?.Dispose(); } catch { }
            _scrAudioWatchdog = null;
            if (_scrApmHandle != 0) { try { LiveKitFfi.DropHandle(_scrApmHandle); } catch { } _scrApmHandle = 0; }
            _scrApmAccumLen = 0;
            try { if (_procLoop != null) { _procLoop.DataAvailable -= OnScreenAudioData; _procLoop.Dispose(); } } catch { }
            _procLoop = null;
            try { if (_loopback != null) { _loopback.DataAvailable -= OnScreenAudioData; _loopback.StopRecording(); _loopback.Dispose(); } } catch { }
            _loopback = null;
            try
            {
                if (!string.IsNullOrEmpty(_scrAudioTrackSid) && _localHandle != 0)
                    LiveKitFfi.Request(new FfiRequest
                    {
                        UnpublishTrack = new UnpublishTrackRequest
                        {
                            LocalParticipantHandle = _localHandle,
                            TrackSid = _scrAudioTrackSid,
                            StopOnUnpublish = true
                        }
                    });
            }
            catch { }
            _scrAudioSource = 0; _scrAudioTrack = 0; _scrAudioTrackSid = null;
        }

        public void StopScreenShare()
        {
            StopScreenAudio();
            _scrRun = false;
            try { _scrThread?.Join(500); } catch { }
            _scrThread = null;

            try
            {
                if (!string.IsNullOrEmpty(_scrTrackSid) && _localHandle != 0)
                    LiveKitFfi.Request(new FfiRequest
                    {
                        UnpublishTrack = new UnpublishTrackRequest
                        {
                            LocalParticipantHandle = _localHandle,
                            TrackSid = _scrTrackSid,
                            StopOnUnpublish = true
                        }
                    });
            }
            catch { }

            _scrSource = 0; _scrTrack = 0; _scrTrackSid = null; _scrWindow = IntPtr.Zero; _scrStarted = false;
        }

        // Переиспользуемые буферы захвата: new Bitmap 1080p+ на КАЖДЫЙ кадр (а при
        // даунскейле — два) съедал бюджет кадра и просаживал реальный FPS до 10-15
        // при выставленных 60. Плюс HighQualityBilinear ~20-40мс/кадр → Bilinear.
        private Bitmap _capBmp, _scaledBmp;
        private int _scrConsecFails;   // подряд неудачных кадров (самовосстановление)
        private int _capFrameCount;   // реально ОТПРАВЛЕНО (принял пушер/кодер)
        private int _grabCount;       // реально ЗАХВАЧЕНО (успешный кадр из экрана)
        private long _capFpsAt;
        private string _capMode = "GDI";   // "DXGI" или "GDI" — реальный путь захвата

        private void ReportCaptureFps(int w, int h)
        {
            if (ScreenCaptureStats == null) return;
            long now = _clock.ElapsedMilliseconds;
            if (_capFpsAt == 0) { _capFpsAt = now; return; }
            if (now - _capFpsAt < 1000) return;
            double dt = (now - _capFpsAt) / 1000.0;
            int sent = (int)Math.Round(_capFrameCount / dt);
            int grab = (int)Math.Round(_grabCount / dt);
            _capFrameCount = 0; _grabCount = 0; _capFpsAt = now;
            // отпр/цель · захват · режим — сразу видно, что упирается (кодер/захват/настройка)
            string mode = _capMode == "GDI" && _dxgiErr != null
                ? "GDI (DXGI: " + Trunc(_dxgiErr, 40) + ")" : _capMode;
            try { ScreenCaptureStats.Invoke($"{sent}/{_scrFps} fps · захват {grab} · {mode} {w}x{h}"); } catch { }
        }

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));

        private void ScreenLoop()
        {
            // КРИТИЧНО для 60fps: гранулярность Thread.Sleep по умолчанию ~15.6мс —
            // «доспать 5мс» реально спит 15 → период кадра ~25-30мс → ~35-40fps при
            // выставленных 60. Повышаем разрешение системного таймера до 1мс на
            // время демки (как делают все игры/стримеры).
            try { timeBeginPeriod(1); } catch { }
            // Ресурсы не жалеем: демка важнее фоновых задач ПК.
            try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { }
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var oldPrio = System.Diagnostics.ProcessPriorityClass.Normal;
            bool prioRaised = false;
            try { oldPrio = proc.PriorityClass; proc.PriorityClass = System.Diagnostics.ProcessPriorityClass.High; prioRaised = true; }
            catch { }

            _pushSignal = new AutoResetEvent(false);
            _pushBusy = 0;
            _pushThread = new Thread(PushLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "pismo-screen-push" };
            _pushThread.Start();

            long next = _clock.ElapsedMilliseconds;
            while (_scrRun)
            {
                int delayMs = Math.Max(1, 1000 / Math.Max(1, _scrFps));   // FPS можно менять на лету
                next += delayMs;   // абсолютный дедлайн кадра — без накопления дрейфа
                try
                {
                    // Превью — на полном FPS: GPU-путь (сырой BGRA → WritePixels)
                    // дешёвый, а прореживание делало превью дёрганым (fps/3).
                    var preview = LocalScreenFrame != null
                        ? EmitLocalScreen : (Action<byte[], int, int>)null;

                    IntPtr win; Rectangle bounds;
                    lock (_scrSrcLock) { win = _scrWindow; bounds = _scrBounds; }

                    if (win == IntPtr.Zero && bounds.Width > 0 && bounds.Height > 0)
                    {
                        // МОНИТОР — быстрый путь: DXGI (кадры от GPU) → GDI-блит в
                        // DIB-секцию. DXGI с чёрными кадрами (Optimus) сам уходит на GDI.
                        int th = _scrTargetHeight;
                        int outW = bounds.Width & ~1, outH = bounds.Height & ~1;
                        if (th > 0 && bounds.Height > th)
                        {
                            outH = th & ~1;
                            outW = Math.Max(2, (int)Math.Round(bounds.Width * (outH / (double)bounds.Height))) & ~1;
                        }
                        var d = _dibs[_dibIdx];
                        // Захват монитора — только GDI (BitBlt с экрана). DXGI на
                        // Optimus-ноутбуках отдаёт ЧЁРНЫЕ кадры (рабочий стол ведёт
                        // Intel, дублируем с NVIDIA), а GDI берёт реальные пиксели.
                        // FPS чуть ниже 60, зато картинка есть всегда.
                        if (CaptureMonitorDib(d, bounds, outW, outH))
                        {
                            _scrConsecFails = 0;
                            _grabCount++;
                            if (Interlocked.CompareExchange(ref _pushBusy, 1, 0) == 0)
                            {
                                _dibIdx ^= 1;
                                _pushBuf = d; _pushW = outW; _pushH = outH; _pushPreview = preview;
                                _pushSignal.Set();
                                _capFrameCount++;
                            }
                            ReportCaptureFps(outW, outH);
                        }
                        else if (++_scrConsecFails >= 20) { _scrConsecFails = 0; if (System.Threading.Volatile.Read(ref _pushBusy) == 0) FreeDib(); }
                    }
                    else if (win != IntPtr.Zero)
                    {
                        // ОКНО — PrintWindow: захватывает ИМЕННО это окно (даже
                        // перекрытое). Для GPU-ускоренных окон (Discord/игры) кадр
                        // может быть статичным — тогда делись всем экраном (монитор).
                        Bitmap raw = CaptureScreen();
                        if (raw != null)
                        {
                            _scrConsecFails = 0;
                            int th = _scrTargetHeight;
                            if (th > 0 && raw.Height > th)
                            {
                                int tw = Math.Max(2, (int)Math.Round(raw.Width * (th / (double)raw.Height))) & ~1;
                                if (_scaledBmp == null || _scaledBmp.Width != tw || _scaledBmp.Height != th)
                                {
                                    _scaledBmp?.Dispose();
                                    _scaledBmp = new Bitmap(tw, Math.Max(2, th), PixelFormat.Format32bppArgb);
                                }
                                using (var g = Graphics.FromImage(_scaledBmp))
                                {
                                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                                    g.DrawImage(raw, new Rectangle(0, 0, _scaledBmp.Width, _scaledBmp.Height),
                                                0, 0, raw.Width, raw.Height, GraphicsUnit.Pixel);
                                }
                                PushBitmap(_scrSource, _scaledBmp, preview);
                            }
                            else
                            {
                                PushBitmap(_scrSource, raw, preview);
                            }
                        }
                    }
                }
                catch
                {
                    // Самовосстановление: если кадры падают ПОДРЯД (например, Bitmap
                    // остался залоченным после сбоя LockBits/UnlockBits — тогда КАЖДЫЙ
                    // следующий LockBits кидает исключение, и у зрителей «зависает»
                    // последний удачный кадр) — пересоздаём переиспользуемые буферы.
                    if (++_scrConsecFails >= 20)
                    {
                        _scrConsecFails = 0;
                        try { _capBmp?.Dispose(); } catch { }
                        try { _scaledBmp?.Dispose(); } catch { }
                        _capBmp = null; _scaledBmp = null;
                        if (System.Threading.Volatile.Read(ref _pushBusy) == 0) FreeDib();
                    }
                }
                // Пейсинг с суб-мс точностью: спим до дедлайна минус ~1.5мс, остаток
                // добираем спином (жрёт ядро, зато кадры идут ровно по метроному).
                long now = _clock.ElapsedMilliseconds;
                int sleep = (int)(next - now);
                if (sleep > 2) Thread.Sleep(sleep - 2);
                while (_scrRun && _clock.ElapsedMilliseconds < next) Thread.SpinWait(120);
                if (_clock.ElapsedMilliseconds - next > delayMs) next = _clock.ElapsedMilliseconds;
            }
            try { timeEndPeriod(1); } catch { }
            if (prioRaised) { try { proc.PriorityClass = oldPrio; } catch { } }
            try { _pushSignal?.Set(); _pushThread?.Join(500); } catch { }
            try { _pushSignal?.Dispose(); } catch { }
            _pushSignal = null; _pushThread = null; _pushBuf = null; _pushBusy = 0;
            try { _capBmp?.Dispose(); } catch { }
            try { _scaledBmp?.Dispose(); } catch { }
            _capBmp = null; _scaledBmp = null;
            FreeDib();
            try { _dxgi?.Dispose(); } catch { }
            _dxgi = null; _dxgiFailed = false; _dxgiBlackRun = 0;   // следующая демка снова попробует DXGI
            if (_screenDc != IntPtr.Zero) { try { ReleaseDC(IntPtr.Zero, _screenDc); } catch { } _screenDc = IntPtr.Zero; }
        }

        // ── DIB-захват монитора (двойная буферизация: захват ∥ отправка) ──
        private sealed class DibBuf
        {
            public IntPtr Dc, Bmp, Old, Bits;
            public int W, H;
        }

        private IntPtr _screenDc;
        private readonly DibBuf[] _dibs = { new DibBuf(), new DibBuf() };
        private int _dibIdx;

        // Конвейер: пока пушер отдаёт кадр в FFI (конвертация BGRA→I420 внутри),
        // поток захвата уже блитит следующий кадр во второй буфер.
        private AutoResetEvent _pushSignal;
        private Thread _pushThread;
        private DibBuf _pushBuf;
        private int _pushW, _pushH;
        private Action<byte[], int, int> _pushPreview;
        private int _pushBusy;   // 0 = свободен, 1 = отдаёт кадр

        private bool EnsureDibBuf(DibBuf d, int outW, int outH)
        {
            if (outW <= 0 || outH <= 0) return false;
            if (_screenDc == IntPtr.Zero) _screenDc = GetDC(IntPtr.Zero);
            if (_screenDc == IntPtr.Zero) return false;
            if (d.Dc != IntPtr.Zero && d.W == outW && d.H == outH) return true;

            FreeDib(d);
            d.Dc = CreateCompatibleDC(_screenDc);
            if (d.Dc == IntPtr.Zero) return false;
            var bmi = new BITMAPINFO
            {
                biSize = 40,
                biWidth = outW,
                biHeight = -outH,   // top-down: строки сверху вниз, как ждёт BGRA-кадр
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0   // BI_RGB
            };
            d.Bmp = CreateDIBSection(d.Dc, ref bmi, 0 /*DIB_RGB_COLORS*/, out d.Bits, IntPtr.Zero, 0);
            if (d.Bmp == IntPtr.Zero || d.Bits == IntPtr.Zero) { FreeDib(d); return false; }
            d.Old = SelectObject(d.Dc, d.Bmp);
            SetStretchBltMode(d.Dc, HALFTONE);   // качественный даунскейл
            d.W = outW; d.H = outH;
            return true;
        }

        private bool CaptureMonitorDib(DibBuf d, Rectangle src, int outW, int outH)
        {
            if (!EnsureDibBuf(d, outW, outH)) return false;
            _capMode = "GDI";
            return (outW == src.Width && outH == src.Height)
                ? BitBlt(d.Dc, 0, 0, outW, outH, _screenDc, src.X, src.Y, SRCCOPY)
                : StretchBlt(d.Dc, 0, 0, outW, outH, _screenDc, src.X, src.Y, src.Width, src.Height, SRCCOPY);
        }

        // ── DXGI Desktop Duplication: кадры от видеодрайвера (GPU) ────────
        private DxgiDuplicator _dxgi;
        private Rectangle _dxgiBounds;
        private bool _dxgiFailed;   // DXGI недоступен → навсегда GDI-путь (до рестарта демки)
        private string _dxgiErr;    // причина отказа DXGI (для плашки диагностики)
        private int _dxgiBlackRun;  // подряд чёрных кадров DXGI

        // Проверка «кадр не полностью чёрный»: на Optimus-ноутбуках Desktop
        // Duplication нередко «успешно» отдаёт ЧЁРНЫЕ кадры (рабочий стол ведёт
        // Intel, дублируем с NVIDIA). Сэмплируем буфер и, если подряд идёт много
        // чёрных кадров, — отключаем DXGI и уходим на GDI (тот захватывает реально).
        private static bool BufferHasContent(IntPtr buf, int lenBytes)
        {
            if (buf == IntPtr.Zero || lenBytes < 4) return false;
            // Сэмплируем ~каждые 2000 байт (без unsafe). «Есть картинка» = хотя бы
            // ~1% сэмплов не чёрные (одиночный яркий пиксель не должен «спасать»
            // чёрный DXGI-кадр — иначе плитка чёрная, а откат на GDI не срабатывает).
            int total = 0, nonBlack = 0;
            for (int i = 0; i + 2 < lenBytes; i += 2000)
            {
                total++;
                if (Marshal.ReadByte(buf, i) > 12 || Marshal.ReadByte(buf, i + 1) > 12 || Marshal.ReadByte(buf, i + 2) > 12)
                    nonBlack++;
            }
            return total > 0 && nonBlack * 100 >= total;   // ≥1% непустых
        }

        /// <summary>Кадр монитора через DXGI в DIB-буфер d (масштабирование через
        /// StretchDIBits при необходимости). false = кадр не получить (откат на GDI).</summary>
        private bool CaptureMonitorDxgi(DibBuf d, Rectangle src, int outW, int outH)
        {
            if (_dxgiFailed) return false;
            if (_dxgi == null || _dxgiBounds != src)
            {
                try { _dxgi?.Dispose(); _dxgi = new DxgiDuplicator(src); _dxgiBounds = src; }
                catch (Exception ex) { _dxgi?.Dispose(); _dxgi = null; _dxgiFailed = true; _dxgiErr = ex.Message; return false; }
            }
            try
            {
                _dxgi.TryAcquireFrame(8);   // false = экран не менялся, Buffer хранит прошлый кадр
                if (!_dxgi.HasFrame) return false;
            }
            catch
            {
                // ACCESS_LOST (смена режима/monitor off) — пересоздадим на следующем кадре.
                try { _dxgi.Dispose(); } catch { }
                _dxgi = null;
                return false;
            }

            // Детект чёрных кадров DXGI (Optimus): проверяем КАЖДЫЙ кадр. Если
            // подряд идёт ≥8 чёрных — Desktop Duplication на этой машине не отдаёт
            // картинку (рабочий стол ведёт Intel), навсегда уходим на GDI.
            if (BufferHasContent(_dxgi.Buffer, _dxgi.Width * _dxgi.Height * 4))
            {
                _dxgiBlackRun = 0;
            }
            else if (++_dxgiBlackRun >= 8)
            {
                _dxgiFailed = true;
                _dxgiErr = "чёрные кадры → GDI";
                try { _dxgi.Dispose(); } catch { }
                _dxgi = null;
                return false;
            }

            if (!EnsureDibBuf(d, outW, outH)) return false;
            var bmi = new BITMAPINFO
            {
                biSize = 40,
                biWidth = _dxgi.Width,
                biHeight = -_dxgi.Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };
            bool ok = StretchDIBits(d.Dc, 0, 0, outW, outH, 0, 0, _dxgi.Width, _dxgi.Height,
                                 _dxgi.Buffer, ref bmi, 0, SRCCOPY) > 0;
            if (ok) _capMode = "DXGI";
            return ok;
        }

        private static void FreeDib(DibBuf d)
        {
            try
            {
                if (d.Dc != IntPtr.Zero && d.Old != IntPtr.Zero) SelectObject(d.Dc, d.Old);
                if (d.Bmp != IntPtr.Zero) DeleteObject(d.Bmp);
                if (d.Dc != IntPtr.Zero) DeleteDC(d.Dc);
            }
            catch { }
            d.Dc = IntPtr.Zero; d.Bmp = IntPtr.Zero; d.Old = IntPtr.Zero; d.Bits = IntPtr.Zero;
            d.W = 0; d.H = 0;
        }

        private void FreeDib() { FreeDib(_dibs[0]); FreeDib(_dibs[1]); }

        // Пушер: отдаёт готовый DIB-кадр в FFI и превью, пока захват блитит следующий.
        private void PushLoop()
        {
            while (_scrRun)
            {
                try { _pushSignal.WaitOne(200); } catch { break; }
                if (!_scrRun) break;
                if (System.Threading.Volatile.Read(ref _pushBusy) == 0) continue;
                var d = _pushBuf;
                if (d != null && d.Bits != IntPtr.Zero)
                    try { PushRawPtr(_scrSource, d.Bits, _pushW, _pushH, _pushPreview); } catch { }
                Interlocked.Exchange(ref _pushBusy, 0);
            }
        }

        // Кадр из DIB-памяти → FFI (без каких-либо промежуточных копий) + превью.
        private void PushRawPtr(ulong source, IntPtr bits, int w, int h, Action<byte[], int, int> preview)
        {
            if (source == 0 || bits == IntPtr.Zero) return;
            LiveKitFfi.Request(new FfiRequest
            {
                CaptureVideoFrame = new CaptureVideoFrameRequest
                {
                    SourceHandle = source,
                    TimestampUs = NowUs(),
                    Rotation = (VideoRotation)0,
                    Buffer = new VideoBufferInfo
                    {
                        Type = VideoBufferType.Bgra,
                        Width = (uint)w,
                        Height = (uint)h,
                        DataPtr = (ulong)bits.ToInt64(),
                        Stride = (uint)(w * 4)
                    }
                }
            });
            if (preview != null)
            {
                var buf = RentPreviewBuf(w * h * 4);   // пул вместо аллокации на кадр
                Marshal.Copy(bits, buf, 0, buf.Length);
                preview(buf, w, h);
            }
        }

        // Пул буферов превью (3 по кругу): UI успевает срисовать кадр до того,
        // как буфер переиспользуется через 2 кадра.
        private readonly byte[][] _prevPool = new byte[3][];
        private int _prevPoolIdx;

        private byte[] RentPreviewBuf(int size)
        {
            int i = (_prevPoolIdx = (_prevPoolIdx + 1) % 3);
            var b = _prevPool[i];
            if (b == null || b.Length != size) _prevPool[i] = b = new byte[size];
            return b;
        }

        // ── Качество/кодек/энкодер демки ──────────────────────────────────
        // Кодек демки → LiveKit VideoCodec (VP8=0, H264=1, AV1=2, VP9=3, H265=4).
        // HEVC-энкодера в FFI-libwebrtc обычно нет → h265 отдаём как H264.
        private static int MapCodec(string codec) => (codec ?? "").ToLowerInvariant() switch
        {
            "vp8" => 0,
            "vp9" => 3,
            "av1" => 2,
            "h265" or "hevc" => 1,   // HEVC-энкодер недоступен → безопасный откат на H264
            _ => 1,                  // h264 по умолчанию
        };

        // Предпочтение GPU → VideoEncoderBackend (AUTO=0, SOFTWARE=1, HARDWARE=2,
        // NVENC=3, VAAPI=4, VIDEOTOOLBOX=5). Это ХИНТ: реально задействуется только
        // если в этой сборке libwebrtc есть соответствующий аппаратный энкодер.
        private static int MapGpu(string gpu) => (gpu ?? "").ToLowerInvariant() switch
        {
            "high" => 3,          // дискретная NVIDIA → NVENC
            "integrated" => 2,    // встроенная → HARDWARE (обобщённо)
            "software" => 1,      // принудительно программный энкодер (CPU)
            _ => 0,               // auto
        };

        // Битрейт камеры под высоту (лицо/движение — умереннее, чем демка-текст).
        private static ulong CamBitrateFor(int height)
        {
            if (height >= 1080) return 4_000_000;
            if (height >= 720) return 2_500_000;
            if (height >= 480) return 1_200_000;
            return 1_500_000;
        }

        // Потолок битрейта под высоту (демка = много деталей/текста, не жалеем канал).
        private static ulong BitrateFor(int height)
        {
            if (height >= 1440) return 25_000_000;
            if (height >= 1080) return 15_000_000;
            if (height >= 720) return 8_000_000;
            if (height >= 480) return 4_000_000;
            if (height >= 360) return 2_500_000;
            return 10_000_000;   // неизвестно/родное большое — щедро
        }

        private void EmitLocalScreen(byte[] bgra, int w, int h) => LocalScreenFrame?.Invoke(bgra, w, h);

        // Возвращает переиспользуемый _capBmp (пересоздаётся только при смене размера).
        private Bitmap CaptureScreen()
        {
            // Снимок источника под замком: UI-поток может сменить его на лету
            // (кнопка 🔁), а Rectangle — не атомарен (рваное чтение = мусорные
            // размеры → исключения на каждом кадре).
            IntPtr win; Rectangle bounds;
            lock (_scrSrcLock) { win = _scrWindow; bounds = _scrBounds; }

            int w, h;
            if (win != IntPtr.Zero)
            {
                if (!GetWindowRect(win, out RECT r)) return null;
                w = r.Right - r.Left; h = r.Bottom - r.Top;
            }
            else { w = bounds.Width; h = bounds.Height; }
            if (w <= 0 || h <= 0) return null;

            if (_capBmp == null || _capBmp.Width != w || _capBmp.Height != h)
            {
                _capBmp?.Dispose();
                _capBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            }

            using var g = Graphics.FromImage(_capBmp);
            if (win != IntPtr.Zero)
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(win, hdc, PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            else
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            }
            return _capBmp;
        }

        // Кадр Bitmap → CaptureVideoFrame (BGRA packed). Кадр читается синхронно во
        // время Request, поэтому передаём Scan0 напрямую (без лишней копии), а
        // локальное превью копируем отдельно только при наличии подписчиков.
        private void PushBitmap(ulong source, Bitmap bmp, Action<byte[], int, int> preview)
        {
            if (source == 0 || bmp == null) return;
            int fullW = bmp.Width, fullH = bmp.Height;
            int w = fullW & ~1, h = fullH & ~1;   // libwebrtc требует чётные размеры
            if (w <= 0 || h <= 0) return;

            byte[] previewBuf = null;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, fullW, fullH),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                LiveKitFfi.Request(new FfiRequest
                {
                    CaptureVideoFrame = new CaptureVideoFrameRequest
                    {
                        SourceHandle = source,
                        TimestampUs = NowUs(),
                        Rotation = (VideoRotation)0,   // VIDEO_ROTATION_0 (имя члена protobuf мангли́т цифру)
                        Buffer = new VideoBufferInfo
                        {
                            Type = VideoBufferType.Bgra,
                            Width = (uint)w,
                            Height = (uint)h,
                            DataPtr = (ulong)data.Scan0.ToInt64(),
                            Stride = (uint)data.Stride
                        }
                    }
                });

                if (preview != null)
                {
                    int stride = data.Stride;
                    previewBuf = RentPreviewBuf(w * h * 4);
                    if (stride == w * 4)
                        Marshal.Copy(data.Scan0, previewBuf, 0, previewBuf.Length);
                    else
                        for (int y = 0; y < h; y++)
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), previewBuf, y * w * 4, w * 4);
                }
            }
            finally { bmp.UnlockBits(data); }

            if (previewBuf != null) preview(previewBuf, w, h);
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
                    case FfiEvent.MessageOneofCase.VideoStreamEvent: HandleVideoStream(ev.VideoStreamEvent); break;
                    case FfiEvent.MessageOneofCase.PublishTrack: HandlePublishTrack(ev.PublishTrack); break;
                    case FfiEvent.MessageOneofCase.GetSessionStats: HandleSessionStats(ev.GetSessionStats); break;
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
                EmitVoiceAttrsFromMap(info.Identity, info.Attributes);
                foreach (var pub in pwt.Publications)   // запоминаем источник по sid
                    RememberSource(pub.Info.Sid, pub.Info.Source);
            }

            // КРИТИЧНО: сообщаем FFI, что готовы принимать события комнаты. Без
            // этого FFI буферизует ParticipantConnected/TrackSubscribed — и они
            // теряются: не приходит входящее медиа (звук/камера/демка) и участники
            // появляются «через раз». Слать ПОСЛЕ обработки начального состояния.
            try { LiveKitFfi.Request(new FfiRequest { ReadyForRoomEvent = new ReadyForRoomEventRequest { RoomHandle = _roomHandle } }); }
            catch { }

            // Публикуем своё голосовое состояние, если мьют/наушники включили до подключения.
            if (_selfMicMuted || _selfDeafened) PublishVoiceState(_selfMicMuted, _selfDeafened);

            // Реальный пинг (RTT медиа-канала) — опрашиваем статистику каждые 2 с.
            // ICMP до сервера бесполезен под VPN (сервер = точка выхода туннеля → 0 мс).
            try
            {
                _statsTimer?.Dispose();
                _statsTimer = new System.Threading.Timer(_ => RequestSessionStats(), null, 1000, 2000);
            }
            catch { }
        }

        private void RequestSessionStats()
        {
            if (_roomHandle == 0 || _disposed) return;
            try
            {
                var resp = LiveKitFfi.Request(new FfiRequest
                {
                    GetSessionStats = new GetSessionStatsRequest { RoomHandle = _roomHandle }
                });
                _statsAsyncId = resp.GetSessionStats.AsyncId;
            }
            catch { }
        }

        private void HandleSessionStats(GetSessionStatsCallback cb)
        {
            if (cb.AsyncId != _statsAsyncId) return;
            if (cb.MessageCase != GetSessionStatsCallback.MessageOneofCase.Result) return;
            double rttSec = -1;
            foreach (var s in cb.Result.SubscriberStats) rttSec = PickRtt(s, rttSec);
            if (rttSec < 0) foreach (var s in cb.Result.PublisherStats) rttSec = PickRtt(s, rttSec);
            if (rttSec >= 0) RttUpdated?.Invoke((int)Math.Round(rttSec * 1000));
        }

        // Берём RTT из НОМИНИРОВАННОЙ пары кандидатов (реально используемый маршрут).
        private static double PickRtt(RtcStats s, double cur)
        {
            if (s.StatsCase != RtcStats.StatsOneofCase.CandidatePair) return cur;
            var cp = s.CandidatePair.CandidatePair_;
            if (cp.Nominated && cp.CurrentRoundTripTime >= 0) return cp.CurrentRoundTripTime;
            return cur;
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
                    EmitVoiceAttrsFromMap(info.Identity, info.Attributes);
                    // Новый участник — заново публикуем своё состояние, чтобы он увидел значки.
                    if (_selfMicMuted || _selfDeafened) PublishVoiceState(_selfMicMuted, _selfDeafened);
                    break;
                }
                case RoomEvent.MessageOneofCase.ParticipantDisconnected:
                    ParticipantLeftById?.Invoke(re.ParticipantDisconnected.ParticipantIdentity);
                    break;
                case RoomEvent.MessageOneofCase.TrackPublished:
                    RememberSource(re.TrackPublished.Publication.Info.Sid, re.TrackPublished.Publication.Info.Source);
                    break;
                case RoomEvent.MessageOneofCase.TrackSubscribed:
                    OnTrackSubscribed(re.TrackSubscribed.ParticipantIdentity, re.TrackSubscribed.Track);
                    break;
                case RoomEvent.MessageOneofCase.TrackUnsubscribed:
                    OnTrackUnsubscribed(re.TrackUnsubscribed.ParticipantIdentity, re.TrackUnsubscribed.TrackSid);
                    break;
                case RoomEvent.MessageOneofCase.TrackUnpublished:
                    // Собеседник ВЫКЛЮЧИЛ демку/камеру: unpublish может прийти без
                    // TrackUnsubscribed — без этой ветки плитка висела вечно.
                    OnTrackUnsubscribed(re.TrackUnpublished.ParticipantIdentity, re.TrackUnpublished.PublicationSid);
                    break;
                case RoomEvent.MessageOneofCase.ParticipantAttributesChanged:
                {
                    var pac = re.ParticipantAttributesChanged;
                    EmitVoiceAttrs(pac.ParticipantIdentity, pac.Attributes);
                    break;
                }
                case RoomEvent.MessageOneofCase.ActiveSpeakersChanged:
                {
                    var ids = new string[re.ActiveSpeakersChanged.ParticipantIdentities.Count];
                    re.ActiveSpeakersChanged.ParticipantIdentities.CopyTo(ids, 0);
                    ActiveSpeakersChanged?.Invoke(ids);
                    break;
                }
                case RoomEvent.MessageOneofCase.Disconnected:
                    Disconnected?.Invoke();
                    break;
            }
        }

        private void EmitVoiceAttrsFromMap(string identity, Google.Protobuf.Collections.MapField<string, string> attrs)
        {
            if (attrs == null) return;
            if (attrs.TryGetValue("mic", out var m)) ParticipantMicMuted?.Invoke(identity, m == "0");
            if (attrs.TryGetValue("deaf", out var d)) ParticipantDeafened?.Invoke(identity, d == "1");
        }

        // Разбор атрибутов голосового состояния участника ("mic"/"deaf").
        private void EmitVoiceAttrs(string identity, System.Collections.Generic.IEnumerable<AttributesEntry> attrs)
        {
            bool hasMic = false, micMuted = false, hasDeaf = false, deaf = false;
            foreach (var a in attrs)
            {
                if (a.Key == "mic") { hasMic = true; micMuted = a.Value == "0"; }
                else if (a.Key == "deaf") { hasDeaf = true; deaf = a.Value == "1"; }
            }
            if (hasMic) ParticipantMicMuted?.Invoke(identity, micMuted);
            if (hasDeaf) ParticipantDeafened?.Invoke(identity, deaf);
        }

        private void RememberSource(string sid, TrackSource source)
        {
            if (string.IsNullOrEmpty(sid)) return;
            lock (_videoLock) _sourceBySid[sid] = source;
        }

        private bool IsScreenSid(string sid)
        {
            lock (_videoLock)
                return _sourceBySid.TryGetValue(sid, out var s) && s == TrackSource.SourceScreenshare;
        }

        private void HandlePublishTrack(PublishTrackCallback cb)
        {
            if (cb.MessageCase == PublishTrackCallback.MessageOneofCase.Error) return;
            string sid = cb.Publication.Info.Sid;
            if (cb.AsyncId == _camPublishAsyncId) _camTrackSid = sid;
            else if (cb.AsyncId == _scrPublishAsyncId) _scrTrackSid = sid;
            else if (cb.AsyncId == _scrAudioPublishAsyncId) _scrAudioTrackSid = sid;
        }

        // Подписались на удалённый трек: аудио → микшер; видео → открываем видеострим.
        private void OnTrackSubscribed(string identity, OwnedTrack track)
        {
            if (track.Info.Kind == TrackKind.KindAudio)
            {
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
                // Демка это или голос — по источнику трека (для отдельной громкости).
                bool isScreenAudio;
                lock (_videoLock)
                    isScreenAudio = _sourceBySid.TryGetValue(track.Info.Sid, out var srcKind)
                                    && srcKind == TrackSource.SourceScreenshareAudio;
                lock (_audioLock)
                {
                    EnsurePlayback();
                    var prov = new BufferedWaveProvider(new WaveFormat(SR, 16, CH))
                    { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true };
                    _remoteByStream[streamHandle] = prov;
                    // Обёртка громкости: у каждого потока (голос/демка участника)
                    // своя ручка — «заглушить/поменять громкость конкретной демки».
                    var vol = new VolumeSampleProvider(prov.ToSampleProvider());
                    var ctx = new RemoteAudioCtx { Pid = identity, IsScreen = isScreenAudio, Vol = vol };
                    _audioCtxByStream[streamHandle] = ctx;
                    ApplyRemoteAudioVolume(ctx);
                    _mixer.AddMixerInput(vol);
                }
            }
            else if (track.Info.Kind == TrackKind.KindVideo)
            {
                bool isScreen = IsScreenSid(track.Info.Sid);
                var resp = LiveKitFfi.Request(new FfiRequest
                {
                    NewVideoStream = new NewVideoStreamRequest
                    {
                        TrackHandle = track.Handle.Id,
                        Type = VideoStreamType.VideoStreamNative,
                        Format = VideoBufferType.Bgra,
                        NormalizeStride = true
                    }
                });
                ulong streamHandle = resp.NewVideoStream.Stream.Handle.Id;
                lock (_videoLock) _videoStreamMeta[streamHandle] = new VideoStreamCtx { Identity = identity, IsScreen = isScreen };
            }
        }

        private void OnTrackUnsubscribed(string identity, string sid)
        {
            // Убираем ТОЛЬКО видео-плитки. Раньше отписка ЛЮБОГО трека (в т.ч.
            // звука демки/микрофона) прилетала как RemoteVideoRemoved(isScreen:
            // false) и сносила плитку КАМЕРЫ участника.
            TrackSource src;
            lock (_videoLock) { if (!_sourceBySid.TryGetValue(sid, out src)) return; }
            if (src == TrackSource.SourceScreenshare) RemoteVideoRemoved?.Invoke(identity, true);
            else if (src == TrackSource.SourceCamera) RemoteVideoRemoved?.Invoke(identity, false);
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

        private void HandleVideoStream(VideoStreamEvent vse)
        {
            if (vse.MessageCase == VideoStreamEvent.MessageOneofCase.Eos)
            {
                lock (_videoLock) _videoStreamMeta.Remove(vse.StreamHandle);
                return;
            }
            if (vse.MessageCase != VideoStreamEvent.MessageOneofCase.FrameReceived) return;

            VideoStreamCtx ctx;
            lock (_videoLock) { _videoStreamMeta.TryGetValue(vse.StreamHandle, out ctx); }

            var buffer = vse.FrameReceived.Buffer;
            var info = buffer.Info;
            int w = (int)info.Width, h = (int)info.Height;
            if (ctx != null && info.DataPtr != 0 && w > 0 && h > 0)
            {
                int stride = info.HasStride && info.Stride > 0 ? (int)info.Stride : w * 4;
                var bgra = ctx.Rent(w * h * 4);   // пул: без 8МБ-аллокаций на каждый кадр
                IntPtr src = new IntPtr((long)info.DataPtr);
                if (stride == w * 4)
                    Marshal.Copy(src, bgra, 0, bgra.Length);
                else
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(IntPtr.Add(src, y * stride), bgra, y * w * 4, w * 4);
                RemoteVideoFrame?.Invoke(ctx.Identity, ctx.IsScreen, bgra, w, h);
            }
            LiveKitFfi.DropHandle(buffer.Handle.Id);
        }

        private void EnsurePlayback()
        {
            if (_mixer == null)
                _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SR, CH)) { ReadFully = true };
            if (_out != null) return;
            _out = new WaveOutEvent { DesiredLatency = 120 };
            if (_outputDeviceIndex >= 0 && _outputDeviceIndex < WaveOut.DeviceCount)
                _out.DeviceNumber = _outputDeviceIndex;
            _out.Init(PlaybackChain());
            ApplyPlaybackVolume();
            _out.Play();
        }

        // Микс + врезка эхо-референса (если играем звук, отдаём его копию в APM).
        private ISampleProvider PlaybackChain()
            => new ApmReverseTap(_mixer, SR, CH, ApmReverseFrame);

        private void ApplyPlaybackVolume()
        {
            try { if (_out != null) _out.Volume = _playbackMuted ? 0f : Math.Clamp(_playbackVolume, 0f, 1f); } catch { }
        }

        /// <summary>Заглушить весь входящий звук («наушники»).</summary>
        public void SetPlaybackMuted(bool muted) { _playbackMuted = muted; ApplyPlaybackVolume(); }

        /// <summary>Громкость входящего голоса (0..1; NAudio WaveOut).</summary>
        public void SetPlaybackVolume(float volume) { _playbackVolume = volume; ApplyPlaybackVolume(); }

        /// <summary>Сменить устройство вывода на лету (пересоздаём WaveOut на общем микшере).</summary>
        public void SetOutputDeviceIndex(int index)
        {
            _outputDeviceIndex = index;
            lock (_audioLock)
            {
                try { _out?.Stop(); _out?.Dispose(); } catch { }
                _out = null;
                if (_mixer != null)
                {
                    _out = new WaveOutEvent { DesiredLatency = 120 };
                    if (index >= 0 && index < WaveOut.DeviceCount) _out.DeviceNumber = index;
                    _out.Init(PlaybackChain());
                    ApplyPlaybackVolume();
                    _out.Play();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive; } catch { }
            try { LiveKitFfi.FfiEventReceived -= OnFfiEvent; } catch { }
            try { _statsTimer?.Dispose(); } catch { }
            _statsTimer = null;
            try { StopScreenShare(); } catch { }
            try { StopCamera(); } catch { }
            try { StopMicrophone(); } catch { }
            try { DisconnectCall(); } catch { }
            try { _out?.Stop(); _out?.Dispose(); } catch { }
            _out = null;
        }

        // ── Win32 (захват экрана/окна) ────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        // Разрешение системного таймера (точный Thread.Sleep для пейсинга кадров).
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uMilliseconds);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uMilliseconds);

        // ── Быстрый захват монитора: BitBlt/StretchBlt прямо в DIB-секцию ──
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDst, int x, int y, int cx, int cy,
                                          IntPtr hdcSrc, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr hdcDst, int x, int y, int cx, int cy,
                                              IntPtr hdcSrc, int sx, int sy, int scx, int scy, uint rop);
        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")]
        private static extern int StretchDIBits(IntPtr hdc, int xd, int yd, int wd, int hd,
                                                int xs, int ys, int ws, int hs,
                                                IntPtr bits, ref BITMAPINFO bmi, uint usage, uint rop);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
                                                      out IntPtr bits, IntPtr hSection, uint offset);

        private const uint SRCCOPY = 0x00CC0020;
        private const int HALFTONE = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public int biSize;
            public int biWidth;
            public int biHeight;      // ОТРИЦАТЕЛЬНАЯ = top-down (как BGRA-кадр)
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
            // Палитра не используется (32bpp BI_RGB).
        }
    }
}
