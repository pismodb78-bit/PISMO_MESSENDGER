using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text.Json;

namespace PISMO
{
    /// <summary>
    /// WebRTC транспорт через браузерный движок WebView2.
    /// Использует нативный JavaScript WebRTC API — DTLS/ICE/TURN работают идеально.
    /// </summary>
    public class WebRtcTransport : IDisposable
    {
        public const byte TypeAudio = 1;
        public const byte TypeVideo = 2;
        public const byte TypeScreen = 3;
        public const byte TypeScreenAudio = 4; // звук демонстрации экрана (системный звук), отдельно от микрофона
        public const byte TypeScreenStop = 5;  // явный сигнал об остановке демонстрации экрана
        public const byte TypeHangup = 99;
        public const byte TypeKeepAlive = 98;

        private WebView2 _webView;
        private bool _initialized = false;
        private bool _disposed = false;

        public event Action<byte, byte[]> FrameReceived;
        public event Action Disconnected;
        public event Action Connected;
        public event Action<string> IceCandidateReady;
        public event Action GatheringComplete;

        // --- Видео-трек демонстрации экрана (настоящий WebRTC, не DataChannel) ---
        public event Action<byte[]> RemoteScreenFrameReceived; // декодированный JPEG-кадр из видео-трека
        public event Action RemoteScreenStarted;
        public event Action RemoteScreenStopped;
        public event Action LocalScreenStarted;
        public event Action LocalScreenStopped;
        public event Action<string> LocalScreenError;
        // Renegotiation SDP нужно передать через тот же сигнальный канал (БД),
        // что и первоначальный offer/answer — эти события дают вызывающему
        // коду (CallForm) точку подключения для записи SDP в БД. trackKinds —
        // JSON-карта track.id -> "screen"/"camera", нужна собеседнику, чтобы
        // надёжно различить входящие видео-треки в pc.ontrack.
        public event Action<string, string> RenegotiationOfferReady;
        public event Action<string, string> RenegotiationAnswerReady;

        // --- Камера (настоящий WebRTC video track вместо AForge+JPEG) ---
        public event Action<byte[]> LocalCameraFrameReceived;  // для своего превью (_pbLocal)
        public event Action<byte[]> RemoteCameraFrameReceived; // декодированный кадр камеры собеседника
        public event Action RemoteCameraStarted;
        public event Action RemoteCameraStopped;
        public event Action LocalCameraStarted;
        public event Action LocalCameraStopped;
        public event Action<string> LocalCameraError;

        // --- Превью перед включением (показ "что увидит собеседник" с
        // подтверждением через кнопки "Включить"/"Отмена") ---
        public event Action<byte[]> ScreenPreviewFrameReceived; // переиспользует канал кадров демки до подтверждения
        public event Action ScreenPreviewReady; // поток реально захвачен, можно разрешить подтверждение
        public event Action CameraPreviewReady;
        public event Action CameraDeviceSwitched;
        public event Action<string, string> DevicesEnumerated; // (camerasJson, micsJson)

        private string _turnUrlUdp;
        private string _turnUrlTcp;

        private string _turnUrl;
        private string _turnUsername;
        private string _turnPassword;

        public WebRtcTransport()
        {
            // ВАЖНО: генерируем креды заново непосредственно перед звонком,
            // а не используем то, что было сгенерировано когда-то раньше (например,
            // при старте приложения в MainForm). Если объект WebRtcTransport
            // создаётся не сразу, старые креды могут быть ближе к истечению TTL —
            // на практике это редко успевает стать проблемой при TTL=86400, но
            // принцип "свежие креды на каждый звонок" убирает целый класс багов.
            var (username, password) = TurnSettings.GetCredentials();

            // Приводим транспорт к нижнему регистру для безопасного сравнения
            string transportType = (TurnSettings.TurnTransport ?? "udp").ToLower().Trim();

            if (transportType == "udp")
            {
                // Для UDP оставляем чистый turn-адрес. Chromium по умолчанию использует UDP.
                _turnUrl = $"turn:{TurnSettings.TurnServerAddress}:{TurnSettings.TurnServerPort}";
            }
            else
            {
                // Для TCP оставляем явное указание транспорта
                _turnUrl = $"turn:{TurnSettings.TurnServerAddress}:{TurnSettings.TurnServerPort}?transport={transportType}";
            }

            _turnUsername = username;
            _turnPassword = password;

            LogTurnDiagnostics(transportType);
        }

        /// <summary>
        /// Полная диагностика TURN-кредов: пишет в Debug.WriteLine И в файл
        /// %AppData%\PISMO\turn_debug.log, чтобы можно было сверить байт-в-байт
        /// то, что приложение реально отправляет, с тем, что ожидает coturn.
        /// Если 401 продолжается — пришли содержимое этого файла.
        /// </summary>
        private void LogTurnDiagnostics(string transportType)
        {
            try
            {
                // Независимо пересчитываем HMAC здесь же, чтобы исключить
                // любое искажение между генерацией в TurnSettings и тем,
                // что попало в _turnUsername/_turnPassword.
                string secret = TurnSettings.TurnSecret ?? "";
                string verifyPassword = "";
                string verifyError = "";
                try
                {
                    if (!string.IsNullOrWhiteSpace(secret) && _turnUsername.Contains(":"))
                    {
                        byte[] keyBytes = System.Text.Encoding.UTF8.GetBytes(secret);
                        using var hmac = new System.Security.Cryptography.HMACSHA1(keyBytes);
                        byte[] hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(_turnUsername));
                        verifyPassword = Convert.ToBase64String(hash);
                    }
                }
                catch (Exception ex)
                {
                    verifyError = ex.Message;
                }

                bool passwordMatches = verifyPassword == _turnPassword;

                // Парсим timestamp из username, чтобы видеть просрочен он или нет
                string expiryInfo = "н/д";
                var parts = (_turnUsername ?? "").Split(':');
                if (parts.Length == 2 && long.TryParse(parts[0], out long expiryUnix))
                {
                    var expiryUtc = DateTimeOffset.FromUnixTimeSeconds(expiryUnix);
                    var nowUtc = DateTimeOffset.UtcNow;
                    var diff = expiryUtc - nowUtc;
                    expiryInfo = $"{expiryUtc:O} (через {diff.TotalHours:F1} ч от текущего момента; протух={diff.TotalSeconds < 0})";
                }

                string report =
                    $"=== TURN DEBUG [{DateTime.UtcNow:O}] ===\r\n" +
                    $"Enabled        : {TurnSettings.TurnEnabled}\r\n" +
                    $"Address:Port   : {TurnSettings.TurnServerAddress}:{TurnSettings.TurnServerPort}\r\n" +
                    $"TurnUrl        : {_turnUrl}\r\n" +
                    $"Transport      : {transportType}\r\n" +
                    $"Username       : [{_turnUsername}] (len={_turnUsername?.Length ?? 0})\r\n" +
                    $"Password       : [{_turnPassword}] (len={_turnPassword?.Length ?? 0})\r\n" +
                    $"Secret (head)  : {(secret.Length >= 8 ? secret.Substring(0, 8) : secret)}... (len={secret.Length})\r\n" +
                    $"Expiry         : {expiryInfo}\r\n" +
                    $"Self-check HMAC: {verifyPassword}\r\n" +
                    $"Совпадает?     : {passwordMatches}\r\n" +
                    (string.IsNullOrEmpty(verifyError) ? "" : $"Verify error   : {verifyError}\r\n") +
                    "==========================================";

                System.Diagnostics.Debug.WriteLine(report);

                if (!passwordMatches)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[WebRTC] ⚠️ ВНИМАНИЕ: пароль, переданный в WebRtcTransport, " +
                        "НЕ совпадает с тем, что HMAC должен дать для этого username+secret. " +
                        "Креды были подменены/искажены между генерацией и использованием.");
                }

