using System;
using System.Security.Cryptography;
using System.Text;

namespace PISMO
{
    /// <summary>
    /// Симметричное шифрование текста сообщений (AES-256-CBC, общий ключ).
    ///
    /// Текст шифруется перед сохранением в БД и расшифровывается при показе.
    /// Формат хранения: "enc:v1:" + base64(IV(16) + шифртекст). Старые/системные
    /// сообщения без этого префикса возвращаются как есть (обратная совместимость).
    ///
    /// ВНИМАНИЕ: это защита от «посмотреть переписку прямо в БД». Ключ общий и
    /// зашит в приложении — кто получил exe, теоретически может расшифровать.
    /// Для дружеского мессенджера этого достаточно.
    /// </summary>
    public static class Crypto
    {
        private const string Prefix = "enc:v1:";

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
                using var aes = Aes.Create();
                aes.Key = Key;
                aes.GenerateIV();
                var iv = aes.IV;
                using var enc = aes.CreateEncryptor();
                var pt = Encoding.UTF8.GetBytes(plain);
                var ct = enc.TransformFinalBlock(pt, 0, pt.Length);
                var combined = new byte[iv.Length + ct.Length];
                Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
                Buffer.BlockCopy(ct, 0, combined, iv.Length, ct.Length);
                return Prefix + Convert.ToBase64String(combined);
            }
            catch { return plain; }
        }

        public static string Dec(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
                return stored; // обычный/старый текст
            try
            {
                var data = Convert.FromBase64String(stored.Substring(Prefix.Length));
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
    }
}
