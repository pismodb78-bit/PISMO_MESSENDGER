using System;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// «Настройка оверлея» — ручная правка панели, которая показывается поверх
    /// игры: положение, прозрачность подложки и строк, цвета, масштаб, число
    /// участников.
    ///
    /// Всё применяется сразу: сверху живое превью ровно тем же кодом, которым
    /// рисуется настоящая панель (OverlayRenderer), а сама панель на время
    /// открытия окна переводится в режим перетаскивания и видна на экране, даже
    /// если игра не запущена — иначе её было бы не подвинуть.
    /// </summary>
    internal sealed class OverlayEditorForm : DarkToolWindow
    {
        private readonly PictureBox _preview;
        private readonly Label _lblPos;
        private static OverlayEditorForm _open;

        /// <summary>Открывает окно (или поднимает уже открытое).</summary>
        public static void Open(IWin32Window owner)
        {
            if (_open != null && !_open.IsDisposed)
            {
                try { _open.Activate(); _open.BringToFront(); } catch { }
                return;
            }
            OverlayEditorForm w = null;
            try
            {
                w = new OverlayEditorForm();
                _open = w;
                w.FormClosed += (s, e) => { if (ReferenceEquals(_open, w)) _open = null; };
                // Показываем БЕЗ владельца и поверх остальных окон: настройки
                // устройств открыты модально (ShowDialog), и окно-потомок модального
                // диалога то пряталось за него, то не появлялось вовсе.
                w.Show();
                w.BringToFront();
                w.Activate();
            }
            catch (Exception ex)
            {
                // Молча проглоченная ошибка выглядела как «кнопка ничего не делает»,
                // а вдобавок оставляла взведённым режим настройки — после этого
                // оверлей нельзя было ни выключить, ни убрать по выходе из звонка.
                _open = null;
                try { CallOverlay.SetEditMode(false); } catch { }
                try { w?.Dispose(); } catch { }
                MessageBox.Show("Не удалось открыть настройку оверлея:\n" + ex.Message,
                    "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private OverlayEditorForm() : base("Настройка оверлея", new Size(400, 620))
        {
            // Поверх остальных окон: иначе за модальным окном настроек устройств
            // редактор терялся и выглядел как «не открылся».
            TopMost = true;
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(Math.Max(wa.Left, wa.Right - 440), Math.Max(wa.Top, wa.Top + 60));

            int y = 0;
            // Отступы уже даёт внешняя панель окна — здесь работаем от чистого края.
            int W() => Math.Max(120, Content.ClientSize.Width);

            // ── Превью ──────────────────────────────────────────────────
            Content.Controls.Add(Stretch(Section("Как это выглядит", ref y, W())));
            _preview = new PictureBox
            {
                Location = new Point(0, y),
                Size = new Size(W(), 116),
                BackColor = Color.FromArgb(22, 23, 26),
                SizeMode = PictureBoxSizeMode.CenterImage
            };
            Content.Controls.Add(Stretch(_preview));
            y += 124;

            _lblPos = new Label
            {
                Location = new Point(0, y),
                Size = new Size(W(), 18),
                ForeColor = Color.FromArgb(140, 142, 148),
                Font = new Font("Segoe UI", 8f)
            };
            Content.Controls.Add(Stretch(_lblPos));
            y += 22;

            var btnRow = new Panel
            {
                Location = new Point(0, y),
                Size = new Size(W(), 30),
                BackColor = Color.Transparent
            };
            var btnReset = MkButton("Сбросить положение", 0, 170);
            btnReset.Click += (s, e) =>
            {
                DeviceSettings.OverlayX = -1; DeviceSettings.OverlayY = -1;
                Save(); RefreshAll();
            };
            btnRow.Controls.Add(btnReset);
            Content.Controls.Add(Stretch(btnRow));
            y += 38;

            Content.Controls.Add(Stretch(Hint(
                "Панель уже на экране — просто перетащите её мышью. Пока это окно открыто, " +
                "она видна всегда; после закрытия вернётся к показу только поверх игры.",
                ref y, W())));

            // ── Прозрачность ────────────────────────────────────────────
            Content.Controls.Add(Stretch(Section("Прозрачность", ref y, W())));

            AddSlider("Подложка", 0, 100, DeviceSettings.OverlayBackOpacity, ref y, W(),
                v => { DeviceSettings.OverlayBackOpacity = v; Save(); RefreshAll(); },
                "0 — подложки нет совсем, только имена");

            AddSlider("Когда молчит", 0, 100, DeviceSettings.OverlayAlphaSilent, ref y, W(),
                v => { DeviceSettings.OverlayAlphaSilent = v; Save(); RefreshAll(); });

            AddSlider("Когда говорит", 0, 100, DeviceSettings.OverlayAlphaSpeaking, ref y, W(),
                v => { DeviceSettings.OverlayAlphaSpeaking = v; Save(); RefreshAll(); });

            // ── Размер и состав ─────────────────────────────────────────
            Content.Controls.Add(Stretch(Section("Размер и состав", ref y, W())));

            AddSlider("Масштаб", 75, 150, DeviceSettings.OverlayScale, ref y, W(),
                v => { DeviceSettings.OverlayScale = v; Save(); RefreshAll(); }, null, "%");

            AddSlider("Показывать участников", 1, 20, DeviceSettings.OverlayMaxParticipants, ref y, W(),
                v => { DeviceSettings.OverlayMaxParticipants = v; Save(); RefreshAll(); },
                "минимум 1 — это вы сами", "");

            // ── Цвета ───────────────────────────────────────────────────
            Content.Controls.Add(Stretch(Section("Цвета", ref y, W())));
            AddColor("Подложка", () => DeviceSettings.OverlayBackColor,
                     v => { DeviceSettings.OverlayBackColor = v; Save(); RefreshAll(); }, ref y, W());
            AddColor("Бейдж «В ЭФИРЕ»", () => DeviceSettings.OverlayAccentColor,
                     v => { DeviceSettings.OverlayAccentColor = v; Save(); RefreshAll(); }, ref y, W());

            var btnDefaults = MkButton("Вернуть стандартные настройки", 0, W());
            btnDefaults.Location = new Point(0, y);
            btnDefaults.Click += (s, e) =>
            {
                DeviceSettings.OverlayBackOpacity = 45;
                DeviceSettings.OverlayAlphaSilent = 20;
                DeviceSettings.OverlayAlphaSpeaking = 75;
                DeviceSettings.OverlayScale = 100;
                DeviceSettings.OverlayBackColor = "#1E1F22";
                DeviceSettings.OverlayAccentColor = "#ED4245";
                DeviceSettings.OverlayX = -1; DeviceSettings.OverlayY = -1;
                Save();
                Close();          // проще пересобрать окно, чем синхронизировать все ползунки
                Open(Owner);
            };
            Content.Controls.Add(Stretch(btnDefaults));
            y += 40;

            // Пока окно открыто, режим настройки подтверждаем раз в секунду. Вызов
            // идемпотентный, зато плашка гарантированно окажется на экране, даже
            // если первая попытка пришлась на неудачный момент (модальный диалог,
            // смена фокуса, конец звонка).
            _keepAlive = new System.Windows.Forms.Timer { Interval = 1000 };
            _keepAlive.Tick += (s, e) =>
            {
                if (IsDisposed) return;
                try { CallOverlay.SetEditMode(true, this); } catch { }
            };

            FormClosed += (s, e) =>
            {
                try { _keepAlive.Stop(); _keepAlive.Dispose(); } catch { }
                CallOverlay.PositionChanged -= OnOverlayMoved;
                CallOverlay.SetEditMode(false);
            };
        }

        private readonly System.Windows.Forms.Timer _keepAlive;

        /// <summary>Режим перетаскивания включаем только когда окно РЕАЛЬНО показано,
        /// и привязываем его к этому окну. Если бы он взводился в конструкторе, а окно
        /// затем не открылось, режим остался бы включённым навсегда — и панель нельзя
        /// было бы ни выключить, ни убрать после звонка.</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Reflow();   // подогнать ширину под фактическую полосу прокрутки
            CallOverlay.SetEditMode(true, this);
            CallOverlay.PositionChanged += OnOverlayMoved;
            _keepAlive.Start();
            RefreshAll();
        }

        private void OnOverlayMoved()
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) { BeginInvoke(new Action(UpdatePosLabel)); return; }
                UpdatePosLabel();
            }
            catch { }
        }

        private void UpdatePosLabel()
        {
            _lblPos.Text = DeviceSettings.OverlayX < 0
                ? "Положение: по умолчанию (правый край, по центру)"
                : $"Положение: X={DeviceSettings.OverlayX}, Y={DeviceSettings.OverlayY}";
        }

        private static void Save() { try { DeviceSettings.Save(); } catch { } }

        /// <summary>Перерисовать превью и живую панель.</summary>
        private void RefreshAll()
        {
            UpdatePosLabel();
            try
            {
                var old = _preview.Image;
                _preview.Image = OverlayRenderer.BuildCard(OverlayRenderer.SampleMembers());
                old?.Dispose();
            }
            catch { }
            try { CallOverlay.Refresh(); } catch { }
        }

        // ── Строители контролов ─────────────────────────────────────────
        private static Label Section(string text, ref int y, int w)
        {
            var l = new Label
            {
                Text = text.ToUpperInvariant(),
                Location = new Point(0, y + 6),
                Size = new Size(w, 18),
                ForeColor = Color.FromArgb(150, 152, 158),
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold)
            };
            y += 28;
            return l;
        }

        private static Label Hint(string text, ref int y, int w)
        {
            // Высоту меряем по тексту: при «прикидке» по числу строк длинная
            // подсказка обрезалась на полуслове.
            var f = new Font("Segoe UI", 8f);
            int h = TextRenderer.MeasureText(text, f, new Size(w, int.MaxValue),
                                             TextFormatFlags.WordBreak).Height + 2;
            var l = new Label
            {
                Text = text,
                Location = new Point(0, y),
                Size = new Size(w, h),
                ForeColor = Color.FromArgb(140, 142, 148),
                Font = f
            };
            y += h + 6;
            return l;
        }

        private static Button MkButton(string text, int x, int w) => Style(new Button
        {
            Text = text,
            Location = new Point(x, 0),
            Size = new Size(w, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(64, 68, 75),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand,
            TabStop = false
        });

        private static Button Style(Button b)
        {
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(79, 84, 92);
            return b;
        }

        /// <summary>Подпись + значение справа + плоский ползунок под ними.</summary>
        private void AddSlider(string caption, int min, int max, int val, ref int y, int w,
                               Action<int> onChange, string hint = null, string suffix = "%")
        {
            var cap = new Label
            {
                Text = caption,
                Location = new Point(0, y),
                Size = new Size(w - 60, 18),
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI", 9f)
            };
            var num = new Label
            {
                Text = val + suffix,
                Location = new Point(w - 60, y),
                Size = new Size(60, 18),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(150, 152, 158),
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold)
            };
            Content.Controls.Add(cap);
            Content.Controls.Add(PinRight(num));
            y += 20;

            var sl = new FlatSlider
            {
                Minimum = min,
                Maximum = max,
                Value = val,
                Location = new Point(0, y),
                Size = new Size(w, 20),
                BackColor = Color.FromArgb(32, 34, 37)
            };
            sl.ValueChanged += (s, e) => { num.Text = sl.Value + suffix; onChange(sl.Value); };
            Content.Controls.Add(Stretch(sl));
            y += 26;

            if (!string.IsNullOrEmpty(hint))
            {
                var hl = new Label
                {
                    Text = hint,
                    Location = new Point(0, y),
                    Size = new Size(w, 16),
                    ForeColor = Color.FromArgb(130, 132, 138),
                    Font = new Font("Segoe UI", 7.5f)
                };
                Content.Controls.Add(Stretch(hl));
                y += 18;
            }
            y += 6;
        }

        /// <summary>Подпись + образец цвета, клик открывает системный выбор цвета.</summary>
        private void AddColor(string caption, Func<string> get, Action<string> set, ref int y, int w)
        {
            var cap = new Label
            {
                Text = caption,
                Location = new Point(0, y + 4),
                Size = new Size(w - 110, 22),
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI", 9f)
            };
            var sw = new Panel
            {
                Location = new Point(w - 104, y + 2),
                Size = new Size(104, 26),
                BackColor = DeviceSettings.ParseColor(get(), Color.Gray),
                Cursor = Cursors.Hand,
                BorderStyle = BorderStyle.FixedSingle
            };
            sw.Click += (s, e) =>
            {
                using var dlg = new ColorDialog
                {
                    Color = sw.BackColor,
                    FullOpen = true,
                    AnyColor = true
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                sw.BackColor = dlg.Color;
                set(ColorTranslator.ToHtml(dlg.Color));
            };
            Content.Controls.Add(cap);
            Content.Controls.Add(PinRight(sw));
            y += 34;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _preview?.Image?.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