                try
                {
                    string appData = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "PISMO");
                    System.IO.Directory.CreateDirectory(appData);
                    string logPath = System.IO.Path.Combine(appData, "turn_debug.log");
                    System.IO.File.AppendAllText(logPath, report + "\r\n\r\n", System.Text.Encoding.UTF8);
                }
                catch
                {
                    // не критично, если лог-файл не записался
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebRTC] Ошибка диагностики TURN: {ex.Message}");
            }
        }

        private string _tempHtmlDir;
        private const string VirtualHostName = "pismo-webrtc.local";

        /// <summary>Инициализирует WebView2 — должен вызываться в UI потоке</summary>
        public async Task InitAsync(Form parentForm)
        {
            // _webView должен оставаться полностью невидимым и за пределами
            // экрана — это транспортный движок WebRTC, не UI. Явно задаём
            // Location далеко за пределами видимой области и Anchor=None,
            // чтобы исключить пересчёт позиции/размера при ресайзе формы
            // (что могло привести к случайному отображению сырого
            // HTML/JS-документа на экране, как видно на скрине пользователя).
            // ВАЖНО: системный диалог выбора экрана/окна для getDisplayMedia()
            // это диалог уровня Windows, привязанный к HWND родительского
            // WebView2-контрола — это НЕ браузерный UI, в отличие от
            // permission-prompt для камеры/микрофона. При полностью невидимом
            // окне нулевого размера (Visible=false, 1×1) этот системный
            // диалог может не отрисовываться вообще, из-за чего "выбора
            // окон нет" даже после исправления secure-context проблемы.
            // Даём видимый размер, но размещаем далеко за пределами видимой
            // клиентской области формы, чтобы пользователь не видел сам
            // WebView2 в обычной работе звонка.
            // КРИТИЧНО: WebView должен быть Visible=true всегда, даже когда
            // спрятан за пределами экрана. requestAnimationFrame в JS полностью
            // останавливается когда элемент невидим (Visible=false), что
            // замораживает все frame-extraction loops (превью, извлечение кадров
            // из remote video, local camera preview) — пользователь видит
            // статичную картинку вместо живого видео.
            // Используем минимальный видимый размер 1×1 — этого достаточно
            // для Chromium чтобы не замораживать rAF, но пользователь ничего не видит.
            // Устанавливаем Size и MinimumSize в 1×1 — Chromium не скейлит
            // элемент к 0×0 при таком минимальном размере.
            _webView = new WebView2
            {
                Visible = true,
                Size = new System.Drawing.Size(1, 1),
                MinimumSize = new System.Drawing.Size(1, 1),
                Location = new System.Drawing.Point(-3000, -3000),
                Anchor = AnchorStyles.None
            };
            parentForm.Controls.Add(_webView);
            _webView.SendToBack();

            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;

            // КРИТИЧНО: WebView2, в отличие от обычного Chrome-окна, не выдаёт
            // доступ к камере/микрофону/демонстрации экрана автоматически.
            // Без этих обработчиков getUserMedia()/getDisplayMedia() либо тихо
            // отказывают, либо просто никогда не резолвятся — снаружи это
            // выглядит как "камера не работает" и "демка не запускается без
            // диалога выбора окна", хотя сам JS-код корректен.
            _webView.CoreWebView2.PermissionRequested += (s, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Camera ||
                    e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                {
                    e.State = CoreWebView2PermissionState.Allow;
                }
            };

            _webView.CoreWebView2.ScreenCaptureStarting += (s, e) =>
            {
                // Явно разрешаем показ системного UI выбора экрана/окна.
                // Документация WebView2 описывает Cancel как явный механизм
                // блокировки — оставлять его неявным (пустое тело обработчика)
                // могло давать непредсказуемое поведение в зависимости от
                // версии runtime, что и было вероятной причиной отсутствия
                // диалога выбора экрана.
                e.Cancel = false;
            };

            // Пробрасываем console.log / console.error из WebView2 в Debug-лог,
            // чтобы видеть реальные ошибки ICE/TURN из Chromium, а не только
            // то, что явно прислано через postMessage.
            try
            {
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    (function() {
                        const origLog = console.log;
                        const origError = console.error;
                        console.log = function(...args) {
                            origLog.apply(console, args);
                            try { window.chrome.webview.postMessage({ type: 'jsLog', text: args.map(String).join(' ') }); } catch(e) {}
                        };
                        console.error = function(...args) {
                            origError.apply(console, args);
                            try { window.chrome.webview.postMessage({ type: 'jsError', text: args.map(String).join(' ') }); } catch(e) {}
                        };
                    })();
                ");
            }
            catch { /* не критично */ }

            // КРИТИЧНО: NavigateToString() создаёт страницу с origin
            // about:blank/null, который Chromium НЕ считает secure context —
            // именно из-за этого navigator.mediaDevices был полностью
            // undefined (не отдельный метод, а весь объект), что и давало
            // ошибку "Cannot read properties of undefined (reading
            // 'getDisplayMedia')". getUserMedia/getDisplayMedia требуют
            // secure context (https: или localhost).
            //
            // Решение: пишем HTML во временный файл и грузим через
            // SetVirtualHostNameToFolderMapping — это даёт странице
            // настоящий https-подобный origin, при котором mediaDevices
            // полноценно доступен.
            string html = BuildHtml();
            _tempHtmlDir = Path.Combine(Path.GetTempPath(), "pismo_webrtc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempHtmlDir);
            string htmlPath = Path.Combine(_tempHtmlDir, "index.html");
            File.WriteAllText(htmlPath, html, System.Text.Encoding.UTF8);

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName, _tempHtmlDir, CoreWebView2HostResourceAccessKind.Allow);

            // Navigate() не возвращает Task — ждём NavigationCompleted явно,
            // чтобы гарантировать полную загрузку страницы и inline-скрипта
            // перед тем, как CallForm начнёт вызывать CreateOfferAsync() и
            // другие методы, которые шлют сообщения в JS.
            var navDone = new TaskCompletionSource<bool>();
            void OnNavDone(object s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _webView.CoreWebView2.NavigationCompleted -= OnNavDone;
                navDone.TrySetResult(e.IsSuccess);
            }
            _webView.CoreWebView2.NavigationCompleted += OnNavDone;
            _webView.CoreWebView2.Navigate($"https://{VirtualHostName}/index.html");
            await navDone.Task;
            await Task.Delay(200); // Даем JavaScript движку Chromium время на полную привязку addEventListener('message')

            _initialized = true;
        }

        private string BuildHtml()
        {
            // Сериализуем креды через JSON, а не вставляем как "сырые" строки в JS-литерал.
            // Это устраняет риск поломки синтаксиса, если username/password когда-либо
            // будут содержать кавычки, обратные слеши или другие спецсимволы.
            string turnUrlJson = JsonSerializer.Serialize(_turnUrl);
            string turnUsernameJson = JsonSerializer.Serialize(_turnUsername);
            string turnPasswordJson = JsonSerializer.Serialize(_turnPassword);

            string html = @"<!DOCTYPE html>
<html>
<head><meta charset='utf-8'></head>
<body>
<script>
let pc = null;
let dc = null;       // канал для control-сообщений и совместимости (сейчас не используется для видео)
let dcAudio = null;  // отдельный высокоприоритетный канал для аудио (микрофон + звук демки)

// Карта track.id -> 'screen'|'camera'. track.id одинаков на обеих сторонах
// WebRTC-соединения (сохраняется при передаче через RTCRtpSender/Receiver),
// поэтому достаточно передать эту карту через renegotiation-сигнал, и
// приёмная сторона будет точно знать, какой входящий трек к чему относится
// — надёжнее, чем угадывать по stream id или track.label.
let trackKindById = {};
let remoteTrackKindById = {}; // карта, присланная собеседником через renegotiation

// --- Демонстрация экрана: настоящий WebRTC video track, не DataChannel ---
let screenStream = null;     // локальный поток getDisplayMedia()
let screenSender = null;     // RTCRtpSender для трека демонстрации
let remoteScreenVideoEl = null;  // <video> для приёма удалённого видео-трека демки
let screenFrameLoopHandle = null;

// --- Камера: настоящий WebRTC video track вместо AForge+JPEG ---
let cameraStream = null;
let cameraSender = null;
let localCameraVideoEl = null;   // <video> для локального превью своей камеры
let remoteCameraVideoEl = null;  // <video> для приёма удалённого видео-трека камеры
let localCameraFrameLoopHandle = null;
let remoteCameraFrameLoopHandle = null;

const iceConfig = {
    iceServers: [
        { urls: 'stun:stun.l.google.com:19302' },
        {
            urls: __TURN_URL__,
            username: __TURN_USERNAME__,
            credential: __TURN_PASSWORD__
        }
    ],
    iceTransportPolicy: 'all' // Принудительно пускаем весь трафик через TURN-сервер
};

console.log('[WebRTC] iceConfig:', JSON.stringify(iceConfig));

// ИСПРАВЛЕНО: Убран JSON.stringify, так как WebView2 автоматически сериализует объекты,
// передаваемые в window.chrome.webview.postMessage
function post(msg) {
    window.chrome.webview.postMessage(msg);
}

function createPc() {
    pc = new RTCPeerConnection(iceConfig);

    pc.onicecandidate = (e) => {
        if (e.candidate) {
            post({ type: 'candidate', candidate: JSON.stringify(e.candidate) });
        } else {
            post({ type: 'gatheringComplete' });
        }
    };

    pc.onconnectionstatechange = () => {
        post({ type: 'connectionState', state: pc.connectionState });
    };

    pc.ondatachannel = (e) => {
        if (e.channel.label === 'audio') bindAudioDc(e.channel);
        else bindMainDc(e.channel);
    };

    pc.ontrack = (e) => {
        console.log('[DIAG] pc.ontrack СРАБОТАЛ, kind=', e.track.kind, 'trackId=', e.track.id, 'remoteTrackKindById=', JSON.stringify(remoteTrackKindById));
        if (e.track.kind !== 'video') return;
        const kind = remoteTrackKindById[e.track.id] || 'camera'; // camera как безопасный fallback
        const isScreen = kind === 'screen';
        console.log('[DIAG] resolved kind=', kind, 'isScreen=', isScreen);

        if (isScreen) {
            if (!remoteScreenVideoEl) {
                remoteScreenVideoEl = document.createElement('video');
                remoteScreenVideoEl.autoplay = true;
                remoteScreenVideoEl.muted = true;
                remoteScreenVideoEl.style.display = 'none';
                document.body.appendChild(remoteScreenVideoEl);
            }
            remoteScreenVideoEl.srcObject = e.streams[0];
            post({ type: 'remoteScreenStart' });
            startScreenFrameExtraction();
            e.track.onended = () => stopScreenFrameExtraction();
        } else {
            if (!remoteCameraVideoEl) {
                remoteCameraVideoEl = document.createElement('video');
                remoteCameraVideoEl.autoplay = true;
                remoteCameraVideoEl.muted = true;
                remoteCameraVideoEl.style.display = 'none';
                document.body.appendChild(remoteCameraVideoEl);
            }
            remoteCameraVideoEl.srcObject = e.streams[0];
            post({ type: 'remoteCameraStart' });
            startRemoteCameraFrameExtraction();
            e.track.onended = () => stopRemoteCameraFrameExtraction();
        }
    };
}

// Извлекаем кадры из удалённого <video> через canvas и шлём в C#.
// Это позволяет встроить настоящий, аппаратно декодированный WebRTC-видеопоток
// в существующий PictureBox-based UI без переписывания всего отображения.
// Декодирование самого видео (H.264) идёт через GPU силами Chromium —
// здесь только периодическое чтение уже готового декодированного кадра.
function startScreenFrameExtraction() {
    if (screenFrameLoopHandle) return;
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    let lastSent = 0;
    const targetIntervalMs = 1000 / 20;

    function loop() {
        if (!remoteScreenVideoEl || remoteScreenVideoEl.readyState < 2) {
            screenFrameLoopHandle = requestAnimationFrame(loop);
            return;
        }
        const now = performance.now();
        if (now - lastSent >= targetIntervalMs) {
            const vw = remoteScreenVideoEl.videoWidth, vh = remoteScreenVideoEl.videoHeight;
            if (vw > 0 && vh > 0) {
                if (canvas.width !== vw || canvas.height !== vh) {
                    canvas.width = vw; canvas.height = vh;
                }
                ctx.drawImage(remoteScreenVideoEl, 0, 0, vw, vh);
                canvas.toBlob((blob) => {
                    if (!blob) return;
                    const reader = new FileReader();
                    reader.onload = () => {
                        const b64 = reader.result.split(',')[1];
                        post({ type: 'remoteScreenFrame', data: b64 });
                    };
                    reader.readAsDataURL(blob);
                }, 'image/jpeg', 0.85);
                lastSent = now;
            }
        }
        screenFrameLoopHandle = requestAnimationFrame(loop);
    }
    screenFrameLoopHandle = requestAnimationFrame(loop);
}

function stopScreenFrameExtraction() {
    if (screenFrameLoopHandle) {
        cancelAnimationFrame(screenFrameLoopHandle);
        screenFrameLoopHandle = null;
    }
    if (remoteScreenVideoEl) {
        remoteScreenVideoEl.srcObject = null;
    }
    post({ type: 'remoteScreenStop' });
}

// То же самое для удалённой камеры собеседника.
function startRemoteCameraFrameExtraction() {
    if (remoteCameraFrameLoopHandle) return;
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    let lastSent = 0;
    const targetIntervalMs = 1000 / 20;

    function loop() {
        if (!remoteCameraVideoEl || remoteCameraVideoEl.readyState < 2) {
            remoteCameraFrameLoopHandle = requestAnimationFrame(loop);
            return;
        }
        const now = performance.now();
        if (now - lastSent >= targetIntervalMs) {
            const vw = remoteCameraVideoEl.videoWidth, vh = remoteCameraVideoEl.videoHeight;
            if (vw > 0 && vh > 0) {
                if (canvas.width !== vw || canvas.height !== vh) {
                    canvas.width = vw; canvas.height = vh;
                }
                ctx.drawImage(remoteCameraVideoEl, 0, 0, vw, vh);
                canvas.toBlob((blob) => {
                    if (!blob) return;
                    const reader = new FileReader();
                    reader.onload = () => {
                        const b64 = reader.result.split(',')[1];
                        post({ type: 'remoteCameraFrame', data: b64 });
                    };
                    reader.readAsDataURL(blob);
                }, 'image/jpeg', 0.85);
                lastSent = now;
            }
        }
        remoteCameraFrameLoopHandle = requestAnimationFrame(loop);
    }
    remoteCameraFrameLoopHandle = requestAnimationFrame(loop);
}

function stopRemoteCameraFrameExtraction() {
    if (remoteCameraFrameLoopHandle) {
        cancelAnimationFrame(remoteCameraFrameLoopHandle);
        remoteCameraFrameLoopHandle = null;
    }
    if (remoteCameraVideoEl) {
        remoteCameraVideoEl.srcObject = null;
    }
    post({ type: 'remoteCameraStop' });
}

// --- Демонстрация экрана: двухфазный флоу. Сначала previewScreen()
// захватывает поток (показывает системный диалог выбора экрана/окна) и
// шлёт кадры в C# для окна превью с
// уже захвачен, но ЕЩЁ НЕ добавлен в PeerConnection, так что собеседник
// ничего не видит до подтверждения. confirmScreenShare() добавляет тот же
// поток в PeerConnection. cancelScreenPreview() останавливает захват, если
// пользовател
let screenPreviewLoopHandle = null;
let screenPreviewVideoEl = null;

async function previewScreen() {
    try {
        screenStream = await navigator.mediaDevices.getDisplayMedia({
            video: { frameRate: { ideal: 24, max: 30 } },
            audio: false
        });
        const track = screenStream.getVideoTracks()[0];
        track.onended = () => {
            // Пользователь остановил демку через системный диалог Chrome
            // ('Stop sharing') — работает и во время превью, и после
            // подтверждения (когда screenSender уже существует).
            if (screenSender) stopScreenShareTrack();
            else cancelScreenPreview();
        };

        if (!screenPreviewVideoEl) {
            screenPreviewVideoEl = document.createElement('video');
            screenPreviewVideoEl.autoplay = true;
            screenPreviewVideoEl.muted = true;
            screenPreviewVideoEl.style.display = 'none';
            document.body.appendChild(screenPreviewVideoEl);
        }
        screenPreviewVideoEl.srcObject = screenStream;
        startScreenPreviewFrameLoop();
        post({ type: 'screenPreviewReady' });
    } catch (err) {
        post({ type: 'localScreenError', error: String(err) });
    }
}

function startScreenPreviewFrameLoop() {
    if (screenPreviewLoopHandle) return;
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    let lastSent = 0;
    const targetIntervalMs = 1000 / 10; // превью не нужен высокий fps

    function loop() {
        if (!screenPreviewVideoEl || screenPreviewVideoEl.readyState < 2) {
            screenPreviewLoopHandle = requestAnimationFrame(loop);
            return;
        }
        const now = performance.now();
        if (now - lastSent >= targetIntervalMs) {
            const vw = screenPreviewVideoEl.videoWidth, vh = screenPreviewVideoEl.videoHeight;
            if (vw > 0 && vh > 0) {
                // Снижаем размер холста для превью до 400px по ширине.
                // Это снижает объем IPC данных в десятки раз и устраняет лаги аудио!
                const targetW = Math.min(vw, 400);
                const targetH = Math.round(vh * (targetW / vw));
                if (canvas.width !== targetW || canvas.height !== targetH) { canvas.width = targetW; canvas.height = targetH; }
                ctx.drawImage(screenPreviewVideoEl, 0, 0, targetW, targetH);
                canvas.toBlob((blob) => {
                    if (!blob) return;
                    const reader = new FileReader();
                    reader.onload = () => post({ type: 'screenPreviewFrame', data: reader.result.split(',')[1] });
                    reader.readAsDataURL(blob);
                }, 'image/jpeg', 0.7);
                lastSent = now;
            }
        }
        screenPreviewLoopHandle = requestAnimationFrame(loop);
    }
    screenPreviewLoopHandle = requestAnimationFrame(loop);
}

function stopScreenPreviewFrameLoop() {
    if (screenPreviewLoopHandle) { cancelAnimationFrame(screenPreviewLoopHandle); screenPreviewLoopHandle = null; }
    if (screenPreviewVideoEl) screenPreviewVideoEl.srcObject = null;
}

async function confirmScreenShare() {
    try {
        if (!screenStream) { post({ type: 'localScreenError', error: 'no preview stream' }); return; }
        // Preview frame loop НЕ останавливаем — кадры продолжают идти и
        // после подтверждения, чтобы 
        // работал в течение всей демонстрации, не только до подтверждения.
        const track = screenStream.getVideoTracks()[0];

        screenSender = pc.addTrack(track, screenStream);
        trackKindById[track.id] = 'screen';

        // Щедрый битрейт для экрана — сервер пропускает ~2 Гбит, не экономим.
        const params = screenSender.getParameters();
        // ВАЖНО: getParameters() сразу после addTrack() может вернуть
        // encodings как пустой массив (length 0), а не undefined — пустой
        // массив truthy в JS, поэтому пров
        // пропускала этот случай, и params.encodings[0] был undefined.
        if (!params.encodings || params.encodings.length === 0) params.encodings = [{}];
        params.encodings[0].maxBitrate = 8_000_000; // 8 Мбит/с — заметно лучше JPEG-пайплайна
        await screenSender.setParameters(params);

        await renegotiate();
        post({ type: 'localScreenStarted' });
    } catch (err) {
        post({ type: 'localScreenError', error: String(err) });
    }
}

function cancelScreenPreview() {
    stopScreenPreviewFrameLoop();
    if (screenStream) {
        screenStream.getTracks().forEach(t => t.stop());
        screenStream = null;
    }
    post({ type: 'localScreenStopped' });
}

async function stopScreenShareTrack() {
    try {
        if (screenSender) {
            pc.removeTrack(screenSender);
            screenSender = null;
        }
        if (screenStream) {
            screenStream.getTracks().forEach(t => t.stop());
            screenStream = null;
        }
        stopScreenPreviewFrameLoop();
        await renegotiate();
    } catch (err) {
        console.error('stopScreenShareTrack error', err);
    }
    post({ type: 'localScreenStopped' });
}

// --- Камера: getUserMedia + addTrack, с выбором устройства по имени (label),
// чтобы сохранить совместимость с текущим выбором камеры в настройках
// приложения (DeviceSettings.CameraName), сделанным ДО звонка.
async function previewCamera(deviceLabel) {
    try {
        // КРИТИЧНО: в Chromium имена устройств (label) скрыты (пустые строки) до тех пор,
        // пока разрешение на камеру/микрофон не получено хотя бы раз. Сначала запрашиваем
        // временный поток, чтобы разблокировать label, иначе поиск по deviceLabel не сработает!
        try {
            const tempStream = await navigator.mediaDevices.getUserMedia({ video: true, audio: false });
            tempStream.getTracks().forEach(t => t.stop());
        } catch(e) {}

        let deviceId = null;
        if (deviceLabel) {
            const devices = await navigator.mediaDevices.enumerateDevices();
            const match = devices.find(d => d.kind === 'videoinput' && d.label === deviceLabel);
            if (match) deviceId = match.deviceId;
        }
        await openCameraStream(deviceId);
        post({ type: 'cameraPreviewReady' });
    } catch (err) {
        post({ type: 'localCameraError', error: String(err) });
    }
}

async function openCameraStream(deviceId) {
    const constraints = {
        video: deviceId ? { deviceId: { exact: deviceId } } : true,
        audio: false
    };
    if (cameraStream) {
        cameraStream.getTracks().forEach(t => t.stop());
    }
    cameraStream = await navigator.mediaDevices.getUserMedia(constraints);
    const track = cameraStream.getVideoTracks()[0];
    track.onended = () => {
        if (cameraSender) stopCameraTrack();
        else cancelCameraPreview();
    };

    if (!localCameraVideoEl) {
        localCameraVideoEl = document.createElement('video');
        localCameraVideoEl.autoplay = true;
        localCameraVideoEl.muted = true;
        localCameraVideoEl.style.display = 'none';
        document.body.appendChild(localCameraVideoEl);
    }
    localCameraVideoEl.srcObject = cameraStream;
    startLocalCameraFrameExtraction(); // используется и для превью, и после подтверждения
}

// Смена устройства камеры прямо во время превью или во время уже идущей
// демонстрации — переоткрывает поток на новом устройстве; если трек уже
// добавлен в PeerConnection (cameraSender существует), заменяем его через
// replaceTrack без полной renegotiation.
async function switchCameraDevice(deviceLabel) {
    try {
        const devices = await navigator.mediaDevices.enumerateDevices();
        const match = devices.find(d => d.kind === 'videoinput' && d.label === deviceLabel);
        const deviceId = match ? match.deviceId : null;

        const wasLive = !!cameraSender;
        await openCameraStream(deviceId);

        if (wasLive) {
            const newTrack = cameraStream.getVideoTracks()[0];
            await cameraSender.replaceTrack(newTrack);
            trackKindById[newTrack.id] = 'camera';
        }
        post({ type: 'cameraDeviceSwitched' });
    } catch (err) {
        post({ type: 'localCameraError', error: String(err) });
    }
}

async function confirmCameraShare() {
    console.log('[DIAG] confirmCameraShare() вызван, cameraStream =', cameraStream ? 'есть' : 'NULL');
    try {
        if (!cameraStream) { post({ type: 'localCameraError', error: 'no preview stream' }); return; }
        const track = cameraStream.getVideoTracks()[0];
        console.log('[DIAG] track =', track ? track.readyState : 'NULL');

        cameraSender = pc.addTrack(track, cameraStream);
        trackKindById[track.id] = 'camera';
        console.log('[DIAG] addTrack успешен, вызываю renegotiate()');

        await renegotiate();
        console.log('[DIAG] renegotiate() завершён успешно');
        post({ type: 'localCameraStarted' });
    } catch (err) {
        console.log('[DIAG] confirmCameraShare ОШИБКА:', String(err));
        post({ type: 'localCameraError', error: String(err) });
    }
}

function cancelCameraPreview() {
    stopLocalCameraFrameExtraction();
    if (cameraStream) {
        cameraStream.getTracks().forEach(t => t.stop());
        cameraStream = null;
    }
    post({ type: 'localCameraStopped' });
}

async function stopCameraTrack() {
    try {
        if (cameraSender) {
            pc.removeTrack(cameraSender);
            cameraSender = null;
        }
        if (cameraStream) {
            cameraStream.getTracks().forEach(t => t.stop());
            cameraStream = null;
        }
        stopLocalCameraFrameExtraction();
        await renegotiate();
    } catch (err) {
        console.error('stopCameraTrack error', err);
    }
    post({ type: 'localCameraStopped' });
}

// Список доступных камер/микрофонов для device picker во время звонка.
// label доступен только после того, как разрешение на медиа уже было
// выдано хотя бы раз в этой сессии (см. PermissionRequested на стороне
// C#, который выдаёт его автоматически).
async function enumerateAndPostDevices() {
    try {
        // Сначала запрашиваем временный поток, чтобы разблокировать label в Chromium
        try {
            const tempStream = await navigator.mediaDevices.getUserMedia({ video: true, audio: true });
            tempStream.getTracks().forEach(t => t.stop());
        } catch(e) {}

        const devices = await navigator.mediaDevices.enumerateDevices();
        const cameras = devices.filter(d => d.kind === 'videoinput').map(d => d.label).filter(Boolean);
        const mics = devices.filter(d => d.kind === 'audioinput').map(d => d.label).filter(Boolean);
        post({ type: 'devicesEnumerated', cameras: JSON.stringify(cameras), mics: JSON.stringify(mics) });
    } catch (err) {
        console.error('enumerateAndPostDevices error', err);
    }
}

function startLocalCameraFrameExtraction() {
    if (localCameraFrameLoopHandle) return;
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    let lastSent = 0;
    const targetIntervalMs = 1000 / 15; // локальное превью, 15fps достаточно

    function loop() {
        if (!localCameraVideoEl || localCameraVideoEl.readyState < 2) {
            localCameraFrameLoopHandle = requestAnimationFrame(loop);
            return;
        }
        const now = performance.now();
        if (now - lastSent >= targetIntervalMs) {
            const vw = localCameraVideoEl.videoWidth, vh = localCameraVideoEl.videoHeight;
            if (vw > 0 && vh > 0) {
                // Снижаем размер холста для локального превью до 320px по ширине.
                // Это снижает объем IPC данных в 10-20 раз и полностью убирает задержку звука!
                const targetW = Math.min(vw, 320);
                const targetH = Math.round(vh * (targetW / vw));
                if (canvas.width !== targetW || canvas.height !== targetH) {
                    canvas.width = targetW; canvas.height = targetH;
                }
                ctx.drawImage(localCameraVideoEl, 0, 0, targetW, targetH);
                canvas.toBlob((blob) => {
                    if (!blob) return;
                    const reader = new FileReader();
                    reader.onload = () => {
                        const b64 = reader.result.split(',')[1];
                        post({ type: 'localCameraFrame', data: b64 });
                    };
                    reader.readAsDataURL(blob);
                }, 'image/jpeg', 0.7);
                lastSent = now;
            }
        }
        localCameraFrameLoopHandle = requestAnimationFrame(loop);
    }
    localCameraFrameLoopHandle = requestAnimationFrame(loop);
}

function stopLocalCameraFrameExtraction() {
    if (localCameraFrameLoopHandle) {
        cancelAnimationFrame(localCameraFrameLoopHandle);
        localCameraFrameLoopHandle = null;
    }
    if (localCameraVideoEl) {
        localCameraVideoEl.srcObject = null;
    }
}

// Renegotiation: при добавлении/удалении трека после первоначального
// offer/answer нужен новый цикл SDP, иначе изменение не дойдёт до собеседника.
// Сторона, инициировавшая изменение, шлёт новый offer через тот же
// сигнальный канал (БД), который уже используется для первоначального обмена.
async function renegotiate() {
    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    // КРИТИЧНО: НЕ ждём завершения ICE gathering при renegotiation!
    // При renegotiation соединение уже установлено, и trickle ICE
    // автоматически доставит новые кандидаты собеседнику через события
    // onicecandidate (которые уже отправляются в C# и пишутся в БД).
    // Ожидание gathering completion здесь (waitGathering) было причиной
    // разрыва звонков: 10-секундный таймаут не успевал собрать все
    // TURN-кандидаты при renegotiation камеры/демонстрации, SDP уходил
    // с неполными кандидатами → connection state failed.
    post({ type: 'renegotiationOffer', sdp: pc.localDescription.sdp, trackKinds: JSON.stringify(trackKindById) });
}

async function applyRenegotiationOffer(offerSdp, trackKindsJson) {
    console.log('[DIAG] applyRenegotiationOffer вызван, trackKindsJson=', trackKindsJson);
    if (trackKindsJson) {
        try { Object.assign(remoteTrackKindById, JSON.parse(trackKindsJson)); } catch(e) { console.log('[DIAG] ошибка парсинга trackKindsJson:', String(e)); }
    }
    console.log('[DIAG] remoteTrackKindById после применения:', JSON.stringify(remoteTrackKindById));
    await pc.setRemoteDescription({ type: 'offer', sdp: offerSdp });
    console.log('[DIAG] setRemoteDescription(offer) завершён, senders/receivers count:', pc.getReceivers().length);
    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);
    // КРИТИЧНО: НЕ ждём завершения ICE gathering — trickle ICE автоматически
    // доставит кандидаты через onicecandidate события (уже отправляются в C#).
    // Ожидание gathering completion было причиной разрыва звонков.
    post({ type: 'renegotiationAnswer', sdp: pc.localDescription.sdp, trackKinds: JSON.stringify(trackKindById) });
}

