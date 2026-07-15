using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using AForge.Video.DirectShow;
using NAudio.Wave;
using PISMO.Native;

namespace PISMO
{
    /// <summary>
    /// Мост между окном звонка (CallForm) и НАТИВНЫМ транспортом LiveKit
    /// (livekit_ffi.dll, без WebView2/Chromium). Предоставляет ровно тот же
    /// программный контракт, что и старый WebRtcTransport, поэтому CallForm/
    /// CallForm_Tiles остаются почти без изменений — но под капотом всё видео/
    /// аудио идёт через нативный клиент (обходит 0x8007139F от VR).
    ///
    /// Кадры LiveKit приходят как BGRA; здесь они переупаковываются в BMP-байты,
    /// потому что CallForm ждёт «картинку в байтах» (как раньше JPEG из WebView).
    /// Кодирование делается вне FFI-потока, чтобы не задерживать приём звука.
    ///
    /// Часть возможностей старого WebView-пути (нативный «театр» 60fps, выбор
    /// кодека/GPU, программный энкодер, RTT из WebRTC-статистики, активные
    /// говорящие) в нативном клиенте не воспроизводится — эти методы/события
    /// оставлены заглушками ради совместимости контракта.
    /// </summary>
    public sealed class NativeCallBridge : IDisposable
    {
        private NativeCallTransport _t;
        private Form _parentForm;                                        // для маршалинга в UI-поток
        private readonly Dictionary<string, string> _names = new();     // pid -> имя
        private readonly HashSet<string> _startedTiles = new();          // "pid|source"
        private bool _screenPreviewActive = true;

        // Пинг: у нативного FFI нет готового RTT, поэтому меряем задержку до
        // сервера LiveKit сами (ICMP, с откатом на TCP-подключение к :7880).
        private string _pingHost;
        private int _pingPort = 7880;
        private System.Threading.Timer _pingTimer;

        // ── События (контракт WebRtcTransport, используемый CallForm) ──
        public event Action Disconnected;
        public event Action Connected;
        public event Action RemoteParticipantLeft;
        public event Action<string, string> ParticipantJoined;
        public event Action<string> ParticipantLeftById;
        public event Action<string, string, string> RemoteTileStarted;   // (pid, name, source)
        public event Action<string, string> RemoteTileStopped;           // (pid, source)
        public event Action<string, string, byte[]> RemoteTileFrame;     // (pid, source, image bytes)
        public event Action<string> ActiveSpeakers;
        public event Action<int> PingUpdated;
        public event Action<string, string> ParticipantRenamed;
        public event Action<string, string> RemoteStreamPublished;
        public event Action<string> RemoteStreamUnpublished;
        public event Action<string, string> WatchFailed;
        public event Action<int> StreamWatchersChanged;
        public event Action<bool, string> ScreenSourceSwitched;
        public event Action LocalScreenStarted;
        public event Action LocalScreenStopped;
        public event Action<string> LocalScreenError;
        public event Action<byte[]> ScreenPreviewFrameReceived;
        public event Action ScreenPreviewReady;
        public event Action<byte[]> LocalCameraFrameReceived;
        public event Action CameraPreviewReady;
        public event Action LocalCameraStarted;
        public event Action LocalCameraStopped;
        public event Action<string> LocalCameraError;
        public event Action TheaterExitRequested;
        public event Action TheaterFullscreenToggle;
        public event Action<string> RendererCrashed;
        public event Action ScreenEngineRecovered;
        public event Action<string> ScreenSendStats;
        public event Action SoftwareEncoderDetected;
        public event Action<int, int, int> ScreenCaptureInfo;
        public event Action<string, string, string> DevicesEnumerated;   // (camsJson, micsJson, spkJson)

