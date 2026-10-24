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
        }

        private sealed class StickerMark : Mark
        {
            public Image Image;
            public PointF Pos;
            public float Scale;
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
            _canvas.Resize += (s, e) => _canvas.Invalidate();

            var bar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 56,
                BackColor = Color.FromArgb(28, 29, 32),
                Padding = new Padding(10, 10, 10, 10),
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

            // Инструментов больше, чем влезает в узкое окно, — ряд
            // прокручивается вбок, а не обрезается.
            var tools = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                AutoScroll = true,
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
            {
                var captured = c;
                var sw = new Panel { Size = new Size(26, 26), Margin = new Padding(6, 6, 0, 0), BackColor = c, Cursor = Cursors.Hand };
                sw.Click += (s, e) => { _color = captured; _canvas.Invalidate(); };
                tools.Controls.Add(sw);
            }
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
                var captured = dlg.Color;
                var sw2 = new Panel
                {
                    Size = new Size(26, 26),
                    Margin = new Padding(6, 6, 0, 0),
                    BackColor = captured,
                    Cursor = Cursors.Hand,
                };
                sw2.Click += (s2, e2) => { _color = captured; _canvas.Invalidate(); };
                tools.Controls.Add(sw2);
                _canvas.Invalidate();
            };
            tools.Controls.Add(more);

            // Толщина — доля ширины снимка; на кнопке пишем по-человечески.
            foreach (var w in new[] { 0.006f, 0.012f, 0.022f, 0.036f })
            {
                float captured = w;
                var b = new Button
                {
                    Text = ((int)Math.Round(w * 1000)).ToString(),
                    Size = new Size(36, 28),
                    Margin = new Padding(6, 6, 0, 0),
                };
                Flat(b, Color.FromArgb(57, 60, 66));
                b.Click += (s, e) => _width = captured;
                tools.Controls.Add(b);
            }

            bar.Controls.Add(tools);
            bar.Controls.Add(undo);
            bar.Controls.Add(redo);
            bar.Controls.Add(cancel);
            bar.Controls.Add(done);

            Controls.Add(_canvas);
            Controls.Add(bar);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.Control && e.KeyCode == Keys.Z) Undo();
                else if (e.Control && (e.KeyCode == Keys.Y
                         || (e.Shift && e.KeyCode == Keys.Z))) Redo();
            };

            HighlightTools();
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
                    g.DrawImage(st.Image, new RectangleF(c.X - sw / 2f, c.Y - sh / 2f, sw, sh));
                }
                else if (m is TextMark t)
                {
                    using var f = new Font("Segoe UI", Math.Max(1f, t.Size * _frame.Width),
                                           FontStyle.Bold, GraphicsUnit.Pixel);
                    using var br = new SolidBrush(t.Color);
                    var c = Denorm(t.Pos);
                    var size = g.MeasureString(t.Text, f);
                    g.DrawString(t.Text, f, br, c.X - size.Width / 2f, c.Y - size.Height / 2f);
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

        private static string Prompt(string title)
        {
            using var box = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(380, 110),
                BackColor = Color.FromArgb(32, 34, 37),
                ForeColor = Color.White,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
            };
            var tb = new TextBox
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(44, 47, 51),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(10),
            };
            var ok = new Button { Text = "Добавить", Dock = DockStyle.Right, Width = 120, DialogResult = DialogResult.OK };
            Flat(ok, Color.FromArgb(88, 101, 242));
            var row = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(10) };
            row.Controls.Add(ok);
            box.Controls.Add(row);
            box.Controls.Add(tb);
            box.AcceptButton = ok;
            return box.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
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
                            g.DrawImage(st.Image,
                                new RectangleF(st.Pos.X * w - sw / 2f, st.Pos.Y * h - sh / 2f, sw, sh));
                        }
                        else if (m is TextMark t)
                        {
                            using var f = new Font("Segoe UI", Math.Max(1f, t.Size * w),
                                                   FontStyle.Bold, GraphicsUnit.Pixel);
                            using var br = new SolidBrush(t.Color);
                            var size = g.MeasureString(t.Text, f);
                            g.DrawString(t.Text, f, br,
                                t.Pos.X * w - size.Width / 2f, t.Pos.Y * h - size.Height / 2f);
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