async function applyRenegotiationAnswer(answerSdp, trackKindsJson) {
    if (trackKindsJson) {
        try { Object.assign(remoteTrackKindById, JSON.parse(trackKindsJson)); } catch(e) {}
    }
    await pc.setRemoteDescription({ type: 'answer', sdp: answerSdp });
}

function bindMainDc(channel) {
    dc = channel;
    dc.binaryType = 'arraybuffer';
    dc.onopen = () => post({ type: 'dcOpen' });
    dc.onclose = () => post({ type: 'dcClose' });
    dc.onmessage = (e) => {
        let arr = new Uint8Array(e.data);
        let b64 = btoa(String.fromCharCode(...arr));
        post({ type: 'data', data: b64 });
    };
}

function bindAudioDc(channel) {
    dcAudio = channel;
    dcAudio.binaryType = 'arraybuffer';
    dcAudio.onmessage = (e) => {
        let arr = new Uint8Array(e.data);
        let b64 = btoa(String.fromCharCode(...arr));
        post({ type: 'data', data: b64 });
    };
}

async function createOffer() {
    createPc();
    dc = pc.createDataChannel('media', { ordered: false });
    bindMainDc(dc);
    dcAudio = pc.createDataChannel('audio', { ordered: false, maxRetransmits: 0, priority: 'high' });
    bindAudioDc(dcAudio);
    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    await waitGathering();
    post({ type: 'offerReady', sdp: pc.localDescription.sdp });
}