        // ── Подключение ──────────────────────────────────────────────────
        public Task InitAsync(Form parentForm, string livekitUrl, string token)
        {
            _parentForm = parentForm;
            try
            {
                // ws://host:port → host + port для замера пинга.
                var u = new Uri(livekitUrl.Replace("ws://", "http://").Replace("wss://", "https://"));
                _pingHost = u.Host;
                if (u.Port > 0) _pingPort = u.Port;
            }
            catch { _pingHost = null; }
            _t = new NativeCallTransport();
            _t.Connected += OnNativeConnected;
            // Connected/Disconnected CallForm обрабатывает без UiInvoke (раньше их
            // слал WebView2 уже в UI-потоке) — маршалим сами.
            _t.Disconnected += () => Ui(() => Disconnected?.Invoke());
            _t.ParticipantJoined += (id, name) => { _names[id] = name; ParticipantJoined?.Invoke(id, name); };
            _t.ParticipantLeftById += id =>
            {
                ParticipantLeftById?.Invoke(id);
                RemoteParticipantLeft?.Invoke();
            };
            _t.ConnectError += msg => System.Diagnostics.Debug.WriteLine("[NATIVE CALL] " + msg);
            _t.RemoteVideoFrame += OnRemoteVideo;
            _t.RemoteVideoRemoved += OnRemoteVideoRemoved;
            _t.LocalCameraFrame += OnLocalCameraFrame;
            _t.LocalScreenFrame += OnLocalScreenFrameNative;

            _t.Connect(livekitUrl, token);
            return Task.CompletedTask;
        }

        private void OnNativeConnected()
        {
            // Микрофон стартуем сразу (мьютом рулит CallForm через SetMicrophoneEnabled).
            try
            {
                int mic = ResolveMic(DeviceSettings.MicrophoneName);
                if (mic >= 0) _t.SetInputDeviceIndex(mic);
            }
            catch { }
            try
            {
                _t.StartMicrophone(
                    echoCancel: true,
                    noiseSuppress: DeviceSettings.NoiseSuppression,
                    agc: true);
            }
            catch { }
            Ui(() => Connected?.Invoke());
            StartPing();
        }

        // ── Пинг до сервера LiveKit (📶 в окне звонка) ────────────────────
        private void StartPing()
        {
            if (string.IsNullOrEmpty(_pingHost) || _pingTimer != null) return;
            _pingTimer = new System.Threading.Timer(_ => PingTick(), null, 500, 2000);
        }

        private void PingTick()
        {
            int ms = MeasurePing();
            if (ms >= 0) { try { PingUpdated?.Invoke(ms); } catch { } }
        }

        private int MeasurePing()
        {
            // 1) ICMP — самый честный RTT, если сервер отвечает на пинг.
            try
            {
                using var p = new Ping();
                var r = p.Send(_pingHost, 1500);
                if (r != null && r.Status == IPStatus.Success) return (int)r.RoundtripTime;
            }
            catch { }
            // 2) Откат: время TCP-подключения к сигнальному порту (ICMP часто закрыт).
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var c = new TcpClient();
                var task = c.ConnectAsync(_pingHost, _pingPort);
                if (task.Wait(1500) && c.Connected) { sw.Stop(); return (int)sw.ElapsedMilliseconds; }
            }
            catch { }
            return -1;
        }

