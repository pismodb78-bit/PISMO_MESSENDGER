// ============================================================
//  PhotoEditorForm — рисование поверх снимка
// ============================================================
//
//  То же, что в редакторе на телефоне и в Telegram: кисть, надпись прямо на
//  фотографии и наклейка — другой снимок с диска. Гифки в наклейку не берём:
//  в ней они всё равно замрут первым кадром, а весят как ролик.
//
//  Слои живут в ДОЛЯХ от размера снимка, а не в точках экрана. Поэтому
//  нарисованное ложится одинаково и в окне, и в готовом файле, какого бы
//  размера ни была фотография: при сборке доли умножаются на её пиксели.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    internal sealed class PhotoEditorForm : Form
    {
        private enum Tool { Brush, Text, Sticker }

        private sealed class Stroke
        {
            public readonly List<PointF> Points = new();
            public Color Color;
            public float Width;
        }

        private sealed class TextMark
        {
            public string Text;
            public PointF Pos;
            public Color Color;
            public float Size;
        }

        private sealed class StickerMark
        {
            public Image Image;
            public PointF Pos;
            public float Scale;
        }

        private readonly Image _src;
        private readonly List<Stroke> _strokes = new();
        private readonly List<TextMark> _texts = new();
        private readonly List<StickerMark> _stickers = new();
        private readonly List<string> _order = new();

        private readonly Panel _canvas = new();
        private Tool _tool = Tool.Brush;
        private Color _color = Color.FromArgb(255, 59, 48);
        private float _width = 10f;
        private RectangleF _frame;
        private Stroke _live;

        /// <summary>Готовый снимок с нарисованным.</summary>
        public byte[] Result { get; private set; }

        public PhotoEditorForm(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
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

            var cancel = new Button { Text = "Отмена", Dock = DockStyle.Right, Width = 110 };
            Flat(cancel, Color.FromArgb(57, 60, 66));
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            var undo = new Button { Text = "Отменить", Dock = DockStyle.Right, Width = 110 };
            Flat(undo, Color.FromArgb(57, 60, 66));
            undo.Click += (s, e) => Undo();

            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            tools.Controls.Add(ToolButton("Кисть", () => _tool = Tool.Brush));
            tools.Controls.Add(ToolButton("Текст", AddText));
            tools.Controls.Add(ToolButton("Наклейка", AddSticker));
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
            foreach (var w in new[] { 6f, 10f, 18f, 28f })
            {
                float captured = w;
                var b = new Button { Text = ((int)w).ToString(), Size = new Size(36, 28), Margin = new Padding(6, 6, 0, 0) };
                Flat(b, Color.FromArgb(57, 60, 66));
                b.Click += (s, e) => _width = captured;
                tools.Controls.Add(b);
            }

            bar.Controls.Add(tools);
            bar.Controls.Add(undo);
            bar.Controls.Add(cancel);
            bar.Controls.Add(done);

            Controls.Add(_canvas);
            Controls.Add(bar);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.Control && e.KeyCode == Keys.Z) Undo();
            };
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

        private Button ToolButton(string title, Action onClick)
        {
            var b = new Button { Text = title, Size = new Size(96, 28), Margin = new Padding(0, 6, 6, 0) };
            Flat(b, Color.FromArgb(47, 49, 54));
            b.Click += (s, e) => onClick();
            return b;
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

            foreach (var s in _strokes)
            {
                if (s.Points.Count < 2) continue;
                using var pen = new Pen(s.Color, Math.Max(1f, s.Width * k))
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                var pts = new PointF[s.Points.Count];
                for (int i = 0; i < s.Points.Count; i++) pts[i] = Denorm(s.Points[i]);
                g.DrawLines(pen, pts);
            }

            foreach (var st in _stickers)
            {
                float sw = _frame.Width * st.Scale;
                float sh = sw * st.Image.Height / st.Image.Width;
                var c = Denorm(st.Pos);
                g.DrawImage(st.Image, new RectangleF(c.X - sw / 2f, c.Y - sh / 2f, sw, sh));
            }

            foreach (var t in _texts)
            {
                using var f = new Font("Segoe UI", t.Size * k, FontStyle.Bold, GraphicsUnit.Pixel);
                using var br = new SolidBrush(t.Color);
                var c = Denorm(t.Pos);
                var size = g.MeasureString(t.Text, f);
                g.DrawString(t.Text, f, br, c.X - size.Width / 2f, c.Y - size.Height / 2f);
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

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var n = Norm(e.Location);
            if (n == null) return;

            switch (_tool)
            {
                case Tool.Brush:
                    _live = new Stroke { Color = _color, Width = _width };
                    _live.Points.Add(n.Value);
                    _strokes.Add(_live);
                    _order.Add("stroke");
                    break;
                case Tool.Text:
                    if (_texts.Count > 0) { _texts[^1].Pos = n.Value; _canvas.Invalidate(); }
                    break;
                case Tool.Sticker:
                    if (_stickers.Count > 0) { _stickers[^1].Pos = n.Value; _canvas.Invalidate(); }
                    break;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var n = Norm(e.Location);
            if (n == null) return;

            if (_tool == Tool.Brush && _live != null)
            {
                _live.Points.Add(n.Value);
                _canvas.Invalidate();
            }
            else if (_tool == Tool.Text && _texts.Count > 0)
            {
                _texts[^1].Pos = n.Value;
                _canvas.Invalidate();
            }
            else if (_tool == Tool.Sticker && _stickers.Count > 0)
            {
                _stickers[^1].Pos = n.Value;
                _canvas.Invalidate();
            }
        }

        private void AddText()
        {
            _tool = Tool.Text;
            string text = Prompt("Надпись на фото");
            if (string.IsNullOrWhiteSpace(text)) return;
            _texts.Add(new TextMark { Text = text, Pos = new PointF(0.5f, 0.5f), Color = _color, Size = 28f });
            _order.Add("text");
            _canvas.Invalidate();
        }

        private void AddSticker()
        {
            _tool = Tool.Sticker;
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
                _stickers.Add(new StickerMark { Image = img, Pos = new PointF(0.5f, 0.45f), Scale = 0.35f });
                _order.Add("sticker");
                _canvas.Invalidate();
            }
            catch { }
        }

        private void Undo()
        {
            if (_order.Count == 0) return;
            string last = _order[^1];
            _order.RemoveAt(_order.Count - 1);
            if (last == "stroke" && _strokes.Count > 0) _strokes.RemoveAt(_strokes.Count - 1);
            else if (last == "text" && _texts.Count > 0) _texts.RemoveAt(_texts.Count - 1);
            else if (last == "sticker" && _stickers.Count > 0)
            {
                try { _stickers[^1].Image?.Dispose(); } catch { }
                _stickers.RemoveAt(_stickers.Count - 1);
            }
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
                    // Кисть задавалась в точках окна; переводим через ширину,
                    // иначе на большом снимке линия вышла бы волоском.
                    float k = w / Math.Max(1f, _frame.Width);

                    foreach (var s in _strokes)
                    {
                        if (s.Points.Count < 2) continue;
                        using var pen = new Pen(s.Color, Math.Max(1f, s.Width * k))
                        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                        var pts = new PointF[s.Points.Count];
                        for (int i = 0; i < s.Points.Count; i++)
                            pts[i] = new PointF(s.Points[i].X * w, s.Points[i].Y * h);
                        g.DrawLines(pen, pts);
                    }

                    foreach (var st in _stickers)
                    {
                        float sw = w * st.Scale;
                        float sh = sw * st.Image.Height / st.Image.Width;
                        g.DrawImage(st.Image,
                            new RectangleF(st.Pos.X * w - sw / 2f, st.Pos.Y * h - sh / 2f, sw, sh));
                    }

                    foreach (var t in _texts)
                    {
                        using var f = new Font("Segoe UI", t.Size * k, FontStyle.Bold, GraphicsUnit.Pixel);
                        using var br = new SolidBrush(t.Color);
                        var size = g.MeasureString(t.Text, f);
                        g.DrawString(t.Text, f, br,
                            t.Pos.X * w - size.Width / 2f, t.Pos.Y * h - size.Height / 2f);
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
                foreach (var st in _stickers) { try { st.Image?.Dispose(); } catch { } }
            }
            base.Dispose(disposing);
        }
    }
}
