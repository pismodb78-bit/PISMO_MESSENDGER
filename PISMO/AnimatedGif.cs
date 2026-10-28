using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// PictureBox с анимацией GIF БЕЗ авто-анимации PictureBox/ImageAnimator
    /// (он падает в GDI+). Каждый кадр заранее рендерится в отдельный
    /// одно-кадровый Bitmap, а таймер их переключает.
    /// </summary>
    public static class AnimatedGif
    {
        /// <summary>Больше кадров не держим: длинную гифку прореживаем.</summary>
        private const int MaxFrames = 90;

        /// <summary>Готовые кадры гифки — под размер показа.</summary>
        public sealed class Frames : IDisposable
        {
            public Bitmap[] Images;
            public int[] Delays;
            public Size Size;

            public void Dispose()
            {
                if (Images == null) return;
                foreach (var f in Images) { try { f?.Dispose(); } catch { } }
                Images = null;
            }
        }

        /// <summary>
        /// Раскладывает гифку на кадры. ТЯЖЁЛАЯ работа — звать в фоне.
        ///
        /// Раньше это шло на потоке окна, и каждый кадр хранился в исходном
        /// размере: гифка 480×270 на сотню кадров — полсотни мегабайт на одно
        /// сообщение, и на время раскладки окно стояло. Канал с десятком
        /// гифок, пролистанный вверх, подтормаживал всё приложение.
        /// </summary>
        public static Frames Decode(byte[] gifBytes, int maxW, int maxH)
        {
            if (gifBytes == null || gifBytes.Length == 0) return null;
            using var ms = new MemoryStream(gifBytes);
            using var src = Image.FromStream(ms);

            double ratio = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
            if (ratio > 1) ratio = 1;
            var size = new Size(Math.Max(1, (int)(src.Width * ratio)),
                                Math.Max(1, (int)(src.Height * ratio)));

            int count = 1;
            System.Drawing.Imaging.FrameDimension dim = null;
            try
            {
                dim = new System.Drawing.Imaging.FrameDimension(src.FrameDimensionsList[0]);
                count = Math.Max(1, src.GetFrameCount(dim));
            }
            catch { }

            int[] delays;
            try
            {
                var prop = src.GetPropertyItem(0x5100); // PropertyTagFrameDelay
                delays = new int[count];
                for (int i = 0; i < count; i++)
                    delays[i] = Math.Max(50, BitConverter.ToInt32(prop.Value, i * 4) * 10);
            }
            catch { delays = new int[count]; Array.Fill(delays, 100); }

            // Длинная гифка — каждый N-й кадр, с суммарной задержкой пропущенных:
            // скорость та же, памяти в разы меньше.
            int step = Math.Max(1, (count + MaxFrames - 1) / MaxFrames);
            int kept = (count + step - 1) / step;
            var res = new Frames { Images = new Bitmap[kept], Delays = new int[kept], Size = size };
            try
            {
                for (int k = 0, i = 0; i < count && k < kept; k++, i += step)
                {
                    if (dim != null) src.SelectActiveFrame(dim, i);
                    var f = new Bitmap(size.Width, size.Height,
                        System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(f))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(src, 0, 0, size.Width, size.Height);
                    }
                    res.Images[k] = f;
                    int d = 0;
                    for (int j = i; j < Math.Min(count, i + step); j++) d += delays[j];
                    res.Delays[k] = Math.Max(50, d);
                }
            }
            catch
            {
                // Что разложили — то и покажем; совсем ничего — нечего.
                if (res.Images.Length == 0 || res.Images[0] == null) { res.Dispose(); return null; }
            }
            return res;
        }

        /// <summary>Создаёт PictureBox с анимированным GIF из байтов.
        /// Вписывает в maxW×maxH с сохранением пропорций.</summary>
        public static PictureBox Create(byte[] gifBytes, int maxW, int maxH)
        {
            Frames f = null;
            try { f = Decode(gifBytes, maxW, maxH); } catch { }
            return f != null ? Show(f) : new PictureBox { BackColor = Color.Transparent };
        }

        /// <summary>PictureBox из готовых кадров. Кадрами владеет он: умрёт — освободит.</summary>
        public static PictureBox Show(Frames f)
        {
            var pb = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Transparent,
                Size = f.Size,
            };
            var frames = f.Images;
            var delays = f.Delays;
            int count = 0;
            while (count < frames.Length && frames[count] != null) count++;
            if (count == 0) return pb;

            pb.Image = frames[0];
            if (count > 1)
            {
                int idx = 0;
                var timer = new System.Windows.Forms.Timer { Interval = delays[0] };
                bool disposed = false;
                timer.Tick += (s, e) =>
                {
                    if (disposed || pb.IsDisposed) { timer.Stop(); return; }
                    // Вне экрана кадры не крутим: каждый шаг — это перерисовка,
                    // а гифок в длинной ленте бывают десятки.
                    if (!OnScreen(pb)) { timer.Interval = 400; return; }
                    idx = (idx + 1) % count;
                    pb.Image = frames[idx];
                    timer.Interval = delays[idx];
                };
                timer.Start();
                pb.Disposed += (s, e) =>
                {
                    disposed = true; timer.Stop(); timer.Dispose();
                    f.Dispose();
                };
            }
            else
            {
                pb.Disposed += (s, e) => f.Dispose();
            }
            return pb;
        }

        /// <summary>
        /// Виден ли контрол на экране: не скрыт, окно не свёрнуто и хоть краем
        /// попадает в видимую часть всех своих родителей (лента прокручена —
        /// пузырь за её краем считается невидимым).
        /// </summary>
        public static bool OnScreen(Control c)
        {
            try
            {
                if (c == null || c.IsDisposed || !c.IsHandleCreated || !c.Visible) return false;
                var form = c.FindForm();
                if (form == null || !form.Visible || form.WindowState == FormWindowState.Minimized) return false;
                Rectangle r = c.RectangleToScreen(c.ClientRectangle);
                for (var p = c.Parent; p != null; p = p.Parent)
                {
                    r.Intersect(p.RectangleToScreen(p.ClientRectangle));
                    if (r.IsEmpty) return false;
                }
                return true;
            }
            catch { return true; }
        }
    }
}