        /// <summary>Выполнить действие в UI-потоке формы звонка (события, которые
        /// CallForm обрабатывает без собственного UiInvoke).</summary>
        private void Ui(Action action)
        {
            var f = _parentForm;
            try
            {
                if (f == null || f.IsDisposed) { action(); return; }
                if (f.IsHandleCreated && f.InvokeRequired) f.BeginInvoke(action);
                else action();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // ── Приём видео (камера/демка собеседников) ──────────────────────
        private void OnRemoteVideo(string identity, bool isScreen, byte[] bgra, int w, int h)
        {
            string source = isScreen ? "screen" : "camera";
            string key = identity + "|" + source;
            if (_startedTiles.Add(key))
            {
                string name = _names.TryGetValue(identity, out var n) ? n : identity;
                RemoteTileStarted?.Invoke(identity, name, source);
            }
            // Кодирование BGRA→BMP — вне FFI-потока (чтобы не тормозить звук).
            Task.Run(() =>
            {
                try
                {
                    byte[] img = BgraToBytes(bgra, w, h);
                    if (img != null) RemoteTileFrame?.Invoke(identity, source, img);
                }
                catch { }
            });
        }

        private void OnRemoteVideoRemoved(string identity, bool isScreen)
        {
            string source = isScreen ? "screen" : "camera";
            if (_startedTiles.Remove(identity + "|" + source))
                RemoteTileStopped?.Invoke(identity, source);
        }

        private void OnLocalCameraFrame(byte[] bgra, int w, int h)
        {
            if (LocalCameraFrameReceived == null) return;
            Task.Run(() =>
            {
                try { var img = BgraToBytes(bgra, w, h); if (img != null) LocalCameraFrameReceived?.Invoke(img); }
                catch { }
            });
        }

        private void OnLocalScreenFrameNative(byte[] bgra, int w, int h)
        {
            if (!_screenPreviewActive || ScreenPreviewFrameReceived == null) return;
            Task.Run(() =>
            {
                try { var img = BgraToBytes(bgra, w, h); if (img != null) ScreenPreviewFrameReceived?.Invoke(img); }
                catch { }
            });
        }

        // ── Камера (превью → подтверждение) ──────────────────────────────
        public void PreviewCamera(string deviceLabel)
        {
            try
            {
                _t.StartCameraPreview(ResolveCam(deviceLabel));
                CameraPreviewReady?.Invoke();
            }
            catch (Exception ex) { LocalCameraError?.Invoke(ex.Message); }
        }

        public void ConfirmCameraShare()
        {
            try { _t.PublishCamera(); LocalCameraStarted?.Invoke(); }
            catch (Exception ex) { LocalCameraError?.Invoke(ex.Message); }
        }

        public void CancelCameraPreview() { try { _t.StopCamera(); } catch { } LocalCameraStopped?.Invoke(); }
        public void StopCameraTrack() { try { _t.StopCamera(); } catch { } LocalCameraStopped?.Invoke(); }
        public void SwitchCameraDevice(string deviceLabel) { try { _t.SwitchCameraDevice(ResolveCam(deviceLabel)); } catch { } }

        // ── Демонстрация экрана / окна ───────────────────────────────────
        public void StartMonitorShare(Rectangle bounds, int height, int fps)
        {
            try
            {
                _screenPreviewActive = true;
                _t.StartScreenShare(bounds, fps > 0 ? fps : 15);
                LocalScreenStarted?.Invoke();
                ScreenPreviewReady?.Invoke();
            }
            catch (Exception ex) { LocalScreenError?.Invoke(ex.Message); }
        }

        public void StartWindowShare(IntPtr window, int fps)
        {
            try
            {
                _screenPreviewActive = true;
                _t.StartScreenShareWindow(window, fps > 0 ? fps : 15);
                LocalScreenStarted?.Invoke();
                ScreenPreviewReady?.Invoke();
            }
            catch (Exception ex) { LocalScreenError?.Invoke(ex.Message); }
        }

        public void StopScreenShareTrack() { try { _t.StopScreenShare(); } catch { } LocalScreenStopped?.Invoke(); }
        public void CancelScreenPreview() { try { _t.StopScreenShare(); } catch { } LocalScreenStopped?.Invoke(); }
        public void ConfirmScreenShare() { /* уже публикуется с момента StartMonitorShare */ }
        public void SetScreenPreviewActive(bool on) => _screenPreviewActive = on;

        // ── Звук ──────────────────────────────────────────────────────────
        public void SetMicrophoneEnabled(bool enabled) { try { _t.SetMicMuted(!enabled); } catch { } }
        public void SetRemoteMuted(bool muted) { try { _t.SetPlaybackMuted(muted); } catch { } }
        public void SetRemoteVoiceVolume(float volume) { try { _t.SetPlaybackVolume(volume); } catch { } }
        public void SetInputDevice(string deviceLabel) { try { _t.SetInputDeviceIndex(ResolveMic(deviceLabel)); } catch { } }
        public void SetOutputDevice(string deviceLabel) { try { _t.SetOutputDeviceIndex(ResolveSpk(deviceLabel)); } catch { } }

        // ── Перечисление устройств ───────────────────────────────────────
        public void EnumerateDevices()
        {
            try
            {
                DevicesEnumerated?.Invoke(
                    JsonSerializer.Serialize(CameraLabels()),
                    JsonSerializer.Serialize(MicLabels()),
                    JsonSerializer.Serialize(SpeakerLabels()));
            }
            catch { }
        }

        // ── Заглушки контракта (нативному пути не нужны / не поддержаны) ──
        public void WatchScreen(string pid) { }
        public void UnwatchScreen(string pid) { }
        public void SetTileRate(string pid, string source, int fps, int maxW, bool boost) { }
        public void HideTransportWindow() { }
        public void EnterTheater(string pid, string source, Rectangle bounds) { }
        public void UpdateTheaterBounds(Rectangle bounds) { }
        public void ExitTheater() { }
        public void SwitchScreenSource() { }
        public void SetScreenShareVolume(string pid, float volume) { }
        public void SetScreenCodec(string codec) { }
        public void SetScreenQualityLive(int resHeight, int fps) { }
        public void SetRemoteScreenAudioVolume(float volume) { }
        public void SetParticipantVolume(string pid, float volume) { }
        public void SetParticipantMuted(string pid, bool muted) { }
        public void SetVoiceGate(bool auto, int threshold) { }
        public void SetNoiseSuppression(bool on) { }
        public void SetMicGain(float gain) { }
        public void SetDisplayName(string name) { }

        public void Dispose()
        {
            try { _pingTimer?.Dispose(); } catch { }
            _pingTimer = null;
            try { _t?.Dispose(); } catch { }
            _t = null;
        }

        // ── Устройства: метки и разрешение label → moniker/index ─────────
        private static string[] CameraLabels()
        {
            var list = new List<string>();
            try { foreach (FilterInfo fi in new FilterInfoCollection(FilterCategory.VideoInputDevice)) list.Add(fi.Name); }
            catch { }
            return list.ToArray();
        }

        private static string[] MicLabels()
        {
            var list = new List<string>();
            try { for (int i = 0; i < WaveInEvent.DeviceCount; i++) list.Add(WaveInEvent.GetCapabilities(i).ProductName); }
            catch { }
            return list.ToArray();
        }

        private static string[] SpeakerLabels()
        {
            var list = new List<string>();
            try { for (int i = 0; i < WaveOut.DeviceCount; i++) list.Add(WaveOut.GetCapabilities(i).ProductName); }
            catch { }
            return list.ToArray();
        }

        private static string ResolveCam(string label)
        {
            try
            {
                var devs = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (!string.IsNullOrEmpty(label))
                    foreach (FilterInfo fi in devs)
                        if (fi.Name == label) return fi.MonikerString;
                if (devs.Count > 0) return devs[0].MonikerString;
            }
            catch { }
            return null;
        }

        private static int ResolveMic(string label)
        {
            if (string.IsNullOrEmpty(label)) return -1;
            try { for (int i = 0; i < WaveInEvent.DeviceCount; i++) if (WaveInEvent.GetCapabilities(i).ProductName == label) return i; }
            catch { }
            return -1;
        }

        private static int ResolveSpk(string label)
        {
            if (string.IsNullOrEmpty(label)) return -1;
            try { for (int i = 0; i < WaveOut.DeviceCount; i++) if (WaveOut.GetCapabilities(i).ProductName == label) return i; }
            catch { }
            return -1;
        }

        // ── BGRA (packed, stride = w*4) → BMP-байты для PictureBox ────────
        private static byte[] BgraToBytes(byte[] bgra, int w, int h)
        {
            if (bgra == null || w <= 0 || h <= 0 || bgra.Length < w * h * 4) return null;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                int dstStride = data.Stride;
                if (dstStride == w * 4)
                    Marshal.Copy(bgra, 0, data.Scan0, w * h * 4);
                else
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(bgra, y * w * 4, IntPtr.Add(data.Scan0, y * dstStride), w * 4);
            }
            finally { bmp.UnlockBits(data); }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Bmp);
            return ms.ToArray();
        }
    }
}
