using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Кэш и загрузка пользовательских аватарок (users.avatar_data BLOB).
    /// Загрузка идёт в фоне (не на UI-потоке), готовая картинка кэшируется в
    /// памяти; при готовности поднимается событие AvatarLoaded для перерисовки.
    /// Если у пользователя нет аватарки — в кэше хранится null (рисуется буква).
    /// </summary>
    public static class AvatarStore
    {
        private static readonly ConcurrentDictionary<int, Image> _cache = new();
        private static readonly HashSet<int> _loading = new();
        private static bool _columnOk = true;

        /// <summary>Поднимается (в фоновом потоке!) когда аватар загружен.</summary>
        public static event Action<int> AvatarLoaded;

        /// <summary>Готовый аватар из кэша или null (не трогает БД).</summary>
        public static Image Get(int uid) => _cache.TryGetValue(uid, out var img) ? img : null;

        /// <summary>Запускает фоновую загрузку аватара, если ещё не в кэше.</summary>
        public static void EnsureLoaded(int uid)
        {
            if (!_columnOk) return;
            if (_cache.ContainsKey(uid)) return;
            lock (_loading) { if (!_loading.Add(uid)) return; }

            Task.Run(() =>
            {
                Image img = null;
                bool transientError = false;
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand("SELECT avatar_data FROM users WHERE id=@id", conn);
                    cmd.Parameters.AddWithValue("@id", uid);
                    var o = cmd.ExecuteScalar();
                    if (o != null && o != DBNull.Value)
                    {
                        var bytes = (byte[])o;
                        if (bytes.Length > 0)
                        {
                            using var ms = new MemoryStream(bytes);
                            img = Image.FromStream(ms);
                        }
                    }
                }
                catch (MySqlException mex)
                {
                    // Колонку отключаем НАВСЕГДА только если её реально нет
                    // (1054 = Unknown column). Любая другая ошибка (таймаут/обрыв
                    // под VPN, too many connections и т.п.) — временная: НЕ латчим
                    // _columnOk и НЕ кэшируем результат, чтобы попробовать ещё раз.
                    if (mex.Number == 1054) _columnOk = false;
                    else transientError = true;
                }
                catch { transientError = true; }

                lock (_loading) { _loading.Remove(uid); }

                if (transientError) return; // дадим следующему вызову попробовать снова

                _cache[uid] = img; // null допустим — «загружено, аватара нет»
                try { AvatarLoaded?.Invoke(uid); } catch { }
            });
        }

        /// <summary>Принудительно кладёт аватар в кэш (после загрузки своей).</summary>
        public static void Set(int uid, Image img)
        {
            if (_cache.TryGetValue(uid, out var old) && old != null && !ReferenceEquals(old, img))
            { try { old.Dispose(); } catch { } }
            _cache[uid] = img;
            try { AvatarLoaded?.Invoke(uid); } catch { }
        }

        public static void Invalidate(int uid) => _cache.TryRemove(uid, out _);

        /// <summary>Рисует круглый аватар. Возвращает false, если аватара нет
        /// (тогда вызывающий рисует букву-заглушку) и запускает фоновую загрузку.</summary>
        public static bool DrawAvatar(Graphics g, int uid, int x, int y, int size)
        {
            var av = Get(uid);
            if (av == null) { EnsureLoaded(uid); return false; }
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = new GraphicsPath();
                path.AddEllipse(x, y, size, size);
                var oldClip = g.Clip;
                g.SetClip(path);
                g.DrawImage(av, x, y, size, size);
                g.Clip = oldClip;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Сохраняет аватар текущего пользователя в БД и кэш.</summary>
        public static bool SaveMyAvatar(int uid, byte[] imageBytes)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("UPDATE users SET avatar_data=@a WHERE id=@id", conn);
                cmd.Parameters.Add("@a", MySqlDbType.LongBlob).Value = imageBytes;
                cmd.Parameters.AddWithValue("@id", uid);
                cmd.ExecuteNonQuery();

                try { using var ms = new MemoryStream(imageBytes); Set(uid, Image.FromStream(ms)); } catch { }
                return true;
            }
            catch { return false; }
        }
    }
}
