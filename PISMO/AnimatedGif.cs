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
        /// <summary>Создаёт PictureBox с анимированным GIF из байтов.
        /// Вписывает в maxW×maxH с сохранением пропорций.</summary>
        public static PictureBox Create(byte[] gifBytes, int maxW, int maxH)
        {
            var pb = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Transparent
            };

            Image src;
            try { src = Image.FromStream(new MemoryStream(gifBytes.ToArray())); }
            catch { return pb; }

            double ratio = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
            if (ratio > 1) ratio = 1;
            pb.Size = new Size(Math.Max(1, (int)(src.Width * ratio)),
                               Math.Max(1, (int)(src.Height * ratio)));

            try
            {
                var dim = new System.Drawing.Imaging.FrameDimension(src.FrameDimensionsList[0]);
                int count = Math.Max(1, src.GetFrameCount(dim));

                int[] delays;
                try
                {
                    var prop = src.GetPropertyItem(0x5100); // PropertyTagFrameDelay
                    delays = new int[count];
                    for (int i = 0; i < count; i++)
                        delays[i] = Math.Max(50, BitConverter.ToInt32(prop.Value, i * 4) * 10);
                }
                catch { delays = new int[count]; Array.Fill(delays, 100); }

                var frames = new Bitmap[count];
                for (int i = 0; i < count; i++)
                {
                    src.SelectActiveFrame(dim, i);
                    var f = new Bitmap(src.Width, src.Height,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(f)) g.DrawImage(src, 0, 0, src.Width, src.Height);
                    frames[i] = f;
                }
                src.Dispose();

                pb.Image = frames[0];
                if (count > 1)
                {
                    int idx = 0;
                    var timer = new System.Windows.Forms.Timer { Interval = delays[0] };
                    bool disposed = false;
                    timer.Tick += (s, e) =>
                    {
                        if (disposed || pb.IsDisposed) { timer.Stop(); return; }
                        idx = (idx + 1) % count;
                        if (frames[idx] != null) pb.Image = frames[idx];
                        timer.Interval = delays[idx];
                    };
                    timer.Start();
                    pb.Disposed += (s, e) =>
                    {
                        disposed = true; timer.Stop(); timer.Dispose();
                        foreach (var f in frames) { try { f?.Dispose(); } catch { } }
                    };
                }
                else
                {
                    pb.Disposed += (s, e) => { foreach (var f in frames) { try { f?.Dispose(); } catch { } } };
                }
            }
            catch
            {
                try { pb.Image = src; } catch { }
            }
            return pb;
        }
    }
}
