using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PISMO
{
    public enum AttachKind { Image, Gif, File }

    public sealed class PendingAttachment
    {
        public byte[] Data { get; }
        public string FileName { get; }
        public AttachKind Kind { get; }

        /// <summary>
        /// Кадр ролика. Уходит картинкой в ту же строку сообщения, чтобы
        /// обложка была видна ещё до загрузки файла (см. VideoCover).
        /// </summary>
        public byte[] Cover { get; set; }

        public PendingAttachment(byte[] data, string fileName, AttachKind kind)
        {
            Data = data;
            FileName = fileName;
            Kind = kind;
        }
    }
}