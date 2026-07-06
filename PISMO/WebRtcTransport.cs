using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text.Json;

namespace PISMO
{
    /// <summary>
    /// Транспорт звонков на основе LiveKit SFU (через браузерный движок WebView2 и
    /// официальный livekit-client JS SDK).
    ///
    /// LiveKit полностью заменяет прежний самописный WebRTC-пайплайн (coturn +
    /// offer/answer/ICE/renegotiation через БД). Сервер сам берёт на себя
    /// сигналинг, ICE/TURN и многосторонние звонки (SFU): все участники одной
    /// комнаты слышат и видят друг друга. Это решает сразу:
    ///   • баг с камерой — публикация/подписка треков единообразны на всех сторонах;
    ///   • баг с групповыми звонками («кто успел тот и съел») — нет привязки
    ///     ровно к двум caller/callee.
    ///
    /// Публичный контракт (события камеры/демки/превью/кадров) сохранён, чтобы
    /// существующий PictureBox/JPEG-based UI в CallForm работал без переписывания:
    /// кадры извлекаются из удалённых video-треков через canvas внутри WebView2.
    /// Аудио (микрофон и звук демонстрации) LiveKit передаёт и воспроизводит сам,
    /// поэтому старый путь через DataChannel + NAudio больше не используется.
    /// </summary>
    public class WebRtcTransport : IDisposable
    {
        private WebView2 _webView;
        private bool _disposed = false;

        public event Action Disconnected;
        public event Action Connected;
        public event Action RemoteParticipantLeft;

        // --- Многоучастниковая «плитка» (Discord-grid) ---
        public event Action<string, string> ParticipantJoined;   // (pid, name)
        public event Action<string> ParticipantLeftById;          // (pid)
        public event Action<string, string, string> RemoteTileStarted; // (pid, name, source: camera|screen)
        public event Action<string, string> RemoteTileStopped;    // (pid, source)
        public event Action<string, string, byte[]> RemoteTileFrame;  // (pid, source, jpeg)
        public event Action<string> ActiveSpeakers;               // JSON-массив pid говорящих
        public event Action<int> PingUpdated;                      // RTT в миллисекундах
        public event Action<string, string> ParticipantRenamed;    // (pid, новое имя)

        // --- Видео-трек демонстрации экрана ---
        public event Action<byte[]> RemoteScreenFrameReceived; // декодированный JPEG-кадр из видео-трека
        public event Action RemoteScreenStarted;
        public event Action RemoteScreenStopped;
        public event Action LocalScreenStarted;
        public event Action LocalScreenStopped;
        public event Action<string> LocalScreenError;

        // --- Камера ---
        public event Action<byte[]> LocalCameraFrameReceived;  // для своего превью (_pbLocal)
        public event Action<byte[]> RemoteCameraFrameReceived; // декодированный кадр камеры собеседника
        public event Action RemoteCameraStarted;
        public event Action RemoteCameraStopped;
        public event Action LocalCameraStarted;
        public event Action LocalCameraStopped;
        public event Action<string> LocalCameraError;

        // Диагностика захвата демонстрации (fps, ширина, высота).
        public event Action<int, int, int> ScreenCaptureInfo;

        /// <summary>Статистика отправки демки (что реально видят собеседники).</summary>
        public event Action<string> ScreenSendStats;

        /// <summary>Статистика приёма демки (что реально приходит зрителю).</summary>
        public event Action<string> ScreenRecvStats;
        // Запрос выхода из «театра» (двойной клик / крестик внутри WebView).
        public event Action TheaterExitRequested;
        // Запрос развернуть/свернуть театр на весь экран (кнопка ⛶).
        public event Action TheaterFullscreenToggle;

        // --- Превью перед включением ---
        public event Action<byte[]> ScreenPreviewFrameReceived;
        public event Action ScreenPreviewReady;
        public event Action CameraPreviewReady;
        public event Action CameraDeviceSwitched;
        public event Action<string, string, string> DevicesEnumerated; // (camerasJson, micsJson, speakersJson)

        private string _tempHtmlDir;
        private const string VirtualHostName = "pismo-webrtc.local";

        private string _pendingUrl;
        private string _pendingToken;

        public WebRtcTransport()
        {
        }

        /// <summary>Инициализирует WebView2 и подключается к комнате LiveKit —
        /// должен вызываться в UI потоке.</summary>
        /// <param name="parentForm">Родительская форма (для размещения скрытого WebView2).</param>
        /// <param name="livekitUrl">ws://host:port или wss://… адрес LiveKit-сервера.</param>
        /// <param name="token">JWT access-токен (см. LiveKitSettings.CreateToken).</param>
        public async Task InitAsync(Form parentForm, string livekitUrl, string token)
        {
            _pendingUrl = livekitUrl;
            _pendingToken = token;

            // WebView2 — транспортный движок, держим невидимым и за пределами
            // экрана. Visible=true обязателен: requestAnimationFrame останавливается
            // для невидимых элементов, что заморозило бы извлечение кадров видео.
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

            // КРИТИЧНО: страница грузится с https-origin (виртуальный хост) —
            // это нужно для secure context (getUserMedia/getDisplayMedia). Но
            // LiveKit-сервер работает по ws:// без TLS (без домена/сертификата),
            // а https-страница по умолчанию блокирует ws:// как mixed content.
            // --allow-running-insecure-content снимает эту блокировку, позволяя
            // подключиться к ws:// LiveKit, сохранив при этом secure context для
            // доступа к камере/микрофону/экрану.
            // Аппаратное ускорение (GPU) — управляется настройкой (для демонстрации
            // экрана/видео; на проблемных драйверах его можно выключить).
            // --disable-features=WebRtcAllowWgc…: новые Chromium/WebView2 захватывают
            // экран через WGC (Windows Graphics Capture), который ограничен ~30 fps
            // НЕЗАВИСИМО от запрошенной частоты (поэтому «60» в настройках давал
            // 30-33 fps у собеседников). Отключаем WGC → откат на DXGI Desktop
            // Duplication, который честно отдаёт 60 fps. Неизвестные имена фич
            // Chromium просто игнорирует (совместимо со старыми/новыми версиями).
            var envOptions = new CoreWebView2EnvironmentOptions(
                DeviceSettings.WebViewArgs(
                    "--allow-running-insecure-content --autoplay-policy=no-user-gesture-required" +
                    " --disable-features=WebRtcAllowWgcDesktopCapturer,WebRtcAllowWgcScreenCapturer,WebRtcAllowWgcWindowCapturer,WebRtcWgcRequireBorder"));
            var env = await CoreWebView2Environment.CreateAsync(null, null, envOptions);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.WebMessageReceived += OnWebMessage;

            // WebView2 не выдаёт доступ к камере/микрофону/экрану автоматически.
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
                e.Cancel = false;
            };

            // Пробрасываем console.log/error из Chromium в Debug-лог.
            try
            {
                await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    (function() {
                        const origLog = console.log, origError = console.error;
                        console.log = function(...a){ origLog.apply(console,a); try{ window.chrome.webview.postMessage({type:'jsLog',text:a.map(String).join(' ')}); }catch(e){} };
                        console.error = function(...a){ origError.apply(console,a); try{ window.chrome.webview.postMessage({type:'jsError',text:a.map(String).join(' ')}); }catch(e){} };
                    })();
                ");
            }
            catch { }

            // Страница грузится через SetVirtualHostNameToFolderMapping для
            // настоящего https-origin (secure context для mediaDevices).
            string html = BuildHtml();
            _tempHtmlDir = Path.Combine(Path.GetTempPath(), "pismo_livekit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempHtmlDir);
            string htmlPath = Path.Combine(_tempHtmlDir, "index.html");
            File.WriteAllText(htmlPath, html, System.Text.Encoding.UTF8);

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName, _tempHtmlDir, CoreWebView2HostResourceAccessKind.Allow);

            // Локальная папка noise рядом с exe (офлайн-Krisp), если присутствует.
            try
            {
                string noiseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "noise");
                if (Directory.Exists(noiseDir))
                    _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "pismo-noise.local", noiseDir, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch { }

            var navDone = new TaskCompletionSource<bool>();
            void OnNavDone(object s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _webView.CoreWebView2.NavigationCompleted -= OnNavDone;
                navDone.TrySetResult(e.IsSuccess);
            }
            _webView.CoreWebView2.NavigationCompleted += OnNavDone;
            _webView.CoreWebView2.Navigate($"https://{VirtualHostName}/index.html");
            await navDone.Task;
            await Task.Delay(200);

            // Сохранённые индивидуальные громкости/мьюты собеседников — ДО connect,
            // чтобы применились к любому участнику при подписке на его аудио (в т.ч.
            // без камеры и к тем, кто уже был в звонке).
            try
            {
                var prefs = UserAudioPrefs.Snapshot()
                    .Select(p => new { pid = p.pid, v = p.volume, m = p.muted }).ToArray();
                if (prefs.Length > 0)
                    SendToJs(JsonSerializer.Serialize(new { cmd = "voicePrefs", prefs }));
            }
            catch { }

            // Запускаем подключение к комнате.
            SendToJs(JsonSerializer.Serialize(new
            {
                cmd = "connect",
                url = _pendingUrl,
                token = _pendingToken,
                voiceAuto = DeviceSettings.VoiceAutoSensitivity,
                voiceThreshold = DeviceSettings.VoiceThreshold,
                noiseSuppress = DeviceSettings.NoiseSuppression,
                micGain = DeviceSettings.MicrophoneGain
            }));
        }

