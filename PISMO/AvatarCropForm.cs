using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>Диалог обрезки аватара кружком: перетаскивай мышью, колесо/ползунок —
    /// масштаб. На выходе — квадратный PNG (отображается кружком в приложении).</summary>
    public sealed class AvatarCropForm : Form
    {
        private const int CROP = 300;   // размер области предпросмотра
        private const int OUT = 256;    // итоговый размер аватара

        private readonly Image _src;
        private float _scale = 1f, _minScale = 1f;
        private float _offX, _offY;     // положение левого-верхнего угла картинки в области
        private Point _dragStart;
        private bool _dragging;

        private readonly Panel _area;
        private readonly TrackBar _zoom;

        public byte[] ResultPng { get; private set; }

        public AvatarCropForm(Image source)
        {
            _src = source;
            Text = "Обрезка аватара";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(47, 49, 54);
            ClientSize = new Size(CROP + 40, CROP + 130);

            _area = new Panel { Location = new Point(20, 20), Size = new Size(CROP, CROP), BackColor = Color.FromArgb(20, 21, 24) };
            _area.Paint += AreaPaint;
            _area.MouseDown += (s, e) => { _dragging = true; _dragStart = e.Location; };
            _area.MouseUp += (s, e) => _dragging = false;
            _area.MouseMove += (s, e) =>
            {
                if (!_dragging) return;
                _offX += e.X - _dragStart.X;
                _offY += e.Y - _dragStart.Y;
                _dragStart = e.Location;
                ClampOffsets();
                _area.Invalidate();
            };
            _area.MouseWheel += (s, e) =>
            {
                float old = _scale;
                _scale *= e.Delta > 0 ? 1.08f : 0.92f;
                _scale = Math.Max(_minScale, Math.Min(_minScale * 5f, _scale));
                // зум к центру
                float cx = CROP / 2f, cy = CROP / 2f;
                _offX = cx - (cx - _offX) * (_scale / old);
                _offY = cy - (cy - _offY) * (_scale / old);
                SyncZoomBar();
                ClampOffsets();
                _area.Invalidate();
            };

            _zoom = new TrackBar { Location = new Point(20, CROP + 26), Size = new Size(CROP, 30), TickStyle = TickStyle.None, Minimum = 100, Maximum = 500, Value = 100 };
            _zoom.ValueChanged += (s, e) =>
            {
                float old = _scale;
                _scale = _minScale * (_zoom.Value / 100f);
                float cx = CROP / 2f, cy = CROP / 2f;
                _offX = cx - (cx - _offX) * (_scale / old);
                _offY = cy - (cy - _offY) * (_scale / old);
                ClampOffsets();
                _area.Invalidate();
            };

            var btnOk = new Button { Text = "Готово", BackColor = Color.FromArgb(88, 101, 242), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold), Location = new Point(20, CROP + 64), Size = new Size(CROP / 2 - 6, 40), Cursor = Cursors.Hand };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) => { Confirm(); DialogResult = DialogResult.OK; Close(); };

            var btnCancel = new Button { Text = "Отмена", BackColor = Color.FromArgb(80, 82, 88), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold), Location = new Point(20 + CROP / 2 + 6, CROP + 64), Size = new Size(CROP / 2 - 6, 40), Cursor = Cursors.Hand, DialogResult = DialogResult.Cancel };
            btnCancel.FlatAppearance.BorderSize = 0;

            Controls.AddRange(new Control[] { _area, _zoom, btnOk, btnCancel });

            // Начальный масштаб: картинка заполняет круг.
            _minScale = Math.Max((float)CROP / _src.Width, (float)CROP / _src.Height);
            _scale = _minScale;
            _offX = (CROP - _src.Width * _scale) / 2f;
            _offY = (CROP - _src.Height * _scale) / 2f;
        }

        private void SyncZoomBar()
        {
            int v = (int)Math.Round(_scale / _minScale * 100);
            _zoom.Value = Math.Max(_zoom.Minimum, Math.Min(_zoom.Maximum, v));
        }

        private void ClampOffsets()
        {
            float w = _src.Width * _scale, h = _src.Height * _scale;
            // картинка должна покрывать всю область
            if (_offX > 0) _offX = 0;
            if (_offY > 0) _offY = 0;
            if (_offX + w < CROP) _offX = CROP - w;
            if (_offY + h < CROP) _offY = CROP - h;
        }

        private void AreaPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_src, _offX, _offY, _src.Width * _scale, _src.Height * _scale);

            // Затемнение вне круга + контур.
            using var region = new Region(new Rectangle(0, 0, CROP, CROP));
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, CROP - 1, CROP - 1);
            region.Exclude(path);
            using (var dim = new SolidBrush(Color.FromArgb(150, 20, 21, 24)))
                g.FillRegion(dim, region);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.White, 2);
            g.DrawEllipse(pen, 1, 1, CROP - 3, CROP - 3);
        }

        private void Confirm()
        {
            try
            {
                // Область предпросмотра (квадрат) → источник.
                float sx = -_offX / _scale, sy = -_offY / _scale, sw = CROP / _scale, sh = CROP / _scale;
                var outBmp = new Bitmap(OUT, OUT);
                using (var g = Graphics.FromImage(outBmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_src, new RectangleF(0, 0, OUT, OUT), new RectangleF(sx, sy, sw, sh), GraphicsUnit.Pixel);
                }
                using var ms = new MemoryStream();
                outBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ResultPng = ms.ToArray();
                outBmp.Dispose();
            }
            catch { ResultPng = null; }
        }
    }
}
