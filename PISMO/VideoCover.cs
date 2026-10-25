// ============================================================
//  VideoCover — обложка ролика
// ============================================================
//
//  Кадр, который уходит ВМЕСТЕ с видео: в строке сообщения есть место и под
//  картинку, и под файл, поэтому обложка кладётся туда же, куда обычная
//  фотография. Собеседник видит, что за ролик, ещё до того как скачает его,
//  а пузырь сразу встаёт правильной формы — как в телеграме. Менять базу для
//  этого не нужно: нового столбца не требуется.
//
//  Кадр берём у самой Windows: Проводник умеет делать миниатюры видео, и это
//  те же кадры, что видны в папке. Своего декодера в приложении нет, тащить
//  ради обложки ffmpeg — лишнее.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace PISMO
{
    internal static class VideoCover
    {
        /// <summary>Обложка файла с диска: JPEG или null, если Windows кадра не дала.</summary>
        public static byte[] Jpeg(string path, int maxSide = 640)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                var guid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref guid, out var factory);
                if (factory == null) return null;

                factory.GetImage(new SIZE { cx = maxSide, cy = maxSide },
                                 SIIGBF.RESIZETOFIT | SIIGBF.BIGGERSIZEOK, out hbm);
                if (hbm == IntPtr.Zero) return null;

                using var bmp = Image.FromHbitmap(hbm);
                // Обложка должна быть лёгкой: она едет в каждой ленте, а
                // разглядывать её незачем — для этого есть сам ролик.
                float k = maxSide / (float)Math.Max(bmp.Width, bmp.Height);
                using var small = k >= 1f
                    ? new Bitmap(bmp)
                    : new Bitmap(bmp, Math.Max(1, (int)(bmp.Width * k)), Math.Max(1, (int)(bmp.Height * k)));
                using var flat = new Bitmap(small.Width, small.Height,
                                            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(flat))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Black);
                    g.DrawImage(small, 0, 0, small.Width, small.Height);
                }
                using var ms = new MemoryStream();
                flat.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                return ms.ToArray();
            }
            catch { return null; }
            finally { if (hbm != IntPtr.Zero) try { DeleteObject(hbm); } catch { } }
        }

        /// <summary>
        /// Положить снятый кадр ОБЛОЖКОЙ: в кеш картинок — чтобы показалась
        /// уже на следующей отрисовке, — и в саму строку сообщения, если
        /// обложки там ещё нет. Дальше её видят все клиенты, не скачивая ролик.
        /// Пишем только в пустое место и молча: прав на чужую строку может и
        /// не быть.
        /// </summary>
        public static void Share(string table, int msgId, byte[] jpeg, string cacheKind = "img")
        {
            if (msgId <= 0 || jpeg == null || jpeg.Length == 0 || string.IsNullOrEmpty(table)) return;
            try { MediaCache.Put(msgId, cacheKind, jpeg, null); } catch { }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var up = new MySqlConnector.MySqlCommand(
                        $"UPDATE {table} SET image_data=@img " +
                        "WHERE id=@id AND (image_data IS NULL OR OCTET_LENGTH(image_data)=0)", conn);
                    up.Parameters.AddWithValue("@img", jpeg);
                    up.Parameters.AddWithValue("@id", msgId);
                    up.ExecuteNonQuery();
                }
                catch { }
            });
        }

        /// <summary>
        /// Заменить обложку ролика, даже если она уже есть, — для ЧЁРНЫХ
        /// обложек: раньше кадр брался самый первый, а он у многих роликов
        /// чёрный. Звать только для сообщений-роликов: у снимка картинка — это
        /// сам снимок.
        /// </summary>
        public static void Replace(string table, int msgId, byte[] jpeg, string cacheKind = "img")
        {
            if (msgId <= 0 || jpeg == null || jpeg.Length == 0 || string.IsNullOrEmpty(table)) return;
            try { MediaCache.Put(msgId, cacheKind, jpeg, null); } catch { }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var up = new MySqlConnector.MySqlCommand(
                        $"UPDATE {table} SET image_data=@img WHERE id=@id", conn);
                    up.Parameters.AddWithValue("@img", jpeg);
                    up.Parameters.AddWithValue("@id", msgId);
                    up.ExecuteNonQuery();
                }
                catch { }
            });
        }

        /// <summary>Почти чёрная ли картинка — средняя яркость по копии 16×16.</summary>
        public static bool IsDark(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return false;
            try
            {
                using var ms = new System.IO.MemoryStream(jpeg);
                using var src = System.Drawing.Image.FromStream(ms);
                using var small = new System.Drawing.Bitmap(16, 16);
                using (var g = System.Drawing.Graphics.FromImage(small))
                    g.DrawImage(src, 0, 0, 16, 16);
                double sum = 0;
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        var c = small.GetPixel(x, y);
                        sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    }
                return sum / 256.0 < 14.0;
            }
            catch { return false; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string path, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [Flags]
        private enum SIIGBF
        {
            RESIZETOFIT = 0x00,
            BIGGERSIZEOK = 0x01,
            MEMORYONLY = 0x02,
            ICONONLY = 0x04,
            THUMBNAILONLY = 0x08,
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }
    }
}
