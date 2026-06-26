using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AForge.Video;
using AForge.Video.DirectShow;
using NAudio.Wave;

namespace PISMO
{
    /// <summary>
    /// Управляет UDP-стримингом аудио, видео и захватом экрана.
    /// Использует один UdpClient (порт выбирается ОС),
    /// пакеты тегируются 1-байтным заголовком:
    ///   0x01 = аудио PCM
    ///   0x02 = видео JPEG
    ///   0x03 = экран JPEG
    ///   0x04 = сигнал (mute/unmute/end/ping)
    /// </summary>
    public class CallManager : IDisposable
    {
        // ── Константы пакетов ─────────────────────────────────────────────
        public const byte TAG_AUDIO  = 0x01;
        public const byte TAG_VIDEO  = 0x02;
        public const byte TAG_SCREEN = 0x03;
        public const byte TAG_SIGNAL = 0x04;

        public const byte SIG_MUTE   = 0x10;
        public const byte SIG_UNMUTE = 0x11;
        public const byte SIG_END    = 0x12;
        public const byte SIG_PING   = 0x13;
        public const byte SIG_SCREEN_ON  = 0x14;
        public const byte SIG_SCREEN_OFF = 0x15;

        // ── Состояние ─────────────────────────────────────────────────────
        public int  LocalPort  { get; private set; }
        public bool IsRunning  { get; private set; }
        public bool MicMuted   { get; set; } = false;
        public bool CamEnabled { get; set; } = false;
        public bool ScreenEnabled { get; set; } = false;

        // ── Одноранговые адреса (для P2P) ────────────────────────────────
        // В один момент может быть один «peer» (личный звонок)
        // или список для группового (расширение)
        private IPEndPoint _peerEndpoint;

        // ── UDP ──────────────────────────────────────────────────────────
        private UdpClient  _udp;
        private CancellationTokenSource _cts;

        // ── Аудио вход ───────────────────────────────────────────────────
        private WaveInEvent _waveIn;

        // ── Аудио выход ──────────────────────────────────────────────────
        private BufferedWaveProvider _playBuffer;
        private WaveOutEvent         _waveOut;

        // ── Видео (AForge) ────────────────────────────────────────────────
        private VideoCaptureDevice  _camera;
        private int                 _frameSkip = 0;  // пропуск кадров для ~10 fps

        // ── Захват экрана ─────────────────────────────────────────────────
        private System.Windows.Forms.Timer _screenTimer;

        // ── События для UI ────────────────────────────────────────────────
        public event Action<Bitmap>  OnVideoFrame;    // входящий видеокадр
        public event Action<Bitmap>  OnScreenFrame;   // входящий кадр экрана
        public event Action<byte>    OnSignal;        // входящий сигнал
        public event Action<string>  OnStatusChange;

        // ── Очередь кадров ────────────────────────────────────────────────
        private readonly ConcurrentQueue<byte[]> _videoQueue  = new();
        private readonly ConcurrentQueue<byte[]> _screenQueue = new();

        // ─────────────────────────────────────────────────────────────────
        //  ИНИЦИАЛИЗАЦИЯ
        // ─────────────────────────────────────────────────────────────────
        public void Start(IPEndPoint peerEndpoint, bool enableMic, bool enableCam, bool enableScreen)
        {
            _peerEndpoint = peerEndpoint;
            _cts          = new CancellationTokenSource();

            // UDP на случайном порту
            _udp = new UdpClient(0);
            _udp.Client.ReceiveBufferSize = 1024 * 1024;
            _udp.Client.SendBufferSize    = 1024 * 1024;
            LocalPort = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;

            IsRunning = true;

            // Запускаем приём
            Task.Run(ReceiveLoop, _cts.Token);

            // Запускаем декодер видео/экрана
            Task.Run(DecodeLoop, _cts.Token);

            // Микрофон
            if (enableMic)   StartMic();
            if (enableCam)   StartCamera();
            if (enableScreen) StartScreen();

            // Аудио воспроизведение
            StartPlayback();

            // Пинг каждые 3 сек (чтобы peer знал наш порт)
            Task.Run(PingLoop, _cts.Token);

            OnStatusChange?.Invoke("Подключено");
        }

        public void SetPeer(string ip, int port)
        {
            _peerEndpoint = new IPEndPoint(IPAddress.Parse(ip), port);
        }

        // ─────────────────────────────────────────────────────────────────
        //  ПРИЁМ
        // ─────────────────────────────────────────────────────────────────
        private async Task ReceiveLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.ReceiveAsync();
                    var data   = result.Buffer;
                    if (data.Length < 1) continue;

