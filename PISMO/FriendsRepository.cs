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

        /// <summary>Создаёт таблицу friends (+status) и users.dm_privacy при необходимости.</summary>
        public static void EnsureTable()
        {
            if (_ensured) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var cmd = new MySqlCommand(
                    "CREATE TABLE IF NOT EXISTS friends (" +
                    "user_id INT NOT NULL, friend_id INT NOT NULL, " +
                    "status TINYINT NOT NULL DEFAULT 0, " +   // 0=заявка, 1=приняты
                    "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                    "PRIMARY KEY (user_id, friend_id))", conn))
                    cmd.ExecuteNonQuery();
                // Старая таблица без status: добавляем с DEFAULT 1 — существующие
                // дружбы (до появления заявок) остаются принятыми.
                try
                {
                    using var alt = new MySqlCommand(
                        "ALTER TABLE friends ADD COLUMN status TINYINT NOT NULL DEFAULT 1", conn);
                    alt.ExecuteNonQuery();
                }
                catch { /* колонка уже есть */ }
                // Приватность личных сообщений.
                try
                {
                    using var alt2 = new MySqlCommand(
                        "ALTER TABLE users ADD COLUMN dm_privacy TINYINT NOT NULL DEFAULT 0", conn);
                    alt2.ExecuteNonQuery();
                }
                catch { /* колонка уже есть */ }
                _ensured = true;
            }
            catch { /* нет связи — попробуем позже */ }
        }

        /// <summary>Друзья ли (принятая заявка в ЛЮБОМ направлении).</summary>
        public static bool IsFriend(int a, int b)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM friends WHERE status=1 AND " +
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
                    "SELECT user_id, friend_id FROM friends WHERE status=1 AND (user_id=@me OR friend_id=@me)", conn);
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

        /// <summary>0 = писать могут все, 1 = только друзья.</summary>
        public static int GetDmPrivacy(int uid)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT dm_privacy FROM users WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", uid);
                var o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? 0 : Convert.ToInt32(o);
            }
            catch { return 0; }
        }

        public static void SetDmPrivacy(int uid, int mode)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("UPDATE users SET dm_privacy=@m WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@m", mode);
                cmd.Parameters.AddWithValue("@id", uid);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>Может ли me написать them (учитывая приватность them).</summary>
        public static bool CanMessage(int me, int them)
            => GetDmPrivacy(them) == 0 || IsFriend(me, them);
    }
}
