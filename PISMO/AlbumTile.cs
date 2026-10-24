// ============================================================
//  AlbumTile — плитка альбома в ленте
// ============================================================
//
//  Несколько фотографий и роликов, отправленных одной пачкой, показываются
//  ОДНОЙ плиткой, как в Telegram и как это теперь сделано на телефоне.
//
//  Раньше каждое вложение было отдельным пузырём: десяток снимков занимал
//  ленту на десять прокруток, и понять, что это была одна отправка, было
//  нельзя. Теперь пачка собирается в плитку мозаикой: ряды разной длины, так
//  что пустых клеток не остаётся. Подпись и время — под сеткой, одни на всю
//  пачку.
//
//  Кто попал в одну плитку, решает Albums — по тем же признакам, что и на
//  телефоне: один отправитель, подряд, подпись только у первого. Отдельного
//  столбца в базе для этого не нужно, а значит, не нужно и ждать миграции от
//  администратора.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    internal sealed class AlbumTile : Panel
    {
        internal sealed class Item
        {
            public int MsgId;
            public string FileName;
            public bool IsVideo;
            /// <summary>Ключ кеша: в переписке «img», в каналах сервера «simg».</summary>
            public string CacheKind = "img";
        }

        private const int Pad = 10;
        private const int Gap = 3;

        private readonly List<Item> _items;
        private readonly Dictionary<int, Bitmap> _thumbs = new();
        private readonly string _caption;
        private readonly string _time;
        private readonly bool _isMine;
        private readonly int _readState;
        private readonly Font _capFont = new Font("Segoe UI", 10f);
        private readonly Font _timeFont = new Font("Segoe UI", 7.5f);

        /// <summary>Готовые прямоугольники ячеек — мозаика считается один раз.</summary>
        private readonly List<Rectangle> _cells = new();
        private int _gridBottom;
        private int _capHeight;

        /// <summary>Нажали ячейку — открыть именно её.</summary>
        public event Action<Item> CellClicked;

        public AlbumTile(List<Item> items, string caption, string time, bool isMine,
                         int readState, int width)
        {
            _items = items ?? new List<Item>();
            _caption = caption ?? "";
            _time = time ?? "";
            _isMine = isMine;
            _readState = readState;

            Width = width;
            BackColor = isMine ? Color.FromArgb(88, 101, 242) : Color.FromArgb(48, 51, 58);
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            Layout0();
        }

        /// <summary>
        /// Пересчитать раскладку и высоту.
        ///
        /// Ряды РАЗНОЙ длины — так плитка заполняется целиком. Раньше ряды были
        /// одинаковые, и хвост последнего оставался пустым: в пузыре зияла
        /// синяя проплешина в треть плитки. Теперь ячейки ряда делят ширину
        /// поровну, поэтому ряд из двух выше ряда из трёх — мозаика, как в
        /// телеграме.
        /// </summary>
        private void Layout0()
        {
            int gridW = Width - Pad * 2;
            _cells.Clear();
            int y = Pad;
            int i = 0;
            foreach (int count in RowSplit(_items.Count))
            {
                int side = (gridW - Gap * (count - 1)) / count;
                for (int c = 0; c < count && i < _items.Count; c++, i++)
                    _cells.Add(new Rectangle(Pad + c * (side + Gap), y, side, side));
                y += side + Gap;
            }
            _gridBottom = y;                     // с хвостовым зазором: отсюда подпись

            int h = _gridBottom - Gap;

            _capHeight = 0;
            if (!string.IsNullOrWhiteSpace(_caption))
            {
                using var g = CreateGraphics();
                var sz = g.MeasureString(_caption, _capFont, gridW);
                _capHeight = (int)Math.Ceiling(sz.Height) + 6;
                h += _capHeight;
            }

            h += 16 + Pad;                       // строка времени и нижнее поле
            Height = h;
        }

        /// <summary>Миниатюра ячейки — из кеша страницы; чего нет, то дорисуется
        /// после фоновой выборки и следующей перерисовки ленты.</summary>
        private Bitmap ThumbFor(Item it, int side)
        {
            if (_thumbs.TryGetValue(it.MsgId, out var cached)) return cached;
            Bitmap bmp = null;
            try
            {
                // У ролика картинка сообщения — это его обложка (см.
                // VideoCover), поэтому пробуем её и для видео: раньше в пачке
                // ролики были просто тёмными квадратами.
                var bytes = MediaCache.Get(it.MsgId, it.CacheKind, it.FileName);
                if (bytes != null && bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes);
                    using var src = Image.FromStream(ms);
                    bmp = CropSquare(src, side);
                }
            }
            catch { bmp = null; }
            _thumbs[it.MsgId] = bmp;
            return bmp;
        }

        /// <summary>Квадрат по центру — чтобы ячейки не разъезжались по высоте.</summary>
        private static Bitmap CropSquare(Image src, int side)
        {
            if (side <= 0) side = 1;
            int cut = Math.Min(src.Width, src.Height);
            int sx = (src.Width - cut) / 2, sy = (src.Height - cut) / 2;
            var dst = new Bitmap(side, side);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, side, side),
                        new Rectangle(sx, sy, cut, cut), GraphicsUnit.Pixel);
            return dst;
        }

        /// <summary>
        /// Длины рядов плитки: лишних клеток не остаётся. До трёх — один ряд,
        /// дальше поровну по рядам из двух-четырёх ячеек.
        /// </summary>
        private static List<int> RowSplit(int n)
        {
            var res = new List<int>();
            if (n <= 0) return res;
            if (n <= 3) { res.Add(n); return res; }
            int rows = (n + 2) / 3;
            int bas = n / rows, rem = n % rows;
            for (int i = 0; i < rows; i++) res.Add(i < rem ? bas + 1 : bas);
            return res;
        }

        private Rectangle CellRect(int index) =>
            index >= 0 && index < _cells.Count ? _cells[index] : Rectangle.Empty;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var br = new SolidBrush(BackColor))
                g.FillRoundedRectangle(br, 0, 0, Width - 1, Height - 1, 10);

            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                var rc = CellRect(i);
                var thumb = ThumbFor(it, rc.Width);
                if (thumb != null)
                {
                    g.DrawImage(thumb, rc);
                }
                else
                {
                    using var dark = new SolidBrush(Color.FromArgb(40, 42, 48));
                    g.FillRectangle(dark, rc);
                }

                if (it.IsVideo)
                {
                    // Треугольник проигрывания — ролик виден сразу, как в Telegram.
                    int s = Math.Min(34, rc.Width / 3);
                    var c0 = new Point(rc.X + rc.Width / 2, rc.Y + rc.Height / 2);
                    using var disc = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
                    g.FillEllipse(disc, c0.X - s / 2, c0.Y - s / 2, s, s);
                    var tri = new[]
                    {
                        new Point(c0.X - s / 6, c0.Y - s / 4),
                        new Point(c0.X - s / 6, c0.Y + s / 4),
                        new Point(c0.X + s / 3, c0.Y),
                    };
                    g.FillPolygon(Brushes.White, tri);
                }
                else if (thumb == null)
                {
                    using var f = new Font("Segoe UI", 7.5f);
                    TextRenderer.DrawText(g, "загрузка…", f, rc,
                        Color.FromArgb(150, 255, 255, 255),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }

            int y = _gridBottom;

            if (_capHeight > 0)
            {
                var rc = new RectangleF(Pad, y + 2, Width - Pad * 2, _capHeight);
                using var br = new SolidBrush(_isMine ? Color.White : Color.FromArgb(219, 222, 225));
                g.DrawString(_caption, _capFont, br, rc);
                y += _capHeight;
            }

            string stamp = _time + (_readState == 2 ? "  ✓✓" : _readState == 1 ? "  ✓" : "");
            var tsz = g.MeasureString(stamp, _timeFont);
            using (var br = new SolidBrush(_isMine ? Color.FromArgb(220, 255, 255, 255)
                                                   : Color.FromArgb(150, 153, 160)))
                g.DrawString(stamp, _timeFont, br, Width - Pad - tsz.Width, y + 1);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < _items.Count; i++)
            {
                if (CellRect(i).Contains(e.Location)) { CellClicked?.Invoke(_items[i]); return; }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var kv in _thumbs) { try { kv.Value?.Dispose(); } catch { } }
                _thumbs.Clear();
                try { _capFont.Dispose(); _timeFont.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
