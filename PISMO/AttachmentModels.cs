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

        public PendingAttachment(byte[] data, string fileName, AttachKind kind)
        {
            Data = data;
            FileName = fileName;
            Kind = kind;
        }
    }
}