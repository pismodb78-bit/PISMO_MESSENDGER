using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>Строка участника голосового канала для игрового оверлея.</summary>
    internal sealed class OverlayMember
    {
        public int Uid;
        public string Name = "";
        public bool MicMuted;
        public bool Deafened;
        public bool Streaming;   // камера или демонстрация экрана → бейдж «В ЭФИРЕ»
        public bool Speaking;
        public bool IsSelf;
    }

    /// <summary>
    /// Игровой оверлей в стиле Discord: полупрозрачная панель у ПРАВОГО края
    /// экрана со списком участников голосового канала — аватар, имя, значок
    /// выключенного микрофона/наушников и красный бейдж «В ЭФИРЕ», когда у
    /// человека включена камера или демонстрация экрана.
    ///
    /// Окно слоёное (WS_EX_LAYERED) и сквозное для мыши (WS_EX_TRANSPARENT) —
    /// клики уходят в игру. WS_EX_NOACTIVATE не даёт ему забрать фокус,
    /// WS_EX_TOOLWINDOW убирает его из Alt+Tab и панели задач.
    ///
    /// ОГРАНИЧЕНИЕ: поверх игры в ЭКСКЛЮЗИВНОМ полноэкранном режиме такое окно
    /// не рисуется — так устроен Windows, обойти это можно только перехватом
    /// DirectX. Поверх оконного и «полноэкранного оконного» (borderless) режимов,
    /// в которых играет большинство, всё работает.
    /// </summary>
    internal sealed class GameOverlayForm : Form
    {
        // ── Оформление ──────────────────────────────────────────────────
        private const int Pad = 9;          // внутренние поля карточки
        private const int RowH = 32;        // высота строки участника
        private const int AvatarSz = 24;
        private const int Gap = 8;
        private const int MuteSz = 20;
        private const int ScreenMargin = 14;   // отступ от правого края экрана
        private const int Radius = 12;

        private static readonly Color CardBack = Color.FromArgb(214, 30, 31, 34);
        private static readonly Color CardEdge = Color.FromArgb(90, 0, 0, 0);
        private static readonly Color NameFg = Color.FromArgb(236, 238, 242);
        private static readonly Color SelfFg = Color.FromArgb(120, 200, 140);
        private static readonly Color SpeakRing = Color.FromArgb(59, 165, 93);
        private static readonly Color BadgeBack = Color.FromArgb(237, 66, 69);
        private static readonly Color MoreFg = Color.FromArgb(150, 152, 158);
        // Непрозрачный «двойник» фона карточки — им MuteGlyph рисует вырез под
        // перечёркивающей линией (полупрозрачный цвет дал бы грязный след).
        private static readonly Color SlashBg = Color.FromArgb(255, 34, 35, 39);

        private readonly Font _fName = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        private readonly Font _fBadge = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
        private readonly Font _fMore = new Font("Segoe UI", 8.5f);

        private List<OverlayMember> _members = new();

        public GameOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            // Размер/позицию задаёт UpdateLayeredWindow — стартуем «в никуда»,
            // чтобы пустое окно не мелькнуло в левом верхнем углу.
            Bounds = new Rectangle(-32000, -32000, 1, 1);
        }

        /// <summary>Окно не должно забирать фокус у игры при показе.</summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        /// <summary>Новый состав участников. Пустой список — окно прячется.</summary>
        public void SetMembers(List<OverlayMember> members)
        {
            _members = members ?? new List<OverlayMember>();
            Render();
        }

        /// <summary>Пересобирает картинку окна и толкает её в UpdateLayeredWindow.</summary>
        public void Render()
        {
            var members = _members;
            if (members.Count == 0 || !DeviceSettings.OverlayEnabled)
            {
                if (Visible) Visible = false;
                return;
            }

            int max = Math.Max(1, DeviceSettings.OverlayMaxParticipants);
            int shown = Math.Min(members.Count, max);
            int hidden = members.Count - shown;

            // Ширина — по самому длинному видимому имени, в разумных пределах:
            // узкая панель резала бы имена, широкая зря перекрывала бы экран.
            int nameW = 0;
            for (int i = 0; i < shown; i++)
            {
                int w = TextRenderer.MeasureText(members[i].Name ?? "", _fName).Width;
                if (w > nameW) nameW = w;
            }
            int badgeW = TextRenderer.MeasureText("В ЭФИРЕ", _fBadge).Width + 12;
            int width = Pad + AvatarSz + Gap + nameW + Gap + MuteSz + 6 + badgeW + Pad;
            width = Math.Clamp(width, 200, 360);
            int height = Pad + shown * RowH + (hidden > 0 ? 18 : 0) + Pad;

            using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.Transparent);

                using (var path = Rounded(new Rectangle(0, 0, width - 1, height - 1), Radius))
                {
                    using var back = new SolidBrush(CardBack);
                    g.FillPath(back, path);
                    using var edge = new Pen(CardEdge, 1f);
                    g.DrawPath(edge, path);
                }

                for (int i = 0; i < shown; i++)
                    DrawRow(g, members[i], new Rectangle(Pad, Pad + i * RowH, width - Pad * 2, RowH));

                if (hidden > 0)
                {
                    var moreRect = new Rectangle(Pad, Pad + shown * RowH, width - Pad * 2, 18);
                    TextRenderer.DrawText(g, $"и ещё {hidden}…", _fMore, moreRect, MoreFg,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }

            // Правый край рабочей области, по вертикали — по центру: так панель не
            // спорит ни с игровым HUD сверху, ни со счётчиками FPS.
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int x = wa.Right - width - ScreenMargin;
            int y = wa.Top + Math.Max(0, (wa.Height - height) / 2);

            if (!Visible) Visible = true;
            PushLayered(bmp, new Point(x, y));
        }

        private void DrawRow(Graphics g, OverlayMember m, Rectangle r)
        {
            int rightEdge = r.Right;

            // Бейдж «В ЭФИРЕ» — только при включённой камере/демонстрации.
            if (m.Streaming)
            {
                int bw = TextRenderer.MeasureText(g, "В ЭФИРЕ", _fBadge).Width + 12;
                var br = new Rectangle(rightEdge - bw, r.Y + (r.Height - 17) / 2, bw, 17);
                using (var path = Rounded(br, 8))
                using (var b = new SolidBrush(BadgeBack))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, "В ЭФИРЕ", _fBadge, br, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                rightEdge = br.Left - 6;
            }

            // Значок мьюта: наушники важнее микрофона (deafen глушит и то, и другое).
            if (m.Deafened || m.MicMuted)
            {
                var mr = new Rectangle(rightEdge - MuteSz, r.Y + (r.Height - MuteSz) / 2, MuteSz, MuteSz);
                MuteGlyph.Draw(g, mr, m.Deafened, SlashBg);
                rightEdge = mr.Left - 6;
            }

            // Аватар + зелёное кольцо, пока человек говорит.
            var av = new Rectangle(r.X, r.Y + (r.Height - AvatarSz) / 2, AvatarSz, AvatarSz);
            if (!AvatarStore.DrawAvatar(g, m.Uid, av.X, av.Y, av.Width))
            {
                string full = m.Name ?? "";
                int h = 0; foreach (char ch in full) h = (h * 31 + ch) & 0x7fffffff;
                Color[] pal = { Color.FromArgb(88,101,242), Color.FromArgb(235,69,158),
                                Color.FromArgb(59,165,93), Color.FromArgb(250,166,26),
                                Color.FromArgb(0,176,244) };
                using var b = new SolidBrush(pal[h % pal.Length]);
                g.FillEllipse(b, av);
                string letter = full.Length > 0 ? full.Substring(0, 1).ToUpper() : "?";
                using var f = new Font("Segoe UI Black", 8f, FontStyle.Bold);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(letter, f, Brushes.White, (RectangleF)av, sf);
            }
            if (m.Speaking && !m.MicMuted && !m.Deafened)
            {
                using var ring = new Pen(SpeakRing, 2f);
                g.DrawEllipse(ring, Rectangle.Inflate(av, 1, 1));
            }

            var nameRect = Rectangle.FromLTRB(av.Right + Gap, r.Y, Math.Max(av.Right + Gap + 20, rightEdge), r.Bottom);
            TextRenderer.DrawText(g, m.Name ?? "", _fName, nameRect, m.IsSelf ? SelfFg : NameFg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width <= d || r.Height <= d) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Заново переводит окно в topmost — полноэкранные игры при смене
        /// фокуса перекрывают чужие окна, и без этого оверлей «тонет».</summary>
        public void ReassertTopMost()
        {
            if (!IsHandleCreated || !Visible) return;
            try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
            catch { }
        }

        // ── UpdateLayeredWindow: попиксельная прозрачность ───────────────
        private void PushLayered(Bitmap bmp, Point location)
        {
            if (!IsHandleCreated) return;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero, hOld = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));   // 0 = сохранить альфу
                hOld = SelectObject(memDc, hBitmap);

                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT { X = 0, Y = 0 };
                var dst = new POINT { X = location.X, Y = location.Y };
                var blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
            }
            catch { }
            finally
            {
                if (hBitmap != IntPtr.Zero) { SelectObject(memDc, hOld); DeleteObject(hBitmap); }
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _fName.Dispose(); _fBadge.Dispose(); _fMore.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        // ── P/Invoke ────────────────────────────────────────────────────
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const byte AC_SRC_OVER = 0;
        private const byte AC_SRC_ALPHA = 1;
        private const int ULW_ALPHA = 2;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    }

    /// <summary>
    /// Управление игровым оверлеем: одно окно на всё приложение, живёт только
    /// пока идёт звонок.
    ///
    /// Показывается ТОЛЬКО поверх полноэкранного приложения — то есть в игре.
    /// На рабочем столе, поверх лаунчера, браузера или самого PISMO панель не
    /// нужна и только мешает. «Игра» определяется не по списку процессов (он
    /// всегда неполный), а по признаку, которым пользуется и сам Windows:
    /// активное окно закрывает монитор целиком.
    /// </summary>
    internal static class CallOverlay
    {
        private static GameOverlayForm _form;
        private static System.Windows.Forms.Timer _tick;
        private static List<OverlayMember> _last = new();
        private static bool _showNow;

        /// <summary>Новый состав участников звонка (вызывается из CallForm).</summary>
        public static void Push(List<OverlayMember> members)
        {
            _last = members ?? new List<OverlayMember>();
            if (!DeviceSettings.OverlayEnabled || _last.Count == 0) { Stop(); return; }
            EnsureForm();
            Apply();
        }

        /// <summary>Звонок закончился либо оверлей выключен — убираем окно.</summary>
        public static void Stop()
        {
            _last = new List<OverlayMember>();
            try { _tick?.Stop(); _tick?.Dispose(); } catch { }
            _tick = null;
            try { if (_form != null && !_form.IsDisposed) _form.Close(); } catch { }
            _form = null;
        }

        /// <summary>Настройки изменились: выключили — гасим, включили — перерисовываем
        /// с последним известным составом (не дожидаясь следующего тика звонка).</summary>
        public static void ApplySettings()
        {
            if (!DeviceSettings.OverlayEnabled) { Stop(); return; }
            if (_last.Count == 0) return;
            EnsureForm();
            Apply();
        }

        private static void EnsureForm()
        {
            if (_form != null && !_form.IsDisposed) return;
            _form = new GameOverlayForm();
            _form.Show();
            _tick = new System.Windows.Forms.Timer { Interval = 700 };
            _tick.Tick += (s, e) =>
            {
                // Полноэкранные игры при переключении фокуса задвигают чужие окна —
                // периодически возвращаем себя наверх.
                try { _form?.ReassertTopMost(); } catch { }
                bool show = IsFullscreenAppForeground();
                if (show != _showNow) { _showNow = show; Apply(); }
            };
            _tick.Start();
            _showNow = IsFullscreenAppForeground();
        }

        private static void Apply()
        {
            if (_form == null || _form.IsDisposed) return;
            // Не в игре — рисовать нечего (пустой список прячет окно).
            _form.SetMembers(_showNow ? _last : new List<OverlayMember>());
        }

        /// <summary>Активное окно — полноэкранное приложение (игра)? Проверяем ровно
        /// то, что делает Windows, решая, глушить ли уведомления: окно переднего
        /// плана закрывает монитор ЦЕЛИКОМ. Развёрнутое на весь экран обычное окно
        /// сюда не попадает — оно не заходит под панель задач.</summary>
        private static bool IsFullscreenAppForeground()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return false;

                // Своё же окно игрой не считаем.
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)Environment.ProcessId) return false;

                // Рабочий стол и оболочка Windows занимают весь экран, но игрой не
                // являются: без этой отсечки панель висела бы прямо на десктопе.
                var cls = new System.Text.StringBuilder(256);
                GetClassName(h, cls, cls.Capacity);
                switch (cls.ToString())
                {
                    case "Progman":               // рабочий стол
                    case "WorkerW":               // подложка обоев
                    case "Shell_TrayWnd":         // панель задач
                    case "Windows.UI.Core.CoreWindow":   // меню «Пуск», поиск
                    case "XamlExplorerHostIslandWindow": // Alt+Tab, представление задач
                        return false;
                }

                if (!GetWindowRect(h, out RECT r)) return false;
                var scr = Screen.FromHandle(h);
                var b = scr.Bounds;   // именно Bounds, а не WorkingArea: полноэкранное
                                      // окно перекрывает и панель задач
                return r.Left <= b.Left && r.Top <= b.Top
                    && r.Right >= b.Right && r.Bottom >= b.Bottom;
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    }
}
