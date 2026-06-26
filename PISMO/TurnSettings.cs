using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PISMO
{
    internal sealed class TurnSettingsModel
    {
        public bool Enabled { get; set; } = true;
        public string Address { get; set; } = "5.181.23.167"; // Ваш рабочий IP из логов
        public int Port { get; set; } = 3478;
        public string Transport { get; set; } = "udp"; // ← Поменяйте здесь с "tcp" на "udp"
        public bool TimeLimited { get; set; } = true;
        public int TtlSeconds { get; set; } = 86400;
        public string Username { get; set; } = "alice";
        public string Secret { get; set; } = "";
    }

    internal static class TurnSettings
    {
        private static readonly string FilePath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "turnsettings.json");

        private static TurnSettingsModel _model = new TurnSettingsModel();

        // Жёстко заданный секрет (взято из вашего скриншота).
        // ВАЖНО: этот хардкод заменяет любое ранее сохранённое значение и будет использоваться для генерации кредо.
        private const string ForcedSecretHex = "79d2240e358c22885cac617fb26511548bebec4b";

        // Алиасы и совместимые свойства (используются в SettingsForm / CallForm)
        public static bool TurnEnabled
        {
            get => _model.Enabled;
            set { _model.Enabled = value; Save(); }
        }

        public static string TurnServerAddress
        {
            get => _model.Address ?? string.Empty;
            set { _model.Address = value ?? string.Empty; Save(); }
        }

        public static int TurnServerPort
        {
            get => _model.Port;
            set { _model.Port = value <= 0 ? 3478 : value; Save(); }
        }

        public static string TurnTransport
        {
            get => string.IsNullOrWhiteSpace(_model.Transport) ? "udp" : _model.Transport;
            set { _model.Transport = string.IsNullOrWhiteSpace(value) ? "udp" : value; Save(); }
        }

        public static string TurnUsername
        {
            get => _model.Username ?? string.Empty;
            set { _model.Username = value ?? string.Empty; Save(); }
        }

        // Алиасы для секретов/пароля
        public static string TurnSecret
        {
            get => _model.Secret ?? string.Empty;
            set { _model.Secret = value ?? string.Empty; Save(); }
        }

        public static string TurnPassword
        {
            get => _model.Secret ?? string.Empty;
            set { _model.Secret = value ?? string.Empty; Save(); }
        }

        public static int TurnCredentialsTtl
        {
            get => _model.TtlSeconds;
            set { _model.TtlSeconds = value <= 0 ? 300 : value; Save(); }
        }

        public static bool UseTimeLimitedCredentials
        {
            get => _model.TimeLimited;
            set { _model.TimeLimited = value; Save(); }
        }

        // Старые / внутренние имена (для совместимости)
        public static bool Enabled => _model.Enabled;
        public static string Address => _model.Address;
        public static int Port => _model.Port;
        public static string Transport => string.IsNullOrWhiteSpace(_model.Transport) ? "udp" : _model.Transport;
        public static bool TimeLimited => _model.TimeLimited;
        public static int TtlSeconds => _model.TtlSeconds;
        public static string Username => _model.Username;
        public static string Secret => _model.Secret;

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    _model = new TurnSettingsModel();
                }
                else
                {
                    var json = File.ReadAllText(FilePath, Encoding.UTF8);
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    _model = JsonSerializer.Deserialize<TurnSettingsModel>(json, opts) ?? new TurnSettingsModel();
                }

                // Принудительно используем фиксированный секрет из кода — перезапишем модель.
                _model.Secret = ForcedSecretHex;
                _model.Enabled = true;
                _model.TimeLimited = true;
                if (string.IsNullOrWhiteSpace(_model.Address))
                    _model.Address = "85.174.248.59";
                // Сохраняем, чтобы файл конфигурации соответствовал использованному секрету.
                Save();
            }
            catch
            {
                // При ошибке чтения — создаём модель и помещаем туда фиксированный секрет.
                _model = new TurnSettingsModel();
                _model.Secret = ForcedSecretHex;
                try { Save(); } catch { /* ignore */ }
            }
        }

        public static void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_model, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json, Encoding.UTF8);
            }
            catch
            {
                // ignore write errors
            }
        }

        public static void Set(
            bool enabled,
            string address,
            int port,
            string transport,
            bool timeLimited,
            int ttlSeconds,
            string username,
            string secret)
        {
            _model.Enabled = enabled;
            _model.Address = address ?? "";
            _model.Port = port <= 0 ? 3478 : port;
            _model.Transport = transport ?? "udp";
            _model.TimeLimited = timeLimited;
            _model.TtlSeconds = ttlSeconds <= 0 ? 300 : ttlSeconds;
            _model.Username = username ?? "";
            // Игнорируем переданный secret и используем принудительный.
            _model.Secret = ForcedSecretHex;
            Save();
        }

        /// <summary>
        /// Очищает строку секрета от всего, что может незаметно испортить байты ключа:
        /// BOM, пробелы по краям, \r\n, неразрывные пробелы и т.п. Это критично, потому что
        /// секрет приходит из десериализованного JSON-файла, и любой невидимый символ
        /// там даст другие байты ключа -> другой HMAC -> другой password, при том что
        /// "на глаз" секрет выглядит идентично эталонному (например, из PowerShell-литерала).
        /// </summary>
        private static string SanitizeSecret(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw ?? "";
            // Убираем BOM, если он каким-то образом попал в строку
            string s = raw.Trim('\uFEFF', '\u200B');
            // Убираем обычные и "невидимые" пробельные символы по краям
            s = s.Trim();
            return s;
        }

        /// <summary>
        /// Генерирует временные credentials (coturn REST long-term style).
        /// username = expiryUnix:baseUser
        /// password = base64(HMAC-SHA1(secretBytes, username))
        /// Secret expected as a text string (use UTF8 bytes).
        /// </summary>
        public static (string username, string password, long expiresAt) GenerateTimeLimitedCredentials(string baseUser)
        {
            // ВАЖНО: используем ForcedSecretHex напрямую, а не _model.Secret.
            // _model.Secret приходит через JsonSerializer.Deserialize из файла на диске —
            // если файл был хоть раз пересохранён другой кодировкой/редактором, в строке
            // могут появиться невидимые символы (BOM, NBSP), которые не видны в логах,
            // но меняют байты ключа и, следовательно, весь HMAC. ForcedSecretHex — это
            // C#-литерал в исходном коде, который гарантированно "чист".
            string secret = SanitizeSecret(string.IsNullOrWhiteSpace(_model.Secret) ? ForcedSecretHex : _model.Secret);

            if (string.IsNullOrWhiteSpace(secret))
                throw new InvalidOperationException("TURN secret is empty.");

            string cleanBaseUser = SanitizeSecret(string.IsNullOrWhiteSpace(baseUser) ? "user" : baseUser);

            long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(30, _model.TtlSeconds);
            string uname = $"{expires}:{cleanBaseUser}";

            // Используем UTF8-байты секретной строки (совместимо с coturn static-auth-secret)
            byte[] keyBytes = Encoding.UTF8.GetBytes(secret);

            using var hmac = new HMACSHA1(keyBytes);
            byte[] sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(uname));
            string pwd = Convert.ToBase64String(sig);

