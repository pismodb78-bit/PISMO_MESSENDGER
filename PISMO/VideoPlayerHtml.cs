using System.Net;

namespace PISMO
{
    /// <summary>
    /// Страница проигрывателя видео — одна на всё приложение.
    ///
    /// ЗАЧЕМ СВОИ КНОПКИ. Chromium рисует видео собственной панелью: она
    /// лежит сплошной полосой по низу кадра, держит пуск, перемотку и
    /// громкость в одном ряду и выглядит иначе, чем на телефоне. Телефон и
    /// компьютер — одно приложение, и плеер в них должен быть один.
    ///
    /// ЧТО ЗДЕСЬ. Главные кнопки — пуск и перемотка на десять секунд —
    /// стоят посередине кадра и полупрозрачны: сквозь них видно видео.
    /// Полоса времени осталась внизу, но приподнята и тоже полупрозрачна,
    /// чтобы не спорить с кнопками. Всё это пропадает, пока видео идёт, и
    /// возвращается от движения мыши или касания — как в любом плеере.
    ///
    /// Полный экран просит САМА СТРАНИЦА (requestFullscreen), поэтому окно
    /// по-прежнему узнаёт о нём через ContainsFullScreenElementChanged и
    /// разворачивается, как раньше.
    /// </summary>
    internal static class VideoPlayerHtml
    {
        /// <param name="src">Адрес файла на виртуальном хосте.</param>
        /// <param name="inline">
        /// true — плеер стоит В ЛЕНТЕ, прямо в пузыре. Там полоса времени
        /// лежит почти по нижнему краю кадра: поднятая, как в отдельном окне,
        /// она закрывала середину видео, а спорить ей в пузыре не с чем —
        /// ни подписи под кадром, ни жестовой черты внизу там нет.
        /// </param>
        public static string Page(string src, bool inline = false)
        {
            return Template
                .Replace("__BARBOTTOM__", inline ? "10px" : "48px")
                .Replace("__INLINE__", inline ? "1" : "0")
                .Replace("__SRC__", WebUtility.HtmlEncode(src ?? ""));
        }

