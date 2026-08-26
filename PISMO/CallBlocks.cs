using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PISMO
{
    /// <summary>
    /// От кого не принимаем ЗВОНКИ. В отличие от «игнорировать», сообщения и
    /// уведомления о них продолжают приходить как обычно — молчит только
    /// телефон: входящий вызов не показывается и не звенит, а у звонящего идут
    /// гудки, как если бы трубку просто не взяли.
    ///
    /// Группы хранятся здесь же, отрицательными ключами: у групп и пользователей
    /// нумерация своя, и без разделения запрет для группы №5 задел бы
    /// пользователя №5.
    ///
    /// Хранится ЛОКАЛЬНО (%LOCALAPPDATA%\PISMO), на сервер не уходит — как и
    /// «игнорировать». Файл на каждый аккаунт свой.
    /// </summary>
    public static class CallBlocks
    {
        /// <summary>Ключ группы — отрицательный, чтобы не пересечься с id людей.</summary>
        public static int GroupKey(int groupId) => -groupId;

        private static readonly object _lock = new();
        private static HashSet<int> _ids;
        private static int _loadedFor = -1;

        private static string PathFor(int me) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PISMO", $"call_blocks_{me}.txt");

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
        public static bool IsBlocked(int uid)
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
