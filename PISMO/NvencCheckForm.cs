using System;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Окно проверки нативного аппаратного энкодера (Этап 1). Скачивает FFmpeg
    /// при необходимости, определяет доступные HW-энкодеры и гоняет бенчмарк
    /// захвата+кодирования на 1080p60 / 720p60, показывая достигнутый fps. Так
    /// подтверждаем, что нативный NVENC на этой машине реально тянет 60 fps.
    /// </summary>
    public sealed class NvencCheckForm : Form
    {
        private readonly TextBox _log;
        private readonly Button _run;
        private readonly Button _open;
        private string _lastFile;

        public NvencCheckForm()
        {
            Text = "Проверка нативного NVENC (демонстрация 2.0)";
            ClientSize = new Size(640, 460);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(40, 42, 46);

            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 25, 28),
                ForeColor = Color.FromArgb(210, 212, 218),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.None
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.FromArgb(40, 42, 46) };
            _run = new Button
            {
                Text = "▶ Запустить проверку",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(59, 165, 93),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Size = new Size(220, 34),
                Location = new Point(12, 7),
                Cursor = Cursors.Hand
            };
            _run.FlatAppearance.BorderSize = 0;
            _run.Click += async (s, e) => await RunAsync();
            bottom.Controls.Add(_run);

            _open = new Button
            {
                Text = "📂 Открыть записанный файл",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(64, 68, 75),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Size = new Size(240, 34),
                Location = new Point(244, 7),
                Cursor = Cursors.Hand,
                Visible = false
            };
            _open.FlatAppearance.BorderSize = 0;
            _open.Click += (s, e) => { try { if (_lastFile != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_lastFile) { UseShellExecute = true }); } catch { } };
            bottom.Controls.Add(_open);

            Controls.Add(_log);
            Controls.Add(bottom);

            NativeNvenc.Log += OnLog;
            FormClosed += (s, e) => NativeNvenc.Log -= OnLog;

            Append("Нажмите «Запустить проверку». Будет скачан FFmpeg (~40 МБ, один раз),");
            Append("определены аппаратные энкодеры и выполнен бенчмарк 1080p60 / 720p60.");
        }

        private void OnLog(string s) { try { if (!IsDisposed) BeginInvoke(new Action(() => Append(s))); } catch { } }

        private void Append(string s)
        {
            _log.AppendText(s + Environment.NewLine);
        }

        private async Task RunAsync()
        {
            _run.Enabled = false;
            try
            {
                Append("");
                Append("=== Шаг 1: FFmpeg ===");
                if (!await NativeNvenc.EnsureFfmpegAsync()) { Append("Не удалось подготовить FFmpeg. Проверьте интернет."); return; }

                Append("");
                Append("=== Шаг 2: аппаратные энкодеры ===");
                var encs = await NativeNvenc.DetectHwEncodersAsync();
                if (encs.Length == 0) { Append("HW-энкодеры не найдены (NVENC/QuickSync недоступны в FFmpeg)."); }
                else Append("Найдены: " + string.Join(", ", encs));

                // Пробуем ВСЕ найденные H264 HW-энкодеры по порядку предпочтения
                // (NVENC → Quick Sync → AMF) и берём первый, что реально работает
                // и тянет 1080p60. Так даже без обновления драйвера NVIDIA
                // сработает через Quick Sync (Intel).
                var order = new[] { "h264_nvenc", "h264_qsv", "h264_amf" };
                string working = null;
                double best1080 = -1, best720 = -1;

                Append("");
                Append("=== Шаг 3: бенчмарк аппаратных энкодеров ===");
                foreach (var enc in order)
                {
                    if (Array.IndexOf(encs, enc) < 0) continue;
                    Append("");
                    Append("— " + enc + " —");
                    double f1080 = await NativeNvenc.BenchmarkAsync(enc, 1080, 60, 5);
                    if (f1080 < 0)
                    {
                        Append(enc + ": не запустился (см. лог — вероятно старый драйвер). Пробую следующий.");
                        continue;
                    }
                    double f720 = await NativeNvenc.BenchmarkAsync(enc, 720, 60, 5);
                    Append($"{enc}: 1080p ~{f1080:0} fps, 720p ~{f720:0} fps");
                    if (working == null) { working = enc; best1080 = f1080; best720 = f720; }
                    if (f1080 >= 60) break;   // нашли тянущий 1080p60 — хватит
                }

                Append("");
                Append("=== ИТОГ ===");
                if (working == null)
                {
                    Append("❌ Ни один аппаратный энкодер не запустился.");
                    Append("Для NVENC обновите драйвер NVIDIA (GeForce Experience / nvidia.com).");
                    Append("Если есть встроенная Intel — должен работать h264_qsv.");
                }
                else
                {
                    Append($"Рабочий энкодер: {working}");
                    Append($"Пропускная способность энкодера: 1080p ~{best1080:0} fps · 720p ~{best720:0} fps");

                    // Шаг 4: реальный захват экрана (DXGI) + кодирование в файл.
                    Append("");
                    Append("=== Шаг 4: реальный захват экрана (DXGI) ===");
                    var (capFps, file) = await NativeNvenc.BenchmarkCaptureAsync(working, 1080, 60, 5);

                    Append("");
                    Append("=== ИТОГ ===");
                    if (capFps >= 55)
                    {
                        Append($"✅ Реальный захват+кодирование 1080p: ~{capFps:0} fps (энкодер {working}).");
                        Append("Аппаратный путь работает — можно строить транспорт (Этап 2).");
                        _lastFile = file;
                        BeginInvoke(new Action(() => _open.Visible = System.IO.File.Exists(file)));
                    }
                    else if (capFps >= 0)
                    {
                        Append($"⚠ Захват дал ~{capFps:0} fps (ниже 60). Энкодер тянет, узкое место — захват.");
                        _lastFile = file;
                        BeginInvoke(new Action(() => _open.Visible = System.IO.File.Exists(file)));
                    }
                    else
                    {
                        Append("⚠ Реальный захват не удался (лог выше), но энкодер рабочий.");
                    }
                }
            }
            catch (Exception ex) { Append("Ошибка проверки: " + ex.Message); }
            finally { _run.Enabled = true; }
        }
    }
}
