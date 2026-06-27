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

            ApplicationConfiguration.Initialize();

            // Автообновление при запуске (GitHub Releases). Тихо пропускаем при
            // отсутствии сети. Если началось обновление — приложение закроется само.
            Updater.CheckOnStartup();

            Application.Run(new LoginForm());
        }
    }
}