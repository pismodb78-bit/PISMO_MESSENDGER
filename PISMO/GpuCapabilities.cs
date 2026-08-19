using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PISMO
{
    /// <summary>
    /// Лёгкое определение видеокарт и их способности к аппаратному кодированию
    /// (NVENC / Quick Sync) БЕЗ тяжёлых зависимостей (WMI/ffmpeg): читаем описания
    /// display-адаптеров из реестра драйверов. Нужно, чтобы режим энкодера "auto"
    /// демонстрации экрана РЕАЛЬНО брал дискретную NVIDIA с NVENC, а не откатывался
    /// на программный H264 (из-за чего у зрителя проседал fps).
    ///
    /// Ключевое различие для нашего железа:
    ///   • RTX 3050 / GTX / Quadro / Tesla / TITAN — есть NVENC → форсим hint NVENC;
    ///   • GeForce MX450 (и вся MX-серия) — NVENC НЕТ → оставляем auto, чтобы не
    ///     передавать несуществующий энкодер (иначе демка могла бы сломаться).
    /// </summary>
    public static class GpuCapabilities
    {
        // Класс «Display adapters» в реестре драйверов Windows.
        private const string DisplayClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static bool _probed;
        private static bool _hasNvenc;
        private static bool _hasIntelQsv;

        /// <summary>Есть ли в системе NVIDIA-карта с аппаратным NVENC.</summary>
        public static bool HasNvenc { get { Probe(); return _hasNvenc; } }

        /// <summary>Есть ли Intel-графика с Quick Sync (аппаратный H264/HEVC).</summary>
        public static bool HasIntelQuickSync { get { Probe(); return _hasIntelQsv; } }

        /// <summary>Все найденные описания видеокарт (для лога/диагностики).</summary>
        public static IReadOnlyList<string> AdapterNames { get { Probe(); return _names; } }
        private static readonly List<string> _names = new();

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
                if (root == null) return;
                foreach (var sub in root.GetSubKeyNames())
                {
                    // Интересуют только пронумерованные подключи адаптеров (0000, 0001…).
                    if (sub.Length != 4) continue;
                    using var k = root.OpenSubKey(sub);
                    string desc = k?.GetValue("DriverDesc") as string;
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    _names.Add(desc);

                    string d = desc.ToUpperInvariant();
                    if (d.Contains("NVIDIA") || d.Contains("GEFORCE") || d.Contains("QUADRO")
                        || d.Contains("RTX") || d.Contains("GTX"))
                    {
                        // MX-серия (MX150/MX250/MX450…) — БЕЗ NVENC. Всё остальное
                        // (RTX/GTX/Quadro/Tesla/TITAN) — с NVENC.
                        bool isMxSeries = System.Text.RegularExpressions.Regex.IsMatch(d, @"\bMX\d");
                        if (!isMxSeries) _hasNvenc = true;
                    }
                    if (d.Contains("INTEL") && (d.Contains("HD GRAPHICS") || d.Contains("UHD GRAPHICS")
                        || d.Contains("IRIS") || d.Contains("ARC")))
                        _hasIntelQsv = true;
                }
            }
            catch { /* реестр недоступен — оставляем всё false (auto) */ }
        }
    }
}
