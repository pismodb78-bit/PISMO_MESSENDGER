using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Плоский ползунок в стиле приложения — замена системному TrackBar.
    ///
    /// Системный в тёмном окне выглядит инородно (светлый объёмный бегунок,
    /// серый жёлоб, лишние 40px высоты) и вдобавок перехватывает колесо мыши:
    /// прокрутка панели над ним меняла громкость. Этот ползунок колесо не
    /// обрабатывает вовсе, поэтому событие уходит родительской панели.
    /// </summary>
    internal sealed class FlatSlider : Control
    {
        private const int ThumbR = 7;    // радиус бегунка
        private const int TrackH = 4;    // толщина жёлоба

        private int _min, _max = 100, _value;
        private bool _drag, _hover;

        public FlatSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 20;
            Cursor = Cursors.Hand;
            TabStop = false;
            BackColor = Color.FromArgb(47, 49, 54);
        }

        public event EventHandler ValueChanged;

        public Color AccentColor { get; set; } = Color.FromArgb(88, 101, 242);
        public Color TrackColor { get; set; } = Color.FromArgb(70, 74, 82);

        public int Minimum
        {
            get => _min;
            set { _min = value; if (_max <= _min) _max = _min + 1; _value = Math.Clamp(_value, _min, _max); Invalidate(); }
        }

        public int Maximum
        {
            get => _max;
            set { _max = Math.Max(value, _min + 1); _value = Math.Clamp(_value, _min, _max); Invalidate(); }
        }

        public int Value
        {
            get => _value;
            set
            {
                int v = Math.Clamp(value, _min, _max);
                if (v == _value) return;
                _value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private int TrackLeft => ThumbR;
        private int TrackRight => Math.Max(ThumbR + 1, Width - ThumbR);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int cy = Height / 2;
            int x0 = TrackLeft, x1 = TrackRight;
            float t = (float)(_value - _min) / (_max - _min);
            int cx = x0 + (int)Math.Round((x1 - x0) * t);

            using (var br = new SolidBrush(TrackColor))
            using (var p = Bar(x0, cy - TrackH / 2, x1 - x0, TrackH))
                g.FillPath(br, p);

            if (cx > x0)
                using (var br = new SolidBrush(AccentColor))
                using (var p = Bar(x0, cy - TrackH / 2, cx - x0, TrackH))
                    g.FillPath(br, p);

            // Лёгкая тень под бегунком, чтобы он читался и на залитой части жёлоба.
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillEllipse(sh, cx - ThumbR, cy - ThumbR + 1, ThumbR * 2, ThumbR * 2);
            using (var th = new SolidBrush(_drag || _hover ? Color.White : Color.FromArgb(238, 239, 242)))
                g.FillEllipse(th, cx - ThumbR, cy - ThumbR, ThumbR * 2, ThumbR * 2);
        }

        private static GraphicsPath Bar(int x, int y, int w, int h)
        {
            var path = new GraphicsPath();
            if (w <= 0) { path.AddRectangle(new Rectangle(x, y, 1, h)); return path; }
            if (w <= h) { path.AddEllipse(x, y, Math.Max(1, w), h); return path; }
            path.AddArc(x, y, h, h, 90, 180);
            path.AddArc(x + w - h, y, h, h, 270, 180);
            path.CloseFigure();
            return path;
        }

        private void SetFromX(int x)
        {
            int x0 = TrackLeft, x1 = TrackRight;
            float t = (float)(x - x0) / (x1 - x0);
            Value = _min + (int)Math.Round(Math.Clamp(t, 0f, 1f) * (_max - _min));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _drag = true; SetFromX(e.X); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag) SetFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = false; Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    }
}
