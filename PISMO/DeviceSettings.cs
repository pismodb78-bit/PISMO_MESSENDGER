using System;
using System.IO;

namespace PISMO
{
    /// <summary>
    /// Локальные настройки устройств (камера/микрофон) и TURN сервера.
    /// Хранятся в файлах devices.ini и turn.ini рядом с exe — настройки привязаны
    /// к конкретному компьютеру, а не к учётной записи пользователя.
    /// </summary>
    internal static class DeviceSettings
    {
        private static string FilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devices.ini");

        /// <summary>Имя выбранной камеры (DirectShow FilterInfo.Name) или "" если не выбрана / системная по умолчанию.</summary>
        public static string CameraName { get; set; } = "";

        /// <summary>Индекс выбранного микрофона (NAudio WaveIn device index), -1 = системный по умолчанию.</summary>
        public static int MicrophoneIndex { get; set; } = -1;

        /// <summary>Имя микрофона (для отображения / сверки при смене устройств в системе).</summary>
        public static string MicrophoneName { get; set; } = "";

        /// <summary>Громкость микрофона при записи (множитель, 0.0–2.0). По умолчанию 1.0.</summary>
        public static float MicrophoneGain { get; set; } = 1.0f;

        /// <summary>Целевая высота (p) для демонстрации экрана: 1080, 720, 480 или 360.</summary>
        public static int ScreenShareResolutionHeight { get; set; } = 720;

        /// <summary>FPS для демонстрации экрана (1..60). Обычно 60,45,30,15.</summary>
        public static int ScreenShareFps { get; set; } = 30;

        /// <summary>Автоопределение чувствительности микрофона (как в Discord).
        /// true = звук передаётся всегда (без порога), false = используется
        /// ручной порог VoiceThreshold.</summary>
        public static bool VoiceAutoSensitivity { get; set; } = true;

        /// <summary>Ручной порог активации голоса в дБ (−60..0): звук тише порога
        /// не передаётся. Действует только при VoiceAutoSensitivity=false.</summary>
        public static int VoiceThreshold { get; set; } = -40;

        // Горячие клавиши в звонке (значение = (int)Keys с модификаторами; 0 = выкл).
        // По умолчанию Ctrl+Alt+M / C / S.
        public static int HotkeyMic { get; set; } = (int)(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.M);
        public static int HotkeyCamera { get; set; } = (int)(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.C);
        public static int HotkeyScreen { get; set; } = (int)(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.S);

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;

                foreach (var rawLine in File.ReadAllLines(FilePath))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;

                    string key = line[..eq].Trim();
                    string val = line[(eq + 1)..].Trim();

                    switch (key)
                    {
                        case "CameraName":
                            CameraName = val;
                            break;
                        case "MicrophoneIndex":
                            if (int.TryParse(val, out int mi)) MicrophoneIndex = mi;
                            break;
                        case "MicrophoneName":
                            MicrophoneName = val;
                            break;
                        case "MicrophoneGain":
                            if (float.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out float g))
                                MicrophoneGain = g;
                            break;
                        case "ScreenShareResolutionHeight":
                            if (int.TryParse(val, out int rh)) ScreenShareResolutionHeight = rh;
                            break;
                        case "ScreenShareFps":
                            if (int.TryParse(val, out int sf)) ScreenShareFps = sf;
                            break;
                        case "VoiceAutoSensitivity":
                            VoiceAutoSensitivity = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "VoiceThreshold":
                            if (int.TryParse(val, out int vt)) VoiceThreshold = Math.Clamp(vt, -60, 0);
                            break;
                        case "HotkeyMic":
                            if (int.TryParse(val, out int hm)) HotkeyMic = hm;
                            break;
                        case "HotkeyCamera":
                            if (int.TryParse(val, out int hc)) HotkeyCamera = hc;
                            break;
                        case "HotkeyScreen":
                            if (int.TryParse(val, out int hs)) HotkeyScreen = hs;
                            break;
                    }
                }
            }
            catch { /* используем значения по умолчанию */ }

            // Загружаем также TURN настройки
            TurnSettings.Load();
            // LiveKit (SFU) — основной транспорт звонков
            LiveKitSettings.Load();
        }

        public static void Save()
        {
            try
            {
                string content =
                    "# PISMO — настройки устройств (создаётся автоматически)\n" +
                    $"CameraName={CameraName}\n" +
                    $"MicrophoneIndex={MicrophoneIndex}\n" +
                    $"MicrophoneName={MicrophoneName}\n" +
                    $"MicrophoneGain={MicrophoneGain.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n" +
                    $"ScreenShareResolutionHeight={ScreenShareResolutionHeight}\n" +
                    $"ScreenShareFps={ScreenShareFps}\n" +
                    $"VoiceAutoSensitivity={(VoiceAutoSensitivity ? 1 : 0)}\n" +
                    $"VoiceThreshold={VoiceThreshold}\n" +
                    $"HotkeyMic={HotkeyMic}\n" +
                    $"HotkeyCamera={HotkeyCamera}\n" +
                    $"HotkeyScreen={HotkeyScreen}\n";

                File.WriteAllText(FilePath, content);

                // Сохраняем также TURN настройки
                TurnSettings.Save();
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "Не удалось сохранить настройки устройств:\n" + ex.Message,
                    "PISMO", System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
        }
    }
}
