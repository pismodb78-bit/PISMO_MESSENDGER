using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace PISMO
{
    /// <summary>
    /// Карточка ссылки: название сайта, заголовок, описание, картинка.
    ///
    /// ЧЕМ ЭТО ОПЛАЧЕНО. Чтобы собрать карточку, приложение само идёт на сайт
    /// по ссылке и читает его страницу. Значит, сайт узнаёт, что сообщение
    /// открыли, и видит адрес читателя — ещё до того, как тот нажал на
    /// ссылку. В Telegram карточку собирает сервер и раздаёт готовой, поэтому
    /// там этого нет; своего сервера у нас нет. Поэтому показ выключается в
    /// настройках, а результат кладётся на диск: один поход на сайт, а не по
    /// одному на каждую отрисовку переписки.
    /// </summary>
    internal static class LinkPreviews
    {
        internal sealed class Preview
        {
            public string Url = "";
            public string Site = "";
            public string Title = "";
            public string Description = "";
            public string ImagePath;

            public bool Empty => string.IsNullOrWhiteSpace(Title)
                              && string.IsNullOrWhiteSpace(Description)
                              && string.IsNullOrEmpty(ImagePath);
        }

        private const int MaxHtml = 512 * 1024;
        private const int MaxImage = 3 * 1024 * 1024;

        private static readonly object Lock = new();
        private static readonly Dictionary<string, Preview> Memory = new();
        private static readonly HashSet<string> Failed = new();
        private static readonly HashSet<string> InFlight = new();
        private static HttpClient _http;

        /// <summary>Карточка появилась — перерисуйте пузырь (событие с фонового потока).</summary>
        public static event Action<string> Ready;

        private static string CacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PISMO", "linkpreview");

        private static string KeyOf(string url) =>
            ((uint)url.GetHashCode()).ToString("x") + "_" + url.Length;

        /// <summary>Готовая карточка из памяти или с диска. null — ещё не собирали.</summary>
        internal static Preview Cached(string url)
        {
            lock (Lock) { if (Memory.TryGetValue(url, out var hit)) return hit; }
            try
            {
                string f = Path.Combine(CacheDir, KeyOf(url) + ".txt");
                if (!File.Exists(f)) return null;
                var parts = File.ReadAllText(f).Split(new[] { "\n\n" }, StringSplitOptions.None);
                if (parts.Length < 4) return null;
                var p = new Preview
                {
                    Url = url,
                    Site = parts[0],
                    Title = parts[1],
                    Description = parts[2],
                    ImagePath = string.IsNullOrWhiteSpace(parts[3]) ? null : parts[3],
                };
                lock (Lock) { Memory[url] = p; }
                return p;
            }
            catch { return null; }
        }

        /// <summary>Собирает карточку в фоне; по готовности поднимает Ready.</summary>
        internal static void QueueFetch(string url)
        {
            if (!DeviceSettings.LinkPreviews) return;
            lock (Lock)
            {
                if (Failed.Contains(url) || InFlight.Contains(url) || Memory.ContainsKey(url)) return;
                InFlight.Add(url);
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                Preview built = null;
                try
                {
                    _http ??= MakeHttp();
                    Directory.CreateDirectory(CacheDir);

                    string html = await ReadHtml(url);
                    if (html != null)
                    {
                        string site = Meta(html, "og:site_name") ?? LinkSources.Of(url).Title;
                        string title = Meta(html, "og:title") ?? TitleTag(html) ?? "";
                        string desc = Meta(html, "og:description") ?? Meta(html, "description") ?? "";
                        string img = Meta(html, "og:image");
                        string imgPath = img == null ? null
                            : await DownloadImage(Absolute(url, img), KeyOf(url));

                        var p = new Preview
                        {
                            Url = url, Site = site, Title = title.Trim(),
                            Description = desc.Trim(), ImagePath = imgPath,
                        };
                        if (!p.Empty) built = p;
                    }
                }
                catch { }

                lock (Lock)
                {
                    InFlight.Remove(url);
                    if (built == null) { Failed.Add(url); return; }
                    Memory[url] = built;
                }
                try
                {
                    File.WriteAllText(Path.Combine(CacheDir, KeyOf(url) + ".txt"),
                        string.Join("\n\n", built.Site, built.Title, built.Description,
                                    built.ImagePath ?? ""));
                }
                catch { }
                try { Ready?.Invoke(url); } catch { }
            });
        }

        private static HttpClient MakeHttp()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            // Часть сайтов без этого отдаёт заглушку.
            c.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 (compatible; PISMO link preview)");
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru,en;q=0.8");
            return c;
        }

        private static async System.Threading.Tasks.Task<string> ReadHtml(string url)
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;
            string type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type.IndexOf("html", StringComparison.OrdinalIgnoreCase) < 0) return null;

            // Разметка сидит в начале документа: читать целиком незачем, а
            // страница может весить мегабайты.
            using var stream = await resp.Content.ReadAsStreamAsync();
            var buf = new byte[MaxHtml];
            int read = 0;
            while (read < buf.Length)
            {
                int n = await stream.ReadAsync(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return System.Text.Encoding.UTF8.GetString(buf, 0, read);
        }

        private static async System.Threading.Tasks.Task<string> DownloadImage(string url, string key)
        {
            try
            {
                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return null;
                if ((resp.Content.Headers.ContentLength ?? 0) > MaxImage) return null;
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var ms = new MemoryStream();
                var buf = new byte[16 * 1024];
                int n;
                while (ms.Length < MaxImage && (n = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                    ms.Write(buf, 0, n);
                if (ms.Length < 100) return null;
                string path = Path.Combine(CacheDir, key + ".img");
                File.WriteAllBytes(path, ms.ToArray());
                return path;
            }
            catch { return null; }
        }

        // ── разбор разметки ───────────────────────────────────────────
        private static string Meta(string html, string name)
        {
            var tag = Regex.Match(html,
                "<meta[^>]+(?:property|name)\\s*=\\s*[\"']" + Regex.Escape(name) + "[\"'][^>]*>",
                RegexOptions.IgnoreCase);
            if (!tag.Success) return null;
            var content = Regex.Match(tag.Value, "content\\s*=\\s*[\"']([^\"']*)[\"']",
                RegexOptions.IgnoreCase);
            if (!content.Success) return null;
            string v = Unescape(content.Groups[1].Value);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        private static string TitleTag(string html)
        {
            var m = Regex.Match(html, "<title[^>]*>([\\s\\S]{0,300}?)</title>", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            string v = Unescape(m.Groups[1].Value).Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        private static string Unescape(string s) => Regex.Replace(
            s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
             .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " "),
            "\\s+", " ");

        /// <summary>og:image часто относительный — достраиваем по адресу страницы.</summary>
        private static string Absolute(string pageUrl, string link)
        {
            if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return link;
            if (link.StartsWith("//")) return "https:" + link;
            int i = pageUrl.IndexOf("://", StringComparison.Ordinal);
            string scheme = i >= 0 ? pageUrl[..i] : "https";
            string rest = i >= 0 ? pageUrl[(i + 3)..] : pageUrl;
            string host = rest.Split('/')[0];
            if (link.StartsWith("/")) return scheme + "://" + host + link;
            int cut = pageUrl.LastIndexOf('/');
            return (cut > 8 ? pageUrl[..cut] : pageUrl) + "/" + link;
        }
    }
}