                    byte tag = data[0];
                    var  payload = data[1..];

                    switch (tag)
                    {
                        case TAG_AUDIO:
                            _playBuffer?.AddSamples(payload, 0, payload.Length);
                            break;

                        case TAG_VIDEO:
                            _videoQueue.Enqueue(payload);
                            break;

                        case TAG_SCREEN:
                            _screenQueue.Enqueue(payload);
                            break;

                        case TAG_SIGNAL:
                            if (payload.Length > 0)
                                OnSignal?.Invoke(payload[0]);
                            break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(50); }
            }
        }

        // Декодер — отдельный поток чтобы не блокировать приём
        private async Task DecodeLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                bool worked = false;

                if (_videoQueue.TryDequeue(out var vjpeg))
                {
                    try
                    {
                        using var ms  = new MemoryStream(vjpeg);
                        var bmp = new Bitmap(ms);
                        OnVideoFrame?.Invoke(bmp);
                    }
                    catch { }
                    worked = true;
                }

                if (_screenQueue.TryDequeue(out var sjpeg))
                {
                    try
                    {
                        using var ms  = new MemoryStream(sjpeg);
                        var bmp = new Bitmap(ms);
                        OnScreenFrame?.Invoke(bmp);
                    }
                    catch { }
                    worked = true;
                }

                if (!worked) await Task.Delay(10);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  МИК
        // ─────────────────────────────────────────────────────────────────
        private void StartMic()
        {
            try
            {
                _waveIn = new WaveInEvent
                {
                    WaveFormat   = new WaveFormat(16000, 1),
                    BufferMilliseconds = 30
                };

                if (DeviceSettings.MicrophoneIndex >= 0
                    && DeviceSettings.MicrophoneIndex < WaveInEvent.DeviceCount)
                    _waveIn.DeviceNumber = DeviceSettings.MicrophoneIndex;

                _waveIn.DataAvailable += (s, ev) =>
                {
                    if (MicMuted || _peerEndpoint == null) return;

                    float gain = DeviceSettings.MicrophoneGain;
                    byte[] buf = gain != 1f
                        ? ApplyGain(ev.Buffer, ev.BytesRecorded, gain)
                        : ev.Buffer;

                    SendTagged(TAG_AUDIO, buf, 0, ev.BytesRecorded);
                };

                _waveIn.StartRecording();
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────
        //  ВОСПРОИЗВЕДЕНИЕ
        // ─────────────────────────────────────────────────────────────────
        private void StartPlayback()
        {
            try
            {
                _playBuffer = new BufferedWaveProvider(new WaveFormat(16000, 1))
                {
                    BufferLength          = 1024 * 1024,
                    DiscardOnBufferOverflow = true
                };
                _waveOut = new WaveOutEvent { DesiredLatency = 80 };
                _waveOut.Init(_playBuffer);
                _waveOut.Play();
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────
        //  КАМЕРА
        // ─────────────────────────────────────────────────────────────────
        public void StartCamera()
        {
            if (_camera != null) return;
            try
            {
                var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (devices.Count == 0) return;

                string moniker = null;
                if (!string.IsNullOrWhiteSpace(DeviceSettings.CameraName))
                    foreach (FilterInfo d in devices)
                        if (d.Name == DeviceSettings.CameraName)
                        { moniker = d.MonikerString; break; }
                moniker ??= devices[0].MonikerString;

                _camera = new VideoCaptureDevice(moniker);
                _camera.NewFrame += Camera_NewFrame;
                _camera.Start();
                CamEnabled = true;
            }
            catch { }
        }

        public void StopCamera()
        {
            CamEnabled = false;
            if (_camera == null) return;
            try
            {
                _camera.SignalToStop();
                _camera.WaitForStop();
                _camera.NewFrame -= Camera_NewFrame;
            }
            catch { }
            _camera = null;
        }

        private void Camera_NewFrame(object sender, NewFrameEventArgs e)
        {
            if (!CamEnabled || _peerEndpoint == null) return;
            _frameSkip++;
            if (_frameSkip % 3 != 0) return;  // ~10 fps из обычных 30

            try
            {
                var frame = CropSquare((Bitmap)e.Frame.Clone(), 240);
                SendJpeg(TAG_VIDEO, frame, 60);
                frame.Dispose();
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────
        //  ЗАХВАТ ЭКРАНА
        // ─────────────────────────────────────────────────────────────────
        public void StartScreen()
        {
            if (_screenTimer != null) return;
            ScreenEnabled = true;
            SendSignal(SIG_SCREEN_ON);

            _screenTimer = new System.Windows.Forms.Timer { Interval = 150 }; // ~6 fps
            _screenTimer.Tick += (s, e) =>
            {
                if (!ScreenEnabled || _peerEndpoint == null) return;
                try
                {
                    var screen   = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                    var bmp      = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
                    using var g  = Graphics.FromImage(bmp);
                    g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);

                    // Масштабируем до 1280 ширины для экономии трафика
                    var scaled = ScaleDown(bmp, 1280);
                    bmp.Dispose();

                    SendJpeg(TAG_SCREEN, scaled, 50);
                    scaled.Dispose();
                }
                catch { }
            };
            _screenTimer.Start();
        }

        public void StopScreen()
        {
            ScreenEnabled = false;
            _screenTimer?.Stop();
            _screenTimer?.Dispose();
            _screenTimer = null;
            SendSignal(SIG_SCREEN_OFF);
        }

        // ─────────────────────────────────────────────────────────────────
        //  ОТПРАВКА
        // ─────────────────────────────────────────────────────────────────
        private void SendTagged(byte tag, byte[] data, int offset, int count)
        {
            if (_peerEndpoint == null || _udp == null) return;
            try
            {
                var buf = new byte[1 + count];
                buf[0] = tag;
                Buffer.BlockCopy(data, offset, buf, 1, count);
                _udp.Send(buf, buf.Length, _peerEndpoint);
            }
            catch { }
        }

        private void SendJpeg(byte tag, Bitmap bmp, long quality)
        {
            try
            {
                using var ms     = new MemoryStream();
                var codec        = GetJpegCodec();
                var encParams    = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(
                    System.Drawing.Imaging.Encoder.Quality, quality);
                bmp.Save(ms, codec, encParams);
                var jpg = ms.ToArray();

                // Разбиваем на чанки если > 60 КБ (ограничение UDP)
                const int CHUNK = 60_000;
                if (jpg.Length <= CHUNK)
                {
                    SendTagged(tag, jpg, 0, jpg.Length);
                }
                else
                {
                    // Для видео просто пропускаем слишком большой кадр
                }
            }
            catch { }
        }

        public void SendSignal(byte signal)
        {
            if (_peerEndpoint == null) return;
            SendTagged(TAG_SIGNAL, new[] { signal }, 0, 1);
        }

        private async Task PingLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try { SendSignal(SIG_PING); } catch { }
                await Task.Delay(3000);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  ВСПОМОГАТЕЛЬНОЕ
        // ─────────────────────────────────────────────────────────────────
        private static Bitmap CropSquare(Bitmap src, int targetSize)
        {
            int side  = Math.Min(src.Width, src.Height);
            int x     = (src.Width - side) / 2;
            int y     = (src.Height - side) / 2;
            var dest  = new Bitmap(targetSize, targetSize);
            using var g = Graphics.FromImage(dest);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            g.DrawImage(src, new Rectangle(0, 0, targetSize, targetSize),
                             new Rectangle(x, y, side, side), GraphicsUnit.Pixel);
            src.Dispose();
            return dest;
        }

        private static Bitmap ScaleDown(Bitmap src, int maxWidth)
        {
            if (src.Width <= maxWidth) return src;
            int h   = (int)(src.Height * (double)maxWidth / src.Width);
            var dst = new Bitmap(maxWidth, h);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
            g.DrawImage(src, 0, 0, maxWidth, h);
            return dst;
        }

        private static byte[] ApplyGain(byte[] buf, int len, float gain)
        {
            var result = new byte[len];
            for (int i = 0; i + 1 < len; i += 2)
            {
                short s = (short)((buf[i + 1] << 8) | buf[i]);
                int   a = Math.Clamp((int)(s * gain), short.MinValue, short.MaxValue);
                result[i]     = (byte)(a & 0xFF);
                result[i + 1] = (byte)((a >> 8) & 0xFF);
            }
            return result;
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            throw new InvalidOperationException("JPEG кодек не найден");
        }

        // ─────────────────────────────────────────────────────────────────
        //  DISPOSE
        // ─────────────────────────────────────────────────────────────────
        public void Dispose()
        {
            IsRunning = false;
            _cts?.Cancel();
            _cts?.Dispose();

            StopCamera();
            StopScreen();

            try { _waveIn?.StopRecording(); _waveIn?.Dispose(); } catch { }
            try { _waveOut?.Stop(); _waveOut?.Dispose(); }       catch { }
            try { _udp?.Close(); _udp?.Dispose(); }              catch { }

            _screenTimer?.Stop();
            _screenTimer?.Dispose();
        }
    }
}
