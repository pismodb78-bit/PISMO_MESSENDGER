using System;
using System.Data;
using System.IO;
using System.Text.Json;

namespace PISMO
{
    /// <summary>
    /// Последнее, что приходило из базы, — на диске. Чтобы без связи
    /// приложение показывало переписку, а не пустоту.
    /// </summary>
    /// <remarks>
    /// Вложения и так лежат локально (см. <see cref="MediaCache"/>): картинки,
    /// голосовые, видео открывались бы и без сети — но добраться до них было
    /// нельзя, потому что сам список сообщений брался только из базы. Нет
    /// связи — нет ленты, и кеш вложений оказывался бесполезен.
    ///
    /// Здесь сохраняются ЛЁГКИЕ выборки: список чатов, страницы сообщений,
    /// списки серверов и каналов. Это текст и числа; блобов тут нет и быть не
    /// должно — их место в кеше вложений.
    ///
    /// Хранилище не претендует на полноту: это снимок последнего увиденного.
    /// Связь вернулась — он тут же перезаписывается свежими данными.
    /// </remarks>
    internal static class OfflineStore
    {
        /// <summary>Сколько строк держим на один ключ. Страница ленты и так короче.</summary>
        private const int MaxRows = 500;

        private static string Dir
        {
            get
            {
                string p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PISMO", "offline", UserSession.EffectiveId.ToString());
                try { Directory.CreateDirectory(p); } catch { }
                return p;
            }
        }

        private static string FileOf(string key)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) key = key.Replace(c, '_');
            return Path.Combine(Dir, key + ".json");
        }

        /// <summary>Сохранить выборку. Ошибки глушим: кеш — удобство, а не обязанность.</summary>
        public static void Save(string key, DataTable dt)
        {
            if (dt == null || UserSession.EffectiveId <= 0) return;
            try
            {
                using var fs = File.Create(FileOf(key));
                using var w = new Utf8JsonWriter(fs);
                w.WriteStartObject();

                w.WriteStartArray("cols");
                foreach (DataColumn c in dt.Columns)
                {
                    // Блобы не сохраняем: офлайн-лента показывает значки
                    // вложений, а сами байты лежат в кеше вложений.
                    if (c.DataType == typeof(byte[])) continue;
                    w.WriteStartObject();
                    w.WriteString("n", c.ColumnName);
                    w.WriteString("t", c.DataType.FullName);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("rows");
                int n = 0;
                foreach (DataRow r in dt.Rows)
                {
                    if (++n > MaxRows) break;
                    w.WriteStartArray();
                    foreach (DataColumn c in dt.Columns)
                    {
                        if (c.DataType == typeof(byte[])) continue;
                        object v = r[c];
                        if (v == null || v == DBNull.Value) { w.WriteNullValue(); continue; }
                        switch (v)
                        {
                            case bool b: w.WriteBooleanValue(b); break;
                            case DateTime d: w.WriteStringValue(d.ToString("o")); break;
                            case sbyte or byte or short or ushort or int or uint or long or ulong:
                                w.WriteNumberValue(Convert.ToInt64(v)); break;
                            case float or double or decimal:
                                w.WriteNumberValue(Convert.ToDouble(v)); break;
                            default: w.WriteStringValue(v.ToString()); break;
                        }
                    }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            catch { }
        }

        /// <summary>Прочитать сохранённое. null — нечего показывать.</summary>
        public static DataTable Load(string key)
        {
            if (UserSession.EffectiveId <= 0) return null;
            try
            {
                string path = FileOf(key);
                if (!File.Exists(path)) return null;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                var dt = new DataTable();
                foreach (var c in root.GetProperty("cols").EnumerateArray())
                {
                    var type = Type.GetType(c.GetProperty("t").GetString() ?? "System.String")
                               ?? typeof(string);
                    dt.Columns.Add(c.GetProperty("n").GetString(), type);
                }

                foreach (var r in root.GetProperty("rows").EnumerateArray())
                {
                    var row = dt.NewRow();
                    int i = 0;
                    foreach (var v in r.EnumerateArray())
                    {
                        if (i >= dt.Columns.Count) break;
                        var col = dt.Columns[i++];
                        if (v.ValueKind == JsonValueKind.Null) { row[col] = DBNull.Value; continue; }
                        try
                        {
                            row[col] = col.DataType == typeof(DateTime)
                                ? DateTime.Parse(v.GetString()!, null,
                                    System.Globalization.DateTimeStyles.RoundtripKind)
                                : Convert.ChangeType(
                                    v.ValueKind == JsonValueKind.String ? v.GetString()
                                    : v.ValueKind == JsonValueKind.True ? "True"
                                    : v.ValueKind == JsonValueKind.False ? "False"
                                    : v.GetRawText(),
                                    col.DataType);
                        }
                        catch { row[col] = DBNull.Value; }
                    }
                    dt.Rows.Add(row);
                }
                return dt;
            }
            catch { return null; }
        }

        /// <summary>
        /// Стереть сохранённые страницы переписок у всех пользователей этого
        /// компьютера (списки чатов и серверов остаются). См. MessageCache.
        /// </summary>
        public static void DropMessagePages()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PISMO", "offline");
                if (!Directory.Exists(root)) return;
                foreach (var user in Directory.GetDirectories(root))
                    foreach (var mask in new[] { "dm_*.json", "grp_*.json", "chan_*.json" })
                        foreach (var f in Directory.GetFiles(user, mask))
                            try { File.Delete(f); } catch { }
            }
            catch { }
        }

        /// <summary>Ключи выборок — в одном месте, чтобы читали и писали одно и то же.</summary>
        public static string DmKey(int partnerId) => "dm_" + partnerId;
        public static string GroupKey(int groupId) => "grp_" + groupId;
        public static string ChannelKey(int channelId) => "chan_" + channelId;
        public const string ChatsKey = "chats";
        public const string GroupsKey = "groups";
        public const string ServersKey = "servers";
        public static string ServerChannelsKey(int serverId) => "srv_" + serverId + "_channels";
    }
}
