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
    /// data = "GpuPreference=N;" (1 = энергосбережение/ВСТРОЕННАЯ, 2 = высокая
    /// производительность/дискретная). Права администратора НЕ нужны (HKCU).
    /// Вступает в силу при следующем старте процесса (перезапуск демонстрации).
    ///
    /// ВАЖНО про выбор GPU для КОДИРОВАНИЯ демки: аппаратный H264-энкодер в
    /// Chromium/MediaFoundation привязан к тому адаптеру, на котором работает
    /// GPU-процесс. Надёжнее всего аппаратно кодирует ВСТРОЕННАЯ графика Intel
    /// (Quick Sync) — она есть почти на всех ноутбуках. Многие дискретные
    /// NVIDIA MX-серии (MX450 и т.п.) НЕ имеют NVENC, поэтому форсировать
    /// дискретную опасно: попадём на GPU без энкодера → откат в софт (OpenH264,
    /// ~14 fps). Поэтому при включённом ускорении предпочитаем ВСТРОЕННУЮ
    /// (Quick Sync); на десктопе без встройки система сама возьмёт единственную
    /// (дискретную с NVENC).
    /// </summary>
    public static class GpuPreference
    {
        private const string RegPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

        /// <summary>Прописать предпочтение GPU для PISMO.exe и всех найденных
        /// msedgewebview2.exe. При HW-ускорении — встроенная (Quick Sync,
        /// надёжный аппаратный энкодер); без ускорения — тоже встроенная
        /// (меньше нагрев, программный рендер).</summary>
        public static void Apply(bool highPerformance)
        {
            // 1 = встроенная (Quick Sync). Не форсируем дискретную — на MX-картах
            // без NVENC это ломает аппаратное кодирование.
            int mode = 1;
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
