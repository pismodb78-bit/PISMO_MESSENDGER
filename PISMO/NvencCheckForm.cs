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

                // Выбираем предпочтительный энкодер: NVENC → QuickSync → AMF.
                string enc = PickEncoder(encs, "h264");
                if (enc == null) { Append("Нет доступного H264 HW-энкодера — нативный путь на этой машине невозможен."); return; }

                Append("");
                Append("=== Шаг 3: бенчмарк (" + enc + ") ===");
                double f1080 = await NativeNvenc.BenchmarkAsync(enc, 1080, 60, 5);
                double f720  = await NativeNvenc.BenchmarkAsync(enc, 720, 60, 5);

                Append("");
                Append("=== ИТОГ ===");
                Append($"1080p60 → ~{(f1080 >= 0 ? f1080.ToString("0") : "?")} fps");
                Append($"720p60  → ~{(f720  >= 0 ? f720.ToString("0")  : "?")} fps");
                if (f1080 >= 55)
                    Append("✅ Нативный NVENC тянет 1080p60 — можно строить транспорт (Этап 2).");
                else if (f720 >= 55)
                    Append("⚠ 1080p60 не дотянул, но 720p60 ок. Транспорт имеет смысл на 720p.");
                else
                    Append("❌ Аппаратный энкодер не дал 60 fps — нужно разобраться (лог выше).");
            }
            catch (Exception ex) { Append("Ошибка проверки: " + ex.Message); }
            finally { _run.Enabled = true; }
        }

        private static string PickEncoder(string[] encs, string codec)
        {
            foreach (var pref in new[] { codec + "_nvenc", codec + "_qsv", codec + "_amf" })
                foreach (var e in encs) if (e == pref) return pref;
            return null;
        }
    }
}