async function createAnswer(offerSdp) {
    createPc();
    await pc.setRemoteDescription({ type: 'offer', sdp: offerSdp });
    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);
    await waitGathering();
    post({ type: 'answerReady', sdp: pc.localDescription.sdp });
}

function applyAnswer(answerSdp) {
    pc.setRemoteDescription({ type: 'answer', sdp: answerSdp });
}

function addCandidate(candidateJson) {
    try {
        const c = JSON.parse(candidateJson);
        pc.addIceCandidate(c);
    } catch(e) { console.error(e); }
}

function sendData(b64) {
    const binary = atob(b64);
    const arr = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) arr[i] = binary.charCodeAt(i);

    const packetType = arr[0];
    const isAudio = (packetType === 1 || packetType === 4); // TypeAudio, TypeScreenAudio

    if (isAudio && dcAudio && dcAudio.readyState === 'open') {
        dcAudio.send(arr.buffer);
    } else if (dc && dc.readyState === 'open') {
        dc.send(arr.buffer);
    }
}

function waitGathering() {
    return new Promise(resolve => {
        if (pc.iceGatheringState === 'complete') { resolve(); return; }
        const check = () => {
            if (pc.iceGatheringState === 'complete') {
                pc.removeEventListener('icegatheringstatechange', check);
                resolve();
            }
        };
        pc.addEventListener('icegatheringstatechange', check);
        setTimeout(resolve, 10000);
    });
}

