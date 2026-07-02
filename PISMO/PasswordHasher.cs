using System;
using System.Security.Cryptography;

namespace PISMO
{
    /// <summary>
    /// Хеширование паролей на встроенном в .NET PBKDF2 (SHA-256) — без внешних
    /// NuGet-пакетов (важно для офлайн/ограниченного NuGet). Формат хранения:
    ///   pbkdf2$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;
    /// Старые пароли, лежащие в БД ОТКРЫТЫМ ТЕКСТОМ, при первом успешном входе
    /// автоматически перехешируются (см. LoginForm) — уходим от plaintext без
    /// сброса паролей.
    /// </summary>
    public static class PasswordHasher
    {
        private const string Prefix = "pbkdf2$";
        private const int Iterations = 100_000;
        private const int SaltSize = 16;
        private const int KeySize = 32;

        /// <summary>Хеширует пароль (PBKDF2-SHA256, 100k итераций, случайная соль).</summary>
        public static string Hash(string password)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                password ?? "", salt, Iterations, HashAlgorithmName.SHA256, KeySize);
            return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
        }

        /// <summary>Проверяет пароль. Наш PBKDF2 — сверяет хеш; открытый текст
        /// (legacy) — прямое сравнение (для последующей миграции). Хеши bcrypt из
        /// веб-версии ($2…) без внешней библиотеки проверить нельзя → false.</summary>
        public static bool Verify(string password, string stored)
        {
            if (string.IsNullOrEmpty(stored)) return false;

            if (stored.StartsWith(Prefix, StringComparison.Ordinal))
            {
                try
                {
                    var p = stored.Split('$');   // [pbkdf2, iter, salt, key]
                    if (p.Length != 4) return false;
                    int iter = int.Parse(p[1]);
                    byte[] salt = Convert.FromBase64String(p[2]);
                    byte[] key = Convert.FromBase64String(p[3]);
                    byte[] test = Rfc2898DeriveBytes.Pbkdf2(
                        password ?? "", salt, iter, HashAlgorithmName.SHA256, key.Length);
                    return CryptographicOperations.FixedTimeEquals(test, key);
                }
                catch { return false; }
            }

            // bcrypt из веб-версии — проверить без внешней библиотеки не можем.
            if (stored.StartsWith("$2", StringComparison.Ordinal)) return false;

            // Legacy: пароль хранился открытым текстом.
            return (password ?? "") == stored;
        }

        /// <summary>Нужно ли перехешировать (всё, что не наш PBKDF2-формат).</summary>
        public static bool NeedsUpgrade(string stored)
            => string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
