// ============================================================
//  PhotoEditorForm — рисование поверх снимка
// ============================================================
//
//  То же, что в редакторе на телефоне и в Telegram: перо, маркер, неон,
//  ластик, надпись прямо на фотографии и наклейка — другой снимок с диска.
//  Гифки в наклейку не берём: в ней они всё равно замрут первым кадром, а
//  весят как ролик.
//
//  Слои живут в ДОЛЯХ от размера снимка — и координаты, и толщина линии, и
//  кегль надписи. Поэтому нарисованное ложится одинаково и в окне, и в
//  готовом файле, какого бы размера ни была фотография. Раньше толщина
//  считалась в точках окна и при сборке домножалась на отношение ширин —
//  тонкая линия в готовом снимке превращалась в жирную полосу.
//
//  Ластик стирает НАРИСОВАННОЕ, а не протирает дыру в фотографии: линии
//  идут отдельным прозрачным слоем, который кладётся поверх снимка.
//
//  Наклейку можно двигать мышью, колесом менять её размер, а с Ctrl —
//  поворачивать.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PISMO
{
    internal sealed class PhotoEditorForm : Form
    {
        private enum Tool { Pen, Marker, Neon, Eraser, Text, Sticker }

        private abstract class Mark { }

        private sealed class Stroke : Mark
        {
            public readonly List<PointF> Points = new();
            public Color Color;
            /// <summary>Доля ширины снимка.</summary>
            public float Width;
            public Tool Kind;
        }

        private sealed class TextMark : Mark
        {
            public string Text;
            public PointF Pos;
            public Color Color;
            /// <summary>Доля ширины снимка.</summary>
            public float Size;
            /// <summary>Поворот в градусах — колесом с Ctrl или правой кнопкой.</summary>
            public float Angle;
        }

        private sealed class StickerMark : Mark
        {
            public Image Image;
            public PointF Pos;
            public float Scale;
            /// <summary>Поворот в градусах — колесом с Ctrl.</summary>
            public float Angle;
        }

        private readonly Image _src;
        // Один список на всё нарисованное, по порядку: только так «отменить»
        // и «вернуть» работают по-человечески — шаг за шагом, что бы это ни
        // было, линия, надпись или наклейка.
        private readonly List<Mark> _marks = new();
        private readonly List<Mark> _undone = new();

        private readonly Panel _canvas = new();
        private Tool _tool = Tool.Pen;
        private Color _color = Color.FromArgb(255, 59, 48);
        private float _width = 0.012f;
        private RectangleF _frame;
        private Stroke _live;
        private readonly List<Button> _toolButtons = new();
        private readonly List<Panel> _swatches = new();
        private readonly List<Panel> _widthDots = new();

        /// <summary>Готовый снимок с нарисованным.</summary>
        public byte[] Result { get; private set; }

        public PhotoEditorForm(byte[] bytes)
        {
            _src = Image.FromStream(new MemoryStream(bytes));

            Text = "Рисование";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(980, 700);
            MinimumSize = new Size(640, 480);
            BackColor = Color.FromArgb(20, 21, 24);
            ForeColor = Color.White;
            KeyPreview = true;
            ShowInTaskbar = false;

            _canvas.Dock = DockStyle.Fill;
            _canvas.BackColor = Color.FromArgb(20, 21, 24);
            typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_canvas, true);
            _canvas.Paint += Canvas_Paint;
            _canvas.MouseDown += Canvas_MouseDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseUp += (s, e) => _live = null;
            // Колесо над наклейкой меняет размер, с Ctrl — поворачивает.
            _canvas.MouseWheel += Canvas_MouseWheel;
            _canvas.Resize += (s, e) => _canvas.Invalidate();

            // ДВЕ ПОЛОСЫ, А НЕ ОДНА.
            //
            // Инструментов стало больше (перо, маркер, неон, ластик, текст,
            // наклейка, палитра, толщина), и в одну полосу они не влезали: у
            // неё появлялась полоса прокрутки, которая съедала высоту, и
            // кнопок было просто не видно. Теперь снизу — кнопки, над ними —
            // инструменты, и они переносятся на вторую строку.
            var bar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                BackColor = Color.FromArgb(28, 29, 32),
                Padding = new Padding(10, 6, 10, 6),
            };
            var toolbar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 88,
                BackColor = Color.FromArgb(24, 25, 28),
                Padding = new Padding(10, 6, 10, 6),
            };

            var done = new Button { Text = "Готово", Dock = DockStyle.Right, Width = 130 };
            Flat(done, Color.FromArgb(88, 101, 242));
            done.Click += (s, e) => Finish();

            var cancel = new Button { Text = "Отмена", Dock = DockStyle.Right, Width = 100 };
            Flat(cancel, Color.FromArgb(57, 60, 66));
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            var redo = new Button { Text = "Вернуть", Dock = DockStyle.Right, Width = 100 };
            Flat(redo, Color.FromArgb(57, 60, 66));
            redo.Click += (s, e) => Redo();

            var undo = new Button { Text = "Отменить", Dock = DockStyle.Right, Width = 100 };
            Flat(undo, Color.FromArgb(57, 60, 66));
            undo.Click += (s, e) => Undo();

            // Переносим на вторую строку, а не прокручиваем: полоса
            // прокрутки в узкой панели съедала саму панель.
            var tools = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = true,
                AutoScroll = false,
            };
            tools.Controls.Add(ToolButton("Перо", Tool.Pen));
            tools.Controls.Add(ToolButton("Маркер", Tool.Marker));
            tools.Controls.Add(ToolButton("Неон", Tool.Neon));
            tools.Controls.Add(ToolButton("Ластик", Tool.Eraser));
            tools.Controls.Add(ActionButton("Текст", AddText));
            tools.Controls.Add(ActionButton("Наклейка", AddSticker));
            foreach (var c in new[]
                     {
                         Color.FromArgb(255,59,48), Color.FromArgb(255,204,0),
                         Color.FromArgb(52,199,89), Color.FromArgb(0,122,255),
                         Color.FromArgb(175,82,222), Color.White, Color.Black,
                     })
                tools.Controls.Add(ColorSwatch(c));
            // Палитра — любой оттенок, а не только готовые. На Windows это
            // системное окно выбора цвета, к нему все привыкли.
            var more = new Button { Text = "Палитра…", Size = new Size(92, 28), Margin = new Padding(6, 6, 0, 0) };
            Flat(more, Color.FromArgb(47, 49, 54));
            more.Click += (s, e) =>
            {
                using var dlg = new ColorDialog
                {
                    Color = _color,
                    FullOpen = true,
                    AnyColor = true,
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _color = dlg.Color;
                // Выбранный цвет встаёт рядом с готовыми — чтобы вернуться к
                // нему одним нажатием.
                tools.Controls.Add(ColorSwatch(dlg.Color));
                HighlightPicks();
                _canvas.Invalidate();
            };
            tools.Controls.Add(more);

            // Толщина — не числом, а кружком в натуральную величину: видно,
            // какой будет линия, и видно, какая выбрана.
            foreach (var w in new[] { 0.006f, 0.012f, 0.022f, 0.036f })
                tools.Controls.Add(WidthDot(w));

            toolbar.Controls.Add(tools);
            bar.Controls.Add(undo);
            bar.Controls.Add(redo);
            bar.Controls.Add(cancel);
            bar.Controls.Add(done);

            // Порядок добавления = порядок раскладки снизу вверх: кнопки у
            // самого края, над ними инструменты, остальное — холсту.
            Controls.Add(_canvas);
            Controls.Add(toolbar);
            Controls.Add(bar);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.Control && e.KeyCode == Keys.Z) Undo();
                else if (e.Control && (e.KeyCode == Keys.Y
                         || (e.Shift && e.KeyCode == Keys.Z))) Redo();
            };

            HighlightTools();
            HighlightPicks();
        }

        /// <summary>
        /// Кружок цвета. Выбранный обведён белым — иначе угадать, каким
        /// цветом рисуешь, можно было только по самой линии.
        /// </summary>
        private Panel ColorSwatch(Color c)
        {
            var sw = new Panel
            {
                Size = new Size(26, 26),
                Margin = new Padding(6, 6, 0, 0),
                BackColor = c,
                Cursor = Cursors.Hand,
            };
            sw.Paint += (s, e) =>
            {
                if (_color.ToArgb() != c.ToArgb()) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(Color.White, 2.5f);
                e.Graphics.DrawRectangle(pen, 1, 1, sw.Width - 3, sw.Height - 3);
            };
            sw.Click += (s, e) => { _color = c; HighlightPicks(); _canvas.Invalidate(); };
            _swatches.Add(sw);
            return sw;
        }

        /// <summary>Толщина кружком в натуральную величину; выбранная подсвечена.</summary>
        private Panel WidthDot(float w)
        {
            var p = new Panel
            {
                Size = new Size(34, 28),
                Margin = new Padding(6, 6, 0, 0),
                BackColor = Color.FromArgb(47, 49, 54),
                Cursor = Cursors.Hand,
                Tag = w,
            };
            p.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool on = Math.Abs(_width - w) < 0.0001f;
                using (var back = new SolidBrush(on ? Color.FromArgb(88, 101, 242)
                                                    : Color.FromArgb(47, 49, 54)))
                    g.FillRectangle(back, 0, 0, p.Width, p.Height);
                // Диаметр — та же доля, что и у линии, пересчитанная на палец:
                // ряд кружков сразу показывает, что выбираешь.
                int d = Math.Max(4, (int)Math.Round(w * 560));
                using var dot = new SolidBrush(Color.White);
                g.FillEllipse(dot, (p.Width - d) / 2f, (p.Height - d) / 2f, d, d);
            };
            p.Click += (s, e) => { _width = w; HighlightPicks(); };
            _widthDots.Add(p);
            return p;
        }

        /// <summary>Перерисовать отметки выбранного цвета и толщины.</summary>
        private void HighlightPicks()
        {
            foreach (var c in _swatches) { try { c.Invalidate(); } catch { } }
            foreach (var d in _widthDots) { try { d.Invalidate(); } catch { } }
        }

        private static void Flat(Button b, Color back)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI", 9f);
            b.Cursor = Cursors.Hand;
            b.TabStop = false;
        }

        private Button ActionButton(string title, Action onClick)
        {
            var b = new Button { Text = title, Size = new Size(90, 28), Margin = new Padding(0, 6, 6, 0) };
            Flat(b, Color.FromArgb(47, 49, 54));
            b.Click += (s, e) => onClick();
            return b;
        }

        private Button ToolButton(string title, Tool tool)
        {
            var b = new Button { Text = title, Size = new Size(84, 28), Margin = new Padding(0, 6, 6, 0) };
            Flat(b, Color.FromArgb(47, 49, 54));
            b.Tag = tool;
            b.Click += (s, e) => { _tool = tool; HighlightTools(); };
            _toolButtons.Add(b);
            return b;
        }

        /// <summary>Выбранный инструмент виден — иначе не угадать, чем рисуешь.</summary>
        private void HighlightTools()
        {
            foreach (var b in _toolButtons)
            {
                bool on = b.Tag is Tool t && t == _tool;
                b.BackColor = on ? Color.FromArgb(88, 101, 242) : Color.FromArgb(47, 49, 54);
            }
        }

        private void Canvas_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            float k = Math.Min((float)_canvas.ClientSize.Width / _src.Width,
                               (float)_canvas.ClientSize.Height / _src.Height);
            float w = _src.Width * k, h = _src.Height * k;
            _frame = new RectangleF((_canvas.ClientSize.Width - w) / 2f,
                                    (_canvas.ClientSize.Height - h) / 2f, w, h);
            g.DrawImage(_src, _frame);

            // Линии — в отдельном прозрачном слое: ластик стирает их, а не
            // саму фотографию.
            var pens = _marks.OfType<Stroke>().ToList();
            if (pens.Count > 0 && _frame.Width >= 1 && _frame.Height >= 1)
            {
                using var layer = new Bitmap((int)_frame.Width, (int)_frame.Height,
                                             System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var lg = Graphics.FromImage(layer))
                {
                    lg.SmoothingMode = SmoothingMode.AntiAlias;
                    foreach (var s in pens)
                        DrawStroke(lg, s, _frame.Width, _frame.Height);
                }
                g.DrawImage(layer, _frame.Left, _frame.Top);
            }

            foreach (var m in _marks)
            {
                if (m is StickerMark st)
                {
                    float sw = _frame.Width * st.Scale;
                    float sh = sw * st.Image.Height / st.Image.Width;
                    var c = Denorm(st.Pos);
                    DrawSticker(g, st.Image, c.X, c.Y, sw, sh, st.Angle);
                }
                else if (m is TextMark t)
                {
                    var c = Denorm(t.Pos);
                    DrawText(g, t, _frame.Width, c.X, c.Y);
                }
            }
        }

        /// <summary>
        /// Одна линия в слое размером w×h. Координаты — доли, толщина — доля
        /// ширины: и в окне, и в готовом файле рисуется одним и тем же кодом,
        /// поэтому что видно, то и уходит.
        /// </summary>
        private static void DrawStroke(Graphics g, Stroke s, float w, float h)
        {
            float pw = Math.Max(1f, s.Width * w);
            var pts = new PointF[s.Points.Count];
            for (int i = 0; i < s.Points.Count; i++)
                pts[i] = new PointF(s.Points[i].X * w, s.Points[i].Y * h);

            if (s.Points.Count == 1)
            {
                float r = pw / 2f;
                if (s.Kind == Tool.Eraser)
                {
                    var save = g.CompositingMode;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using var clear = new SolidBrush(Color.Transparent);
                    g.FillEllipse(clear, pts[0].X - r * 1.6f, pts[0].Y - r * 1.6f, r * 3.2f, r * 3.2f);
                    g.CompositingMode = save;
                    return;
                }
                using var dot = new SolidBrush(s.Color);
                g.FillEllipse(dot, pts[0].X - r, pts[0].Y - r, pw, pw);
                return;
            }
            if (s.Points.Count < 2) return;

            switch (s.Kind)
            {
                case Tool.Eraser:
                {
                    var save = g.CompositingMode;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using var pen = new Pen(Color.Transparent, pw * 1.6f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    g.DrawLines(pen, pts);
                    g.CompositingMode = save;
                    break;
                }
                case Tool.Marker:
                {
                    using var pen = new Pen(Color.FromArgb(102, s.Color), pw * 1.8f)
                    { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Round };
                    g.DrawLines(pen, pts);
                    break;
                }
                case Tool.Neon:
                {
                    using var glow = new Pen(Color.FromArgb(90, s.Color), pw * 2.6f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    g.DrawLines(glow, pts);
                    using var core = new Pen(Color.White, Math.Max(1f, pw * 0.6f))
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    g.DrawLines(core, pts);
                    break;
                }
                default:
                {
                    using var pen = new Pen(s.Color, pw)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                    g.DrawLines(pen, pts);
                    break;
                }
            }
        }

        /// <summary>
        /// Наклейка рисуется вокруг своей середины и с поворотом — одним и тем
        /// же кодом в окне и в готовом файле.
        /// </summary>
        private static void DrawSticker(Graphics g, Image img, float cx, float cy,
                                        float w, float h, float angle)
        {
            var save = g.Save();
            try
            {
                g.TranslateTransform(cx, cy);
                if (Math.Abs(angle) > 0.01f) g.RotateTransform(angle);
                g.DrawImage(img, new RectangleF(-w / 2f, -h / 2f, w, h));
            }
            finally { g.Restore(save); }
        }

        /// <summary>
        /// Надпись вокруг своей середины и с поворотом — одним и тем же кодом
        /// в окне и в готовом файле, чтобы что видно, то и уходило.
        /// </summary>
        private static void DrawText(Graphics g, TextMark t, float baseW, float cx, float cy)
        {
            using var f = new Font("Segoe UI", Math.Max(1f, t.Size * baseW),
                                   FontStyle.Bold, GraphicsUnit.Pixel);
            using var br = new SolidBrush(t.Color);
            var size = g.MeasureString(t.Text, f);
            var save = g.Save();
            try
            {
                g.TranslateTransform(cx, cy);
                if (Math.Abs(t.Angle) > 0.01f) g.RotateTransform(t.Angle);
                g.DrawString(t.Text, f, br, -size.Width / 2f, -size.Height / 2f);
            }
            finally { g.Restore(save); }
        }

        private PointF Denorm(PointF n) =>
            new(_frame.Left + n.X * _frame.Width, _frame.Top + n.Y * _frame.Height);

        private PointF? Norm(Point p)
        {
            if (_frame.Width <= 0 || _frame.Height <= 0) return null;
            float x = (p.X - _frame.Left) / _frame.Width;
            float y = (p.Y - _frame.Top) / _frame.Height;
            if (x < 0 || x > 1 || y < 0 || y > 1) return null;
            return new PointF(x, y);
        }

        private void Add(Mark m)
        {
            _marks.Add(m);
            // Новое действие обрывает ветку «вернуть» — как в любом редакторе.
            _undone.Clear();
        }

        private T LastOf<T>() where T : Mark
        {
            for (int i = _marks.Count - 1; i >= 0; i--)
                if (_marks[i] is T t) return t;
            return null;
        }

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var n = Norm(e.Location);
            if (n == null) return;

            switch (_tool)
            {
                case Tool.Text:
                {
                    var t = LastOf<TextMark>();
                    if (t != null) { t.Pos = n.Value; _canvas.Invalidate(); }
                    break;
                }
                case Tool.Sticker:
                {
                    var st = LastOf<StickerMark>();
                    if (st != null) { st.Pos = n.Value; _canvas.Invalidate(); }
                    break;
                }
                default:
                    _live = new Stroke { Color = _color, Width = _width, Kind = _tool };
                    _live.Points.Add(n.Value);
                    Add(_live);
                    _canvas.Invalidate();
                    break;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var n = Norm(e.Location);
            if (n == null) return;

            if (_tool == Tool.Text)
            {
                var t = LastOf<TextMark>();
                if (t != null) { t.Pos = n.Value; _canvas.Invalidate(); }
            }
            else if (_tool == Tool.Sticker)
            {
                var st = LastOf<StickerMark>();
                if (st != null) { st.Pos = n.Value; _canvas.Invalidate(); }
            }
            else if (_live != null)
            {
                _live.Points.Add(n.Value);
                _canvas.Invalidate();
            }
        }

        /// <summary>
        /// Колесо над холстом меняет РАЗМЕР последнего добавленного — наклейки
        /// или надписи, — а с Ctrl (или с зажатой правой кнопкой) поворачивает
        /// его. Иначе надпись нельзя было ни увеличить, ни наклонить.
        /// </summary>
        private void Canvas_MouseWheel(object sender, MouseEventArgs e)
        {
            int steps = e.Delta / 120;
            if (steps == 0) return;
            bool rotate = ModifierKeys.HasFlag(Keys.Control)
                          || (MouseButtons & MouseButtons.Right) == MouseButtons.Right;

            // Крутим то, что добавили последним: наклейку или надпись.
            Mark last = null;
            for (int i = _marks.Count - 1; i >= 0; i--)
                if (_marks[i] is StickerMark || _marks[i] is TextMark) { last = _marks[i]; break; }
            if (last == null) return;

            if (last is StickerMark st)
            {
                if (rotate) st.Angle = (st.Angle + steps * 5f) % 360f;
                else st.Scale = Math.Max(0.05f, Math.Min(1.6f, st.Scale * (steps > 0 ? 1.08f : 1f / 1.08f)));
            }
            else if (last is TextMark t)
            {
                if (rotate) t.Angle = (t.Angle + steps * 5f) % 360f;
                else t.Size = Math.Max(0.015f, Math.Min(0.5f, t.Size * (steps > 0 ? 1.08f : 1f / 1.08f)));
            }
            _canvas.Invalidate();
        }

        private void AddText()
        {
            _tool = Tool.Text;
            HighlightTools();
            string text = Prompt("Надпись на фото");
            if (string.IsNullOrWhiteSpace(text)) return;
            // Кегль — доля ширины снимка: иначе на большом снимке надпись
            // выходила мелкой, а на маленьком — во весь кадр.
            Add(new TextMark { Text = text, Pos = new PointF(0.5f, 0.5f), Color = _color, Size = 0.06f });
            _canvas.Invalidate();
        }

        private void AddSticker()
        {
            _tool = Tool.Sticker;
            HighlightTools();
            using var dlg = new OpenFileDialog
            {
                Title = "Наклейка — снимок с диска",
                // Гифку намеренно не предлагаем: в наклейке она замрёт.
                Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var img = Image.FromStream(new MemoryStream(File.ReadAllBytes(dlg.FileName)));
                Add(new StickerMark { Image = img, Pos = new PointF(0.5f, 0.45f), Scale = 0.35f });
                _canvas.Invalidate();
            }
            catch { }
        }

        private void Undo()
        {
            if (_marks.Count == 0) return;
            var last = _marks[^1];
            _marks.RemoveAt(_marks.Count - 1);
            // Наклейку не выбрасываем: её ещё могут вернуть.
            _undone.Add(last);
            if (ReferenceEquals(last, _live)) _live = null;
            _canvas.Invalidate();
        }

        private void Redo()
        {
            if (_undone.Count == 0) return;
            var back = _undone[^1];
            _undone.RemoveAt(_undone.Count - 1);
            _marks.Add(back);
            _canvas.Invalidate();
        }

        /// <summary>
        /// Окно ввода надписи.
        ///
        /// Раньше это было поле, прилипшее к краям окна, и кнопка, обрезанная
        /// нижней кромкой. Теперь — как в остальном приложении: подсказка,
        /// поле в собственной рамке с отступами, «Отмена» и «Добавить»
        /// ровным рядом. Размеры пересчитываются под масштаб экрана, иначе на
        /// 125–150 % кнопки снова уезжали бы за край.
        /// </summary>
        private string Prompt(string title)
        {
            using var box = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(30, 31, 34),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.None,
            };
            float k = DeviceDpi / 96f;
            int S(int v) => (int)Math.Round(v * k);
            box.ClientSize = new Size(S(440), S(164));

            var hint = new Label
            {
                AutoSize = false,
                Location = new Point(S(16), S(12)),
                Size = new Size(S(408), S(20)),
                ForeColor = Color.FromArgb(150, 153, 160),
                Text = "Мышью — двигать, колесо — размер, Ctrl+колесо — поворот.",
            };

            // Поле в своей рамке: текст не прилипает к краям, а рамка
            // подсвечивается, пока в поле курсор.
            var field = new Panel
            {
                Location = new Point(S(16), S(40)),
                Size = new Size(S(408), S(44)),
                BackColor = Color.FromArgb(44, 47, 51),
                Padding = new Padding(S(12), S(11), S(12), S(8)),
            };
            var tb = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(44, 47, 51),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 12f),
            };
            field.Controls.Add(tb);
            field.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var c = tb.Focused ? Color.FromArgb(88, 101, 242) : Color.FromArgb(70, 73, 80);
                using var pen = new Pen(c, tb.Focused ? 2f : 1f);
                e.Graphics.DrawRectangle(pen, 0.5f, 0.5f, field.Width - 2, field.Height - 2);
            };
            tb.GotFocus += (s, e) => field.Invalidate();
            tb.LostFocus += (s, e) => field.Invalidate();

            // Кнопки — ровным рядом справа, одной высоты, с зазором.
            var ok = new Button
            {
                Text = "Добавить",
                Size = new Size(S(116), S(36)),
                Location = new Point(S(440 - 16 - 116), S(108)),
                DialogResult = DialogResult.OK,
            };
            Flat(ok, Color.FromArgb(88, 101, 242));
            var cancel = new Button
            {
                Text = "Отмена",
                Size = new Size(S(116), S(36)),
                Location = new Point(S(440 - 16 - 116 - 10 - 116), S(108)),
                DialogResult = DialogResult.Cancel,
            };
            Flat(cancel, Color.FromArgb(57, 60, 66));

            box.Controls.Add(hint);
            box.Controls.Add(field);
            box.Controls.Add(cancel);
            box.Controls.Add(ok);
            box.AcceptButton = ok;
            box.CancelButton = cancel;
            box.Shown += (s, e) => tb.Focus();
            return box.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : null;
        }

        private void Finish()
        {
            try
            {
                var bmp = new Bitmap(_src.Width, _src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_src, 0, 0, bmp.Width, bmp.Height);

                    float w = bmp.Width, h = bmp.Height;

                    // Линии — тем же кодом, что и в окне, только слой размером
                    // с настоящий снимок. Толщина — та же доля ширины, поэтому
                    // линия больше не толстеет при сборке.
                    var pens = _marks.OfType<Stroke>().ToList();
                    if (pens.Count > 0)
                    {
                        using var layer = new Bitmap(bmp.Width, bmp.Height,
                                                     System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var lg = Graphics.FromImage(layer))
                        {
                            lg.SmoothingMode = SmoothingMode.AntiAlias;
                            foreach (var s in pens) DrawStroke(lg, s, w, h);
                        }
                        g.DrawImage(layer, 0, 0);
                    }

                    foreach (var m in _marks)
                    {
                        if (m is StickerMark st)
                        {
                            float sw = w * st.Scale;
                            float sh = sw * st.Image.Height / st.Image.Width;
                            DrawSticker(g, st.Image, st.Pos.X * w, st.Pos.Y * h, sw, sh, st.Angle);
                        }
                        else if (m is TextMark t)
                        {
                            DrawText(g, t, w, t.Pos.X * w, t.Pos.Y * h);
                        }
                    }
                }

                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                Result = ms.ToArray();
                bmp.Dispose();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch
            {
                MessageBox.Show("Не удалось собрать снимок.", "PISMO");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _src?.Dispose(); } catch { }
                foreach (var m in _marks)
                    if (m is StickerMark st) { try { st.Image?.Dispose(); } catch { } }
                foreach (var m in _undone)
                    if (m is StickerMark st) { try { st.Image?.Dispose(); } catch { } }
            }
            base.Dispose(disposing);
        }
    }
}
