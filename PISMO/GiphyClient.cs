using System;
using System.Collections.Generic;
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

        /// <summary>Скачивает байты гифки для отправки.</summary>
        public static async Task<byte[]> DownloadAsync(string url)
        {
            try { return await _http.GetByteArrayAsync(url); }
            catch { return null; }
        }
    }
}
