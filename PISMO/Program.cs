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
                System.Diagnostics.Debug.WriteLine("[UI EXCEPTION] " + e.Exception.Message);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                System.Diagnostics.Debug.WriteLine("[FATAL] " + (e.ExceptionObject as Exception)?.Message);

            ApplicationConfiguration.Initialize();

            // Заставка (как у Discord): показывается СРАЗУ, внутри неё идут
            // проверка обновлений и подключение к БД, затем она открывает окно
            // входа. Так приложение не выглядит зависшим при запуске.
            var ctx = new ApplicationContext();
            new SplashForm(ctx).Show();
            Application.Run(ctx);
        }
    }
}