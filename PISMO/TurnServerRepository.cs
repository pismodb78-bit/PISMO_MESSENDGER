using System;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Репозиторий для работы с TURN-сервером и его ключами в БД.
    /// Отвечает за сохранение, загрузку и обновление конфигурации TURN сервера.
    /// </summary>
    internal static class TurnServerRepository
    {
        private const string TableName = "turn_servers";
        private static bool _tableCheckAttempted = false;
        private static bool _tableExists = false;

        /// <summary>Проверяет наличие таблицы turn_servers в БД.</summary>
        public static bool TableExists()
        {
            if (_tableCheckAttempted) return _tableExists;

            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT 1 FROM information_schema.TABLES WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@table", conn);
                cmd.Parameters.AddWithValue("@db", "bdauth");
                cmd.Parameters.AddWithValue("@table", TableName);
                
                _tableExists = cmd.ExecuteScalar() != null;
                _tableCheckAttempted = true;

                if (_tableExists)
                    System.Diagnostics.Debug.WriteLine("[TURN] Таблица turn_servers найдена в БД");
                else
                    System.Diagnostics.Debug.WriteLine("[TURN] Таблица turn_servers не найдена. Используются локальные настройки.");

                return _tableExists;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TURN] Ошибка проверки таблицы: {ex.Message}");
                _tableCheckAttempted = true;
                _tableExists = false;
                return false;
            }
        }

        /// <summary>Сохраняет или обновляет конфигурацию TURN сервера в БД.</summary>
        public static void SaveTurnServer(
            string serverName,
            string serverAddress,
            int serverPort,
            string transport,
            string username,
            string secretHex,
            int ttlSeconds,
            bool useTimeLimited)
        {
            if (!TableExists()) return; // Таблица не доступна, используются локальные настройки

            try
            {
                using var conn = DBHelper.OpenConnection();
                
                // Проверяем, существует ли запись
                using var checkCmd = new MySqlCommand(
                    $"SELECT id FROM {TableName} WHERE server_name=@name", conn);
                checkCmd.Parameters.AddWithValue("@name", serverName);
                var exists = checkCmd.ExecuteScalar() != null;

                if (exists)
                {
                    const string updateSql = $@"
UPDATE {TableName}
SET server_address=@addr, server_port=@port, transport=@transport,
    username=@user, secret_hex=@secret, ttl_seconds=@ttl,
    use_time_limited=@timeLimited, last_secret_update=UTC_TIMESTAMP()
WHERE server_name=@name";

                    using var cmd = new MySqlCommand(updateSql, conn);
                    cmd.Parameters.AddWithValue("@addr", serverAddress ?? "");
                    cmd.Parameters.AddWithValue("@port", serverPort);
                    cmd.Parameters.AddWithValue("@transport", transport ?? "tcp");
                    cmd.Parameters.AddWithValue("@user", username ?? "");
                    cmd.Parameters.AddWithValue("@secret", secretHex ?? "");
                    cmd.Parameters.AddWithValue("@ttl", ttlSeconds);
                    cmd.Parameters.AddWithValue("@timeLimited", useTimeLimited);
                    cmd.Parameters.AddWithValue("@name", serverName);
                    cmd.ExecuteNonQuery();

                    System.Diagnostics.Debug.WriteLine($"[TURN] Обновлена конфигурация в БД: {serverName}");
                }
                else
                {
                    const string insertSql = $@"
INSERT INTO {TableName}
(server_name, server_address, server_port, transport, username, 
 secret_hex, ttl_seconds, use_time_limited, last_secret_update)
VALUES (@name, @addr, @port, @transport, @user, @secret, @ttl, @timeLimited, UTC_TIMESTAMP())";

                    using var cmd = new MySqlCommand(insertSql, conn);
                    cmd.Parameters.AddWithValue("@name", serverName);
                    cmd.Parameters.AddWithValue("@addr", serverAddress ?? "");
                    cmd.Parameters.AddWithValue("@port", serverPort);
                    cmd.Parameters.AddWithValue("@transport", transport ?? "tcp");
                    cmd.Parameters.AddWithValue("@user", username ?? "");
                    cmd.Parameters.AddWithValue("@secret", secretHex ?? "");
                    cmd.Parameters.AddWithValue("@ttl", ttlSeconds);
                    cmd.Parameters.AddWithValue("@timeLimited", useTimeLimited);
                    cmd.ExecuteNonQuery();

                    System.Diagnostics.Debug.WriteLine($"[TURN] Сохранена конфигурация в БД: {serverName}");
                }
            }
            catch (MySqlException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TURN] Ошибка БД при сохранении: {ex.Number} - {ex.Message}");
                // Таблица недоступна, приложение работает с локальными настройками
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TURN] Ошибка сохранения конфигурации: {ex.Message}");
            }
        }

        /// <summary>Загружает конфигурацию TURN сервера из БД по имени.</summary>
        public static TurnServerConfig LoadTurnServer(string serverName)
        {
            if (!TableExists()) return null; // Таблица не доступна

            try
            {
                using var conn = DBHelper.OpenConnection();
                const string sql = $@"
SELECT server_name, server_address, server_port, transport, username,
       secret_hex, ttl_seconds, use_time_limited, last_secret_update
FROM {TableName}
WHERE server_name=@name";

                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@name", serverName);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new TurnServerConfig
                    {
                        ServerName = reader.GetString("server_name"),
                        ServerAddress = reader.GetString("server_address"),
                        ServerPort = reader.GetInt32("server_port"),
                        Transport = reader.GetString("transport"),
                        Username = reader.IsDBNull(reader.GetOrdinal("username")) 
                            ? "" : reader.GetString("username"),
                        SecretHex = reader.IsDBNull(reader.GetOrdinal("secret_hex")) 
                            ? "" : reader.GetString("secret_hex"),
                        TtlSeconds = reader.GetInt32("ttl_seconds"),
                        UseTimeLimited = reader.GetBoolean("use_time_limited"),
                        LastSecretUpdate = reader.IsDBNull(reader.GetOrdinal("last_secret_update")) 
                            ? DateTime.MinValue : reader.GetDateTime("last_secret_update")
                    };
                }

                System.Diagnostics.Debug.WriteLine($"[TURN] Конфигурация не найдена в БД: {serverName}");
                return null;
            }
            catch (MySqlException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TURN] Ошибка БД при загрузке: {ex.Number} - {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TURN] Ошибка загрузки конфигурации: {ex.Message}");
                return null;
            }
        }

        /// <summary>Проверяет, нужно ли обновить ключ (истекло ли 24 часа).</summary>
        public static bool IsSecretExpired(DateTime lastUpdate)
        {
            return DateTime.UtcNow.Subtract(lastUpdate).TotalHours >= 24;
        }
    }

    /// <summary>Конфигурация TURN сервера из БД.</summary>
    internal class TurnServerConfig
    {
        public string ServerName { get; set; }
        public string ServerAddress { get; set; }
        public int ServerPort { get; set; }
        public string Transport { get; set; }
        public string Username { get; set; }
        public string SecretHex { get; set; }
        public int TtlSeconds { get; set; }
        public bool UseTimeLimited { get; set; }
        public DateTime LastSecretUpdate { get; set; }
    }
}