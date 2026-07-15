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

        // Видео: кадры BGRA (packed, stride = width*4). CallForm рисует плитки.
        public event Action<string, bool, byte[], int, int> RemoteVideoFrame; // (identity, isScreen, bgra, w, h)
        public event Action<string, bool> RemoteVideoRemoved;                 // (identity, isScreen)
        public event Action<byte[], int, int> LocalCameraFrame;               // локальное превью камеры
        public event Action<byte[], int, int> LocalScreenFrame;               // локальное превью демки

        private ulong _connectAsyncId;
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

        // ── Видео (камера/демка) ──────────────────────────────────────────
        private ulong _camSource, _camTrack, _camPublishAsyncId;
        private VideoCaptureDevice _camDevice;
        private string _camTrackSid;
        private bool _camStarted;
        private bool _camPublished;

        private ulong _scrSource, _scrTrack, _scrPublishAsyncId;
        private Thread _scrThread;
        private volatile bool _scrRun;
        private Rectangle _scrBounds;
        private IntPtr _scrWindow;
        private int _scrFps = 15;
        private string _scrTrackSid;
        private bool _scrStarted;

        // Звук демонстрации (системный звук через WASAPI-loopback). Публикуется
        // ОТДЕЛЬНЫМ треком source=SCREENSHARE_AUDIO, с ВЫКЛЮЧЕННЫМИ EC/NS/AGC —
        // чтобы шумодав/эхоподавление не гасили звуки игры/музыки в демке.
        private NAudio.Wave.WasapiLoopbackCapture _loopback;
        private ProcessLoopbackCapture _procLoop;   // захват без своего процесса (без эха)
        private bool _scrAudioFloat;                // true = float (device loopback), false = i16 PCM
        private ulong _scrAudioSource, _scrAudioTrack;
        private ulong _scrAudioPublishAsyncId;
        private string _scrAudioTrackSid;
        private int _scrAudioRate = 48000, _scrAudioCh = 2;

        // sid трека → источник (камера/демка), чтобы различать входящее видео.
        private readonly Dictionary<string, TrackSource> _sourceBySid = new();
        // handle видеострима → (участник, это ли демка).
        private readonly Dictionary<ulong, (string identity, bool isScreen)> _videoStreamMeta = new();
        private readonly object _videoLock = new();

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
                StartMicCapture();
            }
            catch (Exception ex) { ConnectError?.Invoke("микрофон: " + ex.Message); }
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
        }

        /// <summary>Мьют микрофона: трек остаётся опубликованным, кадры не шлём.</summary>
        public void SetMicMuted(bool muted) => _micMuted = muted;

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

                var srcResp = LiveKitFfi.Request(new FfiRequest
                {
                    NewVideoSource = new NewVideoSourceRequest
                    {
                        Type = VideoSourceType.VideoSourceNative,
                        Resolution = new VideoSourceResolution { Width = (uint)width, Height = (uint)height },
                        IsScreencast = false
                    }
                });
                _camSource = srcResp.NewVideoSource.Source.Handle.Id;

                var trkResp = LiveKitFfi.Request(new FfiRequest
                {
                    CreateVideoTrack = new CreateVideoTrackRequest { Name = "camera", SourceHandle = _camSource }
                });
                _camTrack = trkResp.CreateVideoTrack.Track.Handle.Id;

                _camDevice = new VideoCaptureDevice(moniker);
                _camDevice.NewFrame += OnCameraFrame;
                _camDevice.Start();
            }
            catch (Exception ex) { ConnectError?.Invoke("камера: " + ex.Message); _camStarted = false; }
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
                        Options = new TrackPublishOptions { Source = TrackSource.SourceCamera }
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
        public void StartScreenShare(Rectangle bounds, int fps = 15, bool withAudio = false)
            => StartScreenInternal(bounds, IntPtr.Zero, fps, withAudio);

        public void StartScreenShareWindow(IntPtr window, int fps = 15, bool withAudio = false)
        {
            if (!GetWindowRect(window, out RECT r)) return;
            StartScreenInternal(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), window, fps, withAudio);
        }

        private void StartScreenInternal(Rectangle bounds, IntPtr window, int fps, bool withAudio)
        {
            if (_scrStarted || _localHandle == 0) return;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            _scrStarted = true;
            _scrBounds = bounds;
            _scrWindow = window;
            _scrFps = Math.Max(1, Math.Min(60, fps));
            try
            {
                var srcResp = LiveKitFfi.Request(new FfiRequest
                {
                    NewVideoSource = new NewVideoSourceRequest
                    {
                        Type = VideoSourceType.VideoSourceNative,
                        Resolution = new VideoSourceResolution
                        {
                            Width = (uint)(bounds.Width & ~1),
                            Height = (uint)(bounds.Height & ~1)
                        },
                        IsScreencast = true
                    }
                });
                _scrSource = srcResp.NewVideoSource.Source.Handle.Id;

                var trkResp = LiveKitFfi.Request(new FfiRequest
                {
                    CreateVideoTrack = new CreateVideoTrackRequest { Name = "screen", SourceHandle = _scrSource }
                });
                _scrTrack = trkResp.CreateVideoTrack.Track.Handle.Id;

                var pubResp = LiveKitFfi.Request(new FfiRequest
                {
                    PublishTrack = new PublishTrackRequest
                    {
                        LocalParticipantHandle = _localHandle,
                        TrackHandle = _scrTrack,
                        Options = new TrackPublishOptions { Source = TrackSource.SourceScreenshare }
                    }
                });
                _scrPublishAsyncId = pubResp.PublishTrack.AsyncId;

                _scrRun = true;
                _scrThread = new Thread(ScreenLoop) { IsBackground = true, Name = "pismo-screen-capture" };
                _scrThread.Start();

                if (withAudio) StartScreenAudio();
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
                // Сначала пытаемся захватить звук БЕЗ своего процесса (process
                // loopback, exclude-self) — тогда голоса собеседников, которые
                // играет сам PISMO, в демку не попадут → у друзей нет эха. Если
                // ОС не поддержит — откат на обычный loopback устройства.
                try
                {
                    _procLoop = new ProcessLoopbackCapture(
                        System.Diagnostics.Process.GetCurrentProcess().Id, excludeTargetTree: true);
                    _procLoop.Start();
                    _scrAudioRate = _procLoop.WaveFormat.SampleRate;
                    _scrAudioCh = _procLoop.WaveFormat.Channels;
                    _scrAudioFloat = false;
                }
                catch
                {
                    try { _procLoop?.Dispose(); } catch { }
                    _procLoop = null;
                    _loopback = new NAudio.Wave.WasapiLoopbackCapture();
                    _scrAudioRate = _loopback.WaveFormat.SampleRate;
                    _scrAudioCh = Math.Max(1, _loopback.WaveFormat.Channels);
                    _scrAudioFloat = true;
                }

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

                if (_procLoop != null) _procLoop.DataAvailable += OnScreenAudioData;
                else { _loopback.DataAvailable += OnScreenAudioData; _loopback.StartRecording(); }
            }
            catch (Exception ex) { ConnectError?.Invoke("звук демки: " + ex.Message); }
        }

        // Кадр системного звука → i16 PCM → CaptureAudioFrame. Источник даёт либо
        // 32-бит float (device loopback), либо уже i16 (process loopback).
        private void OnScreenAudioData(object sender, WaveInEventArgs e)
        {
            if (_scrAudioSource == 0 || e.BytesRecorded <= 0) return;
            int n;
            short[] pcm;
            if (_scrAudioFloat)
            {
                n = e.BytesRecorded / 4;
                if (n <= 0) return;
                pcm = new short[n];
                for (int i = 0; i < n; i++)
                {
                    float f = BitConverter.ToSingle(e.Buffer, i * 4);
                    if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                    pcm[i] = (short)(f * 32767f);
                }
            }
            else
            {
                n = e.BytesRecorded / 2;
                if (n <= 0) return;
                pcm = new short[n];
                Buffer.BlockCopy(e.Buffer, 0, pcm, 0, n * 2);
            }

            IntPtr buf = Marshal.AllocHGlobal(n * 2);
            try
            {
                Marshal.Copy(pcm, 0, buf, n);
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
                            SamplesPerChannel = (uint)(n / _scrAudioCh)
                        }
                    }
                });
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void StopScreenAudio()
        {
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

        private void ScreenLoop()
        {
            int delayMs = Math.Max(1, 1000 / _scrFps);
            while (_scrRun)
            {
                long start = _clock.ElapsedMilliseconds;
                try
                {
                    using Bitmap bmp = CaptureScreen();
                    if (bmp != null) PushBitmap(_scrSource, bmp, LocalScreenFrame != null ? EmitLocalScreen : null);
                }
                catch { }
                int sleep = delayMs - (int)(_clock.ElapsedMilliseconds - start);
                if (sleep > 0) Thread.Sleep(sleep);
            }
        }

        private void EmitLocalScreen(byte[] bgra, int w, int h) => LocalScreenFrame?.Invoke(bgra, w, h);

        private Bitmap CaptureScreen()
        {
            if (_scrWindow != IntPtr.Zero)
            {
                if (!GetWindowRect(_scrWindow, out RECT r)) return null;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return null;
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(_scrWindow, hdc, PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
                return bmp;
            }
            else
            {
                Rectangle b = _scrBounds;
                var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
                return bmp;
            }
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
                    previewBuf = new byte[w * h * 4];
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
                foreach (var pub in pwt.Publications)   // запоминаем источник по sid
                    RememberSource(pub.Info.Sid, pub.Info.Source);
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
                case RoomEvent.MessageOneofCase.TrackPublished:
                    RememberSource(re.TrackPublished.Publication.Info.Sid, re.TrackPublished.Publication.Info.Source);
                    break;
                case RoomEvent.MessageOneofCase.TrackSubscribed:
                    OnTrackSubscribed(re.TrackSubscribed.ParticipantIdentity, re.TrackSubscribed.Track);
                    break;
                case RoomEvent.MessageOneofCase.TrackUnsubscribed:
                    OnTrackUnsubscribed(re.TrackUnsubscribed.ParticipantIdentity, re.TrackUnsubscribed.TrackSid);
                    break;
                case RoomEvent.MessageOneofCase.Disconnected:
                    Disconnected?.Invoke();
                    break;
            }
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
                lock (_audioLock)
                {
                    EnsurePlayback();
                    var prov = new BufferedWaveProvider(new WaveFormat(SR, 16, CH))
                    { BufferDuration = TimeSpan.FromSeconds(2), DiscardOnBufferOverflow = true };
                    _remoteByStream[streamHandle] = prov;
                    _mixer.AddMixerInput(prov.ToSampleProvider());
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
                lock (_videoLock) _videoStreamMeta[streamHandle] = (identity, isScreen);
            }
        }

        private void OnTrackUnsubscribed(string identity, string sid)
        {
            RemoteVideoRemoved?.Invoke(identity, IsScreenSid(sid));
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

            (string identity, bool isScreen) meta;
            lock (_videoLock) { if (!_videoStreamMeta.TryGetValue(vse.StreamHandle, out meta)) meta = (null, false); }

            var buffer = vse.FrameReceived.Buffer;
            var info = buffer.Info;
            int w = (int)info.Width, h = (int)info.Height;
            if (info.DataPtr != 0 && w > 0 && h > 0)
            {
                int stride = info.HasStride && info.Stride > 0 ? (int)info.Stride : w * 4;
                var bgra = new byte[w * h * 4];
                IntPtr src = new IntPtr((long)info.DataPtr);
                if (stride == w * 4)
                    Marshal.Copy(src, bgra, 0, bgra.Length);
                else
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(IntPtr.Add(src, y * stride), bgra, y * w * 4, w * 4);
                RemoteVideoFrame?.Invoke(meta.identity, meta.isScreen, bgra, w, h);
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
            _out.Init(_mixer);
            ApplyPlaybackVolume();
            _out.Play();
        }

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
                    _out.Init(_mixer);
                    ApplyPlaybackVolume();
                    _out.Play();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { LiveKitFfi.FfiEventReceived -= OnFfiEvent; } catch { }
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
    }
}