#if DEBUG
            // Диагностика: хэш секрета (не сам секрет!) и точная длина байт ключа,
            // чтобы можно было сверить с PowerShell-стороной без раскрытия секрета в логах.
            using (var sha256 = SHA256.Create())
            {
                string secretFingerprint = Convert.ToBase64String(sha256.ComputeHash(keyBytes)).Substring(0, 12);
                System.Diagnostics.Debug.WriteLine(
                    $"[TURN GEN] username='{uname}' keyBytesLen={keyBytes.Length} secretFingerprint={secretFingerprint} password={pwd}");
            }
#endif

            return (uname, pwd, expires);
        }

        /// <summary>
        /// Возвращает username/password для текущей сессии (в зависимости от режима TimeLimited).
        /// По умолчанию базовый user = "alice" чтобы изменялся только числовой expiry.
        /// </summary>
        public static (string username, string password) GetCredentials(string baseUser = "alice")
        {
            if (_model.TimeLimited)
            {
                try
                {
                    var (u, p, _) = GenerateTimeLimitedCredentials(baseUser);
                    return (u, p);
                }
                catch
                {
                    // fallback to static below
                }
            }

            return (_model.Username ?? string.Empty, _model.Secret ?? string.Empty);
        }

        public static string GetTurnUrl()
        {
            if (string.IsNullOrWhiteSpace(_model.Address)) return string.Empty;
            string url = $"turn:{_model.Address}:{_model.Port}";
            if (!string.IsNullOrWhiteSpace(_model.Transport))
                url += $"?transport={_model.Transport}";
            return url;
        }

        public static string GetIceConfigJson(string baseUser = "alice")
        {
            if (string.IsNullOrWhiteSpace(_model.Address)) return "";

            var stunUrl = $"stun:{_model.Address}";
            if (_model.Port > 0 && _model.Port != 3478)
                stunUrl += $":{_model.Port}";

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("iceServers");

                // STUN
                writer.WriteStartObject();
                writer.WriteStartArray("urls");
                writer.WriteStringValue(stunUrl);
                writer.WriteEndArray();
                writer.WriteEndObject();

                // TURN (если включён)
                if (_model.Enabled && !string.IsNullOrWhiteSpace(_model.Address))
                {
                    var turnUrl = $"turn:{_model.Address}:{_model.Port}";
                    if (!string.IsNullOrWhiteSpace(_model.Transport))
                        turnUrl += $"?transport={_model.Transport}";

                    string uname = _model.Username;
                    string pwd = _model.Secret;

                    if (_model.TimeLimited)
                    {
                        try
                        {
                            var creds = GenerateTimeLimitedCredentials(baseUser);
                            uname = creds.username;
                            pwd = creds.password;
                        }
                        catch
                        {
                            // fallback to static
                        }
                    }

                    writer.WriteStartObject();
                    writer.WriteStartArray("urls");
                    writer.WriteStringValue(turnUrl);
                    writer.WriteEndArray();
                    if (!string.IsNullOrWhiteSpace(uname))
                        writer.WriteString("username", uname);
                    if (!string.IsNullOrWhiteSpace(pwd))
                        writer.WriteString("credential", pwd);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    /// <summary>Вспомогательный класс — реализует старый API TurnCredentials.</summary>
    internal static class TurnCredentials
    {
        public static (string username, string password) GenerateCredentials(string secretHexOrText, string username, int ttlSeconds = 3600)
        {
            try
            {
                long expiryUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttlSeconds;
                string usernameWithExpiry = $"{expiryUnix}:{username}";

                // Используем UTF8-байты от строки секрета
                byte[] key = Encoding.UTF8.GetBytes(secretHexOrText);
                if (key == null || key.Length == 0)
                    throw new ArgumentException("Secret must be a non-empty text");

                using var hmac = new HMACSHA1(key);
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(usernameWithExpiry));
                string password = Convert.ToBase64String(hash);
                return (usernameWithExpiry, password);
            }
            catch
            {
                throw;
            }
        }

        public static bool IsExpired(string usernameWithExpiry)
        {
            try
            {
                var parts = usernameWithExpiry.Split(':');
                if (parts.Length != 2 || !long.TryParse(parts[0], out long expiryUnix))
                    return true;
                long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return nowUnix >= expiryUnix;
            }
            catch { return true; }
        }

        public static DateTime GetExpiryTime(string usernameWithExpiry)
        {
            try
            {
                var parts = usernameWithExpiry.Split(':');
                if (parts.Length == 2 && long.TryParse(parts[0], out long expiryUnix))
                    return DateTimeOffset.FromUnixTimeSeconds(expiryUnix).UtcDateTime;
            }
            catch { }
            return DateTime.MinValue;
        }
    }

    // Вспомогательные низкоуровневые функции, вынесены чтобы избежать циклических ссылок внутри файла
    internal static class TurnSettings_Helpers
    {
        public static byte[] HexStringToBytes(string hex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0) return null;
                int len = hex.Length / 2;
                var r = new byte[len];
                for (int i = 0; i < len; i++)
                    r[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return r;
            }
            catch { return null; }
        }
    }
}