namespace PISMO
{
    /// <summary>
    /// Хеширование паролей (bcrypt, совместимо с веб-версией, где хеши вида $2a$…).
    /// Старые пароли, лежащие в БД открытым текстом, при первом успешном входе
    /// автоматически перехешируются (см. LoginForm) — так постепенно уходим от
    /// plaintext без сброса паролей.
    /// </summary>
    public static class PasswordHasher
    {
        /// <summary>Является ли строка bcrypt-хешем ($2a$/$2b$/$2y$).</summary>
        public static bool IsBcrypt(string stored)
            => !string.IsNullOrEmpty(stored) &&
               (stored.StartsWith("$2a$") || stored.StartsWith("$2b$") || stored.StartsWith("$2y$"));

        /// <summary>Хеширует пароль (bcrypt, work factor 11).</summary>
        public static string Hash(string password)
            => BCrypt.Net.BCrypt.HashPassword(password ?? "", workFactor: 11);

        /// <summary>Проверяет пароль. Для bcrypt — сверяет хеш; для старого
        /// открытого текста — прямое сравнение (для последующей миграции).</summary>
        public static bool Verify(string password, string stored)
        {
            if (string.IsNullOrEmpty(stored)) return false;
            try
            {
                if (IsBcrypt(stored)) return BCrypt.Net.BCrypt.Verify(password ?? "", stored);
            }
            catch { return false; }
            // Legacy plaintext.
            return (password ?? "") == stored;
        }

        /// <summary>Нужно ли перехешировать сохранённое значение (не bcrypt = старое).</summary>
        public static bool NeedsUpgrade(string stored) => !IsBcrypt(stored);
    }
}