        private string BuildHtml()
        {
            // livekit-client грузится с CDN. Страница имеет https-origin, поэтому
            // внешний https-скрипт загружается без проблем. Если CDN недоступен —
            // событие connect завершится ошибкой, которая придёт в Disconnected.
            string html = @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<script src='https://cdn.jsdelivr.net/npm/livekit-client@2/dist/livekit-client.umd.min.js'></script>
</head>
<body>
<script>
let room = null;
let LK = null;

// Локальные треки.
let cameraTrack = null;     // LocalVideoTrack камеры (превью и/или опубликован)
let cameraPublished = false;
let screenVideoTrack = null;
let screenAudioTrack = null;
let screenPublished = false;
let screenQualityH = 1080;  // выбранное разрешение демонстрации (высота)
let screenQualityF = 30;    // выбранный fps демонстрации

// Скрытые <video> для извлечения кадров.
let localCameraVideoEl = null;
let remoteCameraVideoEl = null;
let remoteScreenVideoEl = null;
let screenPreviewVideoEl = null;

let localCameraLoop = null, remoteCameraLoop = null, remoteScreenLoop = null, screenPreviewLoop = null;

let remoteScreenAudioTrack = null;
let remoteScreenAudioVolume = 1.0;
let remoteVoiceTracks = [];   // голосовые аудио-треки всех собеседников
let remoteVoiceVolume = 1.0;
let remoteVoiceMuted = false;
let remoteAudioByPid = {};    // pid -> [audioTrack,...]
let perUserVolume = {};       // pid -> громкость 0..5 (усилитель до 500%)
let perUserMuted = {};        // pid -> bool

// ── Свой усилитель звука (WebAudio) ─────────────────────────────────────
// LiveKit-микшер (webAudioMix) выключен из-за бага «звук в одном ухе».
// Вместо него — собственный граф на КАЖДЫЙ трек: MediaStreamSource -> Gain
// (буст >100% возможен) -> общий лимитер -> выход. Моно корректно
// раскладывается в оба уха, а лимитер (DynamicsCompressor) убирает
// клиппинг/хрип при усилении.
let _mixCtx = null, _mixLimiter = null;
let mixByPid = {};            // pid -> [ {track, src, gain, el}, ... ]
let screenMix = null;         // { track, src, gain, el }

function mixCtx(){
    if (!_mixCtx){
        _mixCtx = new (window.AudioContext || window.webkitAudioContext)();
        _mixLimiter = _mixCtx.createDynamicsCompressor();
        _mixLimiter.threshold.value = -3;   // лимитер у потолка
        _mixLimiter.knee.value = 0;
        _mixLimiter.ratio.value = 20;
        _mixLimiter.attack.value = 0.003;
        _mixLimiter.release.value = 0.25;
        _mixLimiter.connect(_mixCtx.destination);
    }
    try { if (_mixCtx.state === 'suspended') _mixCtx.resume(); } catch(e){}
    return _mixCtx;
}

// Подключить аудио-трек через собственный GainNode (усилитель). Возвращает узел.
function attachAmplified(track, initialGain){
    const ctx = mixCtx();
    const mst = track.mediaStreamTrack || track;
    // Элемент нужен, чтобы Chromium не «засыпал» на WebRTC-дорожке; сам его глушим,
    // звук идёт ТОЛЬКО через наш GainNode (иначе был бы двойной звук).
    const el = track.attach();
    el.style.display = 'none';
    el.muted = true; el.volume = 0;
    document.body.appendChild(el);
    const source = ctx.createMediaStreamSource(new MediaStream([mst]));
    const gain = ctx.createGain();
    gain.gain.value = initialGain;
    source.connect(gain); gain.connect(_mixLimiter);
    return { track, src: source, gain, el };
}

function detachAmplified(node){
    if (!node) return;
    try { node.src.disconnect(); } catch(e){}
    try { node.gain.disconnect(); } catch(e){}
    try { if (node.el){ node.el.srcObject = null; node.el.remove(); } } catch(e){}
}

function post(msg){ window.chrome.webview.postMessage(msg); }

// Опции захвата микрофона с шумоподавлением/эхоподавлением/авто-усилением.
// Передаём и стандартные флаги, и Chromium-специфичные goog-констрейнты,
// чтобы шумодав точно включился в движке WebView2.
function micCaptureOpts(){
    // Если включён наш RNNoise — захватываем СЫРОЙ сигнал (без браузерного
    // noiseSuppression/AGC), иначе браузер уже почистит звук и RNNoise не на чем
    // будет показать разницу. Эхоподавление оставляем (оно про колонки, не про шум).
    return {
        echoCancellation: true,
        noiseSuppression: !useNoise,
        autoGainControl: !useNoise,
        deviceId: selectedMicId ? { exact: selectedMicId } : undefined
    };
}

// ── Микрофон: СТАНДАРТНЫЙ путь LiveKit + RNNoise-шумодав (processor) ──
// Микрофон — обычный setMicrophoneEnabled (mute работает штатно). Шум давит
// RNNoise (open-source, wasm самодостаточен → реально фильтрует), оформленный
// как LiveKit TrackProcessor (setProcessor), поэтому mute/голос не ломаются.
let useNoise = false;        // включать ли шумодав (из настроек)
let selectedMicId = undefined;
let _rnnoise = null;         // {mod, wasm, workletUrl}
let micGainValue = 1;        // множитель усиления из ползунка настроек
let _liveGain = null;        // живой GainNode текущего процессора
let gateThreshold = 0.02;    // порог шумового гейта (RMS, 0..1)

function localMicPub(){
    try { return room && room.localParticipant ? room.localParticipant.getTrackPublication(LK.Track.Source.Microphone) : null; }
    catch(e){ return null; }
}

// Загрузка @sapphi-red/web-noise-suppressor (RNNoise) с фолбэком по CDN.
async function loadRnnoise(){
    if (_rnnoise) return _rnnoise;
    const to = (p, ms) => Promise.race([p, new Promise((_,r)=>setTimeout(()=>r(new Error('timeout')), ms))]);
    // ВАЖНО: esm-модуль, wasm и worklet ДОЛЖНЫ грузиться из ОДНОГО источника/версии,
    // иначе RnnoiseWorkletNode и workletProcessor.js несовместимы -> узел молчит.
    const providers = [
        { esm:'https://pismo-noise.local/wns.mjs', base:'https://pismo-noise.local' },
        { esm:'https://esm.sh/@sapphi-red/web-noise-suppressor@0.3.5', base:'https://esm.sh/@sapphi-red/web-noise-suppressor@0.3.5/dist' },
        { esm:'https://cdn.jsdelivr.net/npm/@sapphi-red/web-noise-suppressor@0.3.5/+esm', base:'https://cdn.jsdelivr.net/npm/@sapphi-red/web-noise-suppressor@0.3.5/dist' },
        { esm:'https://unpkg.com/@sapphi-red/web-noise-suppressor@0.3.5/dist/index.mjs', base:'https://unpkg.com/@sapphi-red/web-noise-suppressor@0.3.5/dist' }
    ];
    let lastErr;
    for (const p of providers){
        try {
            const mod = await to(import(p.esm), 8000);
            if (!mod || !mod.loadRnnoise || !mod.RnnoiseWorkletNode) throw new Error('нет экспортов rnnoise');
            const wasm = await to(mod.loadRnnoise({ url: p.base + '/rnnoise.wasm', simdUrl: p.base + '/rnnoise_simd.wasm' }), 8000);
            const workletUrl = p.base + '/rnnoise/workletProcessor.js';
            _rnnoise = { mod, wasm, workletUrl };
            return _rnnoise;
        } catch(e){ lastErr = e; }
    }
    throw lastErr || new Error('rnnoise не загрузился');
}

// LiveKit-совместимый процессор на основе RNNoise.
function createRnnoiseProcessor(){
    let ctx, src, node, node2, dest, gain, gateTimer;
    return {
        name: 'rnnoise',
        processedTrack: undefined,
        async init(opts){
            const r = await loadRnnoise();
            ctx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
            try { if (ctx.state === 'suspended') await ctx.resume(); } catch(e){}
            await ctx.audioWorklet.addModule(r.workletUrl);
            src = ctx.createMediaStreamSource(new MediaStream([opts.track]));
            // ДВА прохода RNNoise — заметно сильнее давит клавиатуру/мышь/фон.
            node = new r.mod.RnnoiseWorkletNode(ctx, { wasmBinary: r.wasm, maxChannels: 1 });
            node2 = new r.mod.RnnoiseWorkletNode(ctx, { wasmBinary: r.wasm, maxChannels: 1 });
            // Усиление после шумодава: компенсирует выключенный браузерный AGC и
            // тихий выход RNNoise. Базовый множитель *1.6, дальше — ползунок настроек.
            gain = ctx.createGain();
            gain.gain.value = 1.6 * (micGainValue || 1);
            _liveGain = gain;
            dest = ctx.createMediaStreamDestination();
            src.connect(node).connect(node2).connect(gain);
            // Шумовой гейт: в паузах речи приглушает остаточные щелчки клавы/мыши.
            gateTimer = buildNoiseGate(ctx, gain, dest);
            this.processedTrack = dest.stream.getAudioTracks()[0];
        },
        async restart(opts){ await this.destroy(); await this.init(opts); },
        async destroy(){
            try { if (gateTimer) clearInterval(gateTimer); } catch(e){}
            try { node && node.disconnect(); } catch(e){}
            try { node2 && node2.disconnect(); } catch(e){}
            try { gain && gain.disconnect(); } catch(e){}
            try { src && src.disconnect(); } catch(e){}
            try { ctx && ctx.close(); } catch(e){}
            if (_liveGain === gain) _liveGain = null;
            this.processedTrack = undefined;
        }
    };
}

// Шумовой гейт: source -> gate -> destination. Анализирует RMS source и плавно
// приглушает звук, когда речи нет (target = floor), открывая при речи (=1).
// Порог берётся из gateThreshold (ползунок настроек, 0..1).
function buildNoiseGate(ctx, source, destination){
    const gate = ctx.createGain(); gate.gain.value = 1;
    const an = ctx.createAnalyser(); an.fftSize = 512;
    source.connect(an);
    source.connect(gate);
    gate.connect(destination);
    const buf = new Uint8Array(an.fftSize);
    let openUntil = 0;
    const timer = setInterval(() => {
        an.getByteTimeDomainData(buf);
        let s = 0; for (let i = 0; i < buf.length; i++){ const x = (buf[i]-128)/128; s += x*x; }
        const rms = Math.sqrt(s/buf.length);
        const now = ctx.currentTime;
        const thr = (typeof gateThreshold === 'number' ? gateThreshold : 0.02);
        if (rms > thr) openUntil = now + 0.25; // держим открытым 250мс после речи (hold)
        const open = now < openUntil;
        const target = open ? 1 : 0.03;        // floor: почти тишина в паузах
        gate.gain.setTargetAtTime(target, now, open ? 0.006 : 0.05);
    }, 20);
    return timer;
}

// Навесить/снять шумодав на текущий микрофонный трек.
async function applyNoiseFilter(){
    const pub = localMicPub();
    if (!pub || !pub.track) return;
    try {
        if (useNoise){
            const proc = createRnnoiseProcessor();
            await pub.track.setProcessor(proc);
            post({type:'jsLog', text:'RNNoise шумодав активен'});
            // Сторож: если обработанный трек молчит при наличии входного сигнала —
            // снимаем процессор, чтобы не потерять голос в звонке.
            try {
                const mt = pub.track.mediaStreamTrack;
                if (mt && proc.processedTrack){
                    const wctx = new (window.AudioContext||window.webkitAudioContext)();
                    try { if (wctx.state==='suspended') await wctx.resume(); } catch(e){}
                    const aOut = wctx.createAnalyser(); aOut.fftSize=512;
                    wctx.createMediaStreamSource(new MediaStream([proc.processedTrack])).connect(aOut);
                    const bo = new Uint8Array(aOut.fftSize);
                    let sum=0,n=0; const tm=setInterval(async()=>{
                        aOut.getByteTimeDomainData(bo);
                        let s=0; for(let i=0;i<bo.length;i++){const x=(bo[i]-128)/128;s+=x*x;}
                        sum+=Math.sqrt(s/bo.length); n++;
                        if(n>=40){ clearInterval(tm); try{ wctx.close(); }catch(e){}
                            if(sum<0.0008 && useNoise){
                                try{ await pub.track.stopProcessor(); }catch(e){}
                                post({type:'jsLog', text:'RNNoise молчал — откат на чистый микрофон'});
                            }
                        }
                    },50);
                }
            } catch(e){}
        } else {
            try { await pub.track.stopProcessor(); } catch(e){}
            post({type:'jsLog', text:'Шумодав выключен'});
        }
    } catch(e){
        post({type:'jsLog', text:'Шумодав недоступен: ' + String(e)});
    }
}

// Публикация микрофона — простой надёжный путь.
async function publishMic(){
    try { await room.localParticipant.setMicrophoneEnabled(true, micCaptureOpts()); }
    catch(e){ console.error('mic enable', String(e)); return; }
    // Навешиваем шумодав после публикации (через небольшую паузу — трек должен появиться).
    setTimeout(() => { applyNoiseFilter(); }, 300);
}

function setMicGain(v){
    micGainValue = (v && v > 0) ? v : 1;
    // Живое применение к текущему графу шумодава (если активен).
    try { if (_liveGain) _liveGain.gain.value = 1.6 * micGainValue; } catch(e){}
}

function setNoiseSuppression(on){
    useNoise = !!on;
    applyNoiseFilter();
}

function waitForLK(){
    return new Promise((resolve) => {
        const t0 = Date.now();
        (function check(){
            if (window.LivekitClient){ LK = window.LivekitClient; resolve(true); return; }
            if (Date.now() - t0 > 15000){ resolve(false); return; }
            setTimeout(check, 100);
        })();
    });
}

async function connectRoom(url, token, voiceAuto, voiceThreshold, noiseSuppress, gainVal){
    if (typeof noiseSuppress !== 'undefined') useNoise = !!noiseSuppress;
    if (typeof gainVal !== 'undefined' && gainVal > 0) micGainValue = gainVal;
    const ok = await waitForLK();
    if (!ok){ post({type:'fatal', error:'livekit-client не загрузился (нет интернета/CDN недоступен)'}); post({type:'disconnected'}); return; }
    try {
        // adaptiveStream/dynacast ОБЯЗАТЕЛЬНО выключены: при включённом
        // adaptiveStream LiveKit ставит видео на паузу, если <video> не виден
        // на экране — а мы намеренно держим элементы display:none и сами
        // извлекаем кадры в canvas. Из-за этого камера/демка не доходили до
        // собеседника ('ожидание видео'). С отключённым adaptiveStream видео
        // передаётся всегда. audioCaptureDefaults включают шумоподавление,
        // эхоподавление и авто-усиление микрофона.
        // webAudioMix ОБЯЗАТЕЛЬНО выключен: WebAudio-микшер LiveKit в Chromium
        // иногда «прибивает» моно-дорожку нового участника к одному каналу —
        // собеседника слышно только в левом/правом ухе (проявляется при 3+
        // участниках). Без микшера звук играет напрямую через <audio> —
        // моно корректно раскладывается в оба уха.
        room = new LK.Room({
            adaptiveStream: false,
            dynacast: false,
            webAudioMix: false,
            audioCaptureDefaults: {
                echoCancellation: true,
                noiseSuppression: true,
                autoGainControl: true
            }
        });

        room.on(LK.RoomEvent.TrackSubscribed, onTrackSubscribed);
        room.on(LK.RoomEvent.TrackUnsubscribed, onTrackUnsubscribed);
        room.on(LK.RoomEvent.Disconnected, () => post({type:'disconnected'}));
        room.on(LK.RoomEvent.Reconnected, () => post({type:'connected'}));
        room.on(LK.RoomEvent.ParticipantConnected, (p) => post({type:'participantJoined', pid:p.identity, name:pidName(p)}));
        room.on(LK.RoomEvent.ParticipantDisconnected, (p) => { cleanupParticipant(p.identity); post({type:'participantLeft', pid:p.identity}); post({type:'remoteLeft'}); });
        // Детектор говорящих — СВОЙ, локальный (см. startSpeakingMonitor):
        // считаем уровень звука каждого участника на клиенте, с задержкой на
        // появление и исчезновение. Серверный ActiveSpeakersChanged не используем
        // (он сбоит из-за нашей обработки микрофона: шумодав/гейт/выкл. AGC).
        try {
            room.on(LK.RoomEvent.ParticipantNameChanged, (name, p) => {
                try { post({type:'participantRenamed', pid: p.identity, name: name || pidName(p)}); } catch(e){}
            });
        } catch(e){}

        await room.connect(url, token);
        post({type:'connected'});

        // Сообщаем об уже присутствующих участниках (мы зашли в идущий звонок).
        try {
            room.remoteParticipants.forEach((p) => post({type:'participantJoined', pid:p.identity, name:pidName(p)}));
        } catch(e){ console.error('enum participants', String(e)); }

        // Микрофон публикуем сразу — аудиозвонок начинается мгновенно.
        try { await publishMic(); } catch(e){ console.error('mic enable', String(e)); }

        // Периодически читаем RTT (пинг) из WebRTC-статистики и шлём в C#.
        try { if (window.__pingTimer) clearInterval(window.__pingTimer); } catch(e){}
        window.__pingTimer = setInterval(readPing, 2000);
        readPing();

        // Локальный детектор говорящих (свои уровни звука, с attack/release).
        startSpeakingMonitor();
    } catch(err) {
        console.error('connect error', String(err));
        post({type:'fatal', error:String(err)});
        post({type:'disconnected'});
    }
}

function srcFor(publication){
    // LK.Track.Source: Camera, Microphone, ScreenShare, ScreenShareAudio
    return publication ? publication.source : null;
}

// Чтение пинга (RTT) из WebRTC getStats() по обоим PeerConnection'ам.
// LiveKit в разных версиях хранит pc по-разному — пробуем все варианты.
async function readPing(){
    try {
        let pcs = [];
        const eng = room && room.engine;
        if (eng) {
            const pm = eng.pcManager;
            if (pm) {
                if (pm.publisher && pm.publisher.pc) pcs.push(pm.publisher.pc);
                if (pm.subscriber && pm.subscriber.pc) pcs.push(pm.subscriber.pc);
            }
            if (eng.publisher && eng.publisher.pc) pcs.push(eng.publisher.pc);
            if (eng.subscriber && eng.subscriber.pc) pcs.push(eng.subscriber.pc);
        }
        let best = null;
        for (const pc of pcs) {
            if (!pc || !pc.getStats) continue;
            const stats = await pc.getStats();
            stats.forEach(r => {
                if (r.type === 'candidate-pair' && (r.nominated || r.state === 'succeeded')
                    && typeof r.currentRoundTripTime === 'number') {
                    const ms = Math.round(r.currentRoundTripTime * 1000);
                    if (best === null || ms < best) best = ms;
                }
            });
        }
        if (best !== null) post({type:'ping', ms: best});
    } catch(e) {}
}

function pidName(p){ return p ? (p.name || p.identity || '') : ''; }

// Видео-элементы и циклы извлечения по ключу 'pid|source'.
let remoteVideoMap = {}; // key -> { el, loop }

function tileKey(pid, source){ return pid + '|' + source; }

function onTrackSubscribed(track, publication, participant){
    const src = srcFor(publication);
    const pid = participant ? participant.identity : 'unknown';
    const name = pidName(participant);
    if (track.kind === 'video'){
        const source = (src === LK.Track.Source.ScreenShare) ? 'screen' : 'camera';
        const key = tileKey(pid, source);
        let entry = remoteVideoMap[key];
        if (!entry){ entry = { el: makeHiddenVideo() }; remoteVideoMap[key] = entry; }
        entry.track = track;   // для статистики приёма (getStats на receiver)
        track.attach(entry.el);

        // НИЗКАЯ ЗАДЕРЖКА для демонстрации: по умолчанию WebRTC копит jitter-buffer
        // «для сглаживания», из-за чего экран/действия отстают на ~секунду. Просим
        // приёмник держать буфер минимальным. (Для камеры оставляем чуть больше —
        // там сглаживание важнее, а задержка не критична.)
        try {
            const r = track.receiver || (track._receiver) || null;
            if (r){
                const targetMs = source === 'screen' ? 100 : 200;
                if ('jitterBufferTarget' in r) r.jitterBufferTarget = targetMs;          // мс (новый API)
                else if ('playoutDelayHint' in r) r.playoutDelayHint = targetMs / 1000;  // сек (старый)
            }
        } catch(e){ console.warn('jitterBufferTarget', String(e)); }

        post({type:'remoteTileStart', pid: pid, name: name, source: source});
        if (!entry.loop){
            const capEl = entry.el;
            // Плитка — это ПРЕВЬЮ: 15fps/960px достаточно. Полное качество и
            // плавность даёт нативный «театр». 30fps/1280px JPEG-извлечение
            // создавало у зрителя постоянную нагрузку (кодирование+interop+GC)
            // и накапливающиеся фризы на длинных демках.
            entry.loop = makeExtractorTile(() => capEl, pid, source, 15, source === 'screen' ? 960 : 360);
        }
        entry.loop.start();
        if (source === 'screen') startScreenRecvStats();
    } else if (track.kind === 'audio'){
        if (src === LK.Track.Source.ScreenShareAudio){
            remoteScreenAudioTrack = track;
            detachAmplified(screenMix);
            screenMix = attachAmplified(track, remoteVoiceMuted ? 0 : remoteScreenAudioVolume);
        } else {
            remoteVoiceTracks.push(track);
            (remoteAudioByPid[pid] = remoteAudioByPid[pid] || []).push(track);
            const node = attachAmplified(track, effectiveVolume(pid));
            (mixByPid[pid] = mixByPid[pid] || []).push(node);
        }
    }
}

// ── Локальный детектор говорящих ────────────────────────────────────
// Считаем уровень звука каждого участника (локальный микрофон + удалённые)
// прямо на клиенте через AnalyserNode. Подсветка появляется с задержкой
// (attack) и исчезает с задержкой (release) — не моргает на микропаузах.
let _spkCtx = null;            // AudioContext для анализа уровней
let _spkStore = {};            // key -> {track, an, buf}
let _spkState = {};            // pid -> {above0, lastAbove, on}
let _spkTimer = null;
const SPK_THRESHOLD = 0.012;   // порог RMS «есть голос»
const SPK_ATTACK_MS = 150;     // сколько держать звук, прежде чем зажечь
const SPK_RELEASE_MS = 600;    // сколько гореть после пропадания звука

function _spkRms(an, buf){
    an.getByteTimeDomainData(buf);
    let s = 0; for (let i = 0; i < buf.length; i++){ const x = (buf[i]-128)/128; s += x*x; }
    return Math.sqrt(s/buf.length);
}

function _spkAnalyser(track, key){
    const mst = (track && track.mediaStreamTrack) ? track.mediaStreamTrack : track;
    const e = _spkStore[key];
    if (e && e.mst === mst) return e;
    try {
        if (!_spkCtx) _spkCtx = new (window.AudioContext || window.webkitAudioContext)();
        try { if (_spkCtx.state === 'suspended') _spkCtx.resume(); } catch(x){}
        const src = _spkCtx.createMediaStreamSource(new MediaStream([mst]));
        const an = _spkCtx.createAnalyser(); an.fftSize = 512;
        src.connect(an);
        _spkStore[key] = { mst, an, src, buf: new Uint8Array(an.fftSize) };
        return _spkStore[key];
    } catch(x){ return null; }
}

function startSpeakingMonitor(){
    if (_spkTimer) return;
    _spkTimer = setInterval(() => {
        try {
            const now = performance.now();
            const levels = {};
            // Локальный микрофон.
            try {
                const pub = localMicPub();
                const selfId = (room && room.localParticipant) ? room.localParticipant.identity : null;
                if (selfId && pub && pub.track && !pub.isMuted){
                    const e = _spkAnalyser(pub.track, 'self');
                    if (e) levels[selfId] = _spkRms(e.an, e.buf);
                }
            } catch(x){}
            // Удалённые участники.
            for (const pid in remoteAudioByPid){
                const arr = remoteAudioByPid[pid] || [];
                let m = 0;
                for (let i = 0; i < arr.length; i++){
                    const e = _spkAnalyser(arr[i], pid + '#' + i);
                    if (e) m = Math.max(m, _spkRms(e.an, e.buf));
                }
                levels[pid] = m;
            }
            // Привязка attack/release к каждому pid.
            const speaking = [];
            for (const pid in levels){
                const lvl = levels[pid];
                let s = _spkState[pid] || { above0: 0, lastAbove: 0, on: false };
                if (lvl > SPK_THRESHOLD){ if (!s.above0) s.above0 = now; s.lastAbove = now; }
                else { s.above0 = 0; }
                if (!s.on && s.above0 && (now - s.above0) >= SPK_ATTACK_MS) s.on = true;      // задержка появления
                if (s.on && (now - s.lastAbove) >= SPK_RELEASE_MS) s.on = false;              // задержка исчезновения
                _spkState[pid] = s;
                if (s.on) speaking.push(pid);
            }
            post({type:'activeSpeakers', pids: JSON.stringify(speaking)});
        } catch(x){}
    }, 60);
}

// Итоговая громкость участника с учётом его персональных настроек и
// глобального «заглушить весь звук».
function effectiveVolume(pid){
    if (remoteVoiceMuted) return 0;
    if (perUserMuted[pid]) return 0;
    let v = (pid in perUserVolume) ? perUserVolume[pid] : remoteVoiceVolume;
    // Свой GainNode принимает буст >1 (лимитер защищает от клиппинга).
    return Math.max(0, Math.min(v, 5));
}

function applyPidVolume(pid){
    const v = effectiveVolume(pid);
    (mixByPid[pid] || []).forEach(n => { try { n.gain.gain.value = v; } catch(e){} });
}

function setParticipantVolume(pid, v){
    perUserVolume[pid] = v;
    applyPidVolume(pid);
}

// Массово применить сохранённые громкости/мьюты (приходит ДО connect). Так
// значение действует для любого участника при подписке на его аудио, включая
// голос-без-камеры и тех, кто уже был в звонке до нашего входа.
function setVoicePrefs(prefs){
    try {
        (prefs || []).forEach(p => {
            if (!p || p.pid == null) return;
            const pid = String(p.pid);
            if (typeof p.v === 'number') perUserVolume[pid] = p.v;
            if (typeof p.m !== 'undefined') perUserMuted[pid] = !!p.m;
            applyPidVolume(pid); // если трек уже есть — сразу, иначе применится при подписке
        });
    } catch(e){ console.warn('setVoicePrefs', String(e)); }
}

function setParticipantMuted(pid, muted){
    perUserMuted[pid] = muted;
    applyPidVolume(pid);
}

function onTrackUnsubscribed(track, publication, participant){
    const src = srcFor(publication);
    const pid = participant ? participant.identity : 'unknown';
    try { track.detach(); } catch(e){}
    if (track.kind === 'video'){
        const source = (src === LK.Track.Source.ScreenShare) ? 'screen' : 'camera';
        const key = tileKey(pid, source);
        const entry = remoteVideoMap[key];
        if (entry){ if (entry.loop) entry.loop.stop(); if (entry.el) entry.el.srcObject = null; delete remoteVideoMap[key]; }
        if (source === 'screen') stopScreenRecvStats();
        post({type:'remoteTileStop', pid: pid, source: source});
    } else if (track.kind === 'audio'){
        if (src === LK.Track.Source.ScreenShareAudio){
            remoteScreenAudioTrack = null;
            detachAmplified(screenMix); screenMix = null;
        } else {
            remoteVoiceTracks = remoteVoiceTracks.filter(t => t !== track);
            if (remoteAudioByPid[pid]) remoteAudioByPid[pid] = remoteAudioByPid[pid].filter(t => t !== track);
            if (mixByPid[pid]){
                mixByPid[pid].filter(n => n.track === track).forEach(detachAmplified);
                mixByPid[pid] = mixByPid[pid].filter(n => n.track !== track);
                if (!mixByPid[pid].length) delete mixByPid[pid];
            }
        }
    }
}

// Останавливает все видео-циклы ушедшего участника и чистит аудио.
function cleanupParticipant(pid){
    Object.keys(remoteVideoMap).forEach((key) => {
        if (key.indexOf(pid + '|') === 0){
            const entry = remoteVideoMap[key];
            if (entry){ if (entry.loop) entry.loop.stop(); if (entry.el) entry.el.srcObject = null; }
            delete remoteVideoMap[key];
        }
    });
    delete remoteAudioByPid[pid];
    (mixByPid[pid] || []).forEach(detachAmplified);
    delete mixByPid[pid];
}

// Извлечение кадров плитки: постит remoteTileFrame с pid+source.
function makeExtractorTile(getVideoEl, pid, source, fps, maxW){
    let handle = null;
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    let lastSent = 0;
    const interval = 1000 / fps;
    function loop(){
        const v = getVideoEl();
        if (!v || v.readyState < 2){ handle = requestAnimationFrame(loop); return; }
        const now = performance.now();
        if (now - lastSent >= interval){
            let vw = v.videoWidth, vh = v.videoHeight;
            if (vw > 0 && vh > 0){
                let tw = vw, th = vh;
                if (maxW && vw > maxW){ tw = maxW; th = Math.round(vh * (maxW/vw)); }
                if (canvas.width !== tw || canvas.height !== th){ canvas.width = tw; canvas.height = th; }
                ctx.drawImage(v, 0, 0, tw, th);
                canvas.toBlob((blob) => {
                    if (!blob) return;
                    const reader = new FileReader();
                    reader.onload = () => post({type:'remoteTileFrame', pid: pid, source: source, data: reader.result.split(',')[1]});
                    reader.readAsDataURL(blob);
                }, 'image/jpeg', 0.8);
                lastSent = now;
            }
        }
        handle = requestAnimationFrame(loop);
    }
    return {
        start(){ if (!handle) handle = requestAnimationFrame(loop); },
        stop(){ if (handle){ cancelAnimationFrame(handle); handle = null; } }
    };
}

function makeHiddenVideo(){
    const v = document.createElement('video');
    v.autoplay = true; v.muted = true; v.playsInline = true;
    v.style.display = 'none';
    document.body.appendChild(v);
    return v;
}

// --- Универсальное извлечение кадров из <video> в JPEG для C# UI ---
function makeExtractor(getVideoEl, postType, fps, maxW){
    let handle = null;
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    let lastSent = 0;
    const interval = 1000 / fps;
    function loop(){
        const v = getVideoEl();
        if (!v || v.readyState < 2){ handle = requestAnimationFrame(loop); return; }
        const now = performance.now();
        if (now - lastSent >= interval){
            let vw = v.videoWidth, vh = v.videoHeight;
            if (vw > 0 && vh > 0){
                let tw = vw, th = vh;
                if (maxW && vw > maxW){ tw = maxW; th = Math.round(vh * (maxW/vw)); }
                if (canvas.width !== tw || canvas.height !== th){ canvas.width = tw; canvas.height = th; }
                ctx.drawImage(v, 0, 0, tw, th);
                canvas.toBlob((blob) => {
                    if (!blob) return;
                    const reader = new FileReader();
                    reader.onload = () => post({type: postType, data: reader.result.split(',')[1]});
                    reader.readAsDataURL(blob);
                }, 'image/jpeg', 0.8);
                lastSent = now;
            }
        }
        handle = requestAnimationFrame(loop);
    }
    return {
        start(){ if (!handle) handle = requestAnimationFrame(loop); },
        stop(){ if (handle){ cancelAnimationFrame(handle); handle = null; } }
    };
}

function startRemoteScreenExtraction(){ if(!remoteScreenLoop) remoteScreenLoop = makeExtractor(()=>remoteScreenVideoEl, 'remoteScreenFrame', 20, 0); remoteScreenLoop.start(); }
function stopRemoteScreenExtraction(){ if(remoteScreenLoop) remoteScreenLoop.stop(); if(remoteScreenVideoEl){ remoteScreenVideoEl.srcObject=null; } }
function startRemoteCameraExtraction(){ if(!remoteCameraLoop) remoteCameraLoop = makeExtractor(()=>remoteCameraVideoEl, 'remoteCameraFrame', 20, 0); remoteCameraLoop.start(); }
function stopRemoteCameraExtraction(){ if(remoteCameraLoop) remoteCameraLoop.stop(); if(remoteCameraVideoEl){ remoteCameraVideoEl.srcObject=null; } }
function startLocalCameraExtraction(){ if(!localCameraLoop) localCameraLoop = makeExtractor(()=>localCameraVideoEl, 'localCameraFrame', 15, 320); localCameraLoop.start(); }
function stopLocalCameraExtraction(){ if(localCameraLoop) localCameraLoop.stop(); if(localCameraVideoEl){ localCameraVideoEl.srcObject=null; } }
// Локальное превью — 10 fps/480px достаточно для мини-плитки; 30 fps/640px
// создавали постоянную фоновую нагрузку (canvas+JPEG+interop 30 раз/сек всю
// демонстрацию) — на длинных демках это добавляло фризов.
function startScreenPreviewExtraction(){ if(!screenPreviewLoop) screenPreviewLoop = makeExtractor(()=>screenPreviewVideoEl, 'screenPreviewFrame', 10, 480); screenPreviewLoop.start(); }
function stopScreenPreviewExtraction(){ if(screenPreviewLoop) screenPreviewLoop.stop(); if(screenPreviewVideoEl){ screenPreviewVideoEl.srcObject=null; } }

// ── Камера ───────────────────────────────────────────────────────────
async function previewCamera(deviceLabel){
    try {
        // Разблокируем label-ы устройств (Chromium прячет их до первого доступа).
        try { const t = await navigator.mediaDevices.getUserMedia({video:true,audio:false}); t.getTracks().forEach(x=>x.stop()); } catch(e){}
        let deviceId = await deviceIdByLabel('videoinput', deviceLabel);
        await openCamera(deviceId);
        post({type:'cameraPreviewReady'});
    } catch(err){ post({type:'localCameraError', error:String(err)}); }
}

async function deviceIdByLabel(kind, label){
    if (!label) return undefined;
    const devices = await navigator.mediaDevices.enumerateDevices();
    const m = devices.find(d => d.kind === kind && d.label === label);
    return m ? m.deviceId : undefined;
}

async function openCamera(deviceId){
    // Камера в 16:10 (1280x800) — чтобы кадр не обрезался (раньше форс 4:3
    // обрезал широкий источник), и при этом разумная нагрузка.
    const camOpts = { resolution: { width: 1280, height: 800, frameRate: 24 } };
    if (deviceId) camOpts.deviceId = { exact: deviceId };
    if (cameraTrack){
        await cameraTrack.restartTrack(camOpts);
    } else {
        cameraTrack = await LK.createLocalVideoTrack(camOpts);
    }
    if (!localCameraVideoEl){ localCameraVideoEl = makeHiddenVideo(); }
    cameraTrack.attach(localCameraVideoEl);
    startLocalCameraExtraction();
}

async function switchCameraDevice(deviceLabel){
    try {
        let deviceId = await deviceIdByLabel('videoinput', deviceLabel);
        await openCamera(deviceId);
        post({type:'cameraDeviceSwitched'});
    } catch(err){ post({type:'localCameraError', error:String(err)}); }
}

async function confirmCameraShare(){
    try {
        if (!cameraTrack){ post({type:'localCameraError', error:'no preview stream'}); return; }
        if (!cameraPublished){
            await room.localParticipant.publishTrack(cameraTrack, { source: LK.Track.Source.Camera });
            cameraPublished = true;
        }
        post({type:'localCameraStarted'});
    } catch(err){ post({type:'localCameraError', error:String(err)}); }
}

function cancelCameraPreview(){
    stopLocalCameraExtraction();
    if (cameraTrack && !cameraPublished){ try{ cameraTrack.stop(); }catch(e){} cameraTrack = null; }
    post({type:'localCameraStopped'});
}

async function stopCameraTrack(){
    try {
        if (cameraTrack){
            if (cameraPublished){ try{ await room.localParticipant.unpublishTrack(cameraTrack, true); }catch(e){} }
            try{ cameraTrack.stop(); }catch(e){}
            cameraTrack = null; cameraPublished = false;
        }
        stopLocalCameraExtraction();
    } catch(err){ console.error('stopCameraTrack', String(err)); }
    post({type:'localCameraStopped'});
}

// ── Демонстрация экрана ──────────────────────────────────────────────
async function previewScreen(resHeight, fps){
    try {
        // Реально применяем выбранные разрешение и частоту кадров.
        // resHeight: 1080/720/480/360; fps: 60/30/15...
        let h = parseInt(resHeight); if (isNaN(h)) h = 1080;   // h<=0 => исходное (без ужатия)
        let f = parseInt(fps) || 30;
        screenQualityH = h; screenQualityF = f;

        // Захват через getDisplayMedia НАПРЯМУЮ: только так можно ЗАПРОСИТЬ нужный fps
        // (ideal). Раньше createLocalScreenTracks создавал трек со своим дефолтным fps,
        // а applyConstraints({max:f}) лишь ограничивал сверху — поднять не мог, поэтому
        // при выборе 60 по факту выходило ~15. Ограничиваем только height (не width) —
        // без обрезки; при «Исходном» (h<=0) высоту не трогаем.
        const videoCons = { frameRate: { ideal: f, max: f } };
        if (h > 0) videoCons.height = { ideal: h };
        // Звук демки — БЕЗ обработки голосового тракта:
        //  • noiseSuppression:false — шумодав браузера принимал звуки игры
        //    (двигатель и т.п.) за шум и глушил их, пропуская только речь;
        //  • autoGainControl:false — не «дышащая» громкость;
        //  • echoCancellation:false — AEC к системному звуку не применяем
        //    (свой голос он не убирал, а обработку добавлял);
        //  • restrictOwnAudio:true — КЛЮЧЕВОЕ: исключает из захвата звук
        //    САМОГО приложения (голоса участников, которые PISMO играет в
        //    колонки) на уровне WASAPI — как это делает Discord. Игра и
        //    остальной системный звук остаются.
        const audioCons = {
            echoCancellation: false,
            noiseSuppression: false,
            autoGainControl: false,
            restrictOwnAudio: true
        };
        const stream = await navigator.mediaDevices.getDisplayMedia({ video: videoCons, audio: audioCons });

        const vt = stream.getVideoTracks()[0];
        const at = stream.getAudioTracks()[0] || null;
        if (!vt){ post({type:'localScreenError', error:'no screen video track'}); return; }
        // 'motion' → WebRTC держит framerate (плавность), резкость — за счёт битрейта.
        try { vt.contentHint = 'motion'; } catch(e){}
        // Диагностика: реальные параметры захвата (видно в статусе звонка).
        try { const st = vt.getSettings(); post({type:'screenCaptureInfo', fps: Math.round(st.frameRate||0), w: st.width||0, h: st.height||0}); } catch(e){}

        screenVideoTrack = new LK.LocalVideoTrack(vt);
        screenAudioTrack = at ? new LK.LocalAudioTrack(at) : null;

        // Остановка через системный диалог Chrome ('Stop sharing').
        const mst = vt;
        if (mst){ mst.onended = () => { if (screenPublished) stopScreenShareTrack(); else cancelScreenPreview(); }; }

        if (!screenPreviewVideoEl){ screenPreviewVideoEl = makeHiddenVideo(); }
        screenVideoTrack.attach(screenPreviewVideoEl);
        startScreenPreviewExtraction();
        post({type:'screenPreviewReady'});
    } catch(err){ post({type:'localScreenError', error:String(err)}); }
}

async function confirmScreenShare(){
    try {
        if (!screenVideoTrack){ post({type:'localScreenError', error:'no preview stream'}); return; }
        if (!screenPublished){
            // Битрейт под РЕАЛЬНУЮ высоту (для «Исходного»/native берём фактическую
            // высоту трека). Высокий битрейт = чёткая картинка без «мыла».
            let effH = screenQualityH;
            if (!effH || effH <= 0){
                try { effH = screenVideoTrack.mediaStreamTrack.getSettings().height || 1080; } catch(e){ effH = 1080; }
            }
            // Для 60 fps даём больше — плавное движение требует больше бит.
            let hi = screenQualityF >= 45;
            let maxBitrate = effH >= 1440 ? (hi ? 30_000_000 : 22_000_000)
                           : effH >= 1080 ? (hi ? 22_000_000 : 15_000_000)
                           : effH >= 720  ? (hi ? 12_000_000 : 8_000_000)
                           : effH >= 480  ? 5_000_000
                           : 2_500_000;
            // videoCodec:'h264' — КЛЮЧЕВОЕ для нативных 1080p60: VP8/VP9 (дефолт WebRTC)
            // на NVIDIA аппаратно НЕ кодируются → софт → просадка. H264 кодирует NVENC
            // на дискретной GPU в железе → 1080p60 без деградации.
            await room.localParticipant.publishTrack(screenVideoTrack, {
                source: LK.Track.Source.ScreenShare,
                simulcast: false,
                videoCodec: 'h264',
                degradationPreference: 'maintain-resolution',
                videoEncoding: { maxBitrate: maxBitrate, maxFramerate: screenQualityF }
            });
            // Hi-fi публикация звука демки: dtx:false — DTX режет ТИХИЕ непрерывные
            // звуки (двигатель, эмбиент) как «тишину»; стерео + высокий битрейт —
            // звук игры, а не «телефонная» речь.
            if (screenAudioTrack){ try{ await room.localParticipant.publishTrack(screenAudioTrack, {
                source: LK.Track.Source.ScreenShareAudio,
                dtx: false,
                red: false,
                forceStereo: true,
                audioPreset: { maxBitrate: 128000 }
            }); }catch(e){} }
            screenPublished = true;
            startScreenSendStats();   // мини-превью показывает, что реально уходит собеседникам

            // Приоритет ЧЁТКОСТИ (стандарт для демонстрации экрана): при нехватке
            // ресурсов WebRTC жертвует FPS, а не РАЗРЕШЕНИЕМ — иначе 1080 «плыл» в
            // 480–720. Полноценные 1080p60 достижимы только с аппаратным энкодером
            // (NVENC на дискретной GPU) — см. --force_high_performance_gpu.
            try {
                const sender = screenVideoTrack.sender;
                if (sender && sender.getParameters){
                    const p = sender.getParameters();
                    p.degradationPreference = 'maintain-resolution';
                    if (!p.encodings || !p.encodings.length) p.encodings = [{}];
                    p.encodings[0].maxFramerate = screenQualityF;
                    p.encodings[0].maxBitrate = maxBitrate;
                    p.encodings[0].scaleResolutionDownBy = 1;      // без авто-уменьшения разрешения
                    try { p.encodings[0].networkPriority = 'high'; } catch(e){}
                    try { p.encodings[0].priority = 'high'; } catch(e){}
                    await sender.setParameters(p);
                }
            } catch(e){ console.warn('setParameters', String(e)); }
        }
        post({type:'localScreenStarted'});
    } catch(err){ post({type:'localScreenError', error:String(err)}); }
}

function cancelScreenPreview(){
    stopScreenPreviewExtraction();
    if (screenVideoTrack && !screenPublished){ try{ screenVideoTrack.stop(); }catch(e){} screenVideoTrack = null; }
    if (screenAudioTrack && !screenPublished){ try{ screenAudioTrack.stop(); }catch(e){} screenAudioTrack = null; }
    post({type:'localScreenStopped'});
}

async function stopScreenShareTrack(){
    try {
        if (screenVideoTrack){
            if (screenPublished){ try{ await room.localParticipant.unpublishTrack(screenVideoTrack, true); }catch(e){} }
            try{ screenVideoTrack.stop(); }catch(e){}
        }
        if (screenAudioTrack){
            if (screenPublished){ try{ await room.localParticipant.unpublishTrack(screenAudioTrack, true); }catch(e){} }
            try{ screenAudioTrack.stop(); }catch(e){}
        }
        screenVideoTrack = null; screenAudioTrack = null; screenPublished = false;
        stopScreenSendStats();
        stopScreenPreviewExtraction();
    } catch(err){ console.error('stopScreenShareTrack', String(err)); }
    post({type:'localScreenStopped'});
}

// ── Прочее ───────────────────────────────────────────────────────────
async function enumerateDevices(){
    try {
        try { const t = await navigator.mediaDevices.getUserMedia({video:true,audio:true}); t.getTracks().forEach(x=>x.stop()); } catch(e){}
        const devices = await navigator.mediaDevices.enumerateDevices();
        const cams = devices.filter(d=>d.kind==='videoinput').map(d=>d.label).filter(Boolean);
        const mics = devices.filter(d=>d.kind==='audioinput').map(d=>d.label).filter(Boolean);
        const spk = devices.filter(d=>d.kind==='audiooutput').map(d=>d.label).filter(Boolean);
        post({type:'devicesEnumerated', cameras: JSON.stringify(cams), mics: JSON.stringify(mics), speakers: JSON.stringify(spk)});
    } catch(err){ console.error('enumerateDevices', String(err)); }
}

async function setMicEnabled(enabled){
    try {
        if (!room) return;
        // Стандартный mute/unmute LiveKit — надёжно отключает передачу звука.
        await room.localParticipant.setMicrophoneEnabled(enabled, micCaptureOpts());
        // После повторного включения трек новый — навешиваем шумодав заново.
        if (enabled) setTimeout(() => applyNoiseFilter(), 300);
    } catch(e){ console.error('setMic', String(e)); }
}

function setScreenAudioVolume(v){
    remoteScreenAudioVolume = Math.max(0, Math.min(v, 5));   // усилитель до 500%
    if (screenMix){ try{ screenMix.gain.gain.value = remoteVoiceMuted ? 0 : remoteScreenAudioVolume; }catch(e){} }
}

function setVoiceVolume(v){
    remoteVoiceVolume = v;
    Object.keys(remoteAudioByPid).forEach(applyPidVolume);
}

function setRemoteMuted(muted){
    remoteVoiceMuted = muted;
    Object.keys(mixByPid).forEach(applyPidVolume);
    if (screenMix){ try{ screenMix.gain.gain.value = muted ? 0 : remoteScreenAudioVolume; }catch(e){} }
}

async function setAudioDevice(kind, deviceLabel){
    try {
        if (!room) return;
        const devices = await navigator.mediaDevices.enumerateDevices();
        const m = devices.find(d => d.kind === kind && d.label === deviceLabel);
        if (m) await room.switchActiveDevice(kind, m.deviceId);
    } catch(e){ console.error('switchActiveDevice ' + kind, String(e)); }
}

// Порог активации голоса (самописный gate) УДАЛЁН: он переключал
// mediaStreamTrack.enabled и мешал штатному mute, ломая выключение микрофона.
// Шум давит Krisp, постоянный фон — браузерный noiseSuppression.
function setVoiceGate(auto, threshold){ /* no-op, оставлено для совместимости команд */ }

// ── «Театр»: нативный полноэкранный рендер видео (плавные 60fps) ──────
// Показываем реальный <video> прямо в WebView, а не извлекаем JPEG. Тот же
// MediaStream, что и у скрытого элемента, — поэтому без потери кадров.
let theaterEl = null;
let theaterWatch = null;          // сторож: чёрный экран не должен жить дольше пары секунд
let theaterPid = null, theaterSource = null;
let theaterFrames = -1, theaterStallTicks = 0;

// Актуальный поток для театра (при переподписке LiveKit у плитки появляется
// НОВЫЙ MediaStream — театр обязан переприцепиться, иначе останется чёрным).
function theaterResolveSrc(){
    let src = null;
    const entry = remoteVideoMap[tileKey(theaterPid, theaterSource)];
    if (entry && entry.el && entry.el.srcObject) src = entry.el.srcObject;
    if (!src && theaterSource === 'screen' && screenPreviewVideoEl && screenPreviewVideoEl.srcObject) src = screenPreviewVideoEl.srcObject;
    if (!src && theaterSource === 'camera' && localCameraVideoEl && localCameraVideoEl.srcObject) src = localCameraVideoEl.srcObject;
    return src;
}

function theaterTick(){
    try {
        if (!theaterEl || theaterEl.style.display === 'none') return;
        const vid = theaterEl.querySelector('#__theaterVideo');
        if (!vid) return;
        const cur = theaterResolveSrc();
        if (!cur){ post({type:'theaterExitRequested'}); return; }   // трек пропал — выходим из театра
        if (vid.srcObject !== cur){
            vid.srcObject = cur;                                     // поток сменился — переприцепились
            theaterFrames = -1; theaterStallTicks = 0;
            try { vid.play(); } catch(e){}
            return;
        }
        const t = cur.getVideoTracks && cur.getVideoTracks()[0];
        if (t && t.readyState === 'ended'){ post({type:'theaterExitRequested'}); return; }
        if (vid.paused){ try { vid.play(); } catch(e){} }
        // Детектор «картинка замерла»: у ЖИВОГО MediaStream currentTime всегда
        // растёт. (Раньше мерили totalVideoFrames — для WebRTC-потока счётчик
        // может не расти, и сторож зря передёргивал srcObject каждые 6 секунд,
        // отчего демка подлагивала.)
        const ct = vid.currentTime || 0;
        if (ct === theaterFrames){
            if (++theaterStallTicks >= 3){
                theaterStallTicks = 0;
                try { vid.srcObject = null; vid.srcObject = cur; vid.play(); } catch(e){}
            }
        } else theaterStallTicks = 0;
        theaterFrames = ct;
    } catch(e){}
}

function theaterShow(pid, source){
    theaterPid = pid; theaterSource = source;
    theaterFrames = -1; theaterStallTicks = 0;
    let src = null;
    const entry = remoteVideoMap[tileKey(pid, source)];
    if (entry && entry.el && entry.el.srcObject) src = entry.el.srcObject;
    if (!src && source === 'screen' && screenPreviewVideoEl && screenPreviewVideoEl.srcObject) src = screenPreviewVideoEl.srcObject;
    if (!src && source === 'camera' && localCameraVideoEl && localCameraVideoEl.srcObject) src = localCameraVideoEl.srcObject;
    if (!src) return;
    if (!theaterEl){
        theaterEl = document.createElement('div');
        theaterEl.style.cssText = 'position:fixed;inset:0;background:#000;z-index:2147483000;display:flex;align-items:center;justify-content:center;cursor:pointer;';
        const v = document.createElement('video');
        v.autoplay = true; v.muted = true; v.playsInline = true;
        v.id = '__theaterVideo';
        v.style.cssText = 'width:100%;height:100%;object-fit:contain;background:#000;';
        theaterEl.appendChild(v);
        // Выход из театра: двойной клик по видео или крестик.
        theaterEl.ondblclick = () => post({type:'theaterExitRequested'});
        const btnCss = 'position:absolute;top:12px;color:#fff;font:700 20px Segoe UI,sans-serif;background:rgba(0,0,0,.45);border-radius:50%;width:40px;height:40px;display:flex;align-items:center;justify-content:center;cursor:pointer;';
        // Развернуть на весь экран / свернуть обратно (нативное качество сохраняется).
        const fs = document.createElement('div');
        fs.textContent = '⛶';
        fs.title = 'Во весь экран';
        fs.style.cssText = btnCss + 'right:64px;';
        fs.onclick = (ev) => { ev.stopPropagation(); post({type:'theaterFullscreenToggle'}); };
        theaterEl.appendChild(fs);
        const x = document.createElement('div');
        x.textContent = '✕';
        x.title = 'Выйти (двойной клик)';
        x.style.cssText = btnCss + 'right:16px;';
        x.onclick = (ev) => { ev.stopPropagation(); post({type:'theaterExitRequested'}); };
        theaterEl.appendChild(x);
        document.body.appendChild(theaterEl);
    }
    const vid = theaterEl.querySelector('#__theaterVideo');
    if (vid.srcObject !== src) vid.srcObject = src;
    try { vid.play(); } catch(e){}
    theaterEl.style.display = 'flex';
    if (theaterWatch) clearInterval(theaterWatch);
    theaterWatch = setInterval(theaterTick, 2000);
    // Пока открыт театр (нативное видео), JPEG-извлечение плитки этой демки —
    // лишняя двойная работа (главный источник фризов у зрителя). Ставим на паузу.
    try { const e2 = remoteVideoMap[tileKey(pid, source)]; if (e2 && e2.loop) e2.loop.stop(); } catch(e){}
}
function theaterHide(){
    if (theaterWatch){ clearInterval(theaterWatch); theaterWatch = null; }
    // Возобновляем извлечение плитки (вернулись из театра к плиткам).
    try {
        if (theaterPid){ const e2 = remoteVideoMap[tileKey(theaterPid, theaterSource)]; if (e2 && e2.loop) e2.loop.start(); }
    } catch(e){}
    theaterPid = null; theaterSource = null;
    if (theaterEl){
        theaterEl.style.display = 'none';
        const vid = theaterEl.querySelector('#__theaterVideo');
        if (vid) vid.srcObject = null;
    }
}

// ── Чип статистики в театре (что реально приходит по сети) ──────────
let theaterStatsEl = null;
function updateTheaterStats(text){
    if (!theaterEl) return;
    if (!theaterStatsEl){
        theaterStatsEl = document.createElement('div');
        theaterStatsEl.style.cssText = 'position:absolute;left:12px;bottom:12px;color:#ddd;font:12px Consolas,monospace;background:rgba(0,0,0,.55);padding:4px 10px;border-radius:6px;pointer-events:none;';
        theaterEl.appendChild(theaterStatsEl);
    }
    theaterStatsEl.textContent = text || '';
    theaterStatsEl.style.display = text ? 'block' : 'none';
}

// ── Статистика демонстрации: ЧТО РЕАЛЬНО уходит собеседникам и что
//    реально приходит зрителю (для диагностики «у кого лагает») ──────
let sendStatsTimer = null, _ssBytes = -1, _ssTs = 0;
function startScreenSendStats(){
    stopScreenSendStats(false);
    _ssBytes = -1;
    sendStatsTimer = setInterval(async () => {
        try {
            if (!screenPublished || !screenVideoTrack || !screenVideoTrack.sender) return;
            const stats = await screenVideoTrack.sender.getStats();
            let o = null;
            stats.forEach(r => { if (r.type === 'outbound-rtp' && (r.kind === 'video' || r.mediaType === 'video')) o = r; });
            if (!o) return;
            let mbps = 0;
            if (_ssBytes >= 0 && o.bytesSent > _ssBytes && o.timestamp > _ssTs)
                mbps = (o.bytesSent - _ssBytes) * 8 / ((o.timestamp - _ssTs) * 1000);
            _ssBytes = o.bytesSent; _ssTs = o.timestamp;
            // qualityLimitationReason — ГЛАВНЫЙ диагност: что душит качество.
            const lim = o.qualityLimitationReason === 'cpu' ? '  ⚠ упор: CPU/энкодер'
                      : o.qualityLimitationReason === 'bandwidth' ? '  ⚠ упор: СЕТЬ (аплоад)' : '';
            post({type:'screenSendStats', text:
                '→ собеседникам: ' + (o.frameWidth||0) + '×' + (o.frameHeight||0) + ' ' +
                Math.round(o.framesPerSecond||0) + 'fps · ' + mbps.toFixed(1) + ' Мбит/с' + lim});
        } catch(e){}
    }, 2000);
}
function stopScreenSendStats(clear = true){
    if (sendStatsTimer){ clearInterval(sendStatsTimer); sendStatsTimer = null; }
    if (clear) post({type:'screenSendStats', text:''});
}

let recvStatsTimer = null, _rsRecv = -1, _rsLost = -1, _rsFreezeDur = -1;
function startScreenRecvStats(){
    stopScreenRecvStats(false);
    _rsRecv = -1; _rsLost = -1; _rsFreezeDur = -1;
    recvStatsTimer = setInterval(async () => {
        try {
            let entry = null;
            for (const k in remoteVideoMap){ if (k.endsWith('|screen')){ entry = remoteVideoMap[k]; break; } }
            const tr = entry && entry.track;
            const rec = tr ? (tr.receiver || tr._receiver) : null;
            if (!rec){ post({type:'screenRecvStats', text:''}); updateTheaterStats(''); return; }
            const stats = await rec.getStats();
            let i = null;
            stats.forEach(r => { if (r.type === 'inbound-rtp' && (r.kind === 'video' || r.mediaType === 'video')) i = r; });
            if (!i) return;
            // Потери пакетов за интервал.
            let lossPart = '';
            const recvNow = i.packetsReceived || 0, lostNow = i.packetsLost || 0;
            if (_rsRecv >= 0){
                const dR = recvNow - _rsRecv, dL = lostNow - _rsLost;
                if (dR + dL > 0 && dL > 0) lossPart = ' · потери ' + (100 * dL / (dR + dL)).toFixed(1) + '%';
            }
            _rsRecv = recvNow; _rsLost = lostNow;
            // Фризы за интервал (freeze = декодер сидел без кадров).
            let freezePart = '';
            const fd = (typeof i.totalFreezesDuration === 'number') ? i.totalFreezesDuration : -1;
            if (fd >= 0 && _rsFreezeDur >= 0 && fd > _rsFreezeDur)
                freezePart = ' · ⚠ фриз +' + (fd - _rsFreezeDur).toFixed(1) + 'с';
            if (fd >= 0) _rsFreezeDur = fd;
            const text = '← приём: ' + (i.frameWidth||0) + '×' + (i.frameHeight||0) + ' ' +
                Math.round(i.framesPerSecond||0) + 'fps' + lossPart + freezePart;
            post({type:'screenRecvStats', text: text});
            updateTheaterStats(text);
        } catch(e){}
    }, 2000);
}
function stopScreenRecvStats(clear = true){
    if (recvStatsTimer){ clearInterval(recvStatsTimer); recvStatsTimer = null; }
    if (clear){ post({type:'screenRecvStats', text:''}); updateTheaterStats(''); }
}

// ── Смена качества демонстрации «НА ГОРЯЧУЮ» ────────────────────────
// Применяется к ЖИВОМУ треку: applyConstraints (разрешение/fps захвата) +
// setParameters на sender (потолок битрейта и fps энкодера). Раньше выбор
// качества во время демки лишь сохранялся в настройки и ни на что не влиял.
async function setScreenQuality(resHeight, fps){
    try {
        let h = parseInt(resHeight); if (isNaN(h)) h = 0;
        let f = parseInt(fps) || 30;
        screenQualityH = h; screenQualityF = f;
        if (!screenVideoTrack || !screenVideoTrack.mediaStreamTrack) return;
        const mt = screenVideoTrack.mediaStreamTrack;

        const cons = { frameRate: { ideal: f, max: f } };
        if (h > 0) cons.height = { ideal: h };
        await mt.applyConstraints(cons);

        // Потолок битрейта под новое качество (та же шкала, что при публикации).
        let effH = h;
        if (!effH || effH <= 0){ try { effH = mt.getSettings().height || 1080; } catch(e){ effH = 1080; } }
        const hi = f >= 45;
        const maxBitrate = effH >= 1440 ? (hi ? 30000000 : 22000000)
                         : effH >= 1080 ? (hi ? 22000000 : 15000000)
                         : effH >= 720  ? (hi ? 12000000 : 8000000)
                         : effH >= 480  ? 5000000
                         : 2500000;
        const sender = screenVideoTrack.sender;
        if (sender){
            const p = sender.getParameters();
            if (p.encodings && p.encodings.length){
                p.encodings[0].maxBitrate = maxBitrate;
                p.encodings[0].maxFramerate = f;
                await sender.setParameters(p);
            }
        }
        try { const st = mt.getSettings(); post({type:'screenCaptureInfo', fps: Math.round(st.frameRate||0), w: st.width||0, h: st.height||0}); } catch(e){}
        post({type:'jsLog', text:'Качество демки на горячую: ' + (h>0 ? h+'p' : 'native') + '@' + f});
    } catch(e){ post({type:'jsLog', text:'setScreenQuality: ' + String(e)}); }
}

async function disconnectRoom(){
    try { if (room) await room.disconnect(); } catch(e){}
}

window.chrome.webview.addEventListener('message', (e) => {
    let msg;
    try { msg = JSON.parse(e.data); } catch(err){ return; }
    switch (msg.cmd){
        case 'connect': connectRoom(msg.url, msg.token, msg.voiceAuto, msg.voiceThreshold, msg.noiseSuppress, msg.micGain); break;
        case 'previewCamera': previewCamera(msg.deviceLabel); break;
        case 'confirmCameraShare': confirmCameraShare(); break;
        case 'cancelCameraPreview': cancelCameraPreview(); break;
        case 'stopCameraTrack': stopCameraTrack(); break;
        case 'switchCameraDevice': switchCameraDevice(msg.deviceLabel); break;
        case 'previewScreen': previewScreen(msg.resHeight, msg.fps); break;
        case 'setScreenQuality': setScreenQuality(msg.resHeight, msg.fps); break;
        case 'confirmScreenShare': confirmScreenShare(); break;
        case 'cancelScreenPreview': cancelScreenPreview(); break;
        case 'stopScreenTrack': stopScreenShareTrack(); break;
        case 'enumerateDevices': enumerateDevices(); break;
        case 'setMicEnabled': setMicEnabled(msg.enabled); break;
        case 'setScreenAudioVolume': setScreenAudioVolume(msg.volume); break;
        case 'setVoiceVolume': setVoiceVolume(msg.volume); break;
        case 'setRemoteMuted': setRemoteMuted(msg.muted); break;
        case 'voicePrefs': setVoicePrefs(msg.prefs); break;
        case 'setParticipantVolume': setParticipantVolume(msg.pid, msg.volume); break;
        case 'setParticipantMuted': setParticipantMuted(msg.pid, msg.muted); break;
        case 'setInputDevice': setAudioDevice('audioinput', msg.deviceLabel); break;
        case 'setOutputDevice': setAudioDevice('audiooutput', msg.deviceLabel); break;
        case 'setVoiceGate': setVoiceGate(msg.auto, msg.threshold); break;
        case 'setNoiseSuppression': setNoiseSuppression(msg.on); break;
        case 'setMicGain': setMicGain(msg.gain); break;
        case 'setDisplayName': try { if (room) room.localParticipant.setName(msg.name); } catch(e){} break;
        case 'theaterShow': theaterShow(msg.pid, msg.source); break;
        case 'theaterHide': theaterHide(); break;
        case 'disconnect': disconnectRoom(); break;
    }
});
</script>
</body>
</html>";
            return html;
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
                string type = msg.GetProperty("type").GetString();

                switch (type)
                {
                    case "connected":
                        Connected?.Invoke();
                        break;
                    case "disconnected":
                        Disconnected?.Invoke();
                        break;
                    case "remoteLeft":
                        RemoteParticipantLeft?.Invoke();
                        break;
                    case "screenCaptureInfo":
                        ScreenCaptureInfo?.Invoke(SafeInt(msg, "fps"), SafeInt(msg, "w"), SafeInt(msg, "h"));
                        break;
                    case "screenSendStats":
                        ScreenSendStats?.Invoke(SafeStr(msg, "text"));
                        break;
                    case "screenRecvStats":
                        ScreenRecvStats?.Invoke(SafeStr(msg, "text"));
                        break;
                    case "theaterExitRequested":
                        TheaterExitRequested?.Invoke();
                        break;
                    case "theaterFullscreenToggle":
                        TheaterFullscreenToggle?.Invoke();
                        break;
                    case "participantJoined":
                        ParticipantJoined?.Invoke(SafeStr(msg, "pid"), SafeStr(msg, "name"));
                        break;
                    case "participantLeft":
                        ParticipantLeftById?.Invoke(SafeStr(msg, "pid"));
                        break;
                    case "remoteTileStart":
                        RemoteTileStarted?.Invoke(SafeStr(msg, "pid"), SafeStr(msg, "name"), SafeStr(msg, "source"));
                        break;
                    case "remoteTileStop":
                        RemoteTileStopped?.Invoke(SafeStr(msg, "pid"), SafeStr(msg, "source"));
                        break;
                    case "remoteTileFrame":
                        RemoteTileFrame?.Invoke(SafeStr(msg, "pid"), SafeStr(msg, "source"),
                            Convert.FromBase64String(msg.GetProperty("data").GetString()));
                        break;
                    case "activeSpeakers":
                        ActiveSpeakers?.Invoke(SafeStr(msg, "pids"));
                        break;
                    case "ping":
                        try { PingUpdated?.Invoke(msg.GetProperty("ms").GetInt32()); } catch { }
                        break;
                    case "participantRenamed":
                        ParticipantRenamed?.Invoke(SafeStr(msg, "pid"), SafeStr(msg, "name"));
                        break;
                    case "fatal":
                        System.Diagnostics.Debug.WriteLine($"[LiveKit FATAL] {SafeStr(msg, "error")}");
                        break;
                    case "jsLog":
                        System.Diagnostics.Debug.WriteLine($"[JS log] {SafeStr(msg, "text")}");
                        break;
                    case "jsError":
                        System.Diagnostics.Debug.WriteLine($"[JS error] {SafeStr(msg, "text")}");
                        break;
                    case "remoteScreenFrame":
                        RemoteScreenFrameReceived?.Invoke(Convert.FromBase64String(msg.GetProperty("data").GetString()));
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
                        LocalScreenError?.Invoke(SafeStr(msg, "error"));
                        break;
                    case "localCameraFrame":
                        LocalCameraFrameReceived?.Invoke(Convert.FromBase64String(msg.GetProperty("data").GetString()));
                        break;
                    case "remoteCameraFrame":
                        RemoteCameraFrameReceived?.Invoke(Convert.FromBase64String(msg.GetProperty("data").GetString()));
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
                        LocalCameraError?.Invoke(SafeStr(msg, "error"));
                        break;
                    case "screenPreviewFrame":
                        ScreenPreviewFrameReceived?.Invoke(Convert.FromBase64String(msg.GetProperty("data").GetString()));
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
                            string spkJson = msg.TryGetProperty("speakers", out var spkEl) ? spkEl.GetString() : "[]";
                            DevicesEnumerated?.Invoke(camsJson, micsJson, spkJson);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveKit MSG ERROR] {ex.Message}");
            }
        }

        private static string SafeStr(JsonElement msg, string prop)
            => msg.TryGetProperty(prop, out var el) ? el.GetString() : "";

        private static int SafeInt(JsonElement msg, string prop)
            => msg.TryGetProperty(prop, out var el) && el.TryGetInt32(out var v) ? v : 0;

        // --- Демонстрация экрана: двухфазный флоу (превью -> подтверждение) ---

        private Form _pickerWindow;
        private Form _originalParentForm;

        /// <summary>Запускает захват экрана (системный диалог выбора экрана/окна).
        /// Системный диалог уровня Windows привязан к HWND родителя WebView2 —
        /// поэтому на время выбора переносим контрол в отдельное видимое окно.</summary>
        public void PreviewScreen(int resHeight = 1080, int fps = 30)
        {
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
                    if (_pickerWindow != null)
                    {
                        ReturnTransportWindowToOriginalParent();
                        LocalScreenError?.Invoke("user_cancelled_picker_window");
                    }
                };

                _pickerWindow.Show();
            }
            catch { }

            SendToJs(JsonSerializer.Serialize(new { cmd = "previewScreen", resHeight, fps }));
        }

        /// <summary>Возвращает _webView обратно в форму звонка после закрытия
        /// системного диалога выбора экрана.</summary>
        public void HideTransportWindow()
        {
            ReturnTransportWindowToOriginalParent();
        }

        // ── «Театр»: нативный рендер видео во весь видео-участок (плавные 60fps) ──
        // WebView остаётся в той же форме звонка — просто разворачиваем его на
        // переданную область (обычно bounds плиточного хоста) и показываем реальный
        // <video>. Кнопки звонка ниже него и остаются кликабельными (нет airspace).

        /// <summary>Развернуть WebView на область bounds и показать нативное видео.</summary>
        public void EnterTheater(string pid, string source, System.Drawing.Rectangle bounds)
        {
            try
            {
                if (_webView == null) return;
                _webView.Dock = DockStyle.None;
                _webView.Bounds = bounds;
                _webView.Visible = true;
                _webView.BringToFront();
            }
            catch { }
            SendToJs(JsonSerializer.Serialize(new { cmd = "theaterShow", pid, source }));
        }

        /// <summary>Обновить область театра (при ресайзе окна).</summary>
        public void UpdateTheaterBounds(System.Drawing.Rectangle bounds)
        {
            try { if (_webView != null && _webView.Visible && _webView.Width > 2) _webView.Bounds = bounds; }
            catch { }
        }

        /// <summary>Смена качества демонстрации «на горячую»: применяется к
        /// живому треку (захват + энкодер), а не только сохраняется.</summary>
        public void SetScreenQualityLive(int resHeight, int fps)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setScreenQuality", resHeight, fps }));

        /// <summary>Выйти из театра — вернуть WebView за пределы экрана (1x1).</summary>
        public void ExitTheater()
        {
            SendToJs(JsonSerializer.Serialize(new { cmd = "theaterHide" }));
            try
            {
                if (_webView == null) return;
                _webView.SendToBack();
                _webView.Size = new System.Drawing.Size(1, 1);
                _webView.Location = new System.Drawing.Point(-3000, -3000);
            }
            catch { }
        }

        private void ReturnTransportWindowToOriginalParent()
        {
            try
            {
                if (_pickerWindow == null) return;

                var pickerToClose = _pickerWindow;
                _pickerWindow = null;

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

        public void ConfirmScreenShare()
            => SendToJs(JsonSerializer.Serialize(new { cmd = "confirmScreenShare" }));

        public void CancelScreenPreview()
            => SendToJs(JsonSerializer.Serialize(new { cmd = "cancelScreenPreview" }));

        public void StopScreenShareTrack()
            => SendToJs(JsonSerializer.Serialize(new { cmd = "stopScreenTrack" }));

        // --- Камера ---

        public void PreviewCamera(string deviceLabel)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "previewCamera", deviceLabel }));

        public void ConfirmCameraShare()
            => SendToJs(JsonSerializer.Serialize(new { cmd = "confirmCameraShare" }));

        public void CancelCameraPreview()
            => SendToJs(JsonSerializer.Serialize(new { cmd = "cancelCameraPreview" }));

        public void StopCameraTrack()
            => SendToJs(JsonSerializer.Serialize(new { cmd = "stopCameraTrack" }));

        public void SwitchCameraDevice(string deviceLabel)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "switchCameraDevice", deviceLabel }));

        public void EnumerateDevices()
            => SendToJs(JsonSerializer.Serialize(new { cmd = "enumerateDevices" }));

        // --- Аудио ---

        /// <summary>Включает/выключает публикацию микрофона (mute).</summary>
        public void SetMicrophoneEnabled(bool enabled)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setMicEnabled", enabled }));

        /// <summary>Громкость звука демонстрации экрана собеседника (0.0–1.0).</summary>
        public void SetRemoteScreenAudioVolume(float volume)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setScreenAudioVolume", volume }));

        /// <summary>Громкость голоса собеседников (0.0–1.0+, можно усилить выше 1).</summary>
        public void SetRemoteVoiceVolume(float volume)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setVoiceVolume", volume }));

        /// <summary>Полностью заглушить весь входящий звук (голос + демка).</summary>
        public void SetRemoteMuted(bool muted)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setRemoteMuted", muted }));

        /// <summary>Громкость конкретного участника (0.0–2.0).</summary>
        public void SetParticipantVolume(string pid, float volume)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setParticipantVolume", pid, volume }));

        /// <summary>Заглушить конкретного участника.</summary>
        public void SetParticipantMuted(string pid, bool muted)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setParticipantMuted", pid, muted }));

        /// <summary>Сменить устройство микрофона по имени (label).</summary>
        public void SetInputDevice(string deviceLabel)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setInputDevice", deviceLabel }));

        /// <summary>Сменить устройство вывода (динамики) по имени (label).</summary>
        public void SetOutputDevice(string deviceLabel)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setOutputDevice", deviceLabel }));

        /// <summary>Порог активации голоса: auto=true — без порога; иначе threshold (0..100).</summary>
        public void SetVoiceGate(bool auto, int threshold)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setVoiceGate", auto, threshold }));

        /// <summary>Включить/выключить шумоподавление RNNoise.</summary>
        public void SetNoiseSuppression(bool on)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setNoiseSuppression", on }));

        /// <summary>Усиление микрофона (множитель, 0..3).</summary>
        public void SetMicGain(float gain)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setMicGain", gain }));

        /// <summary>Сменить отображаемое имя в звонке (рассылается участникам).</summary>
        public void SetDisplayName(string name)
            => SendToJs(JsonSerializer.Serialize(new { cmd = "setDisplayName", name }));

        private void SendToJs(string json)
        {
            if (_webView == null || _disposed) return;
            try
            {
                if (_webView.InvokeRequired)
                    _webView.BeginInvoke(() =>
                    {
                        try { _webView.CoreWebView2?.PostWebMessageAsString(json); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LiveKit SEND ERROR] {ex.Message}"); }
                    });
                else
                    _webView.CoreWebView2?.PostWebMessageAsString(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveKit SEND ERROR] {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { SendToJs(JsonSerializer.Serialize(new { cmd = "disconnect" })); } catch { }
            try { _webView?.Dispose(); } catch { }
            try
            {
                if (!string.IsNullOrEmpty(_tempHtmlDir) && Directory.Exists(_tempHtmlDir))
                    Directory.Delete(_tempHtmlDir, recursive: true);
            }
            catch { }
        }
    }
}
