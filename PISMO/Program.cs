using System;
using System.Windows.Forms;

namespace PISMO
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
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