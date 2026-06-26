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
                    $"ScreenShareFps={ScreenShareFps}\n";

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
