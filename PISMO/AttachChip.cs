// ============================================================
//  AttachChip — плитка выбранного вложения над полем ввода
// ============================================================
//
//  То же, что на телефоне и в Telegram: квадратная плитка со скруглёнными
//  углами, номер по порядку отправки слева сверху и крестик справа сверху.
//  У снимка — сам снимок, у ролика — его кадр со стрелкой, у прочего файла —
//  цветная плашка с расширением.
//
//  Раньше это была голая миниатюра, и НАЖАТИЕ ПО НЕЙ её УДАЛЯЛО: посмотреть
//  выбранное перед отправкой было нельзя, а промахнуться и потерять снимок —
//  проще простого. Теперь нажатие открывает предпросмотр (снимок — в
//  редакторе, ролик — в плеере), а убирает только крестик.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    internal sealed class AttachChip : Control
    {
        private readonly Bitmap _thumb;
        private readonly string _ext;
        private readonly bool _video;
        private readonly int _number;
        private bool _hoverX;

        /// <summary>Нажали крестик — убрать вложение.</summary>
        public event Action Removed;

        /// <summary>Нажали по самой плитке — открыть предпросмотр.</summary>
        public event Action Opened;

        public AttachChip(PendingAttachment a, int number, int side)
        {
            _number = number;
            _ext = Path.GetExtension(a.FileName ?? "").TrimStart('.').ToUpperInvariant();
            _video = a.Kind == AttachKind.File
                     && MediaPlayerForm.IsVideo(_ext.ToLowerInvariant());
            Size = new Size(side, side);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            // Картинка для плитки: снимок или кадр ролика, обрезанные квадратом.
            byte[] src = a.Kind == AttachKind.Image || a.Kind == AttachKind.Gif ? a.Data
                       : _video ? a.Cover : null;
            if (src is { Length: > 0 })
            {
                try
                {
                    using var ms = new MemoryStream(src);
                    using var img = Image.FromStream(ms);
                    _thumb = CropSquare(img, side);
                }
                catch { _thumb = null; }
            }

            var tip = new ToolTip();
            tip.SetToolTip(this, (a.FileName ?? "") + " — нажмите, чтобы посмотреть");
        }

        private Rectangle CloseRect => new Rectangle(Width - 24, 4, 20, 20);

        private static Bitmap CropSquare(Image src, int side)
        {
            int cut = Math.Min(src.Width, src.Height);
            int sx = (src.Width - cut) / 2, sy = (src.Height - cut) / 2;
            var bmp = new Bitmap(side, side);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, side, side),
                        new Rectangle(sx, sy, cut, cut), GraphicsUnit.Pixel);
            return bmp;
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Color.FromArgb(47, 49, 54));

            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Round(box, 8))
            {
                var save = g.Save();
                g.SetClip(path);
                if (_thumb != null)
                {
                    g.DrawImage(_thumb, 0, 0, Width, Height);
                }
                else
                {
                    // Прочий файл — цветная плашка с расширением.
                    Color bg = _ext.ToLowerInvariant() switch
                    {
                        "pdf" => Color.FromArgb(220, 53, 53),
                        "doc" or "docx" => Color.FromArgb(41, 86, 163),
                        "xls" or "xlsx" => Color.FromArgb(32, 120, 62),
                        "zip" or "rar" or "7z" => Color.FromArgb(140, 90, 20),
                        _ => Color.FromArgb(88, 101, 242),
                    };
                    using var br = new SolidBrush(bg);
                    g.FillRectangle(br, box);
                    string label = _ext.Length > 0 ? _ext : "FILE";
                    using var f = new Font("Segoe UI Black", label.Length > 3 ? 10f : 13f, FontStyle.Bold);
                    using var sf = new StringFormat
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(label, f, Brushes.White, new RectangleF(0, 0, Width, Height), sf);
                }
                g.Restore(save);
            }

            // Ролик — стрелка посередине кадра.
            if (_video)
            {
                int s = 26, cx = Width / 2, cy = Height / 2;
                using var disc = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
                g.FillEllipse(disc, cx - s / 2, cy - s / 2, s, s);
                g.FillPolygon(Brushes.White, new[]
                {
                    new Point(cx - 4, cy - 7), new Point(cx - 4, cy + 7), new Point(cx + 7, cy),
                });
            }

            // Номер по порядку отправки.
            using (var nb = new SolidBrush(Color.FromArgb(30, 143, 225)))
                g.FillEllipse(nb, 4, 4, 20, 20);
            using (var f3 = new Font("Segoe UI Semibold", 8f, FontStyle.Bold))
            using (var sf2 = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(_number.ToString(), f3, Brushes.White, new RectangleF(4, 4, 20, 20), sf2);

            // Крестик — убрать.
            var x = CloseRect;
            using (var xb = new SolidBrush(_hoverX ? Color.FromArgb(220, 200, 40, 40)
                                                   : Color.FromArgb(160, 0, 0, 0)))
                g.FillEllipse(xb, x);
            using (var pen = new Pen(Color.White, 1.8f))
            {
                g.DrawLine(pen, x.X + 6, x.Y + 6, x.Right - 6, x.Bottom - 6);
                g.DrawLine(pen, x.Right - 6, x.Y + 6, x.X + 6, x.Bottom - 6);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = CloseRect.Contains(e.Location);
            if (over != _hoverX) { _hoverX = over; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverX) { _hoverX = false; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (CloseRect.Contains(e.Location)) Removed?.Invoke();
            else Opened?.Invoke();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _thumb?.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }
}
