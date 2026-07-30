using System;
using System.IO;
using System.Text;

namespace PISMO.Native
{
    /// <summary>
    /// Диагностический лог событий демонстрации экрана (для отладки «сценария 1/2»
    /// при смене кодека). Пишет строки с таймингами в pismo_screenlog.txt рядом с
    /// exe. Дёшево, потокобезопасно. Включается флагом Enabled (по умолчанию вкл),
    /// чтобы пользователь мог один раз воспроизвести баг и прислать файл.
    /// </summary>
    internal static class ScreenLog
    {
        public static bool Enabled = true;
        private static readonly object _lock = new();
        private static string _path;
        private static System.Diagnostics.Stopwatch _sw;

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    try { _path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pismo_screenlog.txt"); }
                    catch { _path = "pismo_screenlog.txt"; }
                }
                return _path;
            }
        }

        public static void Log(string msg)
        {
            if (!Enabled) return;
            try
            {
                lock (_lock)
                {
                    _sw ??= System.Diagnostics.Stopwatch.StartNew();
                    string line = $"[{_sw.ElapsedMilliseconds,8} ms] {msg}{Environment.NewLine}";
                    File.AppendAllText(Path, line, Encoding.UTF8);
                }
            }
            catch { }
        }

        /// <summary>Отметить начало новой сессии (в начале звонка) — разделитель в логе.</summary>
        public static void Session(string what)
        {
            if (!Enabled) return;
            try
            {
                lock (_lock)
                {
                    _sw = System.Diagnostics.Stopwatch.StartNew();
                    File.AppendAllText(Path,
                        $"{Environment.NewLine}===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {what} ====={Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
