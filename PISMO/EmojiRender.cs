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
            var typeface = new System.Windows.Media.Typeface(
                new System.Windows.Media.FontFamily("Segoe UI Emoji"),
                System.Windows.FontStyles.Normal,
                System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal);
            var ft = new System.Windows.Media.FormattedText(
                emoji,
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                px,
                System.Windows.Media.Brushes.White,
                1.0);   // pixelsPerDip: рендерим в 96dpi-битмап, масштабирует GDI

            int w = Math.Max(1, (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace));
            int h = Math.Max(1, (int)Math.Ceiling(ft.Height));

            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawText(ft, new System.Windows.Point(0, 0));

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(visual);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            using var tmp = new Bitmap(ms);
            return new Bitmap(tmp);   // отвязываем Bitmap от потока
        }

        // Монохромный фолбэк — лучше, чем ничего (например, WPF не поднялся).
        private static Bitmap RenderGdi(string emoji, int px)
        {
            var bmp = new Bitmap(px + 6, px + 6);
            using var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var f = new Font("Segoe UI Emoji", px * 0.72f);
            g.DrawString(emoji, f, Brushes.White, -1, 1);
            return bmp;
        }
    }
}
