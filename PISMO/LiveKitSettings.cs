using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PISMO
{
    /// <summary>
    /// Конфигурация и генерация access-токенов для LiveKit SFU.
    ///
    /// LiveKit полностью заменяет самописный WebRTC-пайплайн (coturn + offer/answer/ICE
    /// через БД): сервер сам берёт на себя сигналинг, ICE/TURN и, главное,
    /// многосторонние звонки (SFU) — это решает и баг с камерой (один и тот же
    /// механизм публикации/подписки треков на всех сторонах), и баг с групповыми
    /// звонками («кто успел тот и съел»), потому что в комнате LiveKit все
    /// участники слышат и видят всех.
    ///
    /// Токен — это обычный JWT, подписанный HMAC-SHA256 секретом проекта, поэтому
    /// генерируется прямо в C# без отдельного Node.js-бэкенда и без сторонних
    /// NuGet-пакетов.
    /// </summary>
    internal sealed class LiveKitSettingsModel
    {
        // ws:// без TLS (Путь 1 — без домена, на голом IP). Mixed-content при
        // этом разрешается через --allow-running-insecure-content в WebView2,
        // см. WebRtcTransport.InitAsync.
        public string Url { get; set; } = "ws://5.181.23.167:7880";
        public string ApiKey { get; set; } = "APIkey5I8EkGBDSc4jdmI5QcVC";
        public string ApiSecret { get; set; } = "Y3pIteGv4BxEEWSmIvE3P9YqDTBdc3nF7IzWNa51flCRS8Gx";
        // Срок жизни токена; на длительность звонка с запасом.
        public int TokenTtlSeconds { get; set; } = 6 * 60 * 60;
    }

    internal static class LiveKitSettings
    {
        private static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "livekitsettings.json");

        private static LiveKitSettingsModel _model = new LiveKitSettingsModel();

        public static string Url => string.IsNullOrWhiteSpace(_model.Url) ? "ws://5.181.23.167:7880" : _model.Url;
        public static string ApiKey => _model.ApiKey ?? string.Empty;
        public static string ApiSecret => _model.ApiSecret ?? string.Empty;
        public static int TokenTtlSeconds => _model.TokenTtlSeconds <= 0 ? 3600 : _model.TokenTtlSeconds;

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath, Encoding.UTF8);
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    _model = JsonSerializer.Deserialize<LiveKitSettingsModel>(json, opts) ?? new LiveKitSettingsModel();
                }
                else
                {
                    _model = new LiveKitSettingsModel();
                }
                // Гарантируем непустые значения подключения.
                if (string.IsNullOrWhiteSpace(_model.Url)) _model.Url = "ws://5.181.23.167:7880";
                if (string.IsNullOrWhiteSpace(_model.ApiKey)) _model.ApiKey = "APIkey5I8EkGBDSc4jdmI5QcVC";
                if (string.IsNullOrWhiteSpace(_model.ApiSecret)) _model.ApiSecret = "Y3pIteGv4BxEEWSmIvE3P9YqDTBdc3nF7IzWNa51flCRS8Gx";
                ApplyIpTxtOverrides();
                Save();
            }
            catch
            {
                _model = new LiveKitSettingsModel();
                try { Save(); } catch { }
            }
        }

        /// <summary>
        /// Адрес сервера звонков можно задать прямо в ip.txt — там же, где база и
        /// сигналинг:
        ///
        ///   server=IP;port=3307;uid=...;pwd=...;database=bdauth;
        ///   ws=ws://IP:8080/;livekit=ws://IP:7880
        ///
        /// Зачем. Настройки звонков лежали только в livekitsettings.json рядом с
        /// exe, и в интерфейсе их нет вовсе — поменять сервер можно было лишь
        /// правкой json на каждой машине. ip.txt при этом и так раздаётся вместе
        /// со сборкой. Держать адреса в двух разных местах — верный способ
        /// однажды поменять только одно из них и потом искать, почему звонки
        /// уходят на старый сервер.
        ///
        /// Ключи: livekit (или lk) — адрес, lkkey — API key, lksecret — секрет.
        /// Любого из них может не быть: чего нет, то берётся из json.
        ///
        /// Что здесь есть — то и побеждает. Значения записываются в json, чтобы
        /// в файле было видно, чем приложение пользуется на самом деле.
        /// </summary>
        private static void ApplyIpTxtOverrides()
        {
            try
            {
                string[] paths =
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ip.txt"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "ip.txt"),
                    "ip.txt"
                };

                string line = null;
                foreach (var p in paths)
                    if (File.Exists(p)) { line = File.ReadAllText(p).Trim(); break; }

                if (string.IsNullOrWhiteSpace(line) || line.IndexOf('=') < 0) return;

                foreach (var part in line.Split(';'))
                {
                    var seg = part.Trim();
                    int eq = seg.IndexOf('=');
                    if (eq <= 0) continue;

                    var key = seg.Substring(0, eq).Trim().ToLowerInvariant();
                    // Берём ВСЁ после первого '=': секрет — base64 и сам может
                    // заканчиваться на '='.
                    var val = seg.Substring(eq + 1).Trim();
                    if (val.Length == 0) continue;

                    switch (key)
                    {
                        case "livekit":
                        case "lk":
                            _model.Url = val;
                            break;
                        case "lkkey":
                        case "livekitkey":
                            _model.ApiKey = val;
                            break;
                        case "lksecret":
                        case "livekitsecret":
                            _model.ApiSecret = val;
                            break;
                    }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_model, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>
        /// Генерирует JWT access-токен для входа участника в комнату.
        /// </summary>
        /// <param name="roomName">Имя комнаты — используем строковый id call-сессии,
        /// чтобы все участники одного звонка попали в одну комнату.</param>
        /// <param name="identity">Уникальный идентификатор участника (id пользователя).</param>
        /// <param name="displayName">Отображаемое имя.</param>
        public static string CreateToken(string roomName, string identity, string displayName)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long exp = now + TokenTtlSeconds;

            var header = new { alg = "HS256", typ = "JWT" };

            // VideoGrant согласно спецификации LiveKit.
            var grant = new
            {
                roomJoin = true,
                room = roomName,
                canPublish = true,
                canSubscribe = true,
                canPublishData = true,
                // Без этого SetLocalAttributes (значки мьюта/наушников на плитках)
                // молча отбрасывается сервером.
                canUpdateOwnMetadata = true
            };

            var payload = new
            {
                iss = ApiKey,        // API key
                sub = identity,      // identity участника
                name = displayName,
                nbf = now,
                exp = exp,
                video = grant
            };

            string headerB64 = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
            string payloadB64 = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
            string signingInput = headerB64 + "." + payloadB64;

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiSecret));
            byte[] sig = hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput));
            string sigB64 = Base64Url(sig);

            return signingInput + "." + sigB64;
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
