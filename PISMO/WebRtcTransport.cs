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
            var envOptions = new CoreWebView2EnvironmentOptions(
                "--allow-running-insecure-content --autoplay-policy=no-user-gesture-required");
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
let perUserVolume = {};       // pid -> громкость 0..2
let perUserMuted = {};        // pid -> bool

function post(msg){ window.chrome.webview.postMessage(msg); }

// Опции захвата микрофона с шумоподавлением/эхоподавлением/авто-усилением.
// Передаём и стандартные флаги, и Chromium-специфичные goog-констрейнты,
// чтобы шумодав точно включился в движке WebView2.
function micCaptureOpts(){
    return {
        echoCancellation: true,
        noiseSuppression: true,
        autoGainControl: true,
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

function localMicPub(){
    try { return room && room.localParticipant ? room.localParticipant.getTrackPublication(LK.Track.Source.Microphone) : null; }
    catch(e){ return null; }
}

// Загрузка @sapphi-red/web-noise-suppressor (RNNoise) с фолбэком по CDN.
async function loadRnnoise(){
    if (_rnnoise) return _rnnoise;
    const to = (p, ms) => Promise.race([p, new Promise((_,r)=>setTimeout(()=>r(new Error('timeout')), ms))]);
    const bases = [
        'https://pismo-noise.local',
        'https://cdn.jsdelivr.net/npm/@sapphi-red/web-noise-suppressor@0.3.5/dist',
        'https://unpkg.com/@sapphi-red/web-noise-suppressor@0.3.5/dist'
    ];
    const esms = [
        'https://pismo-noise.local/wns.mjs',
        'https://esm.sh/@sapphi-red/web-noise-suppressor@0.3.5',
        'https://cdn.jsdelivr.net/npm/@sapphi-red/web-noise-suppressor@0.3.5/+esm'
    ];
    let mod, lastErr;
    for (const s of esms){ try { mod = await to(import(s), 8000); if (mod && mod.loadRnnoise) break; } catch(e){ lastErr = e; mod = null; } }
    if (!mod) throw lastErr || new Error('rnnoise esm не загрузился');
    let wasm, workletUrl, ok;
    for (const b of bases){
        try {
            wasm = await to(mod.loadRnnoise({ url: b + '/rnnoise/rnnoise.wasm', simdUrl: b + '/rnnoise/rnnoise_simd.wasm' }), 8000);
            workletUrl = b + '/rnnoise/workletProcessor.js';
            ok = true; break;
        } catch(e){ lastErr = e; }
    }
    if (!ok) throw lastErr || new Error('rnnoise wasm не загрузился');
    _rnnoise = { mod, wasm, workletUrl };
    return _rnnoise;
}

// LiveKit-совместимый процессор на основе RNNoise.
function createRnnoiseProcessor(){
    let ctx, src, node, dest;
    return {
        name: 'rnnoise',
        processedTrack: undefined,
        async init(opts){
            const r = await loadRnnoise();
            ctx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
            try { if (ctx.state === 'suspended') await ctx.resume(); } catch(e){}
            await ctx.audioWorklet.addModule(r.workletUrl);
            src = ctx.createMediaStreamSource(new MediaStream([opts.track]));
            node = new r.mod.RnnoiseWorkletNode(ctx, { wasmBinary: r.wasm, maxChannels: 1 });
            dest = ctx.createMediaStreamDestination();
            src.connect(node).connect(dest);
            this.processedTrack = dest.stream.getAudioTracks()[0];
        },
        async restart(opts){ await this.destroy(); await this.init(opts); },
        async destroy(){
            try { node && node.disconnect(); } catch(e){}
            try { src && src.disconnect(); } catch(e){}
            try { ctx && ctx.close(); } catch(e){}
            this.processedTrack = undefined;
        }
    };
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

function setMicGain(v){ /* усиление через граф убрано — autoGainControl справляется */ }

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
        room = new LK.Room({
            adaptiveStream: false,
            dynacast: false,
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
        room.on(LK.RoomEvent.ActiveSpeakersChanged, (speakers) => {
            try { post({type:'activeSpeakers', pids: JSON.stringify((speakers||[]).map(s => s.identity))}); } catch(e){}
        });
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
        track.attach(entry.el);
        post({type:'remoteTileStart', pid: pid, name: name, source: source});
        if (!entry.loop){
            const capEl = entry.el;
            entry.loop = makeExtractorTile(() => capEl, pid, source, source === 'screen' ? 20 : 15, source === 'screen' ? 0 : 360);
        }
        entry.loop.start();
    } else if (track.kind === 'audio'){
        const el = track.attach(); // воспроизводится автоматически
        el.style.display = 'none';
        document.body.appendChild(el);
        if (src === LK.Track.Source.ScreenShareAudio){
            remoteScreenAudioTrack = track;
            try { track.setVolume(remoteScreenAudioVolume); } catch(e){}
        } else {
            remoteVoiceTracks.push(track);
            (remoteAudioByPid[pid] = remoteAudioByPid[pid] || []).push(track);
            try { track.setVolume(effectiveVolume(pid)); } catch(e){}
        }
    }
}

// Итоговая громкость участника с учётом его персональных настроек и
// глобального «заглушить весь звук».
function effectiveVolume(pid){
    if (remoteVoiceMuted) return 0;
    if (perUserMuted[pid]) return 0;
    let v = (pid in perUserVolume) ? perUserVolume[pid] : remoteVoiceVolume;
    return v;
}

function applyPidVolume(pid){
    const arr = remoteAudioByPid[pid] || [];
    const v = effectiveVolume(pid);
    arr.forEach(t => { try { t.setVolume(v); } catch(e){} });
}

function setParticipantVolume(pid, v){
    perUserVolume[pid] = v;
    applyPidVolume(pid);
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
        post({type:'remoteTileStop', pid: pid, source: source});
    } else if (track.kind === 'audio'){
        if (src === LK.Track.Source.ScreenShareAudio){
            remoteScreenAudioTrack = null;
        } else {
            remoteVoiceTracks = remoteVoiceTracks.filter(t => t !== track);
            if (remoteAudioByPid[pid]) remoteAudioByPid[pid] = remoteAudioByPid[pid].filter(t => t !== track);
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
function startScreenPreviewExtraction(){ if(!screenPreviewLoop) screenPreviewLoop = makeExtractor(()=>screenPreviewVideoEl, 'screenPreviewFrame', 10, 400); screenPreviewLoop.start(); }
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
        let h = parseInt(resHeight) || 1080;
        let f = parseInt(fps) || 30;
        screenQualityH = h; screenQualityF = f;
        // НЕ форсим разрешение/соотношение захвата — иначе экран обрезается
        // (у мониторов 16:10 и т.п.). Захватываем нативно, а качество и fps
        // регулируем при публикации (maxBitrate/maxFramerate в confirmScreenShare).
        const tracks = await LK.createLocalScreenTracks({ audio: true });
        screenVideoTrack = tracks.find(t => t.kind === 'video') || null;
        screenAudioTrack = tracks.find(t => t.kind === 'audio') || null;
        if (!screenVideoTrack){ post({type:'localScreenError', error:'no screen video track'}); return; }

        // Остановка через системный диалог Chrome ('Stop sharing').
        const mst = screenVideoTrack.mediaStreamTrack;
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
            // Битрейт под выбранное разрешение — иначе LiveKit зажимает картинку
            // (выглядело как «фиксированные 720»). Для экрана simulcast выключаем.
            let maxBitrate = screenQualityH >= 1440 ? 8_000_000
                           : screenQualityH >= 1080 ? 5_000_000
                           : screenQualityH >= 720  ? 2_800_000
                           : 1_200_000;
            await room.localParticipant.publishTrack(screenVideoTrack, {
                source: LK.Track.Source.ScreenShare,
                simulcast: false,
                videoEncoding: { maxBitrate: maxBitrate, maxFramerate: screenQualityF }
            });
            if (screenAudioTrack){ try{ await room.localParticipant.publishTrack(screenAudioTrack, { source: LK.Track.Source.ScreenShareAudio }); }catch(e){} }
            screenPublished = true;
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
    remoteScreenAudioVolume = v;
    if (remoteScreenAudioTrack){ try{ remoteScreenAudioTrack.setVolume(v); }catch(e){} }
}

function setVoiceVolume(v){
    remoteVoiceVolume = v;
    Object.keys(remoteAudioByPid).forEach(applyPidVolume);
}

function setRemoteMuted(muted){
    remoteVoiceMuted = muted;
    Object.keys(remoteAudioByPid).forEach(applyPidVolume);
    if (remoteScreenAudioTrack){ try{ remoteScreenAudioTrack.setVolume(muted ? 0 : remoteScreenAudioVolume); }catch(e){} }
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
        case 'confirmScreenShare': confirmScreenShare(); break;
        case 'cancelScreenPreview': cancelScreenPreview(); break;
        case 'stopScreenTrack': stopScreenShareTrack(); break;
        case 'enumerateDevices': enumerateDevices(); break;
        case 'setMicEnabled': setMicEnabled(msg.enabled); break;
        case 'setScreenAudioVolume': setScreenAudioVolume(msg.volume); break;
        case 'setVoiceVolume': setVoiceVolume(msg.volume); break;
        case 'setRemoteMuted': setRemoteMuted(msg.muted); break;
        case 'setParticipantVolume': setParticipantVolume(msg.pid, msg.volume); break;
        case 'setParticipantMuted': setParticipantMuted(msg.pid, msg.muted); break;
        case 'setInputDevice': setAudioDevice('audioinput', msg.deviceLabel); break;
        case 'setOutputDevice': setAudioDevice('audiooutput', msg.deviceLabel); break;
        case 'setVoiceGate': setVoiceGate(msg.auto, msg.threshold); break;
        case 'setNoiseSuppression': setNoiseSuppression(msg.on); break;
        case 'setMicGain': setMicGain(msg.gain); break;
        case 'setDisplayName': try { if (room) room.localParticipant.setName(msg.name); } catch(e){} break;
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
