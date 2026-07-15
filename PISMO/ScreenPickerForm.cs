using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Свой выбор источника демонстрации в стиле системного диалога: две вкладки —
    /// «Окно» (все видимые окна с превью) и «Весь экран» (все мониторы, включая
    /// виртуальные VR-дисплеи). Полностью на C#/Win32 — без Chromium/getDisplayMedia,
    /// поэтому работает и при активном VR (переход на нативный LiveKit).
    /// </summary>
    public class ScreenPickerForm : Form
    {
        /// <summary>Экранные координаты выбранного источника (монитор или окно).
        /// null — отмена.</summary>
        public Rectangle? SelectedBounds { get; private set; }

        /// <summary>HWND выбранного окна (0 для монитора) — для точного захвата окна.</summary>
        public IntPtr SelectedWindow { get; private set; }

        /// <summary>Выбран монитор (а не окно).</summary>
        public bool SelectedIsScreen { get; private set; }

        private readonly FlowLayoutPanel _grid;
        private readonly Button _btnWindows, _btnScreens, _btnShare, _btnCancel;
        private Panel _selectedTile;
        private bool _showScreens;

        public ScreenPickerForm()
        {
            Text = "Выберите, чем поделиться";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(24, 25, 28);
            ForeColor = Color.FromArgb(220, 221, 222);
            ClientSize = new Size(900, 640);
            MinimumSize = new Size(560, 420);

            var title = new Label
            {
                Text = "Выберите, чем поделиться",
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(16, 12, 0, 0),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(235, 236, 238)
            };

            // Вкладки «Окно» / «Весь экран».
            var tabs = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.FromArgb(24, 25, 28) };
            _btnWindows = MakeTab("Окно", 16);
            _btnScreens = MakeTab("Весь экран", 140);
            _btnWindows.Click += (s, e) => SetTab(false);
            _btnScreens.Click += (s, e) => SetTab(true);
            tabs.Controls.Add(_btnWindows);
            tabs.Controls.Add(_btnScreens);
            var tabLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.FromArgb(45, 47, 51) };
            tabs.Controls.Add(tabLine);

            _grid = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(12),
                BackColor = Color.FromArgb(24, 25, 28)
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(30, 31, 34) };
            _btnShare = new Button
            {
                Text = "Поделиться",
                Size = new Size(130, 34),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f),
                Enabled = false
            };
            _btnShare.FlatAppearance.BorderSize = 0;
            _btnShare.Location = new Point(ClientSize.Width - 270, 11);
            _btnShare.Click += (s, e) => { if (SelectedBounds != null) { DialogResult = DialogResult.OK; Close(); } };

            _btnCancel = new Button
            {
                Text = "Отмена",
                Size = new Size(120, 34),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 47, 51),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f)
            };
            _btnCancel.FlatAppearance.BorderSize = 0;
            _btnCancel.Location = new Point(ClientSize.Width - 132, 11);
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            bottom.Controls.Add(_btnShare);
            bottom.Controls.Add(_btnCancel);

            Controls.Add(_grid);
            Controls.Add(tabs);
            Controls.Add(title);
            Controls.Add(bottom);

            SetTab(false);   // старт с вкладки «Окно», как в системном
        }

        private Button MakeTab(string text, int x)
        {
            return new Button
            {
                Text = text,
                Location = new Point(x, 6),
                Size = new Size(120, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(24, 25, 28),
                ForeColor = Color.FromArgb(200, 202, 208),
                Font = new Font("Segoe UI", 9.5f),
                TabStop = false
            };
        }

        private void SetTab(bool screens)
        {
            _showScreens = screens;
            _btnWindows.ForeColor = screens ? Color.FromArgb(150, 152, 158) : Color.White;
            _btnScreens.ForeColor = screens ? Color.White : Color.FromArgb(150, 152, 158);
            _btnWindows.FlatAppearance.BorderColor = screens ? Color.FromArgb(24, 25, 28) : Color.FromArgb(88, 101, 242);
            _btnScreens.FlatAppearance.BorderColor = screens ? Color.FromArgb(88, 101, 242) : Color.FromArgb(24, 25, 28);
            _btnWindows.FlatAppearance.BorderSize = screens ? 0 : 2;
            _btnScreens.FlatAppearance.BorderSize = screens ? 2 : 0;

            _selectedTile = null;
            SelectedBounds = null;
            SelectedWindow = IntPtr.Zero;
            _btnShare.Enabled = false;

            _grid.SuspendLayout();
            foreach (Control c in _grid.Controls) c.Dispose();
            _grid.Controls.Clear();
            if (screens) BuildScreenTiles(); else BuildWindowTiles();
            _grid.ResumeLayout();
        }

        private void BuildScreenTiles()
        {
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                string tag = screens[i].Primary ? "  (основной)" : "";
                Image thumb = null;
                try { thumb = GrabScreen(b, 300, 170); } catch { }
                AddTile($"Экран {i + 1} — {b.Width}×{b.Height}{tag}", thumb, b, IntPtr.Zero, true);
            }
        }

        private void BuildWindowTiles()
        {
            foreach (var w in EnumerateWindows())
            {
                Image thumb = null;
                try { thumb = GrabWindow(w.Handle, w.Bounds, 300, 170); } catch { }
                AddTile(w.Title, thumb, w.Bounds, w.Handle, false);
            }
        }

        private void AddTile(string caption, Image thumb, Rectangle bounds, IntPtr hwnd, bool isScreen)
        {
            var tile = new Panel
            {
                Size = new Size(300, 216),
                Margin = new Padding(8),
                BackColor = Color.FromArgb(32, 34, 37),
                Cursor = Cursors.Hand
            };
            var pb = new PictureBox
            {
                Dock = DockStyle.Top,
                Height = 168,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(15, 16, 18),
                Image = thumb,
                Cursor = Cursors.Hand
            };
            var cap = new Label
            {
                Text = caption,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(210, 212, 216),
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
                AutoEllipsis = true
            };

            void Select(object s, EventArgs e)
            {
                if (_selectedTile != null && !_selectedTile.IsDisposed)
                    _selectedTile.BackColor = Color.FromArgb(32, 34, 37);
                _selectedTile = tile;
                tile.BackColor = Color.FromArgb(47, 49, 70);
                SelectedBounds = bounds;
                SelectedWindow = hwnd;
                SelectedIsScreen = isScreen;
                _btnShare.Enabled = true;
            }
            tile.Click += Select; pb.Click += Select; cap.Click += Select;

            tile.Controls.Add(cap);
            tile.Controls.Add(pb);
            _grid.Controls.Add(tile);
        }

        // ── Снимок монитора (GDI, совместимо с виртуальными дисплеями) ──
        private static Image GrabScreen(Rectangle b, int w, int h)
        {
            using var full = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(full))
                g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
            return Scale(full, w, h);
        }

        // ── Снимок окна через PrintWindow (берёт окно даже частично перекрытое) ──
        private static Image GrabWindow(IntPtr hwnd, Rectangle b, int w, int h)
        {
            if (b.Width <= 0 || b.Height <= 0) return null;
            using var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            return Scale(bmp, w, h);
        }

        private static Image Scale(Image src, int w, int h)
        {
            double r = Math.Min((double)w / src.Width, (double)h / src.Height);
            int tw = Math.Max(1, (int)(src.Width * r)), th = Math.Max(1, (int)(src.Height * r));
            var thumb = new Bitmap(tw, th);
            using var g = Graphics.FromImage(thumb);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, tw, th);
            return thumb;
        }

        // ── Перечисление видимых окон верхнего уровня ──
        private struct WinInfo { public IntPtr Handle; public string Title; public Rectangle Bounds; }

        private List<WinInfo> EnumerateWindows()
        {
            var list = new List<WinInfo>();
            IntPtr self = Handle;
            EnumWindows((hwnd, _) =>
            {
                if (hwnd == self || !IsWindowVisible(hwnd)) return true;
                // Пропускаем свёрнутые, tool-window, cloaked (UWP-невидимые).
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                try { if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true; } catch { }
                int len = GetWindowTextLength(hwnd);
                if (len == 0) return true;
                var sb = new System.Text.StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                string t = sb.ToString().Trim();
                if (t.Length == 0) return true;
                if (!GetWindowRect(hwnd, out RECT r)) return true;
                var b = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                if (b.Width < 40 || b.Height < 40) return true;   // мусорные/скрытые
                list.Add(new WinInfo { Handle = hwnd, Title = t, Bounds = b });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        #region Win32
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_CLOAKED = 14;
        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int val, int size);
        #endregion
    }
}
