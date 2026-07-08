using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;   // ImplicitUsings тянет System.Threading — без алиаса Timer неоднозначен (CS0104)

namespace PISMO
{
    /// <summary>
    /// Лёгкие UI-анимации для WinForms (2.0): таймер ~60fps + ease-out-cubic.
    /// Используется ТОЛЬКО в интерфейсе звонка/стрима (плавное скрытие плиток,
    /// появление PIP/поп-аутов) — открытие чатов, серверов, настроек и профилей
    /// намеренно остаётся мгновенным.
    ///
    /// Устойчивость: каждый тик обёрнут в try/catch — если контрол уже
    /// уничтожен (окно закрыли на середине анимации), анимация тихо гаснет.
    /// Повторный запуск по тому же ключу отменяет предыдущую (нет «драки» двух
    /// таймеров за одно свойство).
    /// </summary>
    internal static class Anim
    {
        private static readonly Dictionary<object, Timer> _running = new();

        private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

        /// <summary>Анимировать число from→to за ms миллисекунд. key — «владелец»
        /// анимации (обычно контрол): новая анимация с тем же key отменяет старую.</summary>
        public static void Int(object key, int from, int to, int ms, Action<int> apply, Action done = null)
        {
            Cancel(key);
            if (from == to || ms <= 0)
            {
                try { apply(to); done?.Invoke(); } catch { }
                return;
            }
            var start = DateTime.UtcNow;
            var t = new Timer { Interval = 15 };
            _running[key] = t;
            t.Tick += (s, e) =>
            {
                double p = Math.Min(1.0, (DateTime.UtcNow - start).TotalMilliseconds / ms);
                int v = from + (int)Math.Round((to - from) * EaseOutCubic(p));
                try { apply(v); }
                catch { p = 1.0; } // контрол умер — заканчиваем
                if (p >= 1.0)
                {
                    Cancel(key);
                    try { done?.Invoke(); } catch { }
                }
            };
            t.Start();
        }

        /// <summary>Плавное появление формы (opacity 0→1). Безопасно при
        /// закрытии формы на середине анимации.</summary>
        public static void FadeIn(Form f, int ms = 160)
        {
            if (f == null) return;
            try { f.Opacity = 0; } catch { return; }
            Int(f, 0, 100, ms,
                v => { if (!f.IsDisposed) f.Opacity = v / 100.0; },
                () => { try { if (!f.IsDisposed) f.Opacity = 1; } catch { } });
        }

        /// <summary>Остановить анимацию по ключу (если идёт).</summary>
        public static void Cancel(object key)
        {
            if (key == null) return;
            if (_running.TryGetValue(key, out var t))
            {
                try { t.Stop(); t.Dispose(); } catch { }
                _running.Remove(key);
            }
        }
    }
}
