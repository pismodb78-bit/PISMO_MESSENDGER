using Avalonia;
using System;

namespace PISMO.Linux
{
    // Точка входа Avalonia-клиента PISMO для Linux (CachyOS/Arch и др.).
    // GUI строится на Avalonia (кроссплатформенно), а вся логика доступа к БД,
    // шифрование, JWT и парольный хеш переиспользуются из Windows-клиента
    // (linked-файлы в .csproj) — один источник правды.
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
