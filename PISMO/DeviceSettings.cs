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
        // Настройки храним в %APPDATA%\PISMO (как user_audio.json) — это переживает
        // обновления/переустановки, в отличие от папки рядом с exe, которая при
        // установке новой версии оказывается «чистой».
        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PISMO");
                try { Directory.CreateDirectory(dir); } catch { }
                string newPath = Path.Combine(dir, "devices.ini");

                // Одноразовая миграция со старого места (рядом с exe), если в AppData
                // ещё нет файла, но он есть в старой папке.
                try
                {
                    string oldPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "devices.ini");
                    if (!File.Exists(newPath) && File.Exists(oldPath))
                        File.Copy(oldPath, newPath, false);
                }
                catch { }

                return newPath;
            }
        }

        /// <summary>Имя выбранной камеры (DirectShow FilterInfo.Name) или "" если не выбрана / системная по умолчанию.</summary>
        public static string CameraName { get; set; } = "";

        /// <summary>Индекс выбранного микрофона (NAudio WaveIn device index), -1 = системный по умолчанию.</summary>
        public static int MicrophoneIndex { get; set; } = -1;

        /// <summary>Имя микрофона (для отображения / сверки при смене устройств в системе).</summary>
        public static string MicrophoneName { get; set; } = "";

        /// <summary>Имя устройства вывода (динамики/наушники) для звонков;
        /// "" = системное по умолчанию. Выбирается стрелкой у 🎧 в футере.</summary>
        public static string SpeakerName { get; set; } = "";

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

        /// <summary>Шумоподавление RNNoise (давит клавиатуру/мышь/шум). По умолчанию
        /// ВЫКЛ — оно тянется с CDN и на части систем глушит голос; включается
        /// вручную в настройках. Браузерное шумоподавление работает всегда.</summary>
        public static bool NoiseSuppression { get; set; } = false;

        /// <summary>Ручной порог активации голоса в дБ (−60..0): звук тише порога
        /// не передаётся. Действует только при VoiceAutoSensitivity=false.</summary>
        public static int VoiceThreshold { get; set; } = -40;

        /// <summary>Аппаратное ускорение (GPU) для WebView2 — демонстрация экрана,
        /// видео в звонке и т.п. По умолчанию ВКЛ (как в Discord). Выключение
        /// помогает при чёрном экране/артефактах на проблемных видеодрайверах.</summary>
        public static bool HardwareAcceleration { get; set; } = true;

        /// <summary>Доп. аргументы Chromium для WebView2 с учётом настройки HW-ускорения.
        /// При включённом ускорении ЯВНО разрешаем GPU-растеризацию и, главное,
        /// аппаратное кодирование/декодирование видео (NVENC/Media Foundation) +
        /// игнорируем GPU-блоклист — иначе демонстрация экрана кодируется на CPU и
        /// дискретная видеокарта простаивает (Video Encode 0%). При выключенном —
        /// программный рендеринг (--disable-gpu).</summary>
        public static string WebViewArgs(string baseArgs)
        {
            // ЕДИНЫЙ список отключаемых фич: два --disable-features в командной
            // строке нельзя — Chromium применяет только последний.
            //  • WebRtcAllowWgc*/WgcRequireBorder — WGC-захватчик экрана ограничен
            //    ~30 fps независимо от запрошенного; отключение = откат на DXGI
            //    Desktop Duplication с честными 60 fps;
            //  • CalculateNativeWinOcclusion — WebView звонка позиционируется ЗА
            //    экраном (1×1), Windows-окклюзия помечала страницу «невидимой», и
            //    Chromium троттлил её таймеры/конвейер кадров;
            //  • IntensiveWakeUpThrottling — то же на длинных демках (таймеры до
            //    1 раза в секунду после нескольких минут «в фоне»).
            const string disabled =
                "--disable-features=WebRtcAllowWgcDesktopCapturer," +
                "WebRtcAllowWgcScreenCapturer,WebRtcAllowWgcWindowCapturer," +
                "WebRtcWgcRequireBorder,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling";

            // Анти-троттлинг скрытой страницы (транспорт живёт в невидимом WebView).
            const string always =
                " --disable-background-timer-throttling" +
                " --disable-renderer-backgrounding" +
                " --disable-backgrounding-occluded-windows";

            // Аппаратные видео-энкод/декод в Chromium включены ПО УМОЛЧАНИЮ —
            // мешает только GPU-блоклист (обходим). Выбор адаптера (встроенная
            // Intel с Quick Sync — надёжный аппаратный энкодер) делается через
            // реестр UserGpuPreferences (см. GpuPreference), а НЕ форсом
            // дискретной: MX-карты часто без NVENC → откат в софт.
            return HardwareAcceleration
                ? (baseArgs + " " + disabled + always
                    + " --ignore-gpu-blocklist"
                    + " --enable-gpu-rasterization"
                    + " --enable-zero-copy").Trim()
                : (baseArgs + " " + disabled + always
                    + " --disable-gpu --disable-gpu-compositing").Trim();
        }

        // Горячие клавиши в звонке (значение = (int)Keys с модификаторами; 0 = выкл).
        // По умолчанию Ctrl+Alt+M / C / S.
        public static int HotkeyMic { get; set; } = (int)(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.M);
        public static int HotkeyCamera { get; set; } = (int)(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.C);
        public static int HotkeyScreen { get; set; } = (int)(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.S);
        public static int HotkeyDeafen { get; set; } = (int)(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.D);

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
                        case "SpeakerName":
                            SpeakerName = val;
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
                        case "NoiseSuppression":
                            NoiseSuppression = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "VoiceThreshold":
                            if (int.TryParse(val, out int vt)) VoiceThreshold = Math.Clamp(vt, -60, 0);
                            break;
                        case "HardwareAcceleration":
                            HardwareAcceleration = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "HotkeyMic":
                            if (int.TryParse(val, out int hm)) HotkeyMic = hm;
                            break;
                        case "HotkeyCamera":
                            if (int.TryParse(val, out int hc)) HotkeyCamera = hc;
                            break;
                        case "HotkeyDeafen":
                            if (int.TryParse(val, out int hd)) HotkeyDeafen = hd;
                            break;
                        case "HotkeyScreen":
                            if (int.TryParse(val, out int hs)) HotkeyScreen = hs;
                            break;
                    }
                }
            }
            catch { /* используем значения по умолчанию */ }

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
                    $"SpeakerName={SpeakerName}\n" +
                    $"MicrophoneGain={MicrophoneGain.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n" +
                    $"ScreenShareResolutionHeight={ScreenShareResolutionHeight}\n" +
                    $"ScreenShareFps={ScreenShareFps}\n" +
                    $"VoiceAutoSensitivity={(VoiceAutoSensitivity ? 1 : 0)}\n" +
                    $"NoiseSuppression={(NoiseSuppression ? 1 : 0)}\n" +
                    $"VoiceThreshold={VoiceThreshold}\n" +
                    $"HardwareAcceleration={(HardwareAcceleration ? 1 : 0)}\n" +
                    $"HotkeyMic={HotkeyMic}\n" +
                    $"HotkeyCamera={HotkeyCamera}\n" +
                    $"HotkeyScreen={HotkeyScreen}\n" +
                    $"HotkeyDeafen={HotkeyDeafen}\n";

                File.WriteAllText(FilePath, content);
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