        private const string Template = @"<!doctype html>
<html><head><meta charset='utf-8'><style>
html,body{margin:0;height:100%;background:#141518;overflow:hidden;
  font-family:'Segoe UI',system-ui,sans-serif;user-select:none;}
#wrap{position:relative;width:100%;height:100%;background:#141518;}
video{width:100%;height:100%;object-fit:contain;background:#141518;outline:none;display:block;}
#mid{position:absolute;left:0;right:0;top:50%;transform:translateY(-50%);
  display:flex;align-items:center;justify-content:center;gap:22px;
  transition:opacity .22s;}
.rnd{display:flex;align-items:center;justify-content:center;border:0;cursor:pointer;
  color:#fff;background:rgba(0,0,0,.40);border-radius:50%;
  width:46px;height:46px;font-size:14px;font-weight:600;}
.rnd.big{width:60px;height:60px;font-size:22px;}
.rnd:hover{background:rgba(0,0,0,.62);}
#bar{position:absolute;left:16px;right:16px;bottom:__BARBOTTOM__;display:flex;align-items:center;
  gap:10px;padding:6px 12px;border-radius:18px;background:rgba(0,0,0,.35);
  color:#fff;font-size:12px;transition:opacity .22s;}
#seek{flex:1;height:4px;-webkit-appearance:none;appearance:none;border-radius:2px;
  background:rgba(255,255,255,.35);outline:none;cursor:pointer;}
#seek::-webkit-slider-thumb{-webkit-appearance:none;width:12px;height:12px;border-radius:50%;
  background:#fff;cursor:pointer;}
#bar button{border:0;background:transparent;color:#fff;cursor:pointer;font-size:15px;padding:0 2px;}
.gone{opacity:0;pointer-events:none;}
</style></head><body>
<div id='wrap'>
  <video id='v' src='__SRC__' autoplay playsinline preload='auto'></video>
  <div id='mid'>
    <button class='rnd' id='back' title='Назад на 10 секунд'>&minus;10</button>
    <button class='rnd big' id='play' title='Пуск или пауза'>&#10074;&#10074;</button>
    <button class='rnd' id='fwd' title='Вперёд на 10 секунд'>+10</button>
  </div>
  <div id='bar'>
    <span id='cur'>0:00</span>
    <input id='seek' type='range' min='0' max='1000' value='0' step='1'>
    <span id='dur'>0:00</span>
    <button id='mute' title='Звук'>&#128266;</button>
    <button id='full' title='Во весь экран'>&#9974;</button>
  </div>
</div>
<script>
var v=document.getElementById('v'), mid=document.getElementById('mid'), bar=document.getElementById('bar');
var play=document.getElementById('play'), seek=document.getElementById('seek');
var cur=document.getElementById('cur'), dur=document.getElementById('dur');
var mute=document.getElementById('mute'), full=document.getElementById('full');
var hideAt=0, dragging=false;

function say(k){try{window.chrome.webview.postMessage(k);}catch(e){}}
function clock(s){s=Math.max(0,Math.floor(s||0));var m=Math.floor(s/60);
  var r=s%60;return m+':'+(r<10?'0':'')+r;}
function show(){mid.classList.remove('gone');bar.classList.remove('gone');
  hideAt=Date.now()+2800;}
function tick(){
  if(!dragging && v.duration>0){
    seek.value=Math.round(v.currentTime/v.duration*1000);
    cur.textContent=clock(v.currentTime);
    dur.textContent=clock(v.duration);
  }
  // Прячем только идущее видео: на паузе кнопки нужны — ради них и остановили.
  if(!v.paused && Date.now()>hideAt){mid.classList.add('gone');bar.classList.add('gone');}
  requestAnimationFrame(tick);
}
function toggle(){ if(v.paused) v.play(); else v.pause(); show(); }

// Размеры кадра — наружу: по ним лента узнаёт форму ролика и со
// следующего показа ставит коробку по нему, а не 4:3 на всё подряд.
v.addEventListener('loadedmetadata',function(){
  if(v.videoWidth>0&&v.videoHeight>0) say('size:'+v.videoWidth+'x'+v.videoHeight);
});

play.onclick=function(e){e.stopPropagation();toggle();};
document.getElementById('back').onclick=function(e){e.stopPropagation();v.currentTime=Math.max(0,v.currentTime-10);show();};
document.getElementById('fwd').onclick=function(e){e.stopPropagation();v.currentTime=Math.min(v.duration||0,v.currentTime+10);show();};
mute.onclick=function(e){e.stopPropagation();v.muted=!v.muted;
  mute.innerHTML=v.muted?'&#128263;':'&#128266;';show();};
var INLINE=__INLINE__;
full.onclick=function(e){e.stopPropagation();
  // В ЛЕНТЕ разворачивание — это окно просмотра: там и соседние ролики
  // стрелками, и скорость, и «по кругу». В самом окне — настоящий полный
  // экран, как раньше.
  if(INLINE){say('expand');return;}
  var w=document.getElementById('wrap');
  if(document.fullscreenElement) document.exitFullscreen(); else w.requestFullscreen();show();};

// Клавиши ловит САМА СТРАНИЦА: пока в неё вставлен курсор, стрелки до окна
// не доходят вовсе — и листание соседних роликов не работало.
document.addEventListener('keydown',function(e){
  if(e.key==='ArrowLeft'){ if(INLINE){v.currentTime=Math.max(0,v.currentTime-10);show();} else say('key:prev'); e.preventDefault(); }
  else if(e.key==='ArrowRight'){ if(INLINE){v.currentTime=Math.min(v.duration||0,v.currentTime+10);show();} else say('key:next'); e.preventDefault(); }
  else if(e.key===' '){ toggle(); e.preventDefault(); }
  else if(e.key==='Escape'){ if(!INLINE) say('key:close'); }
});

seek.addEventListener('input',function(){dragging=true;show();
  if(v.duration>0) cur.textContent=clock(seek.value/1000*v.duration);});
seek.addEventListener('change',function(){
  if(v.duration>0) v.currentTime=seek.value/1000*v.duration;
  dragging=false;show();});

v.addEventListener('play',function(){play.innerHTML='&#10074;&#10074;';show();say('playing');});

// ОБЛОЖКА — ПРЯМО С КАДРА, который показывает плеер.
//
// Кадр у Проводника Windows умеет не для всех роликов (снятое телефоном в
// HEVC без расширения кодека он не читает), а раз плеер ролик показывает —
// кадр у нас уже есть. Снимаем его один раз, уменьшаем и отдаём окну: оно
// дописывает обложку в сообщение, и её дальше видят все.
//
// ЧЁРНЫЙ КАДР В ОБЛОЖКУ НЕ БЕРЁМ: у многих роликов начало — затемнение или
// заставка, и обложка выходила чёрным квадратом. Пока ролик идёт, ждём первый
// светлый кадр; не нашёлся за восемь секунд — отдаём самый светлый из виденных.
var coverSent=false, coverBest=null, coverBestL=-1;
function frameLum(){
  try{
    var c=document.createElement('canvas'); c.width=24; c.height=24;
    var x=c.getContext('2d'); x.drawImage(v,0,0,24,24);
    var d=x.getImageData(0,0,24,24).data, s=0;
    for(var i=0;i<d.length;i+=4) s+=0.299*d[i]+0.587*d[i+1]+0.114*d[i+2];
    return s/(d.length/4);
  }catch(e){ return 255; }
}
function sendCover(force){
  if(coverSent||!v.videoWidth||!v.videoHeight) return;
  try{
    var l=frameLum();
    var k=Math.min(1,640/Math.max(v.videoWidth,v.videoHeight));
    var c=document.createElement('canvas');
    c.width=Math.max(1,Math.round(v.videoWidth*k));
    c.height=Math.max(1,Math.round(v.videoHeight*k));
    c.getContext('2d').drawImage(v,0,0,c.width,c.height);
    var url=c.toDataURL('image/jpeg',0.72);
    if(!url||url.length<=64) return;
    if(l<22 && !force){
      if(l>coverBestL){ coverBestL=l; coverBest=url; }
      return;
    }
    coverSent=true;say('cover:'+url.substring(url.indexOf(',')+1));
  }catch(e){}
}
var lastCoverTry=0;
v.addEventListener('loadeddata',function(){setTimeout(function(){sendCover(false);},150);});
v.addEventListener('timeupdate',function(){
  if(coverSent||v.currentTime<=0.2) return;
  var now=Date.now(); if(now-lastCoverTry<400) return; lastCoverTry=now;
  if(v.currentTime>=8&&coverBest){ coverSent=true; say('cover:'+coverBest.substring(coverBest.indexOf(',')+1)); return; }
  sendCover(false);
});
v.addEventListener('pause',function(){play.innerHTML='&#9654;';show();});
v.addEventListener('loadedmetadata',function(){
  dur.textContent=clock(v.duration);
  // Нечем раскодировать видеодорожку: Chromium в этом случае молча играет
  // ОДИН ЗВУК и показывает чёрный прямоугольник — со стороны это выглядит
  // как «сломалось приложение». Признак — videoWidth == 0 при уже
  // прочитанных метаданных.
  if(!v.videoWidth) say('novideo');
});
v.addEventListener('error',function(){say('novideo');});

document.addEventListener('mousemove',show);
document.addEventListener('touchstart',show);
// В окне просмотра кнопки вокруг ролика гаснут без движения мыши, а над
// страницей окно мыши не видит — сообщаем о движении сами, не чаще
// пары раз в секунду.
var lastAct=0;
function activity(){ if(INLINE) return; var t=Date.now(); if(t-lastAct<400) return; lastAct=t; say('activity'); }
document.addEventListener('mousemove',activity);
document.addEventListener('mousedown',activity);
document.addEventListener('keydown',activity);
document.getElementById('wrap').addEventListener('click',function(){toggle();});
document.addEventListener('keydown',function(e){
  if(e.code==='Space'){e.preventDefault();toggle();}
  if(e.code==='ArrowLeft'){v.currentTime=Math.max(0,v.currentTime-5);show();}
  if(e.code==='ArrowRight'){v.currentTime=Math.min(v.duration||0,v.currentTime+5);show();}
});

show();tick();
</script>
</body></html>";
    }
}
