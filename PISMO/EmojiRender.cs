using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace PISMO
{
    /// <summary>
    /// Растеризатор ЦВЕТНЫХ эмодзи (2.1). GDI+ (WinForms) рисует Segoe UI Emoji
    /// монохромным контуром — цветные COLR-глифы умеет только DirectWrite.
    /// Поэтому рендерим через WPF FormattedText (DirectWrite под капотом) в
    /// RenderTargetBitmap и конвертируем в GDI Bitmap. Результат кешируется —
    /// каждый эмодзи каждого размера растеризуется один раз за сессию.
    /// Фолбэк (если WPF-рендер упал) — обычный монохромный GDI-глиф.
    /// </summary>
    internal static class EmojiRender
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, Bitmap> _cache = new();

        /// <summary>Цветная картинка эмодзи высотой px (кешируется).</summary>
        public static Bitmap Get(string emoji, int px)
        {
            if (string.IsNullOrEmpty(emoji) || px <= 0) return null;
            string key = emoji + "|" + px;
            lock (_lock) { if (_cache.TryGetValue(key, out var hit)) return hit; }

            Bitmap bmp = null;
            try { bmp = RenderWpf(emoji, px); } catch { }
            if (bmp == null)
                try { bmp = RenderGdi(emoji, px); } catch { }

            lock (_lock) { if (bmp != null) _cache[key] = bmp; }
            return bmp;
        }

        private static Bitmap RenderWpf(string emoji, int px)
        {
            // ВАЖНО: не FormattedText/DrawText — этот путь рисует глиф ОДНОЙ
            // кистью (получались белые силуэты). Цветные COLR-глифы WPF отдаёт
            // только через текстовые ЭЛЕМЕНТЫ — рендерим TextBlock в битмап.
            var tb = new System.Windows.Controls.TextBlock
            {
                Text = emoji,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"),
                FontSize = px,
                Foreground = System.Windows.Media.Brushes.Black,
                Background = System.Windows.Media.Brushes.Transparent
            };
            tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            tb.Arrange(new System.Windows.Rect(tb.DesiredSize));
            tb.UpdateLayout();

            int w = Math.Max(1, (int)Math.Ceiling(tb.DesiredSize.Width));
            int h = Math.Max(1, (int)Math.Ceiling(tb.DesiredSize.Height));

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(tb);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            using var tmp = new Bitmap(ms);
            var result = new Bitmap(tmp);   // отвязываем Bitmap от потока

            // Сторож: пустой кадр (глифа нет) → null, сработает GDI-фолбэк.
            if (IsBlank(result)) { result.Dispose(); return null; }
            return result;
        }

        private static bool IsBlank(Bitmap b)
        {
            try
            {
                for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 8))
                    for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 8))
                        if (b.GetPixel(x, y).A > 8) return false;
            }
            catch { return false; }
            return true;
        }

        // Монохромный фолбэк — лучше, чем ничего (например, WPF не поднялся).
        // Средне-серый: читается и на тёмном пикере, и на светлом системном меню.
        private static Bitmap RenderGdi(string emoji, int px)
        {
            var bmp = new Bitmap(px + 6, px + 6);
            using var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var f = new Font("Segoe UI Emoji", px * 0.72f);
            using var br = new SolidBrush(Color.FromArgb(128, 131, 138));
            g.DrawString(emoji, f, br, -1, 1);
            return bmp;
        }
    }
}
