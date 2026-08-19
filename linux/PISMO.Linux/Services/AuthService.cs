using System;
using System.Data;
using MySql.Data.MySqlClient;

namespace PISMO.Linux
{
    /// <summary>
    /// Вход в аккаунт — та же логика, что в Windows-клиенте (LoginForm.DoLogin),
    /// но без UI: блокировка перебора (RateLimiter), проверка пароля
    /// (PasswordHasher, с авто-перехешем legacy-plaintext), выдача JWT сессии.
    /// Использует переиспользованные DBHelper / PasswordHasher / JwtAuth /
    /// UserSession из общего кода Windows-клиента.
    /// </summary>
    public static class AuthService
    {
        public sealed class Result
        {
            public bool Ok;
            public string Error;          // текст для показа, если !Ok
            public TimeSpan LockLeft;     // > 0 => вход временно заблокирован
        }

        public static Result Login(string login, string password)
        {
            login = (login ?? "").Trim();
            password = password ?? "";
            if (login.Length == 0 || password.Length == 0)
                return new Result { Error = "Заполните логин и пароль." };

            var lockLeft = RateLimiter.LoginLockRemaining(login);
            if (lockLeft > TimeSpan.Zero)
                return new Result
                {
                    LockLeft = lockLeft,
                    Error = $"Слишком много попыток. Повторите через {Math.Ceiling(lockLeft.TotalSeconds):0} с."
                };

            try
            {
                using var conn = DBHelper.OpenConnection();

                DataRow row;
                using (var cmd = new MySqlCommand(
                    "SELECT id, Name, Surname, role, password FROM users WHERE login=@l", conn))
                {
                    cmd.Parameters.AddWithValue("@l", login);
                    var dt = new DataTable();
                    new MySqlDataAdapter(cmd).Fill(dt);
                    if (dt.Rows.Count == 0)
                    {
                        RateLimiter.RegisterLoginFailure(login);
                        return new Result { Error = "Неверный логин или пароль." };
                    }
                    row = dt.Rows[0];
                }

                string stored = row["password"]?.ToString() ?? "";
                if (!PasswordHasher.Verify(password, stored))
                {
                    RateLimiter.RegisterLoginFailure(login);
                    return new Result { Error = "Неверный логин или пароль." };
                }

                RateLimiter.RegisterLoginSuccess(login);
                UserSession.UserId = Convert.ToInt32(row["id"]);
                UserSession.UserName = $"{row["Name"]} {row["Surname"]}".Trim();
                UserSession.Role = row["role"].ToString().ToLower();
                if (string.IsNullOrWhiteSpace(UserSession.UserName))
                    UserSession.UserName = login;

                // Миграция legacy-plaintext → PBKDF2 при первом успешном входе.
                if (PasswordHasher.NeedsUpgrade(stored))
                {
                    try
                    {
                        using var upd = new MySqlCommand(
                            "UPDATE users SET password=@p WHERE id=@id", conn);
                        upd.Parameters.AddWithValue("@p", PasswordHasher.Hash(password));
                        upd.Parameters.AddWithValue("@id", UserSession.UserId);
                        upd.ExecuteNonQuery();
                    }
                    catch { /* миграция не критична для входа */ }
                }
            }
            catch (Exception ex)
            {
                return new Result { Error = "Ошибка БД: " + ex.Message };
            }

            // JWT сессии (uid/login/срок) — используется WS-сервером.
            UserSession.AuthToken = JwtAuth.Create(UserSession.UserId, login);
            return new Result { Ok = true };
        }
    }
}
