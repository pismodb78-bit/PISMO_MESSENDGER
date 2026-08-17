using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Просмотр изображения с масштабированием.
    ///
    /// Раньше картинка показывалась в PictureBox с SizeMode=Zoom: она вписывалась
    /// в окно и увеличить её было нельзя — мелкие детали (скриншот, текст на
    /// фото) рассмотреть не получалось. Здесь рисуем сами, с произвольным
    /// масштабом и перетаскиванием.
    ///
    /// Колесо — масштаб ОТНОСИТЕЛЬНО КУРСОРА (точка под указателем остаётся на
    /// месте, как в просмотрщиках и картах). Двойной клик — «вписать в окно» ↔
    /// «1:1». Перетаскивание мышью двигает картинку, Esc закрывает.
    /// </summary>
    internal sealed class ImageViewerForm : Form
    {
        private const float MinScale = 0.05f;
        private const float MaxScale = 20f;

        private Image _img;
        private IDisposable _hold;      // поток, из которого создан Image (держим живым)
        private float _scale = 1f;
        private PointF _offset;         // левый верхний угол картинки в координатах канвы
        private bool _drag;
        private Point _dragFrom;
        private PointF _offsetFrom;
        private bool _fitMode = true;   // при ресайзе окна держим «вписано», пока не зумили

        private readonly Panel _canvas;
        private readonly Label _lblZoom;

        public ImageViewerForm()
        {
            Text = "Просмотр";
            Size = new Size(900, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.Black;
            KeyPreview = true;

            _canvas = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _canvas.Paint += Canvas_Paint;
            _canvas.MouseDown += Canvas_MouseDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseUp += (s, e) => { _drag = false; _canvas.Cursor = Cursors.Default; };
            _canvas.MouseWheel += Canvas_MouseWheel;
            _canvas.DoubleClick += (s, e) => { if (_fitMode) SetScaleAtCenter(1f); else FitToWindow(); };
            _canvas.Resize += (s, e) => { if (_fitMode) FitToWindow(); else _canvas.Invalidate(); };
            // Колесо приходит только сфокусированному контролу — забираем фокус на канву.
            _canvas.MouseEnter += (s, e) => { try { _canvas.Focus(); } catch { } };
            Controls.Add(_canvas);

            _lblZoom = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(210, 211, 213),
                // Непрозрачный тёмный: у Label альфа-фон поверх собственной
                // отрисовки панели даёт не «стекло», а грязный прямоугольник.
                BackColor = Color.FromArgb(28, 29, 32),
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Padding = new Padding(8, 4, 8, 4),
                Location = new Point(10, 10)
            };
            _canvas.Controls.Add(_lblZoom);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode is Keys.Add or Keys.Oemplus) ZoomBy(1.25f, CanvasCenter());
                else if (e.KeyCode is Keys.Subtract or Keys.OemMinus) ZoomBy(1f / 1.25f, CanvasCenter());
                else if (e.KeyCode == Keys.D0 && e.Control) SetScaleAtCenter(1f);
                else if (e.KeyCode == Keys.F) FitToWindow();
            };
        }

        private sealed class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                TabStop = true;
            }
            // Иначе панель не берёт фокус и не получает колесо мыши.
            protected override bool ShowFocusCues => false;
            protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
        }

        /// <summary>Показывает картинку (окно переиспользуется).</summary>
        public void SetImage(Image img, IDisposable hold)
        {
            var oldImg = _img; var oldHold = _hold;
            _img = img; _hold = hold;
            try { oldImg?.Dispose(); } catch { }
            try { oldHold?.Dispose(); } catch { }
            _fitMode = true;
            FitToWindow();
        }

        private PointF CanvasCenter() => new PointF(_canvas.ClientSize.Width / 2f, _canvas.ClientSize.Height / 2f);

        /// <summary>Вписать в окно. Мелкие картинки НЕ растягиваем — иначе они
        /// показывались бы мыльными на весь экран.</summary>
        private void FitToWindow()
        {
            if (_img == null || _canvas.ClientSize.Width <= 0) return;
            float k = Math.Min((float)_canvas.ClientSize.Width / _img.Width,
                               (float)_canvas.ClientSize.Height / _img.Height);
            _scale = Math.Clamp(Math.Min(k, 1f), MinScale, MaxScale);
            CenterImage();
            _fitMode = true;
            UpdateZoomLabel();
            _canvas.Invalidate();
        }

        private void SetScaleAtCenter(float scale)
        {
            ZoomBy(scale / _scale, CanvasCenter());
        }

        private void CenterImage()
        {
            if (_img == null) return;
            _offset = new PointF((_canvas.ClientSize.Width - _img.Width * _scale) / 2f,
                                 (_canvas.ClientSize.Height - _img.Height * _scale) / 2f);
        }

        /// <summary>Меняет масштаб так, чтобы точка под указателем осталась на месте.</summary>
        private void ZoomBy(float factor, PointF anchor)
        {
            if (_img == null) return;
            float old = _scale;
            float next = Math.Clamp(_scale * factor, MinScale, MaxScale);
            if (Math.Abs(next - old) < 0.0001f) return;

            // Координата картинки под якорем до зума — она же должна оказаться под ним после.
            float ix = (anchor.X - _offset.X) / old;
            float iy = (anchor.Y - _offset.Y) / old;
            _scale = next;
            _offset = new PointF(anchor.X - ix * _scale, anchor.Y - iy * _scale);

            _fitMode = false;
            ClampOffset();
            UpdateZoomLabel();
            _canvas.Invalidate();
        }

        /// <summary>Не даём утащить картинку полностью за пределы окна.</summary>
        private void ClampOffset()
        {
            if (_img == null) return;
            float w = _img.Width * _scale, h = _img.Height * _scale;
            float cw = _canvas.ClientSize.Width, ch = _canvas.ClientSize.Height;

            // Помещается целиком — центрируем по этой оси, иначе держим в пределах.
            _offset.X = w <= cw ? (cw - w) / 2f : Math.Clamp(_offset.X, cw - w, 0);
            _offset.Y = h <= ch ? (ch - h) / 2f : Math.Clamp(_offset.Y, ch - h, 0);
        }

        private void UpdateZoomLabel()
        {
            _lblZoom.Text = $"{Math.Round(_scale * 100)}%   ·   колесо — масштаб, двойной клик — вписать";
            _lblZoom.BringToFront();
        }

        private void Canvas_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Black);
            if (_img == null) return;
            // При увеличении — без сглаживания (видно реальные пиксели), при
            // уменьшении — качественная интерполяция, иначе мелкий текст рябит.
            e.Graphics.InterpolationMode = _scale >= 1f
                ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var dest = new RectangleF(_offset.X, _offset.Y, _img.Width * _scale, _img.Height * _scale);
            try { e.Graphics.DrawImage(_img, dest); } catch { }
        }

        private void Canvas_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _drag = true;
            _dragFrom = e.Location;
            _offsetFrom = _offset;
            _canvas.Cursor = Cursors.SizeAll;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_drag) return;
            _offset = new PointF(_offsetFrom.X + (e.X - _dragFrom.X), _offsetFrom.Y + (e.Y - _dragFrom.Y));
            _fitMode = false;
            ClampOffset();
            _canvas.Invalidate();
        }

        private void Canvas_MouseWheel(object sender, MouseEventArgs e)
        {
            ZoomBy(e.Delta > 0 ? 1.15f : 1f / 1.15f, e.Location);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _img?.Dispose(); } catch { }
            try { _hold?.Dispose(); } catch { }
            _img = null; _hold = null;
            base.OnFormClosed(e);
        }
    }
}
