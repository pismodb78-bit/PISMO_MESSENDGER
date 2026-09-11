using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MySqlConnector;

namespace PISMO
{
    /// <summary>
    /// Закреплённые ЧАТЫ: собеседники, чьи диалоги прижаты к верху списка
    /// личных сообщений (ниже групп) независимо от давности переписки.
    ///
    /// ГДЕ ОНИ ЖИВУТ. В базе, таблица chat_pins (миграция 19), — поэтому
    /// закрепы у аккаунта одни и те же на компьютере и в телефоне. Раньше
    /// каждый клиент хранил свой список у себя, и один и тот же человек видел
    /// РАЗНЫЙ порядок списка на разных устройствах.
    ///
    /// Локальный файл остался, но теперь это КЕШ, а не хранилище. Он нужен по
    /// двум причинам: список спрашивают синхронно, прямо при отрисовке каждой
    /// строки сайдбара (ходить за этим в базу нельзя), и он же держит порядок,
    /// когда базы не видно.
    ///
    /// Прежние закрепы не теряются: при первом успешном чтении из базы всё,
    /// что лежало в файле, туда доливается — один раз, по метке рядом с
    /// файлом. Без метки открепление, сделанное на телефоне, воскресало бы при
    /// каждом запуске компьютера.
    /// </summary>
    public static class ChatPins
    {
        private static readonly object _lock = new();
        private static HashSet<int> _ids;
        private static int _loadedFor = -1;
        private static DateTime _lastFetch = DateTime.MinValue;
        private static bool _fetching;

        /// <summary>Набор изменился — список чатов пора пересобрать.</summary>
        public static event Action Changed;

        private static string PathFor(int me) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PISMO", $"pinned_chats_{me}.txt");

        /// <summary>Метка «старые закрепы уже перенесены в базу».</summary>
        private static string MigratedMarkFor(int me) => PathFor(me) + ".synced";

        private static HashSet<int> Load()
        {
            int me = UserSession.EffectiveId;
            lock (_lock)
            {
                if (_ids != null && _loadedFor == me) return _ids;
                var set = new HashSet<int>();
                try
                {
                    string p = PathFor(me);
                    if (File.Exists(p))
                        foreach (var line in File.ReadAllLines(p))
                            if (int.TryParse(line.Trim(), out var id)) set.Add(id);
                }
                catch { }
                _ids = set;
                _loadedFor = me;
                return set;
            }
        }

        private static void SaveCache(int me, IEnumerable<int> ids)
        {
            try
            {
                string p = PathFor(me);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllLines(p, ids.Select(i => i.ToString()));
            }
            catch { }
        }

        /// <summary>Чат с этим пользователем закреплён?</summary>
        public static bool IsPinned(int uid) { lock (_lock) { return Load().Contains(uid); } }

        /// <summary>Закрепить/открепить чат. Возвращает НОВОЕ состояние (true = закреплён).</summary>
        public static bool Toggle(int uid)
        {
            int me = UserSession.EffectiveId;
            bool nowPinned;
            lock (_lock)
            {
                var set = Load();
                nowPinned = set.Add(uid);
                if (!nowPinned) set.Remove(uid);
                SaveCache(me, set);
            }
            // В базу пишем в фоне: нажатие не должно ждать сервер, а список
            // уже переставлен по кешу.
            System.Threading.Tasks.Task.Run(() => WriteDb(me, uid, nowPinned));
            return nowPinned;
        }

        private static void WriteDb(int me, int uid, bool pinned)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = pinned
                    ? new MySqlCommand(
                        "INSERT IGNORE INTO chat_pins (user_id, scope, target_id) VALUES (@me,0,@t)", conn)
                    : new MySqlCommand(
                        "DELETE FROM chat_pins WHERE user_id=@me AND scope=0 AND target_id=@t", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@t", uid);
                cmd.ExecuteNonQuery();
            }
            catch { /* не дошло — останется в кеше и доедет при следующем закрепе */ }
        }

        /// <summary>
        /// Подтягивает закрепы из базы. Не чаще раза в пятнадцать секунд:
        /// список чатов перерисовывается каждые две с половиной, и запрос на
        /// каждую перерисовку был бы лишним походом к серверу.
        /// </summary>
        public static void EnsureFresh(bool force = false)
        {
            int me = UserSession.EffectiveId;
            if (me <= 0) return;
            lock (_lock)
            {
                if (_fetching) return;
                if (!force && (DateTime.UtcNow - _lastFetch).TotalSeconds < 15) return;
                _fetching = true;
                _lastFetch = DateTime.UtcNow;
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var fromDb = new HashSet<int>();
                    using (var conn = DBHelper.OpenConnection())
                    using (var cmd = new MySqlCommand(
                        "SELECT target_id FROM chat_pins WHERE user_id=@me AND scope=0", conn))
                    {
                        cmd.Parameters.AddWithValue("@me", me);
                        using var rd = cmd.ExecuteReader();
                        while (rd.Read()) fromDb.Add(Convert.ToInt32(rd[0]));
                    }

                    // Разовый перенос того, что копилось у этой машины до
                    // общего хранилища. Метка обязательна: без неё открепление,
                    // сделанное на телефоне, возвращалось бы при каждом запуске.
                    string mark = MigratedMarkFor(me);
                    if (!File.Exists(mark))
                    {
                        int[] local;
                        lock (_lock) local = Load().ToArray();
                        foreach (int id in local)
                            if (!fromDb.Contains(id)) { WriteDb(me, id, true); fromDb.Add(id); }
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(mark)!);
                            File.WriteAllText(mark, DateTime.UtcNow.ToString("O"));
                        }
                        catch { }
                    }

                    bool changed;
                    lock (_lock)
                    {
                        var cur = Load();
                        changed = !cur.SetEquals(fromDb);
                        if (changed)
                        {
                            _ids = fromDb;
                            _loadedFor = me;
                            SaveCache(me, fromDb);
                        }
                    }
                    if (changed) { try { Changed?.Invoke(); } catch { } }
                }
                catch { /* базы не видно — живём по кешу */ }
                finally { lock (_lock) { _fetching = false; } }
            });
        }

        /// <summary>Сброс при смене пользователя: чужие закрепы не наши.</summary>
        public static void Reset()
        {
            lock (_lock) { _ids = null; _loadedFor = -1; _lastFetch = DateTime.MinValue; }
        }
    }
}
