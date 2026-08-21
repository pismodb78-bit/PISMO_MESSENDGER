using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PISMO
{
    /// <summary>
    /// Замер времени по этапам — для поиска того, где именно уходят секунды.
    ///
    /// Debug.WriteLine в релизной сборке никуда не видно, а без чисел разговор
    /// про «долго грузит» превращается в перебор гипотез: сначала кажется, что
    /// виновата сеть, потом отрисовка, потом драйвер. Здесь этапы пишутся в
    /// pismo-perf.log рядом с exe, и лог можно просто прислать.
    ///
    /// Включается наличием файла perf.on в той же папке — чтобы у тех, кто
    /// диагностикой не занимается, ничего не писалось вовсе. Создать его можно
    /// пустым: важно только имя.
    /// </summary>
    internal static class Perf
    {
        private static readonly object Gate = new();
        private static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string LogPath = Path.Combine(Dir, "pismo-perf.log");

        private static bool? _enabled;

        /// <summary>
        /// Включено, если рядом лежит perf.on ЛИБО подключён отладчик. Второе —
        /// чтобы при запуске из студии ничего не надо было готовить заранее:
        /// строки просто появятся в окне вывода.
        /// </summary>
        public static bool Enabled
        {
            get
            {
                _enabled ??= Debugger.IsAttached || File.Exists(Path.Combine(Dir, "perf.on"));
                return _enabled.Value;
            }
        }

        /// <summary>Записать готовую длительность этапа.</summary>
        public static void Log(string stage, long ms)
        {
            if (!Enabled) return;
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff}  {ms,6} мс  {stage}";
                Debug.WriteLine("[PERF] " + line);
                lock (Gate) File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Отметка без длительности — начало операции, разделитель.</summary>
        public static void Mark(string stage)
        {
            if (!Enabled) return;
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff}         ── {stage}";
                Debug.WriteLine("[PERF] " + line);
                lock (Gate) File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Замерить действие и записать результат.</summary>
        public static void Time(string stage, Action action)
        {
            if (!Enabled) { action(); return; }
            var sw = Stopwatch.StartNew();
            try { action(); }
            finally { sw.Stop(); Log(stage, sw.ElapsedMilliseconds); }
        }

        /// <summary>То же для действия с результатом.</summary>
        public static T Time<T>(string stage, Func<T> func)
        {
            if (!Enabled) return func();
            var sw = Stopwatch.StartNew();
            try { return func(); }
            finally { sw.Stop(); Log(stage, sw.ElapsedMilliseconds); }
        }
    }
}
