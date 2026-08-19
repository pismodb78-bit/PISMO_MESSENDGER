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

        /// <summary>Кодек демонстрации: "av1" (чётче при том же битрейте, полностью
        /// поддержан энкодером+декодером FFI), "h264" (совместимость), "vp8"/"vp9".
        /// По умолчанию AV1. Старое значение "h265"/"hevc" мигрируется в AV1 при
        /// загрузке (HEVC в FFI нет — раньше он молча откатывался на H264).</summary>
        public static string ScreenShareCodec { get; set; } = "av1";

        /// <summary>Видеокарта для кодирования демки (через реестр UserGpuPreferences):
        /// "auto" (не трогать выбор Windows), "high" (дискретная RTX/NVENC),
        /// "integrated" (встроенная Intel Quick Sync). По умолчанию auto.</summary>
        public static string GpuEncodePref { get; set; } = "auto";

        /// <summary>Автоопределение чувствительности микрофона (как в Discord).
        /// true = звук передаётся всегда (без порога), false = используется
        /// ручной порог VoiceThreshold.</summary>
        public static bool VoiceAutoSensitivity { get; set; } = false;

        /// <summary>Шумоподавление RNNoise (давит клавиатуру/мышь/шум). По умолчанию
        /// ВЫКЛ — оно тянется с CDN и на части систем глушит голос; включается
        /// вручную в настройках. Браузерное шумоподавление работает всегда.</summary>
        // Режим шумодава: "off" / "standard" (WebRTC APM) / "aggressive" (APM +
        // программный гейт — давит и клавиатурные клики). Меняется на лету.
        private static string _nsMode = "off";
        public static string NoiseSuppressMode
        {
            get => _nsMode;
            set
            {
                var v = (value ?? "").Trim().ToLowerInvariant();
                // Единый режим «включён»: старое значение "aggressive" сводим к нему же.
                _nsMode = v == "standard" || v == "aggressive" ? "standard" : "off";
            }
        }
        // Совместимость со старым булевым флагом (конфиги/старый код).
        public static bool NoiseSuppression
        {
            get => _nsMode != "off";
            set { if (value) { if (_nsMode == "off") _nsMode = "standard"; } else _nsMode = "off"; }
        }

        /// <summary>Сила шумоподавления 0..100 (%): 0 — выкл, 100 — максимум.
        /// Реализована как wet/dry-микс денойзера, поэтому регулируется на лету.</summary>
        private static int _nsStrength = 100;
        public static int NoiseSuppressionStrength
        {
            get => _nsStrength;
            set
            {
                _nsStrength = Math.Clamp(value, 0, 100);
                // Синхронизируем булев режим: 0% = выкл, иначе включён.
                NoiseSuppression = _nsStrength > 0;
            }
        }

        /// <summary>Усиление голоса НА ВЫХОДЕ цепи обработки (после шумодава/порога),
        /// 0..300 (%). Makeup-gain: шумодав приглушает голос — этим добираем громкость.
        /// Регулируется на лету.</summary>
        public static int VoiceOutputGain { get; set; } = 100;

        /// <summary>Ручной порог активации голоса в дБ (−60..0): звук тише порога
        /// не передаётся. Действует только при VoiceAutoSensitivity=false.</summary>
        public static int VoiceThreshold { get; set; } = -40;

        /// <summary>Аппаратное ускорение (GPU) для WebView2 — демонстрация экрана,
        /// видео в звонке и т.п. По умолчанию ВКЛ (как в Discord). Выключение
        /// помогает при чёрном экране/артефактах на проблемных видеодрайверах.</summary>
        public static bool HardwareAcceleration { get; set; } = true;

        /// <summary>Тема оформления: "dark" (по умолчанию, как раньше) или "light".</summary>
        public static string ThemeMode { get; set; } = "dark";

        /// <summary>Игровой оверлей: показывать участников голосового канала поверх
        /// игры (панель у правого края экрана). По умолчанию включён.</summary>
        public static bool OverlayEnabled { get; set; } = true;

        /// <summary>Сколько участников максимум рисовать в игровом оверлее.
        /// Минимум 1 (ты сам) — иначе панель была бы пустой; остальные сворачиваются
        /// в строку «и ещё N…», чтобы большой канал не перекрывал полэкрана.</summary>
        private static int _overlayMax = 5;
        public static int OverlayMaxParticipants
        {
            get => _overlayMax;
            set => _overlayMax = Math.Clamp(value, 1, 20);
        }

        // ── Внешний вид оверлея (окно «Настройка оверлея») ────────────────
        /// <summary>Положение панели на экране. −1 = авто: правый край, по центру
        /// по вертикали. Иначе — координаты, куда её перетащили.</summary>
        public static int OverlayX { get; set; } = -1;
        public static int OverlayY { get; set; } = -1;

        /// <summary>Непрозрачность подложки панели, 0..100. 0 — подложки нет вовсе
        /// (только имена поверх игры).</summary>
        private static int _ovBack = 45;
        public static int OverlayBackOpacity { get => _ovBack; set => _ovBack = Math.Clamp(value, 0, 100); }

        /// <summary>Непрозрачность строки участника, когда он молчит (0..100).</summary>
        private static int _ovSilent = 20;
        public static int OverlayAlphaSilent { get => _ovSilent; set => _ovSilent = Math.Clamp(value, 0, 100); }

        /// <summary>Непрозрачность строки участника, когда он говорит (0..100).</summary>
        private static int _ovSpeak = 75;
        public static int OverlayAlphaSpeaking { get => _ovSpeak; set => _ovSpeak = Math.Clamp(value, 0, 100); }

        /// <summary>Масштаб панели в процентах (75..150): под разные разрешения.</summary>
        private static int _ovScale = 100;
        public static int OverlayScale { get => _ovScale; set => _ovScale = Math.Clamp(value, 75, 150); }

        /// <summary>Цвет подложки панели (HEX).</summary>
        public static string OverlayBackColor { get; set; } = "#1E1F22";

        /// <summary>Цвет бейджа «В ЭФИРЕ» (HEX).</summary>
        public static string OverlayAccentColor { get; set; } = "#ED4245";

        /// <summary>Разбор HEX-цвета из настроек с запасным значением.</summary>
        public static System.Drawing.Color ParseColor(string hex, System.Drawing.Color fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return fallback;
                return System.Drawing.ColorTranslator.FromHtml(hex.Trim());
            }
            catch { return fallback; }
        }

        /// <summary>Показывать ВСЕ мониторы в выборе демонстрации (захват WGC).
        /// По умолчанию выключено: WGC ограничен ~30 fps, зато DXGI-захват (60 fps)
        /// видит только мониторы «своего» GPU — на мульти-GPU системах часть
        /// экранов пропадает из списка. Включите, если в диалоге выбора не все экраны.</summary>
        public static bool ScreenCaptureAllMonitors { get; set; } = false;

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
            //  РЕЖИМ «ВСЕ МОНИТОРЫ» (ScreenCaptureAllMonitors): WGC НЕ отключаем —
            //  DXGI-захват видит только экраны GPU, на котором живёт Chromium, и
            //  на мульти-GPU системах часть мониторов пропадает из диалога выбора.
            //  Цена — потолок ~30 fps у WGC-захвата. Жёлтую рамку WGC всё же гасим.
            string disabled = ScreenCaptureAllMonitors
                ? "--disable-features=WebRtcWgcRequireBorder,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling"
                : "--disable-features=WebRtcAllowWgcDesktopCapturer," +
                  "WebRtcAllowWgcScreenCapturer,WebRtcAllowWgcWindowCapturer," +
                  "WebRtcWgcRequireBorder,CalculateNativeWinOcclusion,IntensiveWakeUpThrottling";

            // Анти-троттлинг скрытой страницы (транспорт живёт в невидимом WebView)
            // enable-features:
            //  • WebRtcAllowH265* / PlatformHEVC* — включают HEVC в WebRTC;
            //  • MediaFoundationD3D11VideoCapture — аппаратный конвейер;
            //  • MediaFoundationVP8/VP9/H264/HEVC hardware encoding в WebRTC у
            //    Chromium ГАТИТСЯ фичей — без неё используется софт OpenH264/libvpx
            //    даже при наличии Quick Sync/NVENC. Включаем аппаратный энкод явно.
            const string always =
                " --disable-background-timer-throttling" +
                " --disable-renderer-backgrounding" +
                " --disable-backgrounding-occluded-windows" +
                " --disable-gpu-driver-bug-workarounds" +   // снимает «обходы», прячущие HW-энкодер
                " --enable-features=WebRtcAllowH265Send,WebRtcAllowH265Receive,PlatformHEVCEncoderSupport,PlatformHEVCDecoderSupport,MediaFoundationH264Encoding,MediaFoundationH264CbpEncoding,HardwareMediaKeyHandling";

            // Аппаратные видео-энкод/декод: GPU-блоклист обходим (--ignore-gpu-
            // blocklist), «обходы багов драйвера», прячущие HW-энкодер, снимаем
            // (--disable-gpu-driver-bug-workarounds). Выбор адаптера (RTX/NVENC
            // или Intel Quick Sync) — через реестр UserGpuPreferences (см.
            // GpuPreference / настройка «Видеокарта для кодирования»).
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
                        case "ScreenShareCodec":
                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                var c = val.Trim().ToLowerInvariant();
                                // HEVC в FFI нет (молча откатывался на H264) → мигрируем в AV1.
                                ScreenShareCodec = (c == "h265" || c == "hevc") ? "av1" : c;
                            }
                            break;
                        case "GpuEncodePref":
                            if (!string.IsNullOrWhiteSpace(val)) GpuEncodePref = val.Trim().ToLowerInvariant();
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
                        case "NoiseSuppressMode":
                            NoiseSuppressMode = val;   // применяется ПОСЛЕ булевого флага — режим главнее
                            break;
                        case "NoiseSuppressionStrength":
                            if (int.TryParse(val, out int nss)) NoiseSuppressionStrength = nss;
                            break;
                        case "VoiceOutputGain":
                            if (int.TryParse(val, out int vog)) VoiceOutputGain = Math.Clamp(vog, 0, 300);
                            break;
                        case "VoiceThreshold":
                            if (int.TryParse(val, out int vt)) VoiceThreshold = Math.Clamp(vt, -90, 0);
                            break;
                        case "HardwareAcceleration":
                            HardwareAcceleration = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "ThemeMode":
                            if (!string.IsNullOrWhiteSpace(val)) ThemeMode = val.Trim().ToLowerInvariant() == "light" ? "light" : "dark";
                            break;
                        case "ScreenCaptureAllMonitors":
                            ScreenCaptureAllMonitors = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "OverlayEnabled":
                            OverlayEnabled = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "OverlayMaxParticipants":
                            if (int.TryParse(val, out int omp)) OverlayMaxParticipants = omp;
                            break;
                        case "OverlayX": if (int.TryParse(val, out int ox)) OverlayX = ox; break;
                        case "OverlayY": if (int.TryParse(val, out int oy)) OverlayY = oy; break;
                        case "OverlayBackOpacity":
                            if (int.TryParse(val, out int obo)) OverlayBackOpacity = obo; break;
                        case "OverlayAlphaSilent":
                            if (int.TryParse(val, out int oas)) OverlayAlphaSilent = oas; break;
                        case "OverlayAlphaSpeaking":
                            if (int.TryParse(val, out int oap)) OverlayAlphaSpeaking = oap; break;
                        case "OverlayScale":
                            if (int.TryParse(val, out int osc)) OverlayScale = osc; break;
                        case "OverlayBackColor":
                            if (!string.IsNullOrWhiteSpace(val)) OverlayBackColor = val.Trim(); break;
                        case "OverlayAccentColor":
                            if (!string.IsNullOrWhiteSpace(val)) OverlayAccentColor = val.Trim(); break;
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
                    $"ScreenShareCodec={ScreenShareCodec}\n" +
                    $"GpuEncodePref={GpuEncodePref}\n" +
                    $"VoiceAutoSensitivity={(VoiceAutoSensitivity ? 1 : 0)}\n" +
                    $"NoiseSuppression={(NoiseSuppression ? 1 : 0)}\n" +
                    $"NoiseSuppressMode={NoiseSuppressMode}\n" +
                    $"NoiseSuppressionStrength={NoiseSuppressionStrength}\n" +
                    $"VoiceOutputGain={VoiceOutputGain}\n" +
                    $"VoiceThreshold={VoiceThreshold}\n" +
                    $"HardwareAcceleration={(HardwareAcceleration ? 1 : 0)}\n" +
                    $"ThemeMode={ThemeMode}\n" +
                    $"ScreenCaptureAllMonitors={(ScreenCaptureAllMonitors ? 1 : 0)}\n" +
                    $"OverlayEnabled={(OverlayEnabled ? 1 : 0)}\n" +
                    $"OverlayMaxParticipants={OverlayMaxParticipants}\n" +
                    $"OverlayX={OverlayX}\n" +
                    $"OverlayY={OverlayY}\n" +
                    $"OverlayBackOpacity={OverlayBackOpacity}\n" +
                    $"OverlayAlphaSilent={OverlayAlphaSilent}\n" +
                    $"OverlayAlphaSpeaking={OverlayAlphaSpeaking}\n" +
                    $"OverlayScale={OverlayScale}\n" +
                    $"OverlayBackColor={OverlayBackColor}\n" +
                    $"OverlayAccentColor={OverlayAccentColor}\n" +
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
