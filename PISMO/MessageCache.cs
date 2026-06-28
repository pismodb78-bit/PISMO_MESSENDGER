using System;
using System.Data;
using System.IO;

namespace PISMO
{
    /// <summary>
    /// Постоянный кеш текста переписок на диске (AppData/PISMO/msgcache).
    /// Храним метаданные сообщений (DataTable, без тяжёлых BLOB — те в MediaCache)
    /// в XML, чтобы при открытии чата сразу показать историю из кеша, а свежие
    /// данные подтянуть в фоне. Текст в кеше остаётся ЗАШИФРОВАННЫМ (как в БД).
    /// </summary>
    public static class MessageCache
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PISMO", "msgcache");

        private static string PathFor(string key)
        {
            // ключ безопасен (буквы/цифры/подчёркивания), но на всякий случай чистим
            foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
            return Path.Combine(Dir, key + ".xml");
        }

        public static string DirectKey(int me, int them) => $"d_{me}_{them}";
        public static string GroupKey(int gid) => $"g_{gid}";
        public static string ChannelKey(int channelId) => $"s_{channelId}";

        /// <summary>Сохраняет таблицу метаданных переписки в кеш (в фоне вызывающего).</summary>
        public static void Save(string key, DataTable dt)
        {
            if (dt == null) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var copy = dt.Copy();
                copy.TableName = "msgs";
                copy.WriteXml(PathFor(key), XmlWriteMode.WriteSchema);
            }
            catch { /* кеш не критичен */ }
        }

        /// <summary>Загружает таблицу из кеша или возвращает null, если её нет/повреждена.</summary>
        public static DataTable Load(string key)
        {
            try
            {
                string path = PathFor(key);
                if (!File.Exists(path)) return null;
                var dt = new DataTable();
                dt.ReadXml(path);
                return dt;
            }
            catch { return null; }
        }

        /// <summary>Размер кеша в байтах.</summary>
        public static long GetCacheSize()
        {
            try
            {
                if (!Directory.Exists(Dir)) return 0;
                long total = 0;
                foreach (var f in Directory.GetFiles(Dir, "*.xml"))
                    total += new FileInfo(f).Length;
                return total;
            }
            catch { return 0; }
        }

        /// <summary>Полностью очищает кеш переписок.</summary>
        public static void Clear()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); }
            catch { }
        }
    }
}
