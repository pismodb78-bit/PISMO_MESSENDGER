using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    /// Рисование панели оверлея. Вынесено из формы, потому что этой же картинкой
    /// живёт превью в окне «Настройка оверлея» — иначе превью и реальная панель
    /// неизбежно разъехались бы.
    /// </summary>
    internal static class OverlayRenderer
    {
        private const int BasePad = 7;
        private const int BaseRowH = 28;
        private const int BaseAvatar = 22;
        private const int BaseMute = 18;
        private const int BaseGap = 7;
        private const int BaseRadius = 10;

        // Непрозрачный «двойник» фона — им MuteGlyph рисует вырез под
        // перечёркивающей линией (полупрозрачный цвет дал бы грязный след).
        private static readonly Color SlashBg = Color.FromArgb(255, 34, 35, 39);
        private static readonly Color NameFg = Color.FromArgb(236, 238, 242);
        private static readonly Color SelfFg = Color.FromArgb(140, 210, 160);
        private static readonly Color MoreFg = Color.FromArgb(150, 152, 158);

        private static int _fontScale = -1;
        private static Font _fName, _fBadge, _fMore, _fLetter;

        private static void EnsureFonts(int scale)
        {
            if (_fontScale == scale && _fName != null) return;
            _fName?.Dispose(); _fBadge?.Dispose(); _fMore?.Dispose(); _fLetter?.Dispose();
            float k = scale / 100f;
            _fName = new Font("Segoe UI Semibold", 9f * k, FontStyle.Bold);
            _fBadge = new Font("Segoe UI Semibold", 7f * k, FontStyle.Bold);
            _fMore = new Font("Segoe UI", 8f * k);
            _fLetter = new Font("Segoe UI Black", 7.5f * k, FontStyle.Bold);
            _fontScale = scale;
        }

        private static int S(int v, int scale) => Math.Max(1, (int)Math.Round(v * scale / 100.0));

        private static Color WithAlpha(Color c, float k)
            => Color.FromArgb((int)Math.Round(Math.Clamp(k, 0f, 1f) * 255), c.R, c.G, c.B);

        /// <summary>Собирает картинку панели. null — рисовать нечего.</summary>
        public static Bitmap BuildCard(IReadOnlyList<OverlayMember> members)
        {
            if (members == null || members.Count == 0) return null;

            int scale = DeviceSettings.OverlayScale;
            EnsureFonts(scale);

            int pad = S(BasePad, scale), rowH = S(BaseRowH, scale);
            int avatar = S(BaseAvatar, scale), mute = S(BaseMute, scale);
            int gap = S(BaseGap, scale), radius = S(BaseRadius, scale);

            int max = Math.Max(1, DeviceSettings.OverlayMaxParticipants);
            int shown = Math.Min(members.Count, max);
            int hidden = members.Count - shown;

            int nameW = 0, badgeW;
            using (var mb = new Bitmap(1, 1))
            using (var mg = Graphics.FromImage(mb))
            {
                mg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                for (int i = 0; i < shown; i++)
                {
                    int w = (int)Math.Ceiling(mg.MeasureString(members[i].Name ?? "", _fName).Width);
                    if (w > nameW) nameW = w;
                }
                badgeW = (int)Math.Ceiling(mg.MeasureString("В ЭФИРЕ", _fBadge).Width) + S(12, scale);
            }

            int width = pad + avatar + gap + nameW + gap + mute + 6 + badgeW + pad;
            width = Math.Clamp(width, S(180, scale), S(340, scale));
            int moreH = hidden > 0 ? S(16, scale) : 0;
            int height = pad + shown * rowH + moreH + pad;

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // ClearType на слоёном окне некорректен (нужен непрозрачный фон);
                // GDI+ со сглаживанием пишет альфу правильно.
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                // Подложка: при нулевой непрозрачности не рисуем вовсе — остаются
                // только имена поверх игры.
                float backK = DeviceSettings.OverlayBackOpacity / 100f;
                if (backK > 0.01f)
                {
                    var bc = DeviceSettings.ParseColor(DeviceSettings.OverlayBackColor, Color.FromArgb(30, 31, 34));
                    using var path = Rounded(new Rectangle(0, 0, width - 1, height - 1), radius);
                    using var back = new SolidBrush(WithAlpha(bc, backK));
                    g.FillPath(back, path);
                }

                float aSilent = DeviceSettings.OverlayAlphaSilent / 100f;
                float aSpeak = DeviceSettings.OverlayAlphaSpeaking / 100f;

                for (int i = 0; i < shown; i++)
                {
                    var m = members[i];
                    bool speaking = m.Speaking && !m.MicMuted && !m.Deafened;
                    DrawRow(g, m, new Rectangle(pad, pad + i * rowH, width - pad * 2, rowH),
                            speaking ? aSpeak : aSilent, scale, avatar, mute, gap);
                }

                if (hidden > 0)
                {
                    var moreRect = new RectangleF(pad, pad + shown * rowH, width - pad * 2, moreH);
                    using var moreBr = new SolidBrush(WithAlpha(MoreFg, aSilent));
                    using var sf = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center };
                    g.DrawString($"и ещё {hidden}…", _fMore, moreBr, moreRect, sf);
                }
            }
            return bmp;
        }

        /// <summary>Строка целиком собирается в отдельном слое и накладывается с нужной
        /// альфой: поэлементно прозрачность не задать — аватар это картинка.</summary>
        private static void DrawRow(Graphics g, OverlayMember m, Rectangle r, float alpha,
                                    int scale, int avatarSz, int muteSz, int gap)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using var layer = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var lg = Graphics.FromImage(layer))
            {
                lg.SmoothingMode = SmoothingMode.AntiAlias;
                lg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                DrawRowContent(lg, m, new Rectangle(0, 0, r.Width, r.Height), scale, avatarSz, muteSz, gap);
            }
            var cm = new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0f, 1f) };
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(cm);
            g.DrawImage(layer, r, 0, 0, r.Width, r.Height, GraphicsUnit.Pixel, ia);
        }

        /// <summary>Только GDI+: TextRenderer (GDI) не пишет альфа-канал, и на
        /// прозрачном слое текст пропал бы.</summary>
        private static void DrawRowContent(Graphics g, OverlayMember m, Rectangle r,
                                           int scale, int avatarSz, int muteSz, int gap)
        {
            int rightEdge = r.Right;

            if (m.Streaming)
            {
                int bh = S(16, scale);
                int bw = (int)Math.Ceiling(g.MeasureString("В ЭФИРЕ", _fBadge).Width) + S(12, scale);
                var br = new Rectangle(rightEdge - bw, r.Y + (r.Height - bh) / 2, bw, bh);
                var accent = DeviceSettings.ParseColor(DeviceSettings.OverlayAccentColor, Color.FromArgb(237, 66, 69));
                using (var path = Rounded(br, bh / 2))
                using (var b = new SolidBrush(accent))
                    g.FillPath(b, path);
                using (var sf = new StringFormat(StringFormatFlags.NoWrap)
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("В ЭФИРЕ", _fBadge, Brushes.White, (RectangleF)br, sf);
                rightEdge = br.Left - 6;
            }

            // Наушники важнее микрофона: deafen глушит и то, и другое.
            if (m.Deafened || m.MicMuted)
            {
                var mr = new Rectangle(rightEdge - muteSz, r.Y + (r.Height - muteSz) / 2, muteSz, muteSz);
                MuteGlyph.Draw(g, mr, m.Deafened, SlashBg);
                rightEdge = mr.Left - 6;
            }

            var av = new Rectangle(r.X, r.Y + (r.Height - avatarSz) / 2, avatarSz, avatarSz);
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
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(letter, _fLetter, Brushes.White, (RectangleF)av, sf);
            }

            var nameRect = RectangleF.FromLTRB(av.Right + gap, r.Y,
                                               Math.Max(av.Right + gap + 20, rightEdge), r.Bottom);
            using (var nameBr = new SolidBrush(m.IsSelf ? SelfFg : NameFg))
            using (var sfName = new StringFormat(StringFormatFlags.NoWrap)
            { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center })
                g.DrawString(m.Name ?? "", _fName, nameBr, nameRect, sfName);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            if (r.Width <= d || r.Height <= d) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Демо-состав для превью и режима перетаскивания, когда звонка нет.</summary>
        public static List<OverlayMember> SampleMembers() => new()
        {
            new OverlayMember { Uid = 0, Name = "Вы",   IsSelf = true, Speaking = true },
            new OverlayMember { Uid = 0, Name = "Никита", Streaming = true },
            new OverlayMember { Uid = 0, Name = "Артём",  MicMuted = true },
        };
    }

    /// <summary>
    /// Игровой оверлей: полупрозрачная панель со списком участников голосового
    /// канала — аватар, имя, значок выключенного микрофона и бейдж «В ЭФИРЕ».
    ///
    /// Окно слоёное (WS_EX_LAYERED) и сквозное для мыши (WS_EX_TRANSPARENT) —
    /// клики уходят в игру. WS_EX_NOACTIVATE не даёт ему забрать фокус,
    /// WS_EX_TOOLWINDOW убирает его из Alt+Tab и панели задач.
    ///
    /// В режиме настройки (<see cref="SetEditMode"/>) сквозной проход
    /// отключается, и панель можно перетащить мышью.
    ///
    /// ОГРАНИЧЕНИЕ: поверх игры в ЭКСКЛЮЗИВНОМ полноэкранном режиме такое окно
    /// не рисуется — так устроен Windows. Оконный и «полноэкранный оконный»
    /// (borderless) режимы работают.
    /// </summary>
    internal sealed class GameOverlayForm : Form
    {
        private const int ScreenMargin = 14;

        private List<OverlayMember> _members = new();
        private bool _editMode;

        public GameOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            // Размер и позицию задаёт UpdateLayeredWindow — стартуем «в никуда»,
            // чтобы пустое окно не мелькнуло в левом верхнем углу.
            Bounds = new Rectangle(-32000, -32000, 1, 1);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                if (!_editMode) cp.ExStyle |= WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void SetMembers(List<OverlayMember> members)
        {
            _members = members ?? new List<OverlayMember>();
            Render();
        }

        /// <summary>Режим настройки: панель ловит мышь и её можно перетащить.</summary>
        public void SetEditMode(bool on)
        {
            if (_editMode == on) return;
            _editMode = on;
            try
            {
                if (IsHandleCreated)
                {
                    int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                    ex = on ? (ex & ~WS_EX_TRANSPARENT) : (ex | WS_EX_TRANSPARENT);
                    SetWindowLong(Handle, GWL_EXSTYLE, ex);
                    // Без SWP_FRAMECHANGED смена расширенного стиля может не
                    // подхватиться, и панель осталась бы сквозной для мыши.
                    SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                                 SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
            }
            catch { }
            Cursor = on ? Cursors.SizeAll : Cursors.Default;
            Render();
        }

        public bool EditMode => _editMode;

        /// <summary>Пересобирает картинку окна и толкает её в UpdateLayeredWindow.</summary>
        public void Render()
        {
            var members = _members;
            if (!DeviceSettings.OverlayEnabled || members.Count == 0)
            {
                if (Visible) Visible = false;
                return;
            }

            using var bmp = OverlayRenderer.BuildCard(members);
            if (bmp == null) { if (Visible) Visible = false; return; }

            Point pos;
            // Пока панель тащат, позиция берётся у САМОГО ОКНА (иначе Render
            // возвращал бы её в сохранённую точку на каждом кадре) — но через
            // GetWindowRect, а НЕ через Form.Location: окно двигает
            // UpdateLayeredWindow, и кэш координат WinForms остаётся стартовым
            // (-32000). Из-за этого панель показывалась на мгновение и тут же
            // улетала за экран.
            if (_editMode && Visible && TryGetWindowPos(out var live) && OnAnyScreen(live, bmp.Size))
            {
                pos = live;
            }
            else
            {
                var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
                int x = DeviceSettings.OverlayX >= 0
                    ? DeviceSettings.OverlayX : wa.Right - bmp.Width - ScreenMargin;
                int y = DeviceSettings.OverlayY >= 0
                    ? DeviceSettings.OverlayY : wa.Top + Math.Max(0, (wa.Height - bmp.Height) / 2);
                // Не даём панели уехать за пределы экрана (сменилось разрешение и т.п.).
                x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - bmp.Width));
                y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - bmp.Height));
                pos = new Point(x, y);
            }

            if (!Visible) Visible = true;
            PushLayered(bmp, pos);
        }

        /// <summary>Заново переводит окно в topmost — полноэкранные игры при смене
        /// фокуса задвигают чужие окна.</summary>
        public void ReassertTopMost()
        {
            if (!IsHandleCreated || !Visible) return;
            try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            // В режиме настройки всё окно ведёт себя как заголовок — тащим мышью.
            if (_editMode && m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTCAPTION; return; }
            if (_editMode && m.Msg == WM_EXITSIZEMOVE)
            {
                // Координаты спрашиваем у окна, а не у Form.Location (см. Render).
                if (TryGetWindowPos(out var p))
                {
                    DeviceSettings.OverlayX = p.X;
                    DeviceSettings.OverlayY = p.Y;
                    try { DeviceSettings.Save(); } catch { }
                    PositionChanged?.Invoke();
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>Панель перетащили — редактор обновляет подпись с координатами.</summary>
        public event Action PositionChanged;

        /// <summary>Настоящая позиция окна. Form.Location для слоёного окна не годится:
        /// его двигает UpdateLayeredWindow, а кэш координат WinForms при этом может
        /// остаться стартовым.</summary>
        private bool TryGetWindowPos(out Point p)
        {
            p = Point.Empty;
            try
            {
                if (!IsHandleCreated || !GetWindowRect(Handle, out var r)) return false;
                p = new Point(r.Left, r.Top);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Проверка, что панель такого размера в этой точке реально видна хоть
        /// на одном мониторе: страховка от «улетела за экран».</summary>
        private static bool OnAnyScreen(Point p, Size size)
        {
            try
            {
                var rect = new Rectangle(p, size);
                foreach (var sc in Screen.AllScreens)
                    if (sc.Bounds.IntersectsWith(rect)) return true;
            }
            catch { }
            return false;
        }

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

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int GWL_EXSTYLE = -20;
        private const int WM_NCHITTEST = 0x0084, WM_EXITSIZEMOVE = 0x0232, HTCAPTION = 2;
        private const byte AC_SRC_OVER = 0;
        private const byte AC_SRC_ALPHA = 1;
        private const int ULW_ALPHA = 2;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                           SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    }

    /// <summary>
    /// Управление игровым оверлеем: одно окно на всё приложение.
    ///
    /// Показывается ТОЛЬКО поверх полноэкранного приложения — то есть в игре. На
    /// рабочем столе, поверх лаунчера, браузера или самого PISMO панель не нужна.
    /// «Игра» определяется не по списку процессов (он всегда неполный), а по
    /// признаку, которым пользуется и сам Windows: активное окно закрывает
    /// монитор целиком. Исключение — режим настройки: там панель видна всегда,
    /// иначе её было бы не перетащить.
    /// </summary>
    internal static class CallOverlay
    {
        private static GameOverlayForm _form;
        private static System.Windows.Forms.Timer _tick;
        private static List<OverlayMember> _last = new();
        private static bool _showNow;
        private static bool _editMode;
        private static bool _sampleShown;   // на экране демо-состав, а не реальный звонок
        // Окно редактора. Режим настройки живёт ровно столько, сколько живо ЭТО окно:
        // раньше это был «голый» флаг, и если редактор не открылся (или закрылся
        // аварийно), он оставался взведённым — Stop() превращался в пустышку, и
        // панель нельзя было ни выключить, ни убрать после выхода из звонка.
        private static Form _editOwner;

        /// <summary>Панель перетащили в режиме настройки.</summary>
        public static event Action PositionChanged;

        public static void Push(List<OverlayMember> members)
        {
            _last = members ?? new List<OverlayMember>();
            // Выключенный оверлей сносим ВСЕГДА, даже с открытым редактором:
            // настраивать отключённую панель всё равно нечего.
            if (!DeviceSettings.OverlayEnabled) { Stop(true); return; }
            if (_last.Count == 0 && !EditActive()) { Stop(); return; }
            EnsureForm();
            Apply();
        }

        public static void Stop() => Stop(false);

        /// <summary><paramref name="force"/> — снести панель, даже если открыт редактор.</summary>
        public static void Stop(bool force)
        {
            if (!force && EditActive())
            {
                // Редактор открыт (звонок мог закончиться) — панель остаётся, но
                // реальный состав подменяем демонстрационным.
                _last = OverlayRenderer.SampleMembers();
                _sampleShown = true;
                Apply();
                return;
            }
            _editMode = false; _editOwner = null; _sampleShown = false;
            _last = new List<OverlayMember>();
            try { _tick?.Stop(); _tick?.Dispose(); } catch { }
            _tick = null;
            try
            {
                if (_form != null && !_form.IsDisposed)
                {
                    _form.Visible = false;   // гасим до разрушения окна
                    _form.Close();
                    _form.Dispose();
                }
            }
            catch { }
            _form = null;
        }

        /// <summary>Режим настройки считается активным, только пока живо окно
        /// редактора — сам по себе флаг залипнуть не может.</summary>
        private static bool EditActive()
        {
            if (!_editMode) return false;
            if (_editOwner == null || _editOwner.IsDisposed) { _editMode = false; _editOwner = null; return false; }
            return true;
        }

        /// <summary>Перерисовать с текущими настройками (вызывает редактор).</summary>
        public static void Refresh()
        {
            if (!DeviceSettings.OverlayEnabled) { Stop(true); return; }
            if (_last.Count == 0 && !EditActive()) return;
            EnsureForm();
            Apply();
        }

        public static void ApplySettings() => Refresh();

        /// <summary>Режим настройки: панель видна всегда и перетаскивается мышью.
        /// sample — что показывать, если звонка сейчас нет.</summary>
        public static void SetEditMode(bool on, Form owner = null, List<OverlayMember> sample = null)
        {
            _editMode = on;
            _editOwner = on ? owner : null;
            if (on)
            {
                // Звонка может и не быть — тогда показываем демо-состав, иначе
                // настраивать было бы нечего.
                if (_last.Count == 0)
                {
                    _last = sample ?? OverlayRenderer.SampleMembers();
                    _sampleShown = true;
                }
                EnsureForm();
                _form?.SetEditMode(true);
                _showNow = true;
                Apply();
                // Сразу наверх, не дожидаясь тика: редактор тоже поверх всех окон,
                // и без этого плашка могла оказаться под ним.
                try { _form?.ReassertTopMost(); } catch { }
            }
            else
            {
                _form?.SetEditMode(false);
                // Демо-состав живёт только на время настройки: иначе панель осталась
                // бы висеть с выдуманными именами после закрытия окна.
                if (_sampleShown) { _sampleShown = false; _last = new List<OverlayMember>(); }
                if (_last.Count == 0) { Stop(); return; }
                _showNow = IsFullscreenAppForeground();
                Apply();
            }
        }

        private static void EnsureForm()
        {
            if (_form != null && !_form.IsDisposed) return;
            _form = new GameOverlayForm();
            _form.PositionChanged += () => { try { PositionChanged?.Invoke(); } catch { } };
            _form.Show();
            if (_editMode) _form.SetEditMode(true);
            _tick = new System.Windows.Forms.Timer { Interval = 700 };
            _tick.Tick += (s, e) =>
            {
                // Страховка: что бы ни случилось выше по стеку, выключенный оверлей
                // не должен оставаться на экране.
                if (!DeviceSettings.OverlayEnabled) { Stop(true); return; }
                bool edit = EditActive();          // заодно снимает залипший режим
                try { _form?.ReassertTopMost(); } catch { }
                if (edit)
                {
                    // В режиме настройки панель обязана быть на экране ВСЕГДА, а не
                    // только в игре. Показ повторяем каждый тик: окно редактора
                    // открывается из МОДАЛЬНОГО диалога настроек, и первая попытка
                    // показа могла не пройти. Так оно само чинится за один тик.
                    _showNow = true;
                    Apply();
                    return;
                }
                bool show = IsFullscreenAppForeground();
                if (show != _showNow) { _showNow = show; Apply(); }
            };
            _tick.Start();
            _showNow = EditActive() || IsFullscreenAppForeground();
        }

        private static void Apply()
        {
            if (_form == null || _form.IsDisposed) return;
            _form.SetMembers(_showNow ? _last : new List<OverlayMember>());
        }

        /// <summary>Активное окно — полноэкранное приложение (игра)? Проверяем то же,
        /// что Windows, решая, глушить ли уведомления: окно переднего плана закрывает
        /// монитор ЦЕЛИКОМ. Развёрнутое обычное окно сюда не попадает — оно не
        /// заходит под панель задач.</summary>
        private static bool IsFullscreenAppForeground()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return false;

                GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)Environment.ProcessId) return false;

                var cls = new System.Text.StringBuilder(256);
                GetClassName(h, cls, cls.Capacity);
                switch (cls.ToString())
                {
                    case "Progman":                      // рабочий стол
                    case "WorkerW":                      // подложка обоев
                    case "Shell_TrayWnd":                // панель задач
                    case "Windows.UI.Core.CoreWindow":   // «Пуск», поиск
                    case "XamlExplorerHostIslandWindow": // Alt+Tab, представление задач
                        return false;
                }

                if (!GetWindowRect(h, out RECT r)) return false;
                var b = Screen.FromHandle(h).Bounds;   // Bounds, а не WorkingArea:
                                                       // полноэкранное окно кроет и панель задач
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
