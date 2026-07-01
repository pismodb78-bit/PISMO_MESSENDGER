using System;
using System.Collections.Generic;
using System.Data;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Друзья — личный список пользователя (таблица friends). Направленный:
    /// "я добавил его". Таблица создаётся автоматически при первом обращении
    /// (EnsureTable), поэтому отдельная миграция не обязательна.
    /// </summary>
    public static class FriendsRepository
    {
        public sealed class UserHit
        {
            public int Id;
            public string Name;
            public string Login;
            public bool IsFriend;
        }

        private static bool _ensured;

        /// <summary>Создаёт таблицу friends, если её ещё нет (один раз за сессию).</summary>
        public static void EnsureTable()
        {
            if (_ensured) return;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "CREATE TABLE IF NOT EXISTS friends (" +
                    "user_id INT NOT NULL, friend_id INT NOT NULL, " +
                    "created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP, " +
                    "PRIMARY KEY (user_id, friend_id))", conn);
                cmd.ExecuteNonQuery();
                _ensured = true;
            }
            catch { /* нет прав/связи — попробуем позже */ }
        }

        public static bool IsFriend(int me, int them)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM friends WHERE user_id=@me AND friend_id=@them LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@them", them);
                return cmd.ExecuteScalar() != null;
            }
            catch { return false; }
        }

        public static void Add(int me, int them)
        {
            if (me == them) return;
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "INSERT IGNORE INTO friends (user_id, friend_id) VALUES (@me, @them)", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@them", them);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        public static void Remove(int me, int them)
        {
            EnsureTable();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "DELETE FROM friends WHERE user_id=@me AND friend_id=@them", conn);
                cmd.Parameters.AddWithValue("@me", me);
                cmd.Parameters.AddWithValue("@them", them);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        /// <summary>Множество id моих друзей (пусто, если таблицы нет).</summary>
        public static HashSet<int> ListIds(int me)
        {
            EnsureTable();
            var set = new HashSet<int>();
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT friend_id FROM friends WHERE user_id=@me", conn);
                cmd.Parameters.AddWithValue("@me", me);
                using var r = cmd.ExecuteReader();
                while (r.Read()) set.Add(Convert.ToInt32(r["friend_id"]));
            }
            catch { }
            return set;
        }

        /// <summary>Поиск пользователей по имени/логину (для окна «добавить друга»).
        /// Ведущие @ и # в запросе игнорируются (логин вводят как @name или #name).</summary>
        public static List<UserHit> Search(int me, string query)
        {
            EnsureTable();
            var list = new List<UserHit>();
            query = (query ?? "").Trim().TrimStart('@', '#');
            if (query.Length == 0) return list;
            try
            {
                var friends = ListIds(me);
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
                    string nm = string.Join(" ",
                        new[] { r["Name"]?.ToString(), r["Surname"]?.ToString() }).Trim();
                    if (string.IsNullOrWhiteSpace(nm)) nm = login;
                    list.Add(new UserHit { Id = id, Name = nm, Login = login, IsFriend = friends.Contains(id) });
                }
            }
            catch { }
            return list;
        }
    }
}
