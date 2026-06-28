using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PISMO
{
    /// <summary>
    /// JWT (HS256) для авторизации: при входе клиент выпускает подписанный токен
    /// с uid/login/срок действия. Используется для «Запомнить меня» (вместо
    /// хранения пароля) и для подтверждения личности на WS-сервере сигналинга.
    ///
    /// Секрет общий (вшит в приложение и в ws-server). Меняется одновременно в
    /// обоих местах. Для настоящей защиты секрет должен совпадать на сервере.
    /// </summary>
    public static class JwtAuth
    {
        // ВАЖНО: тот же секрет должен стоять в ws-server/server.js (JWT_SECRET).
        private const string Secret = "PISMO::jwt::secret::v1::change-me-please";

        public sealed class Claims
        {
            public int Uid;
            public string Login = "";
            public long Exp;     // unix-время истечения
        }

        /// <summary>Создаёт JWT с указанным сроком жизни (по умолчанию 30 дней).</summary>
        public static string Create(int uid, string login, int ttlDays = 30)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long exp = now + (long)ttlDays * 86400;

            string header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            var payloadObj = new { uid, login = login ?? "", iat = now, exp };
            string payload = B64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadObj)));
            string signingInput = header + "." + payload;
            string sig = B64Url(Sign(signingInput));
            return signingInput + "." + sig;
        }

        /// <summary>Проверяет подпись и срок; возвращает claims или null.</summary>
        public static Claims Validate(string token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token)) return null;
                var parts = token.Split('.');
                if (parts.Length != 3) return null;

                string signingInput = parts[0] + "." + parts[1];
                string expected = B64Url(Sign(signingInput));
                // Сравнение в постоянном времени.
                if (!FixedEquals(expected, parts[2])) return null;

                var json = Encoding.UTF8.GetString(FromB64Url(parts[1]));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                long exp = root.TryGetProperty("exp", out var e) ? e.GetInt64() : 0;
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= exp) return null; // истёк

                return new Claims
                {
                    Uid = root.TryGetProperty("uid", out var u) ? u.GetInt32() : 0,
                    Login = root.TryGetProperty("login", out var l) ? (l.GetString() ?? "") : "",
                    Exp = exp
                };
            }
            catch { return null; }
        }

        private static byte[] Sign(string data)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            return h.ComputeHash(Encoding.UTF8.GetBytes(data));
        }

        private static string B64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromB64Url(string s)
        {
            string b = s.Replace('-', '+').Replace('_', '/');
            switch (b.Length % 4) { case 2: b += "=="; break; case 3: b += "="; break; }
            return Convert.FromBase64String(b);
        }

        private static bool FixedEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
