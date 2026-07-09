using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PISMO
{
    /// <summary>
    /// Закреплённые ЧАТЫ (2.1): id собеседников, чьи диалоги прижаты к верху
    /// списка личных сообщений (ниже групп) независимо от давности переписки.
    /// Хранится ЛОКАЛЬНО на этой машине (%LOCALAPPDATA%\PISMO), на сервер не
    /// ходит. Файл на каждый аккаунт свой — закрепы разных пользователей на
    /// одном ПК не смешиваются.
    /// </summary>
    public static class ChatPins
    {
        private static readonly object _lock = new();
        private static HashSet<int> _ids;
        private static int _loadedFor = -1;

        private static string PathFor(int me) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PISMO", $"pinned_chats_{me}.txt");

        private static HashSet<int> Load()
        {
            int me = UserSession.EffectiveId;
            lock (_lock)
            {
                if (_ids != null && _loadedFor == me) return _ids;
                var set = new HashSet<int>();
                try
                {
                    string p = PathFor(me);
                    if (File.Exists(p))
                        foreach (var line in File.ReadAllLines(p))
                            if (int.TryParse(line.Trim(), out var id)) set.Add(id);
                }
                catch { }
                _ids = set;
                _loadedFor = me;
                return set;
            }
        }

        /// <summary>Чат с этим пользователем закреплён?</summary>
        public static bool IsPinned(int uid) { lock (_lock) { return Load().Contains(uid); } }

        /// <summary>Закрепить/открепить чат. Возвращает НОВОЕ состояние (true = закреплён).</summary>
        public static bool Toggle(int uid)
        {
            lock (_lock)
            {
                var set = Load();
                bool nowPinned = set.Add(uid);
                if (!nowPinned) set.Remove(uid);
                try
                {
                    string p = PathFor(UserSession.EffectiveId);
                    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    File.WriteAllLines(p, set.Select(i => i.ToString()));
                }
                catch { }
                return nowPinned;
            }
        }
    }
}
