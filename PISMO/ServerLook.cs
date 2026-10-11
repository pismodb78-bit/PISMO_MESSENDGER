using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using MySqlConnector;

namespace PISMO
{
    /// <summary>
    /// Картинки значков — серверов и групп.
    /// </summary>
    /// <remarks>
    /// Картинка лежит в самой таблице (servers.avatar_data,
    /// group_chats.avatar_data) и нужна при каждой перерисовке списка,
    /// поэтому держим её в памяти. Срок годности — три минуты, как у
    /// аватаров: значок меняет владелец, а видят его все, и без протухания
    /// они остались бы со старой картинкой до перезапуска программы.
    ///
    /// Старые картинки не освобождаем намеренно: ссылка на них остаётся в
    /// обработчике Paint, и Dispose в момент перерисовки роняет GDI+.
    /// Значки — 256×256, серверов и групп у человека единицы: держать их все
    /// дешевле, чем ловить эту гонку.
    /// </remarks>
    internal sealed class LookCache
    {
        private const int TtlSeconds = 180;

        private readonly string _table;
        private readonly Dictionary<int, Image?> _icons = new();
        private readonly Dictionary<int, DateTime> _readAt = new();

        public LookCache(string table) { _table = table; }

        /// <summary>Картинка из памяти — возможно, устаревшая. null = буквенный значок.</summary>
        public Image? Cached(int id)
        {
            lock (_icons) return _icons.TryGetValue(id, out var img) ? img : null;
        }

        /// <summary>Свежая ли запись в памяти — иначе стоит перечитать.</summary>
        public bool Fresh(int id)
        {
            lock (_icons)
                return _readAt.TryGetValue(id, out var at)
                       && (DateTime.UtcNow - at).TotalSeconds < TtlSeconds;
        }

        /// <summary>Есть ли среди этих значков тот, который пора перечитать.</summary>
        public bool AnyStale(IEnumerable<int> ids)
        {
            foreach (int id in ids) if (!Fresh(id)) return true;
            return false;
        }

        public void Remember(int id, Image? img)
        {
            lock (_icons) { _icons[id] = img; _readAt[id] = DateTime.UtcNow; }
        }

        public void Forget(int id)
        {
            lock (_icons) { _icons.Remove(id); _readAt.Remove(id); }
        }

        /// <summary>
        /// Читает картинку из базы и запоминает её. Зовётся из фонового
        /// потока: блоб тяжёлый, а списки рисуются в потоке интерфейса.
        /// </summary>
        public Image? Load(int id)
        {
            Image? img = null;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    $"SELECT avatar_data FROM {_table} WHERE id=@s", conn);
                cmd.Parameters.AddWithValue("@s", id);
                var o = cmd.ExecuteScalar();
                if (o is byte[] data && data.Length > 0) img = Look.Decode(data);
            }
            catch
            {
                // Нет связи или колонки — значок останется буквенным, но
                // «прочитано» отмечаем: иначе список перезапрашивал бы без конца.
            }
            Remember(id, img);
            return img;
        }
    }

    /// <summary>Общее для значков: разбор картинки и цвета.</summary>
    internal static class Look
    {
        /// <summary>
        /// Картинка из байтов. Через WIC, если GDI+ формат не знает: значок
        /// мог быть залит с телефона, а там это мог быть и WebP.
        /// </summary>
        public static Image? Decode(byte[]? data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                using var ms = new MemoryStream(data);
                using var src = Image.FromStream(ms);
                return new Bitmap(src);   // копия: поток закроется, картинка останется
            }
            catch { return MainForm.DecodeWithWic(data); }
        }

        /// <summary>Цвет значка из «#RRGGBB». null — цвет по умолчанию.</summary>
        public static Color? ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try { return ColorTranslator.FromHtml(hex.Trim()); }
            catch { return null; }
        }
    }

    /// <summary>Значки серверов.</summary>
    internal static class ServerLook
    {
        private static readonly LookCache C = new("servers");

        public static Image? Cached(int serverId) => C.Cached(serverId);
        public static bool Fresh(int serverId) => C.Fresh(serverId);
        public static bool AnyStale(IEnumerable<int> ids) => C.AnyStale(ids);
        public static void Remember(int serverId, Image? img) => C.Remember(serverId, img);
        public static void Forget(int serverId) => C.Forget(serverId);
        public static Image? Load(int serverId) => C.Load(serverId);
        public static Image? Decode(byte[]? data) => Look.Decode(data);
        public static Color? ParseColor(string? hex) => Look.ParseColor(hex);
    }

    /// <summary>Значки групп — устроены так же, как у серверов.</summary>
    internal static class GroupLook
    {
        private static readonly LookCache C = new("group_chats");

        public static Image? Cached(int groupId) => C.Cached(groupId);
        public static bool Fresh(int groupId) => C.Fresh(groupId);
        public static bool AnyStale(IEnumerable<int> ids) => C.AnyStale(ids);
        public static void Remember(int groupId, Image? img) => C.Remember(groupId, img);
        public static void Forget(int groupId) => C.Forget(groupId);
        public static Image? Load(int groupId) => C.Load(groupId);
        public static Image? Decode(byte[]? data) => Look.Decode(data);
        public static Color? ParseColor(string? hex) => Look.ParseColor(hex);
    }
}
