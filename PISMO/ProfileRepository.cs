using System;
using MySqlConnector;

namespace PISMO
{
    public sealed class ProfileData
    {
        public int Id;
        public string Name = "";
        public string Surname = "";
        public string Login = "";
        public string About = "";
        public string SocialLinks = ""; // строки "label|url" через \n
    }

    /// <summary>Чтение/запись профиля пользователя (имя, логин, о себе, ссылки,
    /// баннер). Аватар — через AvatarStore.</summary>
    public static class ProfileRepository
    {
        public static ProfileData Load(int uid)
        {
            var p = new ProfileData { Id = uid };
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT Name, Surname, login, about, social_links FROM users WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", uid);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    p.Name = r["Name"]?.ToString() ?? "";
                    p.Surname = r["Surname"]?.ToString() ?? "";
                    p.Login = r["login"]?.ToString() ?? "";
                    p.About = r["about"] == DBNull.Value ? "" : r["about"].ToString();
                    p.SocialLinks = r["social_links"] == DBNull.Value ? "" : r["social_links"].ToString();
                }
            }
            catch (MySqlException) { /* колонок about/social_links может не быть до миграции */ }
            catch { }
            return p;
        }

        /// <summary>Свободен ли логин (не занят другим пользователем).</summary>
        public static bool IsLoginAvailable(string login, int exceptUid)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM users WHERE login=@l AND id<>@id", conn);
                cmd.Parameters.AddWithValue("@l", login);
                cmd.Parameters.AddWithValue("@id", exceptUid);
                return Convert.ToInt32(cmd.ExecuteScalar()) == 0;
            }
            catch { return false; }
        }

        /// <summary>Сохраняет основные поля профиля. Возвращает текст ошибки или null.</summary>
        public static string Save(ProfileData p)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "UPDATE users SET Name=@n, Surname=@s, login=@l, about=@a, social_links=@sl WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@n", p.Name ?? "");
                cmd.Parameters.AddWithValue("@s", p.Surname ?? "");
                cmd.Parameters.AddWithValue("@l", p.Login ?? "");
                cmd.Parameters.AddWithValue("@a", (object)(p.About ?? "") );
                cmd.Parameters.AddWithValue("@sl", (object)(p.SocialLinks ?? ""));
                cmd.Parameters.AddWithValue("@id", p.Id);
                cmd.ExecuteNonQuery();
                return null;
            }
            catch (MySqlException mex) when (mex.Number == 1054)
            {
                // Нет колонок about/social_links — сохраняем хотя бы имя/логин.
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand(
                        "UPDATE users SET Name=@n, Surname=@s, login=@l WHERE id=@id", conn);
                    cmd.Parameters.AddWithValue("@n", p.Name ?? "");
                    cmd.Parameters.AddWithValue("@s", p.Surname ?? "");
                    cmd.Parameters.AddWithValue("@l", p.Login ?? "");
                    cmd.Parameters.AddWithValue("@id", p.Id);
                    cmd.ExecuteNonQuery();
                    return "Поля «о себе»/ссылки не сохранены: выполните profile_migration.sql.";
                }
                catch (Exception ex) { return ex.Message; }
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Смена пароля. Возвращает текст ошибки или null.</summary>
        public static string ChangePassword(int uid, string oldPass, string newPass)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var chk = new MySqlCommand("SELECT password FROM users WHERE id=@id", conn))
                {
                    chk.Parameters.AddWithValue("@id", uid);
                    var cur = chk.ExecuteScalar()?.ToString() ?? "";
                    if (!PasswordHasher.Verify(oldPass, cur)) return "Текущий пароль неверный.";
                }
                using var cmd = new MySqlCommand("UPDATE users SET password=@p WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@p", PasswordHasher.Hash(newPass)); // bcrypt
                cmd.Parameters.AddWithValue("@id", uid);
                cmd.ExecuteNonQuery();
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ── Баннер (фон профиля) ────────────────────────────────────────
        private static bool _bannerColOk = true;

        public static byte[] LoadBanner(int uid)
        {
            if (!_bannerColOk) return null;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT banner_data FROM users WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", uid);
                var o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? null : (byte[])o;
            }
            catch (MySqlException) { _bannerColOk = false; return null; }
            catch { return null; }
        }

        public static bool SaveBanner(int uid, byte[] data)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("UPDATE users SET banner_data=@b WHERE id=@id", conn);
                cmd.Parameters.Add("@b", MySqlDbType.LongBlob).Value = (object)data ?? DBNull.Value;
                cmd.Parameters.AddWithValue("@id", uid);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch { return false; }
        }
    }
}
