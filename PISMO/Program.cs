using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PISMO
{
    internal static class Program
    {
        // ── Единственный экземпляр ───────────────────────────────────────
        // Приложение живёт в трее, и о нём легко забыть: запуск ярлыка поднимал
        // ВТОРОЙ процесс, и в трее висели две иконки (два подключения к БД, два
        // набора уведомлений). Теперь повторный запуск не стартует приложение, а
        // просит уже работающее показаться и разворачивает его из трея.
        private const string InstanceMutexName = @"Local\PISMO_SingleInstance";
        private static Mutex _instanceLock;

        /// <summary>Широковещательное «покажи главное окно». Номер получаем по имени,
        /// поэтому у обоих процессов он совпадает.</summary>
        internal static readonly int WmPismoShow = RegisterWindowMessage("PISMO_SHOW_MAIN_WINDOW");

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>Отпустить блокировку перед перезапуском, иначе НОВЫЙ процесс
        /// увидит «уже запущено» и молча выйдет — перезапуск бы не сработал.</summary>
        internal static void ReleaseSingleInstanceLock()
        {
            try { _instanceLock?.ReleaseMutex(); } catch { }
            try { _instanceLock?.Dispose(); } catch { }
            _instanceLock = null;
        }

        /// <summary>Занимает блокировку. false — приложение уже работает.</summary>
        private static bool TakeSingleInstanceLock()
        {
            // Перезапуск (смена темы) стартует новый процесс, пока старый ещё
            // доживает — ему даём подождать освобождения.
            bool isRestart = false;
            try
            {
                foreach (var a in Environment.GetCommandLineArgs())
                    if (string.Equals(a, "--restart", StringComparison.OrdinalIgnoreCase)) { isRestart = true; break; }
            }
            catch { }

            _instanceLock = new Mutex(false, InstanceMutexName);
            try { return _instanceLock.WaitOne(isRestart ? 8000 : 0, false); }
            catch (AbandonedMutexException) { return true; }   // прошлый процесс умер, не отпустив — блокировка наша
            catch { return true; }                             // не смогли проверить — не мешаем запуску
        }

        /// <summary>Просит уже запущенный экземпляр показаться.</summary>
        private static void WakeRunningInstance()
        {
            // Окно может быть спрятано в трее — тогда у процесса нет MainWindowHandle,
            // и достучаться можно только широковещательным сообщением.
            try { PostMessage(HWND_BROADCAST, WmPismoShow, IntPtr.Zero, IntPtr.Zero); } catch { }
            // Подстраховка для стадии заставки/входа, когда главного окна ещё нет:
            // просто выносим вперёд видимое окно того процесса.
            try
            {
                var me = System.Diagnostics.Process.GetCurrentProcess();
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
                {
                    if (p.Id == me.Id) continue;
                    if (p.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(p.MainWindowHandle);
                }
            }
            catch { }
        }

        [STAThread]
        static void Main()
        {
            if (!TakeSingleInstanceLock()) { WakeRunningInstance(); return; }

            // ВАЖНО: больше НЕ поднимаем локальный WS-сервер в каждом клиенте.
            // Сигналинг теперь идёт через ЕДИНЫЙ сервер (ws-server/, на машине 85),
            // а локальный Start(8080) только конфликтовал по порту (EADDRINUSE)
            // на той же машине и всё равно не работал между разными ПК.
            // WebSocketSignalingServer.Instance.Start(8080);

            // Чтобы редкие не критичные ошибки отрисовки (например GDI+ при
            // анимации гифок) не роняли приложение жёстким диалогом.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[UI EXCEPTION] " + e.Exception.Message);
                LogCrash("UI", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[FATAL] " + (e.ExceptionObject as Exception)?.Message);
                LogCrash("FATAL(background)", e.ExceptionObject as Exception);
            };

            ApplicationConfiguration.Initialize();

            // Тёмный режим для всего процесса ДО создания окон — тогда нативные
            // полосы прокрутки тёмные всегда (и в оконном, и в полноэкранном режиме),
            // а не белые.
            try { ChatScroll.EnableAppDarkMode(); } catch { }

            // Глобально запрещаем менять значения контролов колесом мыши (комбобоксы,
            // ползунки и т.п.) — прокрутка над ними листает страницу, а не крутит настройку.
            Application.AddMessageFilter(new WheelGuard());

            // Заставка (как у Discord): показывается СРАЗУ, внутри неё идут
            // проверка обновлений и подключение к БД, затем она открывает окно
            // входа. Так приложение не выглядит зависшим при запуске.
            var ctx = new ApplicationContext();
            new SplashForm(ctx).Show();
            Application.Run(ctx);
        }

        // Пишет полное исключение (со стеком) в pismo_crash.txt рядом с exe, чтобы
        // «тихие» падения (фоновый поток аудио/таймеры) можно было диагностировать.
        private static void LogCrash(string where, Exception ex)
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pismo_crash.txt");
                string txt = $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] =====\n{ex}\n";
                System.IO.File.AppendAllText(path, txt);
            }
            catch { }
        }
    }
}