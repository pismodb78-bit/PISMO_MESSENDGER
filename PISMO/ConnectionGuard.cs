using System;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Состояние связи с базой — СТРОКОЙ В ШАПКЕ, а не заставкой во весь экран.
    /// </summary>
    /// <remarks>
    /// Раньше здесь была панель на весь клиентский прямоугольник окна: «Нет
    /// связи с сервером», спиннер, и под ней ничего не нажималось. Связь
    /// пропадает не только насовсем — сервер перезапустили, провайдер моргнул,
    /// ноутбук вышел из сна, — и каждый такой пустяк запирал приложение
    /// целиком: нельзя было ни перечитать уже загруженную переписку, ни
    /// перейти в другой чат, ни открыть настройки.
    ///
    /// Мессенджеры делают иначе: заголовок меняется, остальное работает.
    /// Здесь так же:
    ///
    ///     Соединение…   связи с базой нет, идут попытки
    ///     Обновление…   связь есть, список перечитывается
    ///     (обычный вид) всё в порядке
    ///
    /// ЗАДЕРЖКА. Мелькнувший обрыв — не новость: одно упавшее соединение из
    /// пула тут же сменяется живым. Поэтому «Соединение…» показывается,
    /// только если связь не вернулась за <see cref="GraceMs"/>; переподключение
    /// при этом начинается сразу, не дожидаясь надписи.
    /// </remarks>
    public static class ConnectionGuard
    {
        public enum Status { Ready, Connecting, Updating }

        /// <summary>Сколько терпим обрыв, прежде чем сказать о нём человеку.</summary>
        private const int GraceMs = 1200;

        /// <summary>Период проб при обрыве — как и было.</summary>
        private const int RetryMs = 2500;

        private static Form _owner;
        private static System.Threading.SynchronizationContext _ui;
        private static System.Windows.Forms.Timer _retryTimer;
        private static System.Windows.Forms.Timer _graceTimer;
        private static volatile bool _lost;
        private static int _syncing;

        private static Status _status = Status.Ready;

        /// <summary>Текущее состояние связи.</summary>
        public static Status Current => _status;

        /// <summary>
        /// Связь заведомо потеряна. По этому признаку обращения к базе
        /// отказываются сразу, не дожидаясь таймаута: именно ожидание
        /// таймаутов в потоке интерфейса и выглядит как зависшее окно.
        /// </summary>
        public static bool Offline => _lost;

        /// <summary>Состояние изменилось — шапке пора обновиться.</summary>
        public static event Action<Status> Changed;

        /// <summary>Надпись для шапки или null, если всё в порядке.</summary>
        public static string Label => _status switch
        {
            Status.Connecting => "Соединение…",
            Status.Updating => "Обновление…",
            _ => null,
        };

        /// <summary>Вызвать один раз из главной формы (на UI-потоке).</summary>
        public static void Init(Form owner)
        {
            _owner = owner;
            _ui = System.Threading.SynchronizationContext.Current;

            _retryTimer = new System.Windows.Forms.Timer { Interval = RetryMs };
            _retryTimer.Tick += (s, e) => PingOnce();

            _graceTimer = new System.Windows.Forms.Timer { Interval = GraceMs };
            _graceTimer.Tick += (s, e) =>
            {
                _graceTimer.Stop();
                if (_lost) Set(Status.Connecting);
            };
        }

        /// <summary>Соединение успешно.</summary>
        public static void NotifyOk()
        {
            if (!_lost) return;
            _lost = false;
            Post(() =>
            {
                try { _retryTimer?.Stop(); } catch { }
                try { _graceTimer?.Stop(); } catch { }
                Set(_syncing > 0 ? Status.Updating : Status.Ready);
            });
        }

        /// <summary>Соединение потеряно — начать переподключение.</summary>
        public static void NotifyLost()
        {
            if (_lost) return;
            _lost = true;
            Post(() =>
            {
                // Переподключение — сразу, надпись — через паузу: мигать
                // шапкой из-за одного мёртвого соединения из пула незачем.
                try { _retryTimer?.Start(); } catch { }
                try { _graceTimer?.Stop(); _graceTimer?.Start(); } catch { }
                PingOnce();
            });
        }

        /// <summary>
        /// Список перечитывается. Считаем счётчиком, а не флагом: загрузок
        /// бывает несколько разом, и первая закончившаяся не должна гасить
        /// надпись, пока идут остальные.
        /// </summary>
        public static void BeginSync()
        {
            System.Threading.Interlocked.Increment(ref _syncing);
            Post(() => { if (!_lost) Set(Status.Updating); });
        }

        public static void EndSync()
        {
            if (System.Threading.Interlocked.Decrement(ref _syncing) < 0)
                System.Threading.Interlocked.Exchange(ref _syncing, 0);
            Post(() => { if (!_lost && _syncing <= 0) Set(Status.Ready); });
        }

        private static void Set(Status s)
        {
            if (_status == s) return;
            _status = s;
            try { Changed?.Invoke(s); } catch { }
        }

        private static void Post(Action a)
        {
            try
            {
                if (_owner != null && !_owner.IsDisposed && _owner.IsHandleCreated)
                {
                    if (_owner.InvokeRequired) _owner.BeginInvoke(new Action(() => { try { a(); } catch { } }));
                    else a();
                }
                else if (_ui != null) _ui.Post(_ => { try { a(); } catch { } }, null);
            }
            catch { }
        }

        /// <summary>
        /// Пробуем открыть соединение в фоне; успех сам позовёт
        /// <see cref="NotifyOk"/> изнутри OpenConnection.
        /// </summary>
        private static void PingOnce()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                // Проба идёт в обход быстрого отказа — иначе запрет, который
                // она должна снять, не дал бы ей и попробовать.
                try { using var c = DBHelper.OpenProbe(); }
                catch { /* ещё нет связи — ждём следующего тика */ }
            });
        }
    }
}
