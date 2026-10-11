using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using MySqlConnector;

namespace PISMO
{
    /// <summary>
    /// Оформление серверов: картинка значка и его цвет.
    /// </summary>
    /// <remarks>
    /// Картинка лежит в servers.avatar_data и нужна рейлу при каждой
    /// перерисовке, поэтому держим её в памяти. Срок годности — три минуты,
    /// как у аватаров пользователей: значок меняет владелец, а видят его все
    /// участники, и без протухания они остались бы со старой картинкой до
    /// перезапуска программы.
    ///
    /// Старые картинки не освобождаем намеренно: ссылка на них остаётся в
    /// обработчике Paint у кружка рейла, и Dispose в момент перерисовки роняет
    /// GDI+. Значки — 256×256, серверов у человека единицы: держать их все
    /// дешевле, чем ловить эту гонку.
    /// </remarks>
    internal static class ServerLook
    {
        private const int TtlSeconds = 180;

        private static readonly Dictionary<int, Image?> _icons = new();
        private static readonly Dictionary<int, DateTime> _readAt = new();

        /// <summary>Картинка из памяти — возможно, устаревшая. null = буквенный значок.</summary>
        public static Image? Cached(int serverId)
        {
            lock (_icons) return _icons.TryGetValue(serverId, out var img) ? img : null;
        }

        /// <summary>Свежая ли запись в памяти — иначе стоит перечитать.</summary>
        public static bool Fresh(int serverId)
        {
            lock (_icons)
                return _readAt.TryGetValue(serverId, out var at)
                       && (DateTime.UtcNow - at).TotalSeconds < TtlSeconds;
        }

        public static void Remember(int serverId, Image? img)
        {
            lock (_icons)
            {
                _icons[serverId] = img;
                _readAt[serverId] = DateTime.UtcNow;
            }
        }

        /// <summary>Забыть сервер — например, после смены его номера.</summary>
        public static void Forget(int serverId)
        {
            lock (_icons) { _icons.Remove(serverId); _readAt.Remove(serverId); }
        }

        /// <summary>
        /// Читает картинку из базы и запоминает её. Зовётся из фонового потока:
        /// блоб тяжёлый, а рейл перерисовывается в потоке интерфейса.
        /// </summary>
        public static Image? Load(int serverId)
        {
            Image? img = null;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT avatar_data FROM servers WHERE id=@s", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                var o = cmd.ExecuteScalar();
                if (o is byte[] data && data.Length > 0) img = Decode(data);
            }
            catch
            {
                // Нет связи или колонки — значок останется буквенным, но
                // «прочитано» отмечаем: иначе рейл перезапрашивал бы без конца.
            }
            Remember(serverId, img);
            return img;
        }

        /// <summary>
        /// Картинка из байтов. Через WIC, если GDI+ формат не знает: значок мог
        /// быть залит с телефона, а там это мог быть и WebP.
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

        /// <summary>Цвет значка из «#RRGGBB». null — цвет по умолчанию (по id).</summary>
        public static Color? ParseColor(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try { return ColorTranslator.FromHtml(hex.Trim()); }
            catch { return null; }
        }
    }
}
