using System;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Сохранение вложения сообщения на диск («Скачать» в контекстном меню).
    /// Байты у клиента уже есть (они пришли вместе с сообщением), так что запрос
    /// к БД не нужен — только диалог выбора файла.
    /// </summary>
    internal static class MediaSaver
    {
        /// <summary>Показывает «Сохранить как…» и пишет файл.</summary>
        public static void Save(IWin32Window owner, byte[] data, string suggestedName)
        {
            if (data == null || data.Length == 0)
            {
                MessageBox.Show(owner,
                    "Файл ещё не загружен — откройте сообщение и попробуйте снова.",
                    "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string name = SafeName(suggestedName);
            string ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

            try
            {
                using var dlg = new SaveFileDialog
                {
                    FileName = name,
                    // Первым — фильтр «родного» расширения, чтобы имя не потеряло его.
                    Filter = string.IsNullOrEmpty(ext)
                        ? "Все файлы (*.*)|*.*"
                        : $"{ext.ToUpperInvariant()} (*.{ext})|*.{ext}|Все файлы (*.*)|*.*",
                    OverwritePrompt = true,
                    RestoreDirectory = true
                };
                if (dlg.ShowDialog(owner) != DialogResult.OK) return;
                File.WriteAllBytes(dlg.FileName, data);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Не удалось сохранить файл:\n" + ex.Message,
                    "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Имя для изображения: расширение определяем по сигнатуре байтов,
        /// потому что в БД лежат только байты, без имени файла.</summary>
        public static string ImageName(byte[] data, int msgId)
            => $"pismo_{(msgId > 0 ? msgId.ToString() : DateTime.Now.ToString("yyyyMMdd_HHmmss"))}.{ImageExt(data)}";

        public static string VideoName(int msgId, bool circle = false)
            => $"pismo_{(circle ? "circle_" : "video_")}{(msgId > 0 ? msgId.ToString() : DateTime.Now.ToString("yyyyMMdd_HHmmss"))}.mp4";

        public static string AudioName(int msgId)
            => $"pismo_voice_{(msgId > 0 ? msgId.ToString() : DateTime.Now.ToString("yyyyMMdd_HHmmss"))}.wav";

        /// <summary>Расширение картинки по «магическим» первым байтам.</summary>
        private static string ImageExt(byte[] d)
        {
            if (d == null || d.Length < 4) return "png";
            if (d[0] == 0xFF && d[1] == 0xD8) return "jpg";
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "png";
            if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return "gif";
            if (d[0] == 0x42 && d[1] == 0x4D) return "bmp";
            if (d.Length > 11 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46
                && d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50) return "webp";
            return "png";
        }

        /// <summary>Убирает из имени символы, недопустимые в файловой системе.</summary>
        private static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "pismo_file";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
