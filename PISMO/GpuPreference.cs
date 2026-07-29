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

                // Главный процесс PISMO.exe пиним по выбору пользователя:
                //  • "high" → дискретная NVIDIA (=2): пользователь явно выбрал
                //    «Дискретная», процесс уходит на RTX и она реально грузится
                //    (3D/захват). ВАЖНО: аппаратного энкода это НЕ включает — в
                //    bundled livekit_ffi.dll NVENC не вкомпилен (фабрика только
                //    LibvpxVp8/OpenH264/AV1/VP9), поэтому кодирование H264 остаётся
                //    программным (Video Encode = 0%), а на RTX ложится лишь захват/
                //    цветоконвертация. Захват идёт через WGC (кросс-GPU, без чёрного
                //    экрана) + детектор чёрного кадра с откатом на GDI.
                //  • "integrated" → встроенная Intel (=1, Quick Sync).
                //  • "auto"/иное → не переопределяем выбор Windows.
                string mainExe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(mainExe))
                {
                    try
                    {
                        // "high" пиним на дискретку ТОЛЬКО если у неё реально есть
                        // NVENC (RTX/GTX/Quadro…). У MX-серии (MX150/MX250/MX450)
                        // NVENC вырезан, а сама карта для захвата слабее встройки —
                        // пин туда только ухудшил бы демку. Поэтому без NVENC ведём
                        // себя как auto (не пиним), даже если тумблер включён.
                        if (mode == "high" && GpuCapabilities.HasNvenc) SetFor(key, mainExe, 2);
                        else if (mode == "integrated") SetFor(key, mainExe, 1);
                        else key.DeleteValue(mainExe, throwOnMissingValue: false);   // auto / high-без-NVENC → не пиним
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
