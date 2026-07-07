using System;
using System.IO;
using Microsoft.Win32;

namespace PISMO
{
    /// <summary>
    /// Программно закрепляет за приложением и его WebView2-процессами ДИСКРЕТНУЮ
    /// видеокарту (как это делает Discord — «просто работает» при включённом
    /// аппаратном ускорении, без ручной возни в «Параметры → Графика»).
    ///
    /// Механизм: та же настройка, что пишет системное окно «Графика» —
    /// HKCU\Software\Microsoft\DirectX\UserGpuPreferences, value = путь к exe,
    /// data = "GpuPreference=2;" (2 = высокая производительность / дискретная,
    /// 1 = энергосбережение / встроенная). Права администратора НЕ нужны (HKCU).
    /// Вступает в силу при следующем старте процесса (перезапуск демонстрации).
    /// </summary>
    public static class GpuPreference
    {
        private const string RegPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

        /// <summary>Прописать предпочтение GPU для PISMO.exe и всех найденных
        /// msedgewebview2.exe. mode: 2 = дискретная (HW-ускорение вкл),
        /// 1 = встроенная (выкл).</summary>
        public static void Apply(bool highPerformance)
        {
            int mode = highPerformance ? 2 : 1;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegPath);
                if (key == null) return;

                // Само приложение.
                try { SetFor(key, Environment.ProcessPath, mode); } catch { }

                // Все msedgewebview2.exe (демка/камера кодируются именно там).
                foreach (var exe in FindWebView2Executables())
                    try { SetFor(key, exe, mode); } catch { }
            }
            catch { /* реестр недоступен — не критично */ }
        }

        private static void SetFor(RegistryKey key, string exePath, int mode)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
            string desired = $"GpuPreference={mode};";
            var current = key.GetValue(exePath) as string;
            if (current == desired) return;               // уже стоит — не трогаем
            key.SetValue(exePath, desired, RegistryValueKind.String);
        }

        /// <summary>Ищет msedgewebview2.exe во всех типичных местах установки
        /// рантайма (per-machine x86/x64 и per-user), включая версионные папки.</summary>
        private static System.Collections.Generic.IEnumerable<string> FindWebView2Executables()
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft", "EdgeWebView", "Application"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Microsoft", "EdgeWebView", "Application"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "EdgeWebView", "Application"),
            };

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string[] hits;
                try { hits = Directory.GetFiles(root, "msedgewebview2.exe", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var h in hits) if (seen.Add(h)) yield return h;
            }
        }
    }
}
