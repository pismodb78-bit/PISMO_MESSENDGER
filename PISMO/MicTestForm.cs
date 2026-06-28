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
        private string _tempDir;

        public MicTestForm(bool noiseSuppression)
        {
            _noise = noiseSuppression;
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
const st=(t)=>{document.getElementById('st').textContent=t;};
async function loadKrisp(){
  // Сначала локально (папка noise рядом с exe), потом CDN.
  try { return await import('https://pismo-noise.local/krisp.mjs'); } catch(e){}
  return await import('https://cdn.jsdelivr.net/npm/@livekit/krisp-noise-filter/+esm');
}
async function start(){
  let stream;
  try { stream = await navigator.mediaDevices.getUserMedia({audio:{echoCancellation:true,noiseSuppression:true,autoGainControl:true}}); }
  catch(e){ st('Нет доступа к микрофону: '+e); return; }
  const ctx = new (window.AudioContext||window.webkitAudioContext)();
  try { if (ctx.state==='suspended') await ctx.resume(); } catch(e){}
  let track = stream.getAudioTracks()[0];
  if (NOISE){
    try {
      const m = await loadKrisp();
      if (m.isKrispNoiseFilterSupported && !m.isKrispNoiseFilterSupported()){ st('Krisp не поддерживается, без шумодава'); }
      else { const proc = m.KrispNoiseFilter(); await proc.init({ track, audioContext: ctx }); if (proc.processedTrack) track = proc.processedTrack; st('Шумодав Krisp активен — слышите себя'); }
    } catch(e){ st('Krisp недоступен ('+e+') — слышите себя без шумодава'); }
  } else { st('Слышите себя (шумодав выключен)'); }
  const out = new MediaStream([track]);
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
