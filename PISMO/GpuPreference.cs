using System;
using System.IO;
using Microsoft.Win32;

namespace PISMO
{
    /// <summary>
    /// Управляет тем, на какой видеокарте работают приложение и его WebView2 —
    /// от этого зависит, каким энкодером кодируется демонстрация:
    ///  • дискретная NVIDIA с NVENC (RTX/GTX) — отличный аппаратный H264/HEVC;
    ///  • встроенная Intel с Quick Sync — тоже отличный аппаратный H264/HEVC;
    ///  • многие NVIDIA MX-серии (MX450 и т.п.) БЕЗ NVENC — на них дискретная
    ///    даёт софт-энкодер, а Quick Sync (встроенная) — аппаратный.
    /// Поэтому «правильная» карта зависит от железа, и выбор отдаётся
    /// пользователю (см. DeviceSettings.GpuEncodePref).
    ///
    /// Механизм: та же настройка, что пишет системное окно «Графика» —
    /// HKCU\Software\Microsoft\DirectX\UserGpuPreferences, value = путь к exe,
    /// data = "GpuPreference=N;" (2 = высокая производительность/дискретная,
    /// 1 = энергосбережение/встроенная). Права администратора НЕ нужны.
    /// Режим "auto" — НЕ трогаем выбор Windows (что выставил пользователь/ОС).
    /// Вступает в силу при следующем старте процесса (перезапуск демки).
    /// </summary>
    public static class GpuPreference
    {
        private const string RegPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

        /// <summary>mode: "high" = дискретная (RTX/NVENC), "integrated" = встроенная
        /// (Quick Sync), "auto"/иное = не переопределять настройку Windows.</summary>
        public static void Apply(string mode)
        {
            mode = (mode ?? "auto").Trim().ToLowerInvariant();
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegPath);
                if (key == null) return;

                // КРИТИЧНО: главный процесс PISMO.exe НЕЛЬЗЯ прибивать к дискретке
                // (GpuPreference=2). Это ломает DXGI Desktop Duplication захвата
                // демки на Optimus-ноутбуках (монитор ведёт Intel, а процесс на
                // NVIDIA → DuplicateOutput 0x887A0004 UNSUPPORTED → откат на GDI
                // ~35fps). NVENC при этом НЕ страдает: в нативном пути кодек берётся
                // через драйвер NVIDIA независимо от «рендер-GPU» процесса.
                // Поэтому для главного exe убираем любой high-perf пин, а Quick Sync
                // (=1, Intel) не мешает DXGI и разрешён.
                string mainExe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(mainExe))
                {
                    try
                    {
                        if (mode == "integrated") SetFor(key, mainExe, 1);
                        else key.DeleteValue(mainExe, throwOnMissingValue: false);   // high/auto → не пиним
                    }
                    catch { }
                }

                // WebView2-процессы (legacy, звонки их не используют) — как раньше.
                if (mode != "high" && mode != "integrated") return;
                foreach (var exe in FindWebView2Executables())
                {
                    try { SetFor(key, exe, mode == "high" ? 2 : 1); }
                    catch { }
                }
            }
            catch { /* реестр недоступен — не критично */ }
        }

        private static System.Collections.Generic.IEnumerable<string> AllTargetExecutables()
        {
            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
                yield return Environment.ProcessPath;
            foreach (var exe in FindWebView2Executables())
                yield return exe;
        }

        private static void SetFor(RegistryKey key, string exePath, int mode)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;
            string desired = $"GpuPreference={mode};";
            if (key.GetValue(exePath) as string == desired) return;
            key.SetValue(exePath, desired, RegistryValueKind.String);
        }

        /// <summary>Ищет msedgewebview2.exe во всех типичных местах установки.</summary>
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
