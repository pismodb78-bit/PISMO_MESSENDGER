using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace PISMO
{
    /// <summary>
    /// Тест микрофона на WebView2: тот же конвейер, что в звонке
    /// (микрофон → Krisp-шумодав → обратно в наушники), плюс индикатор уровня.
    /// Так слышно ровно то, что услышат собеседники.
    ///
    /// Krisp грузится сначала ЛОКАЛЬНО (папка noise рядом с exe, отдаётся через
    /// virtual host), при отсутствии — с CDN.
    /// </summary>
    public sealed class MicTestForm : Form
    {
        private readonly WebView2 _web;
        private readonly bool _noise;
        private readonly string _micLabel;
        private string _tempDir;

        public MicTestForm(bool noiseSuppression, string micLabel = null)
        {
            _noise = noiseSuppression;
            _micLabel = micLabel ?? "";
            Text = "PISMO — Проверка микрофона";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(30, 31, 34);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 240);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);
            Load += async (s, e) => await InitAsync();
        }

        private async System.Threading.Tasks.Task InitAsync()
        {
            try
            {
                var envOptions = new CoreWebView2EnvironmentOptions(
                    "--allow-running-insecure-content --autoplay-policy=no-user-gesture-required");
                // Отдельная папка данных, чтобы не конфликтовать с WebView2 звонка
                // (две среды с одной папкой и разными опциями → пустое окно).
                string udf = Path.Combine(Path.GetTempPath(), "pismo_wv_mictest");
                var env = await CoreWebView2Environment.CreateAsync(null, udf, envOptions);
                await _web.EnsureCoreWebView2Async(env);

                _web.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    if (!e.IsSuccess)
                        try { _web.CoreWebView2.NavigateToString("<body style='background:#1e1f22;color:#ed4245;font-family:sans-serif;padding:16px'>Не удалось загрузить тест (" + e.WebErrorStatus + ")</body>"); } catch { }
                };

                _web.CoreWebView2.PermissionRequested += (s, e) =>
                {
                    if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                        e.State = CoreWebView2PermissionState.Allow;
                };

                _tempDir = Path.Combine(Path.GetTempPath(), "pismo_mictest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_tempDir);
                File.WriteAllText(Path.Combine(_tempDir, "index.html"), BuildHtml(), System.Text.Encoding.UTF8);
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "pismo-mic.local", _tempDir, CoreWebView2HostResourceAccessKind.Allow);

                // Локальная папка noise рядом с exe (для офлайн-Krisp), если есть.
                try
                {
                    string noiseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "noise");
                    if (Directory.Exists(noiseDir))
                        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            "pismo-noise.local", noiseDir, CoreWebView2HostResourceAccessKind.Allow);
                }
                catch { }

                _web.CoreWebView2.Navigate("https://pismo-mic.local/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось запустить тест микрофона: " + ex.Message, "PISMO");
                Close();
            }
        }

        private string BuildHtml()
        {
            string noiseJs = _noise ? "true" : "false";
            string micJs = System.Text.Json.JsonSerializer.Serialize(_micLabel ?? "");
            return @"<!doctype html><html><head><meta charset='utf-8'><style>
html,body{margin:0;height:100%;background:#1e1f22;color:#dcddde;font-family:Segoe UI,sans-serif;}
.wrap{display:flex;flex-direction:column;gap:14px;padding:18px;}
h3{margin:0;font-size:15px;}
#st{font-size:12px;color:#a8aab0;min-height:16px;}
#bar{height:26px;background:#202225;border-radius:6px;overflow:hidden;}
#fill{height:100%;width:0%;background:linear-gradient(90deg,#3ba55d,#faa61a,#ed4245);transition:width .05s;}
.hint{font-size:11px;color:#72767d;}
</style></head><body><div class='wrap'>
<h3>🎤 Говорите — вы слышите себя</h3>
<div id='bar'><div id='fill'></div></div>
<div id='st'>Запуск…</div>
<div class='hint'>Если включён шумодав — постучите по клавиатуре: в звонке он давится так же.</div>
</div>
<audio id='mon' autoplay></audio>
<script>
const NOISE=" + noiseJs + @";
const MICLABEL=" + micJs + @";
const st=(t)=>{document.getElementById('st').textContent=t;};
async function pickDeviceId(){
  if(!MICLABEL) return undefined;
  try{
    const ds = await navigator.mediaDevices.enumerateDevices();
    const norm = (x)=> (x||'').toLowerCase();
    const m = ds.find(d=> d.kind==='audioinput' && d.label && (norm(d.label).includes(norm(MICLABEL)) || norm(MICLABEL).includes(norm(d.label))));
    return m ? m.deviceId : undefined;
  }catch(e){ return undefined; }
}
function withTimeout(p, ms){ return Promise.race([p, new Promise((_,rej)=>setTimeout(()=>rej(new Error('таймаут '+ms+'мс')), ms))]); }
let _rn=null;
async function loadRnnoise(){
  if(_rn) return _rn;
  // ВАЖНО: esm-модуль, wasm и worklet ДОЛЖНЫ грузиться из ОДНОГО источника/версии,
  // иначе RnnoiseWorkletNode и workletProcessor.js несовместимы -> узел молчит.
  const providers=[
    {esm:'https://pismo-noise.local/wns.mjs', base:'https://pismo-noise.local'},
    {esm:'https://esm.sh/@sapphi-red/web-noise-suppressor@0.3.5', base:'https://esm.sh/@sapphi-red/web-noise-suppressor@0.3.5/dist'},
    {esm:'https://cdn.jsdelivr.net/npm/@sapphi-red/web-noise-suppressor@0.3.5/+esm', base:'https://cdn.jsdelivr.net/npm/@sapphi-red/web-noise-suppressor@0.3.5/dist'},
    {esm:'https://unpkg.com/@sapphi-red/web-noise-suppressor@0.3.5/dist/index.mjs', base:'https://unpkg.com/@sapphi-red/web-noise-suppressor@0.3.5/dist'}
  ];
  let err;
  for(const p of providers){
    try{
      const mod=await withTimeout(import(p.esm),8000);
      if(!mod||!mod.loadRnnoise||!mod.RnnoiseWorkletNode) throw new Error('нет экспортов');
      const wasm=await withTimeout(mod.loadRnnoise({url:p.base+'/rnnoise.wasm',simdUrl:p.base+'/rnnoise_simd.wasm'}),8000);
      const wurl=p.base+'/rnnoise/workletProcessor.js';
      _rn={mod,wasm,wurl}; return _rn;
    }catch(e){ err=e; }
  }
  throw err||new Error('rnnoise');
}

async function start(){
  st('Запрашиваю микрофон…');
  // Когда наш RNNoise включён — берём СЫРОЙ сигнал (без браузерного шумодава/AGC),
  // чтобы фильтрация была слышна. Без него — обычная браузерная очистка.
  const baseAudio = NOISE
    ? {echoCancellation:true, noiseSuppression:false, autoGainControl:false}
    : {echoCancellation:true, noiseSuppression:true, autoGainControl:true};
  let stream;
  try { stream = await navigator.mediaDevices.getUserMedia({audio:baseAudio}); }
  catch(e){ st('Нет доступа к микрофону: '+e); return; }
  // Если в настройках выбран конкретный микрофон — переключаемся на него.
  try {
    const id = await pickDeviceId();
    if (id){
      stream.getTracks().forEach(t=>t.stop());
      stream = await navigator.mediaDevices.getUserMedia({audio:Object.assign({}, baseAudio, {deviceId:{exact:id}})});
    }
  } catch(e){}
  const ctx = new (window.AudioContext||window.webkitAudioContext)({ sampleRate: 48000 });
  try { if (ctx.state==='suspended') await ctx.resume(); } catch(e){}
  let outStream = stream;
  if (NOISE){
    st('Загружаю шумодав RNNoise…');
    try {
      const r = await loadRnnoise();
      await ctx.audioWorklet.addModule(r.wurl);
      const srcN = ctx.createMediaStreamSource(stream);
      const node = new r.mod.RnnoiseWorkletNode(ctx, { wasmBinary: r.wasm, maxChannels: 1 });
      const dest = ctx.createMediaStreamDestination();
      srcN.connect(node).connect(dest);
      outStream = dest.stream;
      st('✓ Шумодав RNNoise активен — слышите себя');
      // Защита от ""активен, но молчит"": если обработанный поток молчит,
      // а на вход звук идёт — откатываемся на сырой микрофон.
      try{
        const aIn=ctx.createAnalyser(); aIn.fftSize=512; ctx.createMediaStreamSource(stream).connect(aIn);
        const aOut=ctx.createAnalyser(); aOut.fftSize=512; ctx.createMediaStreamSource(dest.stream).connect(aOut);
        const bi=new Uint8Array(aIn.fftSize), bo=new Uint8Array(aOut.fftSize);
        let inSum=0,outSum=0,n=0; const tm=setInterval(()=>{
          aIn.getByteTimeDomainData(bi); aOut.getByteTimeDomainData(bo);
          let si=0,so=0; for(let i=0;i<bi.length;i++){const x=(bi[i]-128)/128;si+=x*x;} for(let i=0;i<bo.length;i++){const x=(bo[i]-128)/128;so+=x*x;}
          inSum+=Math.sqrt(si/bi.length); outSum+=Math.sqrt(so/bo.length); n++;
          if(n>=30){ clearInterval(tm);
            if(inSum>0.01 && outSum<inSum*0.05){
              const a=document.getElementById('mon'); a.srcObject=stream;
              st('⚠ Шумодав молчал — переключился на чистый микрофон');
            }
          }
        },50);
      }catch(e){}
    } catch(e){ st('Шумодав недоступен ('+e+') — слышите себя без него'); outStream = stream; }
  } else { st('Слышите себя (шумодав выключен)'); }
  const out = outStream;
  const a = document.getElementById('mon'); a.srcObject = out; try { await a.play(); } catch(e){}
  const an = ctx.createAnalyser(); an.fftSize=512;
  ctx.createMediaStreamSource(out).connect(an);
  const data=new Uint8Array(an.fftSize);
  const fill=document.getElementById('fill');
  function draw(){
    an.getByteTimeDomainData(data);
    let s=0; for(let i=0;i<data.length;i++){const x=(data[i]-128)/128; s+=x*x;}
    const rms=Math.sqrt(s/data.length);
    const pct=Math.min(100, Math.round(rms*300));
    fill.style.width=pct+'%';
    requestAnimationFrame(draw);
  }
  draw();
}
start();
</script></body></html>";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _web.Dispose(); } catch { }
            try { if (_tempDir != null && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
            base.OnFormClosed(e);
        }
    }
}
