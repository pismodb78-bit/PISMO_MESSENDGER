using System;
using System.Collections.Generic;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Друзья — личный список пользователя (таблица friends, миграция
    /// scripts/friends_migration.sql). Направленный: "я добавил его".
    /// Все операции терпимы к отсутствию таблицы (если миграция не выполнена).
    /// </summary>
    public static class FriendsRepository
    {
        public static bool IsFriend(int me, int them)
        {
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
    }
}
