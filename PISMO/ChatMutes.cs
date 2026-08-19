using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PISMO
{
    /// <summary>
    /// Игнорируемые собеседники: от них не приходят ни уведомления о сообщениях,
    /// ни входящие звонки. Сообщения при этом продолжают доставляться и лежат в
    /// чате — это именно тишина, а не блокировка (для блокировки есть отдельный
    /// механизм user_blocks на сервере).
    ///
    /// Хранится ЛОКАЛЬНО (%LOCALAPPDATA%\PISMO), на сервер не уходит: список
    /// «кого я не хочу слышать» — дело этой машины, и в базе ему места нет.
    /// Файл на каждый аккаунт свой, чтобы настройки разных пользователей на
    /// одном ПК не смешивались.
    /// </summary>
    public static class ChatMutes
    {
        private static readonly object _lock = new();
        private static HashSet<int> _ids;
        private static int _loadedFor = -1;

        private static string PathFor(int me) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PISMO", $"muted_chats_{me}.txt");

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

        /// <summary>Этот собеседник игнорируется?</summary>
        public static bool IsMuted(int uid)
        {
            if (uid <= 0) return false;
            lock (_lock) { return Load().Contains(uid); }
        }

        /// <summary>Включить/выключить игнор. Возвращает НОВОЕ состояние.</summary>
        public static bool Toggle(int uid)
        {
            lock (_lock)
            {
                var set = Load();
                bool nowMuted = set.Add(uid);
                if (!nowMuted) set.Remove(uid);
                Save(set);
                return nowMuted;
            }
        }

        private static void Save(HashSet<int> set)
        {
            try
            {
                string p = PathFor(UserSession.EffectiveId);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllLines(p, set.Select(i => i.ToString()));
            }
            catch { }
        }
    }
}
