using System;
using System.IO;

namespace PISMO
{
    /// <summary>
    /// Локальный кеш медиафайлов сообщений.
    ///
    /// Структура папок:
    ///   %AppData%\PISMO\cache\{userId}\{msgType}\{msgId}.{ext}
    ///
    /// Типы (msgType):
    ///   img   — изображения / GIF
    ///   audio — голосовые сообщения (WAV)
    ///   video — видео-кружочки (PSMOVID1)
    ///   file  — документы/архивы (любой ext из fileName)
    ///
    /// Использование:
    ///   byte[] data = MediaCache.Get(msgId, "img");
    ///   if (data == null) { ...грузим из БД... MediaCache.Put(msgId, "img", data); }
    ///
    /// Кеш инвалидируется вручную через Clear() или по папке.
    /// </summary>
    public static class MediaCache
    {
        // Максимальный размер всего кеша (500 МБ). При превышении — удаляем старые файлы.
        private const long MaxCacheBytes = 500L * 1024 * 1024;

        private static string _root;

        /// <summary>Корневая папка кеша для текущего пользователя.</summary>
        private static string Root
        {
            get
            {
                if (_root == null)
                    Init();
                return _root;
            }
        }

        /// <summary>Инициализировать кеш для текущей сессии (вызвать после логина).</summary>
        public static void Init()
        {
            int uid = UserSession.EffectiveId;
            _root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PISMO", "cache", uid.ToString());

            try
            {
                Directory.CreateDirectory(Path.Combine(_root, "img"));
                Directory.CreateDirectory(Path.Combine(_root, "audio"));
                Directory.CreateDirectory(Path.Combine(_root, "video"));
                Directory.CreateDirectory(Path.Combine(_root, "file"));
            }
            catch { /* нет прав — кеш просто не будет работать */ }
        }

        // ────────────────────────────────────────────────────────────────
        //  ОСНОВНЫЕ ОПЕРАЦИИ
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Попытаться получить медиа из кеша.
        /// Возвращает null если нет в кеше или кеш недоступен.
        /// </summary>
        public static byte[] Get(int msgId, string kind, string fileName = null)
        {
            string path = GetPath(msgId, kind, fileName);
            if (path == null || !File.Exists(path)) return null;

            try { return File.ReadAllBytes(path); }
            catch { return null; }
        }

        /// <summary>
        /// Сохранить медиа в кеш.
        /// </summary>
        public static void Put(int msgId, string kind, byte[] data, string fileName = null)
        {
            if (data == null || data.Length == 0) return;

            string path = GetPath(msgId, kind, fileName);
            if (path == null) return;

            try
            {
                File.WriteAllBytes(path, data);
                // Асинхронно проверяем размер кеша (не блокируем UI)
                System.Threading.ThreadPool.QueueUserWorkItem(_ => TrimCacheIfNeeded());
            }
            catch { /* запись не удалась — работаем без кеша */ }
        }

        /// <summary>
        /// Проверить наличие в кеше (без чтения файла).
        /// </summary>
        public static bool Has(int msgId, string kind, string fileName = null)
        {
            string path = GetPath(msgId, kind, fileName);
            return path != null && File.Exists(path);
        }

        /// <summary>
        /// Удалить конкретный элемент из кеша (например, после удаления сообщения).
        /// </summary>
        public static void Invalidate(int msgId, string kind, string fileName = null)
        {
            string path = GetPath(msgId, kind, fileName);
            if (path != null && File.Exists(path))
                try { File.Delete(path); } catch { }
        }

        /// <summary>
        /// Полностью очистить кеш текущего пользователя.
        /// </summary>
        public static void Clear()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
                Init();
            }
            catch { }
        }

        /// <summary>
        /// Размер кеша в байтах.
        /// </summary>
        public static long GetCacheSize()
        {
            try
            {
                if (!Directory.Exists(Root)) return 0;
                long total = 0;
                foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { }
                return total;
            }
            catch { return 0; }
        }

        // ────────────────────────────────────────────────────────────────
        //  ВСПОМОГАТЕЛЬНЫЕ
        // ────────────────────────────────────────────────────────────────

        private static string GetPath(int msgId, string kind, string fileName)
        {
            try
            {
                // s-префикс — медиа серверных каналов (отдельное пространство id,
                // чтобы не путалось с личными/групповыми сообщениями).
                string dir = Path.Combine(Root, kind);
                try { Directory.CreateDirectory(dir); } catch { }
                string ext = kind switch
                {
                    "img" or "simg"   => IsGifName(fileName) ? "gif" : "jpg",
                    "audio" or "saudio" => "wav",
                    "video" or "svideo" => "psmvid",
                    "file" or "sfile"  => string.IsNullOrWhiteSpace(fileName)
                               ? "bin"
                               : Path.GetExtension(fileName).TrimStart('.').ToLower(),
                    _       => "bin"
                };
                return Path.Combine(dir, $"{msgId}.{ext}");
            }
            catch { return null; }
        }

        private static bool IsGifName(string name)
            => !string.IsNullOrEmpty(name) &&
               name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Удаляем самые старые файлы, пока размер кеша не вернётся в норму.
        /// Запускается в фоновом потоке.
        /// </summary>
        private static void TrimCacheIfNeeded()
        {
            try
            {
                if (!Directory.Exists(Root)) return;

                var files = new System.Collections.Generic.List<FileInfo>();
                foreach (var f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                    try { files.Add(new FileInfo(f)); } catch { }

                long total = 0;
                foreach (var f in files) total += f.Length;

                if (total <= MaxCacheBytes) return;

                // Сортируем по дате последнего доступа — удаляем самые старые
                files.Sort((a, b) => a.LastAccessTime.CompareTo(b.LastAccessTime));

                foreach (var f in files)
                {
                    if (total <= MaxCacheBytes * 8 / 10) break; // до 80% от лимита
                    try
                    {
                        long sz = f.Length;
                        f.Delete();
                        total -= sz;
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
