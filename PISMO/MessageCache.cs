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
                lock (Gate) { if (_memKey == key) { _memKey = null; _memTable = null; } }
            }
            catch { /* кеш не критичен */ }
        }

        // ── История переписки целиком ────────────────────────────────────
        //
        // Раньше кеш переписки ЗАМЕНЯЛСЯ последней страницей на каждом
        // открытии чата. То есть всё, что человек когда-то долистал, тут же
        // и выбрасывалось, а подниматься по истории приходилось заново через
        // сервер — хотя эти самые сообщения только что лежали на диске.
        //
        // Теперь кеш только прирастает: свежая страница вливается в то, что
        // уже есть. Подъём по уже прочитанному становится мгновенным, как на
        // телефоне, и переживает перезапуск.

        private static readonly object Gate = new object();
        private static string _memKey;
        private static DataTable _memTable;

        /// <summary>Сколько сообщений держим на чат. Дальше — отрезаем самые старые.</summary>
        private const int HistoryCap = 4000;

        /// <summary>
        /// Вливает свежую страницу в кеш, не теряя того, что там уже лежало.
        /// Возвращает получившуюся историю целиком.
        /// </summary>
        public static DataTable Merge(string key, DataTable fresh)
        {
            if (fresh == null) return null;
            try
            {
                var old = History(key);
                if (old == null || old.Rows.Count == 0) { Save(key, fresh); Remember(key, fresh); return fresh; }

                // По id: свежая строка вытесняет старую (текст могли изменить,
                // сообщение — удалить), всё прочее из кеша остаётся.
                var byId = new System.Collections.Generic.SortedDictionary<int, DataRow>();
                foreach (DataRow r in old.Rows)
                    if (r["id"] != DBNull.Value) byId[Convert.ToInt32(r["id"])] = r;
                foreach (DataRow r in fresh.Rows)
                    if (r["id"] != DBNull.Value) byId[Convert.ToInt32(r["id"])] = r;

                // Схему берём у СВЕЖЕЙ: она заведомо соответствует нынешнему
                // запросу, а кеш мог быть записан прошлой версией программы.
                var merged = fresh.Clone();
                merged.TableName = "msgs";
                foreach (var kv in byId) CopyByName(merged, kv.Value);
                while (merged.Rows.Count > HistoryCap) merged.Rows.RemoveAt(0);

                Save(key, merged);
                Remember(key, merged);
                return merged;
            }
            catch { Save(key, fresh); return fresh; }
        }

        /// <summary>
        /// Сообщения СТАРШЕ указанного — до limit штук, в хронологическом
        /// порядке. null, если в кеше таких нет.
        /// </summary>
        public static DataTable Older(string key, int beforeId, int limit)
        {
            if (beforeId <= 0 || limit <= 0) return null;
            try
            {
                var hist = History(key);
                if (hist == null || hist.Rows.Count == 0) return null;

                var take = new System.Collections.Generic.List<DataRow>();
                foreach (DataRow r in hist.Rows)
                {
                    if (r["id"] == DBNull.Value) continue;
                    if (Convert.ToInt32(r["id"]) < beforeId) take.Add(r);
                }
                if (take.Count == 0) return null;
                take.Sort((a, b) => Convert.ToInt32(a["id"]).CompareTo(Convert.ToInt32(b["id"])));

                var res = hist.Clone();
                res.TableName = "msgs";
                for (int i = Math.Max(0, take.Count - limit); i < take.Count; i++)
                    CopyByName(res, take[i]);
                return res.Rows.Count > 0 ? res : null;
            }
            catch { return null; }
        }

        /// <summary>История из памяти, иначе с диска (разбор XML не бесплатный).</summary>
        private static DataTable History(string key)
        {
            lock (Gate)
            {
                if (_memKey == key && _memTable != null) return _memTable;
            }
            var dt = Load(key);
            Remember(key, dt);
            return dt;
        }

        private static void Remember(string key, DataTable dt)
        {
            lock (Gate) { _memKey = key; _memTable = dt; }
        }

        /// <summary>
        /// Копирование строки ПО ИМЕНАМ колонок, а не по их номерам.
        ///
        /// ImportRow переносит значения по порядку, и кеш, записанный прошлой
        /// версией программы, тихо перемешал бы поля местами: текст оказался бы
        /// в имени файла, дата в идентификаторе. По именам лишняя колонка
        /// просто не переносится, а недостающая остаётся пустой.
        /// </summary>
        private static void CopyByName(DataTable target, DataRow src)
        {
            var nr = target.NewRow();
            foreach (DataColumn c in target.Columns)
                if (src.Table.Columns.Contains(c.ColumnName))
                {
                    var v = src[c.ColumnName];
                    if (v != DBNull.Value && v != null) nr[c] = v;
                }
            target.Rows.Add(nr);
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
