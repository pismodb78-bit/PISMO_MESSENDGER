using System;
using System.Collections.Generic;
using System.Data;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Друзья через ЗАЯВКИ (как в Discord): A отправляет заявку (status=0,
    /// pending), B принимает (status=1, accepted) — только после этого они
    /// друзья (взаимно, в любом направлении строки). Плюс настройка приватности
    /// users.dm_privacy: 0 = писать могут все, 1 = только друзья.
    /// Таблица/колонки создаются автоматически (EnsureTable).
    /// </summary>
    public static class FriendsRepository
    {
        public enum Relation { None, Friend, OutgoingPending, IncomingPending }

        public sealed class UserHit
        {
            public int Id;
            public string Name;
            public string Login;
            public Relation Rel;
        }

        private static bool _ensured;

        /// <summary>Есть ли в таблице friends колонка status (после миграции). Если
        /// миграция почему-то не прошла — запросы строятся без неё, чтобы не падать
        /// с «Unknown column 'f.status'».</summary>
        public static bool HasStatus { get; private set; }

        /// <summary>Есть ли колонка users.dm_privacy (запасное хранилище приватности).</summary>
        public static bool HasDmPrivacy { get; private set; }

        /// <summary>SQL-предикат «принятая дружба» для алиаса таблицы friends.
        /// С колонкой status → "alias.status=1"; без неё любая строка = дружба.</summary>
        public static string AcceptedPredicate(string alias)
            => HasStatus ? $"{alias}.status=1" : "(1=1)";

        /// <summary>Создаёт таблицу friends (+status) и users.dm_privacy при необходимости.
        /// Каждый шаг независим; наличие колонок проверяется через information_schema,
        /// чтобы миграция была надёжной на любой существующей БД.</summary>
        public static void EnsureTable()
        {
            if (_ensured) return;
            try
            {
                using var conn = DBHelper.OpenConnection();

                try
                {
                    using var cmd = new MySqlCommand(
                        "CREATE TABLE IF NOT EXISTS friends (" +
                        "user_id INT NOT NULL, friend_id INT NOT NULL, " +
                        "status TINYINT NOT NULL DEFAULT 0, " +   // 0=заявка, 1=приняты
                        "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                        "PRIMARY KEY (user_id, friend_id))", conn);
                    cmd.ExecuteNonQuery();
                }
                catch { }

                // Старая таблица без status → добавляем с DEFAULT 1 (существующие
                // дружбы остаются принятыми). Проверяем наличие явно.
                HasStatus = EnsureColumn(conn, "friends", "status", "TINYINT NOT NULL DEFAULT 1");

                // Приватность храним в ОТДЕЛЬНОЙ таблице (CREATE TABLE надёжнее,
                // чем ALTER на импортированной БД). users.dm_privacy — как запасной
                // вариант для старых сборок.
                try
                {
                    using var prefs = new MySqlCommand(
                        "CREATE TABLE IF NOT EXISTS user_prefs (" +
                        "user_id INT NOT NULL PRIMARY KEY, " +
                        "dm_privacy TINYINT NOT NULL DEFAULT 0)", conn);
                    prefs.ExecuteNonQuery();
                }
                catch { }
                HasDmPrivacy = EnsureColumn(conn, "users", "dm_privacy", "TINYINT NOT NULL DEFAULT 0");

                _ensured = true;
            }
            catch { /* нет связи — попробуем позже (флаг не ставим) */ }
        }

        /// <summary>Гарантирует наличие колонки; возвращает true, если она есть по
        /// итогу (уже была или успешно добавлена).</summary>
        private static bool EnsureColumn(MySqlConnection conn, string table, string column, string ddl)
        {
            try
            {
                if (ColumnExists(conn, table, column)) return true;
                try
                {
                    using var alt = new MySqlCommand($"ALTER TABLE `{table}` ADD COLUMN `{column}` {ddl}", conn);
                    alt.ExecuteNonQuery();
                }
                catch { /* напр. параллельно уже добавили — проверим ниже */ }
                return ColumnExists(conn, table, column);
            }
            catch { return false; }
        }

        private static bool ColumnExists(MySqlConnection conn, string table, string column)
        {
            using var chk = new MySqlCommand(
                "SELECT COUNT(*) FROM information_schema.COLUMNS " +
                "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t AND COLUMN_NAME=@c", conn);
            chk.Parameters.AddWithValue("@t", table);
            chk.Parameters.AddWithValue("@c", column);
            return Convert.ToInt32(chk.ExecuteScalar()) > 0;
        }

        /// <summary>Друзья ли (принятая заявка в ЛЮБОМ направлении).</summary>
        public static bool IsFriend(int a, int b)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM friends WHERE " + AcceptedPredicate("friends") + " AND " +
                    "((user_id=@a AND friend_id=@b) OR (user_id=@b AND friend_id=@a)) LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@a", a);
                cmd.Parameters.AddWithValue("@b", b);
                return cmd.ExecuteScalar() != null;
            }
            catch { return false; }
        }

        /// <summary>Отношение me→them (друг / исходящая / входящая заявка / ничего).</summary>
        public static Relation GetRelation(int me, int them)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT user_id, status FROM friends WHERE " +
                    "(user_id=@me AND friend_id=@them) OR (user_id=@them AND friend_id=@me)", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@them", them);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int from = Convert.ToInt32(r["user_id"]);
                    int st = Convert.ToInt32(r["status"]);
                    if (st == 1) return Relation.Friend;
                    return from == me ? Relation.OutgoingPending : Relation.IncomingPending;
                }
            }
            catch { }
            return Relation.None;
        }

        /// <summary>Отправить заявку в друзья. Если встречная заявка уже есть —
        /// считается взаимным согласием (сразу друзья).</summary>
        public static void SendRequest(int me, int them)
        {
            if (me == them) return;
            EnsureTable();
            try
            {
                var rel = GetRelation(me, them);
                if (rel == Relation.Friend || rel == Relation.OutgoingPending) return;
                if (rel == Relation.IncomingPending) { AcceptRequest(me, them); return; }
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT IGNORE INTO friends (user_id, friend_id, status) VALUES (@me, @them, 0)", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@them", them);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>Принять заявку от requester (только адресат может принять).</summary>
        public static void AcceptRequest(int me, int requester)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE friends SET status=1 WHERE user_id=@req AND friend_id=@me", conn);
                cmd.Parameters.AddWithValue("@req", requester);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>Отклонить заявку от requester (удаляет её).</summary>
        public static void DeclineRequest(int me, int requester)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM friends WHERE user_id=@req AND friend_id=@me AND status=0", conn);
                cmd.Parameters.AddWithValue("@req", requester);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>Удалить из друзей / отменить заявку (обе стороны).</summary>
        public static void Remove(int me, int them)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM friends WHERE (user_id=@me AND friend_id=@them) " +
                    "OR (user_id=@them AND friend_id=@me)", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@them", them);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>Id всех принятых друзей (в любом направлении).</summary>
        public static HashSet<int> AcceptedIds(int me)
        {
            EnsureTable();
            var set = new HashSet<int>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT user_id, friend_id FROM friends WHERE " + AcceptedPredicate("friends") + " AND (user_id=@me OR friend_id=@me)", conn);
                cmd.Parameters.AddWithValue("@me", me);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int a = Convert.ToInt32(r["user_id"]), b = Convert.ToInt32(r["friend_id"]);
                    set.Add(a == me ? b : a);
                }
            }
            catch { }
            return set;
        }

        /// <summary>Входящие заявки (кто хочет добавить меня).</summary>
        public static List<UserHit> IncomingRequests(int me)
        {
            EnsureTable();
            var list = new List<UserHit>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT u.id, u.Name, u.Surname, u.login FROM friends f " +
                    "JOIN users u ON u.id=f.user_id WHERE f.friend_id=@me AND f.status=0", conn);
                cmd.Parameters.AddWithValue("@me", me);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    string nm = string.Join(" ", new[] { r["Name"]?.ToString(), r["Surname"]?.ToString() }).Trim();
                    string login = r["login"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(nm)) nm = login;
                    list.Add(new UserHit { Id = Convert.ToInt32(r["id"]), Name = nm, Login = login, Rel = Relation.IncomingPending });
                }
            }
            catch { }
            return list;
        }

        /// <summary>Исходящие заявки (кого я добавил, ещё не приняли).</summary>
        public static List<UserHit> OutgoingRequests(int me)
        {
            EnsureTable();
            var list = new List<UserHit>();
            if (!HasStatus) return list;   // без status исходящих заявок нет
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT u.id, u.Name, u.Surname, u.login FROM friends f " +
                    "JOIN users u ON u.id=f.friend_id WHERE f.user_id=@me AND f.status=0", conn);
                cmd.Parameters.AddWithValue("@me", me);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    string nm = string.Join(" ", new[] { r["Name"]?.ToString(), r["Surname"]?.ToString() }).Trim();
                    string login = r["login"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(nm)) nm = login;
                    list.Add(new UserHit { Id = Convert.ToInt32(r["id"]), Name = nm, Login = login, Rel = Relation.OutgoingPending });
                }
            }
            catch { }
            return list;
        }

        /// <summary>Все принятые друзья (с именами) — для страницы «Друзья».</summary>
        public static List<UserHit> Friends(int me)
        {
            EnsureTable();
            var list = new List<UserHit>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT u.id, u.Name, u.Surname, u.login FROM friends f " +
                    "JOIN users u ON u.id = IF(f.user_id=@me, f.friend_id, f.user_id) " +
                    "WHERE " + AcceptedPredicate("f") + " AND (f.user_id=@me OR f.friend_id=@me)", conn);
                cmd.Parameters.AddWithValue("@me", me);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    int id = Convert.ToInt32(r["id"]);
                    if (id == me) continue;
                    string nm = string.Join(" ", new[] { r["Name"]?.ToString(), r["Surname"]?.ToString() }).Trim();
                    string login = r["login"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(nm)) nm = login;
                    list.Add(new UserHit { Id = id, Name = nm, Login = login, Rel = Relation.Friend });
                }
            }
            catch { }
            return list;
        }

        /// <summary>Id тех, кто «в сети» (last_seen обновлялся недавно).</summary>
        public static HashSet<int> OnlineIds(IEnumerable<int> ids)
        {
            var set = new HashSet<int>();
            var idList = new List<int>(ids ?? Array.Empty<int>());
            if (idList.Count == 0) return set;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT id FROM users WHERE id IN (" + string.Join(",", idList) + ") " +
                    "AND last_seen IS NOT NULL AND TIMESTAMPDIFF(SECOND, last_seen, NOW()) <= 40", conn);
                using var r = cmd.ExecuteReader();
                while (r.Read()) set.Add(Convert.ToInt32(r["id"]));
            }
            catch { }
            return set;
        }

        /// <summary>Поиск пользователей (для «Добавить друга»); @ и # в начале игнорируются.</summary>
        public static List<UserHit> Search(int me, string query)
        {
            EnsureTable();
            var list = new List<UserHit>();
            query = (query ?? "").Trim().TrimStart('@', '#');
            if (query.Length == 0) return list;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT id, Name, Surname, login FROM users " +
                    "WHERE id <> @me AND (Name LIKE @q OR Surname LIKE @q OR login LIKE @q) " +
                    "ORDER BY (login = @exact) DESC, Name LIMIT 40", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@q", "%" + query + "%");
                cmd.Parameters.AddWithValue("@exact", query);
                var dt = new DataTable(); new MySqlDataAdapter(cmd).Fill(dt);
                foreach (DataRow r in dt.Rows)
                {
                    int id = Convert.ToInt32(r["id"]);
                    string login = r["login"]?.ToString() ?? "";
                    string nm = string.Join(" ", new[] { r["Name"]?.ToString(), r["Surname"]?.ToString() }).Trim();
                    if (string.IsNullOrWhiteSpace(nm)) nm = login;
                    list.Add(new UserHit { Id = id, Name = nm, Login = login, Rel = GetRelation(me, id) });
                }
            }
            catch { }
            return list;
        }

        // ── Приватность личных сообщений ────────────────────────────────

        /// <summary>0 = писать могут все, 1 = только друзья. Основное хранилище —
        /// user_prefs; users.dm_privacy читается как запасное (старые сборки).</summary>
        public static int GetDmPrivacy(int uid)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var cmd = new MySqlCommand(
                    "SELECT dm_privacy FROM user_prefs WHERE user_id=@id", conn))
                {
                    cmd.Parameters.AddWithValue("@id", uid);
                    var o = cmd.ExecuteScalar();
                    if (o != null && o != DBNull.Value) return Convert.ToInt32(o);
                }
                if (HasDmPrivacy)
                {
                    using var cmd2 = new MySqlCommand("SELECT dm_privacy FROM users WHERE id=@id", conn);
                    cmd2.Parameters.AddWithValue("@id", uid);
                    var o2 = cmd2.ExecuteScalar();
                    if (o2 != null && o2 != DBNull.Value) return Convert.ToInt32(o2);
                }
            }
            catch { }
            return 0;
        }

        public static void SetDmPrivacy(int uid, int mode)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var cmd = new MySqlCommand(
                    "INSERT INTO user_prefs (user_id, dm_privacy) VALUES (@id, @m) " +
                    "ON DUPLICATE KEY UPDATE dm_privacy=@m", conn))
                {
                    cmd.Parameters.AddWithValue("@id", uid);
                    cmd.Parameters.AddWithValue("@m", mode);
                    cmd.ExecuteNonQuery();
                }
                if (HasDmPrivacy)
                {
                    try
                    {
                        using var cmd2 = new MySqlCommand("UPDATE users SET dm_privacy=@m WHERE id=@id", conn);
                        cmd2.Parameters.AddWithValue("@m", mode);
                        cmd2.Parameters.AddWithValue("@id", uid);
                        cmd2.ExecuteNonQuery();
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>Может ли me написать them (учитывая приватность them).
        /// isAdmin — админ обходит ограничение «только друзья».</summary>
        public static bool CanMessage(int me, int them, bool isAdmin = false)
            => isAdmin || GetDmPrivacy(them) == 0 || IsFriend(me, them);
    }
}