window.chrome.webview.addEventListener('message', (e) => {
    const msg = JSON.parse(e.data);
    console.log('[DIAG] получена команда:', msg.cmd);
    if (msg.cmd === 'createOffer') createOffer();
    else if (msg.cmd === 'createAnswer') createAnswer(msg.sdp);
    else if (msg.cmd === 'applyAnswer') applyAnswer(msg.sdp);
    else if (msg.cmd === 'addCandidate') addCandidate(msg.candidate);
    else if (msg.cmd === 'send') sendData(msg.data);
    else if (msg.cmd === 'previewScreen') previewScreen();
    else if (msg.cmd === 'confirmScreenShare') confirmScreenShare();
    else if (msg.cmd === 'cancelScreenPreview') cancelScreenPreview();
    else if (msg.cmd === 'stopScreenTrack') stopScreenShareTrack();
    else if (msg.cmd === 'previewCamera') previewCamera(msg.deviceLabel);
    else if (msg.cmd === 'confirmCameraShare') confirmCameraShare();
    else if (msg.cmd === 'cancelCameraPreview') cancelCameraPreview();
    else if (msg.cmd === 'stopCameraTrack') stopCameraTrack();
    else if (msg.cmd === 'switchCameraDevice') switchCameraDevice(msg.deviceLabel);
    else if (msg.cmd === 'enumerateDevices') enumerateAndPostDevices();
    else if (msg.cmd === 'applyRenegotiationOffer') applyRenegotiationOffer(msg.sdp, msg.trackKinds);
    else if (msg.cmd === 'applyRenegotiationAnswer') applyRenegotiationAnswer(msg.sdp, msg.trackKinds);
});
</script>
</body>
</html>";
            return html.Replace("__TURN_URL__", turnUrlJson).Replace("__TURN_USERNAME__", turnUsernameJson).Replace("__TURN_PASSWORD__", turnPasswordJson);
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
                string type = msg.GetProperty("type").GetString();

                switch (type)
                {
                    case "offerReady":
                        _pendingOfferTcs?.TrySetResult(msg.GetProperty("sdp").GetString());
                        break;
                    case "answerReady":
                        _pendingAnswerTcs?.TrySetResult(msg.GetProperty("sdp").GetString());
                        break;
                    case "candidate":
                        IceCandidateReady?.Invoke(msg.GetProperty("candidate").GetString());
                        break;
                    case "gatheringComplete":
                        GatheringComplete?.Invoke();
                        break;
                    case "dcOpen":
                        Connected?.Invoke();
                        break;
                    case "dcClose":
                        Disconnected?.Invoke();
                        break;
                    case "connectionState":
                        var state = msg.GetProperty("state").GetString();
                        System.Diagnostics.Debug.WriteLine($"[WebRTC] Connection state: {state}");
                        if (state == "failed" || state == "closed" || state == "disconnected")
                            Disconnected?.Invoke();
                        break;
                    case "jsLog":
                        System.Diagnostics.Debug.WriteLine($"[JS console.log] {msg.GetProperty("text").GetString()}");
                        break;
                    case "jsError":
                        System.Diagnostics.Debug.WriteLine($"[JS console.error] {msg.GetProperty("text").GetString()}");
                        break;
                    case "remoteScreenFrame":
                        {
                            var b64Frame = msg.GetProperty("data").GetString();
                            var frameBytes = Convert.FromBase64String(b64Frame);
                            RemoteScreenFrameReceived?.Invoke(frameBytes);
                        }
                        break;
                    case "remoteScreenStart":
                        RemoteScreenStarted?.Invoke();
                        break;
                    case "remoteScreenStop":
                        RemoteScreenStopped?.Invoke();
                        break;
                    case "localScreenStarted":
                        LocalScreenStarted?.Invoke();
                        break;
                    case "localScreenStopped":
                        LocalScreenStopped?.Invoke();
                        break;
                    case "localScreenError":
                        LocalScreenError?.Invoke(msg.GetProperty("error").GetString());
                        break;
                    case "renegotiationOffer":
                        {
                            string tkOffer = msg.TryGetProperty("trackKinds", out var tkOfferEl) ? tkOfferEl.GetString() : null;
                            RenegotiationOfferReady?.Invoke(msg.GetProperty("sdp").GetString(), tkOffer);
                        }
                        break;
                    case "renegotiationAnswer":
                        {
                            string tkAnswer = msg.TryGetProperty("trackKinds", out var tkAnswerEl) ? tkAnswerEl.GetString() : null;
                            RenegotiationAnswerReady?.Invoke(msg.GetProperty("sdp").GetString(), tkAnswer);
                        }
                        break;
                    case "localCameraFrame":
                        {
                            var b64LocalCam = msg.GetProperty("data").GetString();
                            LocalCameraFrameReceived?.Invoke(Convert.FromBase64String(b64LocalCam));
                        }
                        break;
                    case "remoteCameraFrame":
                        {
                            var b64RemoteCam = msg.GetProperty("data").GetString();
                            RemoteCameraFrameReceived?.Invoke(Convert.FromBase64String(b64RemoteCam));
                        }
                        break;
                    case "remoteCameraStart":
                        RemoteCameraStarted?.Invoke();
                        break;
                    case "remoteCameraStop":
                        RemoteCameraStopped?.Invoke();
                        break;
                    case "localCameraStarted":
                        LocalCameraStarted?.Invoke();
                        break;
                    case "localCameraStopped":
                        LocalCameraStopped?.Invoke();
                        break;
                    case "localCameraError":
                        LocalCameraError?.Invoke(msg.GetProperty("error").GetString());
                        break;
                    case "screenPreviewFrame":
                        {
                            var b64ScreenPrev = msg.GetProperty("data").GetString();
                            ScreenPreviewFrameReceived?.Invoke(Convert.FromBase64String(b64ScreenPrev));
                        }
                        break;
                    case "screenPreviewReady":
                        ScreenPreviewReady?.Invoke();
                        break;
                    case "cameraPreviewReady":
                        CameraPreviewReady?.Invoke();
                        break;
                    case "cameraDeviceSwitched":
                        CameraDeviceSwitched?.Invoke();
                        break;
                    case "devicesEnumerated":
                        {
                            string camsJson = msg.TryGetProperty("cameras", out var camsEl) ? camsEl.GetString() : "[]";
                            string micsJson = msg.TryGetProperty("mics", out var micsEl) ? micsEl.GetString() : "[]";
                            DevicesEnumerated?.Invoke(camsJson, micsJson);
                        }
                        break;
                    case "data":
                        var b64 = msg.GetProperty("data").GetString();
                        var bytes = Convert.FromBase64String(b64);
                        if (bytes.Length >= 1)
                        {
                            byte pktType = bytes[0];
                            var payload = new byte[bytes.Length - 1];
                            if (payload.Length > 0)
                                Buffer.BlockCopy(bytes, 1, payload, 0, payload.Length);
                            FrameReceived?.Invoke(pktType, payload);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebRTC MSG ERROR] {ex.Message}");
            }
        }

        private TaskCompletionSource<string> _pendingOfferTcs;
        private TaskCompletionSource<string> _pendingAnswerTcs;

        public async Task<string> CreateOfferAsync()
        {
            _pendingOfferTcs = new TaskCompletionSource<string>();
            SendToJs(JsonSerializer.Serialize(new { cmd = "createOffer" }));
            return await _pendingOfferTcs.Task;
        }

        public async Task<string> CreateAnswerAsync(string offerSdp)
        {
            _pendingAnswerTcs = new TaskCompletionSource<string>();
            SendToJs(JsonSerializer.Serialize(new { cmd = "createAnswer", sdp = offerSdp }));
            return await _pendingAnswerTcs.Task;
        }

        public void ApplyAnswer(string answerSdp)
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "applyAnswer", sdp = answerSdp }));
        }

        public void AddRemoteIceCandidate(string candidateJson)
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "addCandidate", candidate = candidateJson }));
        }

        // --- Демонстрация экрана: двухфазный флоу (превью -> подтверждение) ---

        /// <summary>Запускает захват экрана (системный диалог выбора экрана/окна),
        /// но НЕ добавляет трек в PeerConnection — собеседник пока ничего не видит.
        /// Кадры идут в ScreenPreviewFrameReceived для показа в окне превью с
        /// кнопками "Включить"/"Отмена".</summary>
        private Form _pickerWindow;
        private Form _originalParentForm;

        public void PreviewScreen()
        {
            // Системный диалог выбора экрана/окна — это диалог уровня Windows,
            // привязанный к HWND родительского окна WebView2-контрола. При
            // оставлении _webView в форме звонка с минимальным размером
            // диалог физически не может отрисоваться видимым образом.
            //
            // Переносим _webView в отдельное временное топ-левел окно ТОЛЬКО
            // на время показа диалога — без какой-либо "своей" формы
            // подтверждения поверх него (только нативный Windows picker).
            // Сразу после получения потока (или ошибки/отмены) контрол
            // возвращается в исходную форму через HideTransportWindow().
            try
            {
                _originalParentForm = _webView.FindForm();

                _pickerWindow = new Form
                {
                    Text = "Демонстрация экрана",
                    StartPosition = FormStartPosition.CenterScreen,
                    Size = new System.Drawing.Size(960, 720),
                    FormBorderStyle = FormBorderStyle.Sizable,
                    ShowInTaskbar = true,
                    BackColor = System.Drawing.Color.FromArgb(20, 21, 23)
                };

                _originalParentForm?.Controls.Remove(_webView);
                _webView.Dock = DockStyle.Fill;
                _webView.Visible = true;
                _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(20, 21, 23);
                _pickerWindow.Controls.Add(_webView);

                _pickerWindow.FormClosed += (s, e) =>
                {
                    if (_pickerWindow != null) // окно реально закрыто пользователем (крестик), не нашим кодом
                    {
                        ReturnTransportWindowToOriginalParent();
                        // КРИТИЧНО: без этого события CallForm никогда не узнаёт
                        // об отмене через крестик — флаг ожидания (_screenPreviewPending)
                        // остаётся true навсегда, и повторное нажатие кнопки демки
                        // больше не срабатывает (именно это наблюдалось как баг).
                        LocalScreenError?.Invoke("user_cancelled_picker_window");
                    }
                };

                _pickerWindow.Show();
            }
            catch { }

            SendToJs(JsonSerializer.Serialize(new { cmd = "previewScreen" }));
        }

        /// <summary>Возвращает _webView обратно в форму звонка после того, как
        /// системный диалог выбора экрана закрылся (поток получен, либо
        /// произошла ошибка/отмена). Само окно-обёртку закрывает и освобождает.</summary>
        public void HideTransportWindow()
        {
            ReturnTransportWindowToOriginalParent();
        }

        private void ReturnTransportWindowToOriginalParent()
        {
            try
            {
                if (_pickerWindow == null) return;

                var pickerToClose = _pickerWindow;
                _pickerWindow = null; // обнуляем первым, чтобы FormClosed выше не сработал повторно

                pickerToClose.Controls.Remove(_webView);
                _webView.Dock = DockStyle.None;
                _webView.Visible = true;
                _webView.Size = new System.Drawing.Size(1, 1);
                _webView.Location = new System.Drawing.Point(-3000, -3000);

                _originalParentForm?.Controls.Add(_webView);
                _webView.SendToBack();

                try { pickerToClose.Close(); } catch { }
                pickerToClose.Dispose();
            }
            catch { }
        }

        /// <summary>Подтверждает демонстрацию — уже захваченный превью-поток
        /// добавляется в PeerConnection, аппаратное кодирование/декодирование
        /// выполняет Chromium через GPU.</summary>
        public void ConfirmScreenShare()
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "confirmScreenShare" }));
        }

        /// <summary>Отменяет превью без добавления трека — останавливает захват.</summary>
        public void CancelScreenPreview()
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "cancelScreenPreview" }));
        }

        public void StopScreenShareTrack()
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "stopScreenTrack" }));
        }

        // --- Камера: тот же двухфазный флоу + смена устройства на лету ---

        /// <summary>Запускает захват камеры для превью (НЕ добавляет в PeerConnection).
        /// deviceLabel — точное имя устройства (совпадает с DeviceSettings.CameraName).
        /// Если null/не найдено — используется системная камера по умолчанию.</summary>
        public void PreviewCamera(string deviceLabel)
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "previewCamera", deviceLabel }));
        }

        public void ConfirmCameraShare()
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "confirmCameraShare" }));
        }

        public void CancelCameraPreview()
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "cancelCameraPreview" }));
        }

        public void StopCameraTrack()
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "stopCameraTrack" }));
        }

        /// <summary>Переключает камеру на другое устройство — работает и во время
        /// превью, и во время уже идущей передачи (через replaceTrack, без полной
        /// renegotiation, если трек уже передаётся собеседнику).</summary>
        public void SwitchCameraDevice(string deviceLabel)
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "switchCameraDevice", deviceLabel }));
        }

        /// <summary>Запрашивает список доступных камер/микрофонов из браузера —
        /// результат приходит через событие DevicesEnumerated. Названия устройств
        /// доступны только если разрешение на медиа уже было выдано (см.
        /// PermissionRequested в InitAsync, который выдаёт его автоматически).</summary>
        public void EnumerateDevices()
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "enumerateDevices" }));
        }

        /// <summary>Применяет offer renegotiation, полученный от собеседника через
        /// сигнальный канал (БД), и возвращает answer через RenegotiationAnswerReady.
        /// trackKindsJson — карта track.id->"screen"/"camera" от собеседника, нужна
        /// для надёжного различения треков камеры/демки в pc.ontrack.</summary>
        public void ApplyRenegotiationOffer(string offerSdp, string trackKindsJson = null)
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "applyRenegotiationOffer", sdp = offerSdp, trackKinds = trackKindsJson }));
        }

        public void ApplyRenegotiationAnswer(string answerSdp, string trackKindsJson = null)
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "applyRenegotiationAnswer", sdp = answerSdp, trackKinds = trackKindsJson }));
        }

        public void Send(byte type, byte[] payload)
        {
            var packet = new byte[1 + payload.Length];
            packet[0] = type;
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, packet, 1, payload.Length);
            string b64 = Convert.ToBase64String(packet);
            SendToJs(JsonSerializer.Serialize(new { cmd = "send", data = b64 }));
        }

        public void SendHangup()
        {
            Send(TypeHangup, Array.Empty<byte>());
        }

        private void SendToJs(string json)
        {
            if (_webView == null || _disposed) return;
            try
            {
                if (_webView.InvokeRequired)
                    // BeginInvoke вместо Invoke: не блокирует вызывающий поток (например,
                    // фоновый поток захвата экрана) в ожидании свободного UI-потока.
                    // Синхронный Invoke здесь вызывал затор всего приложения при перегрузке
                    // UI-потока во время демонстрации экрана.
                    _webView.BeginInvoke(() =>
                    {
                        try { _webView.CoreWebView2?.PostWebMessageAsString(json); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WebRTC SEND ERROR] {ex.Message}");
                        }
                    });
                else
                    _webView.CoreWebView2.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WebRTC SEND ERROR] {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _webView?.Dispose(); } catch { }
            try
            {
                if (!string.IsNullOrEmpty(_tempHtmlDir) && Directory.Exists(_tempHtmlDir))
                    Directory.Delete(_tempHtmlDir, recursive: true);
            }
            catch { /* не критично, ОС подчистит temp позже */ }
        }
    }
}