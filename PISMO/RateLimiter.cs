using System;
using System.Collections.Generic;

namespace PISMO
{
    /// <summary>
    /// Простейшее клиентское ограничение частоты (в памяти процесса): защищает
    /// от перебора пароля и спама заявками в друзья. Сбрасывается при
    /// перезапуске — для клиента этого достаточно (полноценный лимит — на
    /// сервере). Потокобезопасно.
    /// </summary>
    public static class RateLimiter
    {
        private static readonly object _lock = new();

        // ── Скользящее окно: не более maxEvents событий за window ──
        private static readonly Dictionary<string, List<DateTime>> _events = new();

        /// <summary>Разрешить событие key? Регистрирует его, если да.</summary>
        public static bool Allow(string key, int maxEvents, TimeSpan window)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (!_events.TryGetValue(key, out var list)) { list = new(); _events[key] = list; }
                list.RemoveAll(t => now - t > window);
                if (list.Count >= maxEvents) return false;
                list.Add(now);
                return true;
            }
        }

        // ── Блокировка входа с нарастающей задержкой ──
        private sealed class Fail { public int Count; public DateTime LockedUntil; }
        private static readonly Dictionary<string, Fail> _login = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Если вход по login сейчас заблокирован — вернёт остаток времени
        /// блокировки; иначе TimeSpan.Zero.</summary>
        public static TimeSpan LoginLockRemaining(string login)
        {
            lock (_lock)
            {
                if (_login.TryGetValue(login ?? "", out var f) && f.LockedUntil > DateTime.UtcNow)
                    return f.LockedUntil - DateTime.UtcNow;
                return TimeSpan.Zero;
            }
        }

        /// <summary>Отметить неудачную попытку входа. После 5 подряд — блокировка,
        /// растущая с каждой следующей серией (30с · множитель).</summary>
        public static void RegisterLoginFailure(string login)
        {
            lock (_lock)
            {
                login ??= "";
                if (!_login.TryGetValue(login, out var f)) { f = new Fail(); _login[login] = f; }
                f.Count++;
                if (f.Count >= 5)
                {
                    int over = f.Count - 4;                 // 1,2,3,…
                    int secs = Math.Min(300, 30 * over);    // 30с,60с,90с,… до 5 мин
                    f.LockedUntil = DateTime.UtcNow.AddSeconds(secs);
                }
            }
        }

        /// <summary>Успешный вход — сбросить счётчик неудач.</summary>
        public static void RegisterLoginSuccess(string login)
        {
            lock (_lock) { _login.Remove(login ?? ""); }
        }
    }
}
