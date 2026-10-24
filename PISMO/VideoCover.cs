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
