using System;
using System.Windows.Forms;

namespace PISMO
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Автоматически запускаем локальный WebSocket сервер в фоне
            WebSocketSignalingServer.Instance.Start(8080);

            ApplicationConfiguration.Initialize();
            Application.Run(new LoginForm());
        }
    }
}