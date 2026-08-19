using System;
using System.Security.Cryptography;
using System.Text;

namespace PISMO
{
    /// <summary>
    /// Симметричное шифрование текста сообщений.
    ///
    /// Новый формат (пишем всегда): "enc:v2:" + base64(nonce(12) + tag(16) +
    /// шифртекст) — AES-256-GCM: это аутентифицированное шифрование, тег
    /// гарантирует ЦЕЛОСТНОСТЬ и подлинность (подменённый/битый шифртекст не
    /// расшифруется молча — он просто не пройдёт проверку тега).
    ///
    /// Старый формат "enc:v1:" (AES-256-CBC, без проверки целостности) читаем для
    /// обратной совместимости — уже сохранённые сообщения не ломаются. Сообщения
    /// без префикса возвращаются как есть.
    ///
    /// ВНИМАНИЕ: ключ по-прежнему общий и зашит в приложении — это защита «не
    /// прочитать/не подменить прямо в БД», но не end-to-end: кто получил exe,
    /// теоретически может расшифровать. Для дружеского мессенджера этого достаточно.
    /// </summary>
    public static class Crypto
    {
        private const string PrefixV1 = "enc:v1:";   // AES-CBC (старый, только чтение)
        private const string PrefixV2 = "enc:v2:";   // AES-GCM (пишем сейчас)
        private const int NonceLen = 12;             // рекомендованный размер nonce для GCM
        private const int TagLen = 16;               // тег аутентификации GCM

        // Ключ выводится из секретной фразы через SHA-256 (32 байта для AES-256).
        // Менять фразу нельзя после начала использования — иначе старые
        // зашифрованные сообщения перестанут читаться.
        private static readonly byte[] Key = SHA256.HashData(
            Encoding.UTF8.GetBytes("PISMO::message::secret::v1::do-not-change"));

        public static string Enc(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain;
            try
            {
                var pt = Encoding.UTF8.GetBytes(plain);
                var nonce = RandomNumberGenerator.GetBytes(NonceLen);
                var ct = new byte[pt.Length];
                var tag = new byte[TagLen];
                using (var gcm = new AesGcm(Key, TagLen))
                    gcm.Encrypt(nonce, pt, ct, tag);

                var combined = new byte[NonceLen + TagLen + ct.Length];
                Buffer.BlockCopy(nonce, 0, combined, 0, NonceLen);
                Buffer.BlockCopy(tag, 0, combined, NonceLen, TagLen);
                Buffer.BlockCopy(ct, 0, combined, NonceLen + TagLen, ct.Length);
                return PrefixV2 + Convert.ToBase64String(combined);
            }
            catch { return plain; }
        }

        public static string Dec(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;

            // Новый формат: AES-GCM с проверкой целостности.
            if (stored.StartsWith(PrefixV2, StringComparison.Ordinal))
            {
                try
                {
                    var data = Convert.FromBase64String(stored.Substring(PrefixV2.Length));
                    if (data.Length < NonceLen + TagLen) return stored;
                    var nonce = new byte[NonceLen];
                    var tag = new byte[TagLen];
                    int ctLen = data.Length - NonceLen - TagLen;
                    var ct = new byte[ctLen];
                    Buffer.BlockCopy(data, 0, nonce, 0, NonceLen);
                    Buffer.BlockCopy(data, NonceLen, tag, 0, TagLen);
                    Buffer.BlockCopy(data, NonceLen + TagLen, ct, 0, ctLen);
                    var pt = new byte[ctLen];
                    // Бросит CryptographicException, если тег не сошёлся (подмена/порча).
                    using (var gcm = new AesGcm(Key, TagLen))
                        gcm.Decrypt(nonce, ct, tag, pt);
                    return Encoding.UTF8.GetString(pt);
                }
                catch { return stored; }   // подмена/битые данные — не падаем
            }

            // Старый формат: AES-CBC (без проверки целостности) — читаем как раньше.
            if (stored.StartsWith(PrefixV1, StringComparison.Ordinal))
            {
                try
                {
                    var data = Convert.FromBase64String(stored.Substring(PrefixV1.Length));
                    if (data.Length <= 16) return stored;
                    using var aes = Aes.Create();
                    aes.Key = Key;
                    var iv = new byte[16];
                    Buffer.BlockCopy(data, 0, iv, 0, 16);
                    aes.IV = iv;
                    using var dec = aes.CreateDecryptor();
                    var pt = dec.TransformFinalBlock(data, 16, data.Length - 16);
                    return Encoding.UTF8.GetString(pt);
                }
                catch { return stored; }
            }

            return stored; // обычный/старый текст без шифрования
        }
    }
}
