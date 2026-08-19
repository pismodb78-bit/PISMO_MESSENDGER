using System;
using System.Collections.Generic;
using System.IO;

namespace PISMO
{
    /// <summary>
    /// Внешняя конфигурация секретов, чтобы НЕ хранить их в репозитории.
    /// Значения берутся (в порядке приоритета):
    ///   1) переменная окружения (например PISMO_JWT_SECRET);
    ///   2) файл pismo.config рядом с exe / в %LOCALAPPDATA%\PISMO (key=value);
    ///   3) вшитый дефолт (только как крайний фолбэк для локальной отладки).
    ///
    /// Файл pismo.config НЕ коммитится (см. .gitignore). Пример строки:
    ///   jwt_secret=БАЗА64-СЕКРЕТ-СОВПАДАЕТ-С-WS-СЕРВЕРОМ
    /// </summary>
    public static class AppConfig
    {
        private static readonly object _lock = new();
        private static Dictionary<string, string> _file;

        // Дефолт совпадает с фолбэком ws-server/server.js (JWT_SECRET), чтобы
        // подпись клиента проходила проверку сервера «из коробки». В проде
        // переопределяется переменной окружения на обоих концах.
        private const string DefaultJwtSecret =
            "uc5KT2e+qYwa6tb0HUXnLZwsC55VuB93szkSpkucr8i1BFjKA6RXbyIrjk0+ign9";

        /// <summary>Секрет для подписи JWT (HS256). Должен совпадать с ws-server.</summary>
        public static string JwtSecret =>
            Get("PISMO_JWT_SECRET", "jwt_secret", DefaultJwtSecret);

        /// <summary>Достаёт значение: env → файл pismo.config → дефолт.</summary>
        public static string Get(string envVar, string fileKey, string fallback)
        {
            try
            {
                var env = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

                var fromFile = FromFile(fileKey);
                if (!string.IsNullOrWhiteSpace(fromFile)) return fromFile;
            }
            catch { }
            return fallback;
        }

        private static string FromFile(string key)
        {
            lock (_lock)
            {
                _file ??= LoadFile();
                return _file.TryGetValue(key.ToLowerInvariant(), out var v) ? v : null;
            }
        }

        private static Dictionary<string, string> LoadFile()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in ConfigPaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        var val = line.Substring(eq + 1).Trim();
                        if (!dict.ContainsKey(k)) dict[k] = val;
                    }
                }
                catch { }
            }
            return dict;
        }

        private static IEnumerable<string> ConfigPaths()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
            yield return Path.Combine(baseDir, "pismo.config");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "PISMO", "pismo.config");
        }
    }
}
