using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace PISMO
{
    public sealed class GifItem
    {
        public string PreviewUrl;  // маленькая гифка для сетки
        public string FullUrl;     // версия для отправки
    }

    /// <summary>Клиент Giphy для поиска гифок (как в Discord).</summary>
    public static class GiphyClient
    {
        // API-ключ Giphy (Beta — лимит 100 запросов/час).
        private const string ApiKey = "yNJ3u3R2019HeM5VjpFqCz2wSpfrIYf9";

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        public static Task<List<GifItem>> TrendingAsync(int limit = 24)
            => RequestAsync($"https://api.giphy.com/v1/gifs/trending?api_key={ApiKey}&limit={limit}&rating=pg-13");

        public static Task<List<GifItem>> SearchAsync(string query, int limit = 24)
        {
            string q = Uri.EscapeDataString(query ?? "");
            return RequestAsync($"https://api.giphy.com/v1/gifs/search?api_key={ApiKey}&q={q}&limit={limit}&rating=pg-13&bundle=messaging_non_clips");
        }

        private static async Task<List<GifItem>> RequestAsync(string url)
        {
            var result = new List<GifItem>();
            try
            {
                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (var g in data.EnumerateArray())
                {
                    if (!g.TryGetProperty("images", out var imgs)) continue;
                    string preview = GetUrl(imgs, "fixed_width_small") ?? GetUrl(imgs, "fixed_width") ?? GetUrl(imgs, "downsized");
                    string full = GetUrl(imgs, "downsized_medium") ?? GetUrl(imgs, "downsized") ?? GetUrl(imgs, "original");
                    if (preview != null && full != null)
                        result.Add(new GifItem { PreviewUrl = preview, FullUrl = full });
                }
            }
            catch { }
            return result;
        }

        private static string GetUrl(JsonElement images, string rendition)
            => images.TryGetProperty(rendition, out var r) && r.TryGetProperty("url", out var u)
               ? u.GetString() : null;

        // ── Кеш гифок ────────────────────────────────────────────────────
        //
        // Раньше каждая гифка в канале скачивалась заново при КАЖДОЙ сборке
        // ленты: новое сообщение, догрузка истории, переключение канала — и все
        // гифки страницы снова шли из сети. Чем выше пролистано, тем их больше,
        // и тем дольше подтормаживало всё приложение. Теперь — память, потом
        // диск, и только потом сеть; одна и та же гифка, запрошенная дважды
        // разом, качается один раз.
        private static readonly object _lock = new();
        private static readonly Dictionary<string, byte[]> _mem = new();
        private static readonly LinkedList<string> _order = new();
        private static long _memBytes;
        private const long MemLimit = 48L * 1024 * 1024;
        private const int DiskFiles = 400;
        private static int _saves;
        private static readonly Dictionary<string, Task<byte[]>> _inflight = new();

        private static string DiskDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PISMO", "cache", "gif");

        private static string DiskPath(string url)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
            return Path.Combine(DiskDir, Convert.ToHexString(h) + ".gif");
        }

        /// <summary>Байты гифки — из кеша, а нет — из сети.</summary>
        public static Task<byte[]> DownloadAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<byte[]>(null);
            lock (_lock)
            {
                if (_mem.TryGetValue(url, out var hit))
                {
                    _order.Remove(url);
                    _order.AddFirst(url);
                    return Task.FromResult(hit);
                }
                if (_inflight.TryGetValue(url, out var running)) return running;
                // Task.Run — чтобы выборка никогда не завершилась прямо здесь,
                // под замком, раньше, чем её запишут в «уже едет».
                var t = Task.Run(() => FetchAsync(url));
                _inflight[url] = t;
                return t;
            }
        }

        private static async Task<byte[]> FetchAsync(string url)
        {
            byte[] data = null;
            try
            {
                string path = DiskPath(url);
                if (File.Exists(path))
                {
                    data = File.ReadAllBytes(path);
                    try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
                }
                if (data == null || data.Length == 0)
                {
                    data = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
                    if (data is { Length: > 0 }) SaveDisk(path, data);
                }
            }
            catch { data = null; }

            lock (_lock)
            {
                _inflight.Remove(url);
                if (data is { Length: > 0 } && !_mem.ContainsKey(url))
                {
                    _mem[url] = data;
                    _order.AddFirst(url);
                    _memBytes += data.Length;
                    while (_memBytes > MemLimit && _order.Count > 1)
                    {
                        string old = _order.Last.Value;
                        _order.RemoveLast();
                        if (_mem.Remove(old, out var gone)) _memBytes -= gone.Length;
                    }
                }
            }
            return data;
        }

        private static void SaveDisk(string path, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(DiskDir);
                File.WriteAllBytes(path, data);
                // Изредка подрезаем папку: самые давно открытые — вон.
                if (System.Threading.Interlocked.Increment(ref _saves) % 40 != 0) return;
                var files = new DirectoryInfo(DiskDir).GetFiles("*.gif");
                if (files.Length <= DiskFiles) return;
                Array.Sort(files, (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
                for (int i = 0; i < files.Length - DiskFiles; i++)
                    try { files[i].Delete(); } catch { }
            }
            catch { }
        }
    }
}
