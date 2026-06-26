using System;
using MySql.Data.MySqlClient;
using System.IO;
using System.Net;

namespace PISMO
{
    public static class DBHelper
    {
        private static string _connectionString = string.Empty;

        static DBHelper()
        {
            LoadConnectionString();
        }

        private static void LoadConnectionString()
        {
            try
            {
                string[] possiblePaths = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ip.txt"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "ip.txt"),
                    "ip.txt"
                };

                string ipLine = null;
                foreach (var path in possiblePaths)
                {
                    if (File.Exists(path))
                    {
                        ipLine = File.ReadAllText(path).Trim();
                        System.Diagnostics.Debug.WriteLine($"[DBHelper] IP файл найден: {path}");
                        System.Diagnostics.Debug.WriteLine($"[DBHelper] Содержимое: {ipLine}");
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(ipLine))
                {
                    System.Diagnostics.Debug.WriteLine("[DBHelper] ip.txt НЕ НАЙДЕН! Используется localhost:3306");
                    ipLine = "localhost:3306";
                }

                // Если ip.txt содержит ключи ("server=" или ';') — считаем, что это полная строка подключения или набор пар ключ=значение.
                if (ipLine.Contains("=") || ipLine.Contains(";"))
                {
                    try
                    {
                        var builder = new MySqlConnectionStringBuilder(ipLine);

                        // Если в файле указана серверная часть — корректируем порт по локальной логике
                        var server = builder.Server;
                        var portStr = builder.Port > 0 ? builder.Port.ToString() : "3306";
                        var actualPort = DetermineActualPort(server, portStr);

                        if (uint.TryParse(actualPort, out var p)) builder.Port = p;
                        if (string.IsNullOrWhiteSpace(builder.CharacterSet)) builder.CharacterSet = "utf8mb4";

                        // Сохраняем строку подключения (оставляем пользовательские credentials из ip.txt)
                        _connectionString = builder.ConnectionString;

                        // Логирование — маскируем пароль
                        var logBuilder = new MySqlConnectionStringBuilder(_connectionString);
                        if (!string.IsNullOrEmpty(logBuilder.Password)) logBuilder.Password = "***";
                        System.Diagnostics.Debug.WriteLine($"[DBHelper] Connection String: {logBuilder.ConnectionString}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DBHelper] Ошибка парсинга connection string из ip.txt: {ex.Message}");
                        // fallback на старую логику ниже
                    }
                }

                // Старый формат: host[:port]
                string[] parts = ipLine.Split(':');
                string serverSimple = parts[0].Trim();
                string portSimple = parts.Length > 1 ? parts[1].Trim() : "3306";

                string actualPortSimple = DetermineActualPort(serverSimple, portSimple);

                System.Diagnostics.Debug.WriteLine($"[DBHelper] Сервер: {serverSimple}, Указанный порт: {portSimple}, Используемый порт: {actualPortSimple}");

                var defaultBuilder = new MySqlConnectionStringBuilder
                {
                    Server = serverSimple,
                    Port = uint.TryParse(actualPortSimple, out var parsed) ? parsed : 3306,
                    Database = "bdauth",
                    UserID = "root",
                    Password = string.Empty,
                    CharacterSet = "utf8mb4"
                };

                _connectionString = defaultBuilder.ConnectionString;

                var dbg = new MySqlConnectionStringBuilder(_connectionString);
                if (!string.IsNullOrEmpty(dbg.Password)) dbg.Password = "***";
                System.Diagnostics.Debug.WriteLine($"[DBHelper] Connection String: {dbg.ConnectionString}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DBHelper ERROR] {ex.Message}");
                _connectionString = "Server=localhost;Port=3306;Database=bdauth;Uid=root;Pwd=;CharSet=utf8mb4;";
            }
        }

        /// <summary>Определяет правильный порт для подключения.</summary>
        private static string DetermineActualPort(string server, string port)
        {
            if (string.IsNullOrWhiteSpace(server)) return port;

            // Нормализуем хост (если случайно передали "server=..." — уберём префиксы)
            if (server.StartsWith("server=", StringComparison.OrdinalIgnoreCase))
            {
                var idx = server.IndexOf('=', StringComparison.Ordinal);
                if (idx >= 0 && idx + 1 < server.Length) server = server.Substring(idx + 1);
            }

            // Если localhost или 127.0.0.1 — всегда используем 3306
            if (string.Equals(server, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(server, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine("[DBHelper] Локальное подключение → используем порт 3306");
                return "3306";
            }

            // Если это IP адрес и он не в локальной подсети — оставляем указанный порт
            if (!IsLocalIp(server))
            {
                System.Diagnostics.Debug.WriteLine($"[DBHelper] Внешнее подключение к {server} → используем порт {port}");
                return port;
            }

            // Иначе считаем локальной сетью — 3306
            System.Diagnostics.Debug.WriteLine("[DBHelper] Локальная сеть → используем порт 3306");
            return "3306";
        }

        /// <summary>Проверяет, является ли IP адрес локальным.</summary>
        private static bool IsLocalIp(string ip)
        {
            if (string.Equals(ip, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ip, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                if (!IPAddress.TryParse(ip, out var addr))
                {
                    // Если строка содержит префиксы типа "server=..." — попробуем очистить
                    var eq = ip.IndexOf('=');
                    if (eq >= 0 && eq + 1 < ip.Length)
                    {
                        var cleaned = ip.Substring(eq + 1);
                        if (IPAddress.TryParse(cleaned, out var addr2))
                        {
                            addr = addr2;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }

                byte[] bytes = addr.GetAddressBytes();

                if (bytes.Length < 1) return false;

                // 127.x.x.x
                if (bytes[0] == 127) return true;

                // 192.168.x.x
                if (bytes.Length >= 2 && bytes[0] == 192 && bytes[1] == 168) return true;

                // 10.x.x.x
                if (bytes[0] == 10) return true;

                // 172.16.x.x - 172.31.x.x
                if (bytes.Length >= 2 && bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

                return false;
            }
            catch
            {
                return false;
            }
        }

        public static MySqlConnection OpenConnection()
        {
            var conn = new MySqlConnection(_connectionString);
            try
            {
                conn.Open();
                System.Diagnostics.Debug.WriteLine("[DBHelper] ✓ Соединение успешно открыто");
                return conn;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DBHelper] ✗ ОШИБКА ПОДКЛЮЧЕНИЯ: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[DBHelper] Строка подключения: {_connectionString}");
                throw;
            }
        }
    }
}