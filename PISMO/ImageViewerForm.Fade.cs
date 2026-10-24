// ============================================================
//  ImageViewerForm.Fade — интерфейс просмотра гаснет, как на телефоне
// ============================================================
//
//  Стрелки, подпись «Фото 3 из 12», строка масштаба и панель инструментов
//  плавно гаснут через пару секунд без движения мыши и плавно возвращаются,
//  стоит её сдвинуть. Ради снимка просмотр и открывают — рамка кнопок
//  поверх него лишняя, пока ею не пользуются.
//
//  Прозрачности у обычных контролов Windows нет, поэтому «затухание» — это
//  плавное смешивание их цветов с фоном просмотра, а в самом конце они
//  прячутся совсем, чтобы невидимые кнопки не ловили нажатия.
//
//  Над роликом мышь видит не окно, а страница плеера: она сама сообщает о
//  движении (см. VideoPlayerHtml — 'activity'), и интерфейс просыпается так
//  же, как над снимком.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    internal sealed partial class ImageViewerForm
    {
        /// <summary>Через сколько без движения мыши интерфейс начинает гаснуть.</summary>
        private const int IdleMs = 2600;

        private System.Windows.Forms.Timer _fadeTimer;
        private float _chromeAlpha = 1f;
        private float _chromeTarget = 1f;
        private DateTime _lastActivity = DateTime.UtcNow;
        private Point _lastMouse = Point.Empty;
        private bool _menuOpen;

        /// <summary>Исходные цвета гаснущих контролов — чтобы вернуть их точь-в-точь.</summary>
        private readonly Dictionary<Control, (Color back, Color fore)> _fadeBase = new();

        /// <summary>Доля видимости для стрелок — их рисует сама панель.</summary>
        internal float ChromeAlpha => _chromeAlpha;

        private void EnsureFade()
        {
            if (_fadeTimer != null) return;
            _fadeTimer = new System.Windows.Forms.Timer { Interval = 30 };
            _fadeTimer.Tick += (s, e) => FadeTick();
            _fadeTimer.Start();

            // Любое движение мыши над окном и его содержимым будит интерфейс.
            HookActivity(this);
            KeyDown += (s, e) => WakeChrome();
            Disposed += (s, e) => { try { _fadeTimer.Stop(); _fadeTimer.Dispose(); } catch { } };
        }

        private void HookActivity(Control c)
        {
            c.MouseMove += (s, e) =>
            {
                // Подрагивание мыши на месте событием не считаем.
                var p = Cursor.Position;
                if (Math.Abs(p.X - _lastMouse.X) + Math.Abs(p.Y - _lastMouse.Y) < 3) return;
                _lastMouse = p;
                WakeChrome();
            };
            c.MouseDown += (s, e) => WakeChrome();
            c.MouseWheel += (s, e) => WakeChrome();
            c.ControlAdded += (s, e) => { if (e.Control != null) HookActivity(e.Control); };
            foreach (Control ch in c.Controls) HookActivity(ch);
        }

        /// <summary>Мышь шевельнулась — интерфейс виден ещё пару секунд.</summary>
        internal void WakeChrome()
        {
            _lastActivity = DateTime.UtcNow;
            if (_chromeTarget < 1f)
            {
                _chromeTarget = 1f;
                SetChromeVisible(true);
            }
        }

        private IEnumerable<Control> ChromeControls()
        {
            if (_btnPrev != null) yield return _btnPrev;
            if (_btnNext != null) yield return _btnNext;
            if (_lblInfo != null) yield return _lblInfo;
            if (_tools != null) yield return _tools;
            if (_lblZoom != null) yield return _lblZoom;
        }

        private void FadeTick()
        {
            if (IsDisposed) return;

            // Курсор на самом интерфейсе — не гасим: человек целится в кнопку.
            bool overChrome = false;
            try
            {
                var p = _canvas.PointToClient(Cursor.Position);
                foreach (var c in ChromeControls())
                    if (c.Visible && c.Bounds.Contains(p)) { overChrome = true; break; }
            }
            catch { }
            if (overChrome || _menuOpen) _lastActivity = DateTime.UtcNow;

            if ((DateTime.UtcNow - _lastActivity).TotalMilliseconds > IdleMs) _chromeTarget = 0f;

            if (Math.Abs(_chromeAlpha - _chromeTarget) < 0.001f) return;
            // Появление быстрее, чем угасание: вернуть кнопки нужно сразу.
            float step = _chromeTarget > _chromeAlpha ? 0.22f : 0.08f;
            _chromeAlpha = _chromeTarget > _chromeAlpha
                ? Math.Min(_chromeTarget, _chromeAlpha + step)
                : Math.Max(_chromeTarget, _chromeAlpha - step);

            ApplyChromeAlpha();
            if (_chromeAlpha <= 0.001f) SetChromeVisible(false);
        }

        private void ApplyChromeAlpha()
        {
            var bg = _canvas.BackColor;
            foreach (var c in ChromeControls()) Blend(c, bg);
            // Стрелки рисуются сами — им достаточно перерисоваться с новой долей.
            try { _btnPrev?.Invalidate(); _btnNext?.Invalidate(); } catch { }
        }

        /// <summary>Смешать цвета контрола (и его детей) с фоном по текущей доле.</summary>
        private void Blend(Control c, Color bg)
        {
            if (c is Panel p && (ReferenceEquals(p, _btnPrev) || ReferenceEquals(p, _btnNext))) return;
            if (!_fadeBase.ContainsKey(c)) _fadeBase[c] = (c.BackColor, c.ForeColor);
            var (back, fore) = _fadeBase[c];
            try
            {
                if (back.A == 255) c.BackColor = Mix(bg, back, _chromeAlpha);
                c.ForeColor = Mix(bg, fore, _chromeAlpha);
                if (c is Button b)
                {
                    // Пока кнопка гаснет, подсветка под мышью не должна
                    // вспыхивать прежним цветом; видимой — подсветка обычная.
                    bool full = _chromeAlpha >= 0.999f;
                    b.FlatAppearance.MouseOverBackColor = full ? Color.Empty : c.BackColor;
                    b.FlatAppearance.MouseDownBackColor = full ? Color.Empty : c.BackColor;
                }
            }
            catch { }
            foreach (Control ch in c.Controls) Blend(ch, bg);
        }

        private static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private void SetChromeVisible(bool on)
        {
            foreach (var c in ChromeControls())
            {
                try
                {
                    if (!on) { c.Visible = false; continue; }
                    // Стрелки — только там, где есть куда листать.
                    if (ReferenceEquals(c, _btnPrev)) c.Visible = _nav != null && _navIdx > 0;
                    else if (ReferenceEquals(c, _btnNext)) c.Visible = _nav != null && _navIdx < _nav.Count - 1;
                    else c.Visible = true;
                }
                catch { }
            }
            if (on) { try { LayoutChrome(); } catch { } }
        }
    }
}
