using System;
using System.Text.RegularExpressions;

namespace PISMO
{
    /// <summary>
    /// Какие ссылки можно проиграть, не уходя из приложения.
    ///
    /// Два разных случая, и путать их нельзя. Прямой файл (.mp4 и подобные) —
    /// это просто видео. А YouTube, RUTUBE и TikTok отдают не файл, а
    /// страницу: играть их можно только их собственным встроенным
    /// проигрывателем. Это не обход, а ровно тот способ, который они для
    /// встраивания и предлагают, — иначе пришлось бы разбирать их внутренние
    /// потоки, чего их правила не разрешают.
    ///
    /// Instagram сюда НЕ входит намеренно: их адрес встраивания требует
    /// входа, и вместо ролика показалась бы просьба зарегистрироваться.
    /// Такая ссылка открывается в браузере, где вход уже есть.
    /// </summary>
    internal static class VideoLinks
    {
        internal enum Kind { None, Direct, Embed }

        /// <param name="Url">Что открывать: сам файл или адрес встраивания.</param>
        internal readonly record struct Playable(Kind Kind, string Url, string Title);

        private static readonly string[] FileExt =
            { "mp4", "webm", "m4v", "mov", "mkv", "m3u8", "3gp", "ogv" };

        internal static Playable Of(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return default;
            string host = LinkSources.DomainOf(url);

            string yt = YoutubeId(url, host);
            if (yt != null)
                return new Playable(Kind.Embed,
                    $"https://www.youtube.com/embed/{yt}?autoplay=1&rel=0&playsinline=1", "YouTube");

            string rt = RutubeId(url, host);
            if (rt != null)
                return new Playable(Kind.Embed,
                    $"https://rutube.ru/play/embed/{rt}", "RUTUBE");

            string tt = TiktokId(url, host);
            if (tt != null)
                return new Playable(Kind.Embed,
                    $"https://www.tiktok.com/embed/v2/{tt}", "TikTok");

            // Прямой файл: расширение смотрим в пути, не задевая параметры
            // после «?» — там оно может встретиться случайно.
            int q = url.IndexOfAny(new[] { '?', '#' });
            string path = q >= 0 ? url[..q] : url;
            int dot = path.LastIndexOf('.');
            if (dot > 0)
            {
                string ext = path[(dot + 1)..].ToLowerInvariant();
                if (Array.IndexOf(FileExt, ext) >= 0)
                    return new Playable(Kind.Direct, url, "Видео");
            }

            return default;
        }

        private static string YoutubeId(string url, string host)
        {
            string id = null;
            if (host == "youtu.be")
                id = After(url, "youtu.be/");
            else if (host.EndsWith("youtube.com", StringComparison.Ordinal) ||
                     host.EndsWith("youtube-nocookie.com", StringComparison.Ordinal))
            {
                if (url.Contains("/watch", StringComparison.OrdinalIgnoreCase))
                {
                    var m = Regex.Match(url, "[?&]v=([^&#]+)");
                    if (m.Success) id = m.Groups[1].Value;
                }
                else if (url.Contains("/shorts/", StringComparison.OrdinalIgnoreCase))
                    id = After(url, "/shorts/");
                else if (url.Contains("/embed/", StringComparison.OrdinalIgnoreCase))
                    id = After(url, "/embed/");
            }
            // Идентификатор у YouTube — одиннадцать знаков из ограниченного
            // набора. Проверка нужна, чтобы не собрать проигрыватель из мусора.
            if (id == null || id.Length < 8 || id.Length > 16) return null;
            foreach (char c in id)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return null;
            return id;
        }

        private static string RutubeId(string url, string host)
        {
            if (!host.EndsWith("rutube.ru", StringComparison.Ordinal)) return null;
            string id = url.Contains("/video/", StringComparison.OrdinalIgnoreCase)
                ? After(url, "/video/")
                : url.Contains("/play/embed/", StringComparison.OrdinalIgnoreCase)
                    ? After(url, "/play/embed/")
                    : null;
            if (id == null || id.Length < 8) return null;
            foreach (char c in id) if (!char.IsLetterOrDigit(c)) return null;
            return id;
        }

        private static string TiktokId(string url, string host)
        {
            if (!host.EndsWith("tiktok.com", StringComparison.Ordinal)) return null;
            if (!url.Contains("/video/", StringComparison.OrdinalIgnoreCase)) return null;
            string id = After(url, "/video/");
            if (id == null || id.Length < 8) return null;
            foreach (char c in id) if (!char.IsDigit(c)) return null;
            return id;
        }

        /// <summary>Кусок после метки — до первого «?», «&», «#» или «/».</summary>
        private static string After(string url, string marker)
        {
            int i = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            string rest = url[(i + marker.Length)..];
            int cut = rest.IndexOfAny(new[] { '?', '&', '#', '/' });
            return cut >= 0 ? rest[..cut] : rest;
        }
    }
}
