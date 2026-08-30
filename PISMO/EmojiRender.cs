using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Растеризатор ЦВЕТНЫХ эмодзи (2.1.2). Порядок источников:
    ///   1) дисковый кеш Twemoji-картинок (%LOCALAPPDATA%\PISMO\emoji72) — как
    ///      в Discord, гарантированно цветные и одинаковые у всех;
    ///   2) WPF TextBlock/DirectWrite — если ДАЛ ЦВЕТ (на части машин WPF рисует
    ///      COLR-глифы силуэтом — такое отбраковываем проверкой HasColor);
    ///   3) фоновая докачка Twemoji с CDN: пока качается, показывается серый
    ///      глиф, по готовности поднимается событие Loaded — UI перерисовывает.
    /// Всё кешируется; повторные запуски работают полностью офлайн (диск).
    /// </summary>
    internal static class EmojiRender
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, Bitmap> _cache = new();     // "emoji|px" -> готовая картинка
        private static readonly HashSet<string> _fetching = new();             // эмодзи в докачке
        private static readonly HashSet<string> _unavailable = new();          // 404 на CDN — не долбим повторно
        private static HttpClient _http;

        /// <summary>Эмодзи докачан с CDN — перерисуйте свои контролы (событие
        /// приходит с ФОНОВОГО потока: в обработчике нужен BeginInvoke).</summary>
        public static event Action<string> Loaded;

        private static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PISMO", "emoji72");

        /// <summary>Картинка эмодзи высотой px. Может вернуть временный серый
        /// глиф — когда Twemoji докачается, придёт Loaded и Get вернёт цветной.</summary>
        public static Bitmap Get(string emoji, int px)
        {
            if (string.IsNullOrEmpty(emoji) || px <= 0) return null;
            string key = emoji + "|" + px;
            lock (_lock) { if (_cache.TryGetValue(key, out var hit)) return hit; }

            // 1) Twemoji с диска.
            Bitmap bmp = null;
            try { bmp = LoadTwemojiFromDisk(emoji, px); } catch { }

            // 2) DirectWrite, но только если получился ЦВЕТ.
            if (bmp == null)
            {
                Bitmap wpf = null;
                try { wpf = RenderWpf(emoji, px); } catch { }
                if (wpf != null && HasColor(wpf)) bmp = wpf;
                else
                {
                    // серый глиф — временно; параллельно тянем Twemoji
                    bmp = wpf;   // силуэт лучше, чем пусто
                    if (bmp == null)
                        try { bmp = RenderGdi(emoji, px); } catch { }
                    QueueFetch(emoji);
                }
            }

            lock (_lock) { if (bmp != null) _cache[key] = bmp; }
            return bmp;
        }

        // ── Twemoji ───────────────────────────────────────────────────────
        /// <summary>Имя файла Twemoji: кодпоинты через '-', без FE0F (вариант
        /// с FE0F пробуем при докачке как запасной).</summary>
        private static string TwemojiCode(string emoji, bool keepVs16)
        {
            var parts = new List<string>();
            for (int i = 0; i < emoji.Length;)
            {
                int cp = char.ConvertToUtf32(emoji, i);
                i += char.IsSurrogatePair(emoji, i) ? 2 : 1;
                if (cp == 0xFE0F && !keepVs16) continue;
                parts.Add(cp.ToString("x"));
            }
            return string.Join("-", parts);
        }

        private static Bitmap LoadTwemojiFromDisk(string emoji, int px)
        {
            string path = Path.Combine(CacheDir, TwemojiCode(emoji, keepVs16: false) + ".png");
            if (!File.Exists(path)) return null;
            using var src = new Bitmap(path);
            return ScaleSquare(src, px);
        }

        private static Bitmap ScaleSquare(Bitmap src, int px)
        {
            var bmp = new Bitmap(px, px);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, px, px);
            return bmp;
        }

        private static void QueueFetch(string emoji)
        {
            lock (_lock)
            {
                if (_fetching.Contains(emoji) || _unavailable.Contains(emoji)) return;
                _fetching.Add(emoji);
            }
            System.Threading.Tasks.Task.Run(async () =>
            {
                bool ok = false;
                try
                {
                    _http ??= MakeHttp();
                    Directory.CreateDirectory(CacheDir);
                    // Пробуем оба варианта имени (Twemoji непоследователен с FE0F).
                    foreach (bool keep in new[] { false, true })
                    {
                        string code = TwemojiCode(emoji, keep);
                        if (code.Length == 0) break;
                        try
                        {
                            var bytes = await _http.GetByteArrayAsync(
                                "https://cdn.jsdelivr.net/gh/jdecked/twemoji@14.1.2/assets/72x72/" + code + ".png");
                            if (bytes is { Length: > 100 })
                            {
                                // Файл всегда под именем БЕЗ fe0f — так его найдёт LoadTwemojiFromDisk.
                                File.WriteAllBytes(Path.Combine(CacheDir, TwemojiCode(emoji, false) + ".png"), bytes);
                                ok = true;
                                break;
                            }
                        }
                        catch { /* 404/сеть — пробуем второй вариант */ }
                    }
                }
                catch { }
                lock (_lock)
                {
                    _fetching.Remove(emoji);
                    if (!ok) { _unavailable.Add(emoji); return; }
                    // Сбрасываем серые версии всех размеров этого эмодзи.
                    foreach (var k in _cache.Keys.Where(k => k.StartsWith(emoji + "|")).ToList())
                        _cache.Remove(k);
                }
                try { Loaded?.Invoke(emoji); } catch { }
            });
        }

        private static HttpClient MakeHttp()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13
                }
            };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PISMO-Emoji");
            return http;
        }

        /// <summary>
        /// Есть ли в строке хоть один эмодзи.
        ///
        /// Нужно, чтобы не гонять через WPF ВСЕ сообщения: обычный текст
        /// прекрасно живёт в поле ввода, которое к тому же можно выделять.
        /// Картинкой рисуем только те, где иначе будет монохромный контур.
        /// </summary>
        public static bool ContainsEmoji(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // Суррогатная пара — почти наверняка эмодзи: сюда попадают
                // все плоскости выше базовой, включая 1F300–1FAFF.
                if (char.IsHighSurrogate(c)) return true;
                // Символьные блоки, у которых есть цветные начертания:
                // разное типографское (2190–2BFF) и «Dingbats».
                if (c >= 0x2190 && c <= 0x2BFF) return true;
                if (c == 0xFE0F) return true;   // селектор эмодзи-начертания
            }
            return false;
        }

        /// <summary>Эмодзи и его ширина — кусок разобранного сообщения.</summary>
        private readonly record struct Atom(string Text, bool IsEmoji, int Width, bool Break);

        /// <summary>Начинается ли в этой позиции эмодзи, и какой длины кластер.</summary>
        private static int EmojiClusterLength(string s, int i)
        {
            if (i >= s.Length) return 0;
            int start = i, len = 0;
            bool first = true;
            while (i < s.Length)
            {
                int cp = char.ConvertToUtf32(s, i);
                int size = char.IsSurrogatePair(s, i) ? 2 : 1;
                bool baseEmoji = cp >= 0x1F000 || (cp >= 0x2190 && cp <= 0x2BFF) || cp == 0xA9 || cp == 0xAE;
                bool joiner = cp == 0xFE0F || cp == 0x200D || cp == 0x20E3
                              || (cp >= 0x1F3FB && cp <= 0x1F3FF);
                if (first)
                {
                    if (!baseEmoji) return 0;
                    first = false;
                }
                else if (!joiner)
                {
                    // Продолжаем кластер только после соединителя.
                    if (len == 0 || s[start + len - 1] != '\u200D') break;
                    if (!baseEmoji) break;
                }
                i += size; len += size;
            }
            return len;
        }

        /// <summary>
        /// Рисует текст сообщения картинкой: обычные слова — средствами GDI+,
        /// эмодзи — теми же картинками, что и чипы реакций.
        ///
        /// ПОЧЕМУ НЕ ЦЕЛИКОМ ЧЕРЕЗ WPF. Первая попытка так и делала — и дала
        /// белый силуэт. У Get цепочка из трёх шагов: цветной Twemoji с диска,
        /// затем DirectWrite, но ТОЛЬКО если он реально вернул цвет, иначе
        /// силуэт временно и докачка цветного в фоне. На машинах, где
        /// DirectWrite цвета не даёт, весь цвет приходит из этих загруженных
        /// картинок — а голый WPF о них не знает.
        ///
        /// Пока картинка эмодзи не докачалась, на её месте силуэт; событие
        /// Loaded говорит вызывающему, что пора перерисовать, — так же
        /// устроены чипы реакций.
        /// </summary>
        public static Bitmap RenderMessage(string text, Font font, Color fore, Color back, int maxWidthPx)
        {
            if (string.IsNullOrEmpty(text) || font == null || maxWidthPx <= 8) return null;
            try
            {
                int em = font.Height;                       // эмодзи ростом со строку
                const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

                // ── разбор на «атомы»: слова, эмодзи, переводы строк ──────────
                var atoms = new List<Atom>();
                using (var probe = new Bitmap(1, 1))
                using (var g0 = Graphics.FromImage(probe))
                {
                    var word = new System.Text.StringBuilder();
                    void FlushWord()
                    {
                        if (word.Length == 0) return;
                        string w = word.ToString(); word.Clear();
                        int wd = TextRenderer.MeasureText(g0, w, font, new Size(int.MaxValue, int.MaxValue), F).Width;
                        atoms.Add(new Atom(w, false, wd, false));
                    }
                    for (int i = 0; i < text.Length;)
                    {
                        if (text[i] == '\n') { FlushWord(); atoms.Add(new Atom("", false, 0, true)); i++; continue; }
                        int el = EmojiClusterLength(text, i);
                        if (el > 0)
                        {
                            FlushWord();
                            atoms.Add(new Atom(text.Substring(i, el), true, em, false));
                            i += el;
                            continue;
                        }
                        word.Append(text[i]);
                        // Пробел — граница переноса, но остаётся в слове.
                        if (text[i] == ' ') FlushWord();
                        i++;
                    }
                    FlushWord();
                }
                if (atoms.Count == 0) return null;

                // ── раскладка по строкам ─────────────────────────────────────
                var lines = new List<List<Atom>>();
                var cur = new List<Atom>();
                int curW = 0;
                foreach (var a in atoms)
                {
                    if (a.Break) { lines.Add(cur); cur = new List<Atom>(); curW = 0; continue; }
                    if (curW > 0 && curW + a.Width > maxWidthPx)
                    {
                        lines.Add(cur); cur = new List<Atom>(); curW = 0;
                    }
                    cur.Add(a); curW += a.Width;
                }
                lines.Add(cur);

                int width = 0;
                foreach (var line in lines)
                {
                    int w = 0;
                    foreach (var a in line) w += a.Width;
                    if (w > width) width = w;
                }
                width = Math.Max(1, Math.Min(width, maxWidthPx));
                int lineH = Math.Max(em, font.Height);
                int height = Math.Max(1, lines.Count * lineH);

                // ── отрисовка ────────────────────────────────────────────────
                // Прозрачный фон — для превью в списке чатов: карточка меняет
                // цвет при наведении, и картинка с запечённым фоном показывала бы
                // на ней прямоугольник чужого цвета. С прозрачностью PictureBox
                // подставляет фон карточки, и подсветка работает как раньше.
                bool transparent = back.A == 0;
                var bmp = new Bitmap(width, height);
                using (var g = Graphics.FromImage(bmp))
                {
                    if (transparent) g.TextRenderingHint =
                        System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    else g.Clear(back);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    int y = 0;
                    foreach (var line in lines)
                    {
                        int x = 0;
                        foreach (var a in line)
                        {
                            if (a.IsEmoji)
                            {
                                var img = Get(a.Text, em);
                                if (img != null) g.DrawImage(img, new Rectangle(x, y, em, em));
                            }
                            else if (transparent)
                            {
                                // GDI (TextRenderer) рисует непрозрачно и затирает
                                // альфу — на прозрачном фоне нужен GDI+.
                                using var br = new SolidBrush(fore);
                                g.DrawString(a.Text, font, br, new PointF(x, y),
                                    System.Drawing.StringFormat.GenericTypographic);
                            }
                            else
                            {
                                TextRenderer.DrawText(g, a.Text, font, new Point(x, y), fore, F);
                            }
                            x += a.Width;
                        }
                        y += lineH;
                    }
                }
                return bmp;
            }
            catch { return null; }
        }

        // ── DirectWrite (быстрый путь, если даёт цвет) ────────────────────
        private static Bitmap RenderWpf(string emoji, int px)
        {
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
            var result = new Bitmap(tmp);
            if (IsBlank(result)) { result.Dispose(); return null; }
            return result;
        }

        /// <summary>Есть ли в картинке ЦВЕТ (а не только серые тона): признак
        /// того, что DirectWrite реально отдал COLR-глиф, а не силуэт.</summary>
        private static bool HasColor(Bitmap b)
        {
            try
            {
                int stepX = Math.Max(1, b.Width / 12), stepY = Math.Max(1, b.Height / 12);
                for (int y = 0; y < b.Height; y += stepY)
                    for (int x = 0; x < b.Width; x += stepX)
                    {
                        var p = b.GetPixel(x, y);
                        if (p.A < 24) continue;
                        if (Math.Abs(p.R - p.G) > 14 || Math.Abs(p.G - p.B) > 14 || Math.Abs(p.R - p.B) > 14)
                            return true;
                    }
            }
            catch { }
            return false;
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

        // Монохромный фолбэк (нет сети и WPF не поднялся). Средне-серый —
        // читается и на тёмном, и на светлом фоне.
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
