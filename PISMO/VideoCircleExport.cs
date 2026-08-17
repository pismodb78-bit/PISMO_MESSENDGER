using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace PISMO
{
    /// <summary>
    /// Экспорт видео-кружка в обычный AVI (видео MJPG + звук PCM).
    ///
    /// Кружки лежат в собственном контейнере PSMOVID1 (см. VideoCircleCodec) —
    /// его не откроет ни один сторонний плеер, поэтому «скачать как .mp4» давало
    /// файл с ошибкой «тип файла не поддерживается».
    ///
    /// Перекодирования тут НЕТ: внутри кружка уже лежат JPEG-кадры и WAV, а это
    /// ровно то, что кладётся в AVI как MJPG-видео и PCM-звук. Мы только
    /// переписываем заголовки — быстро и без потери качества, и без FFmpeg,
    /// которого в проекте намеренно нет.
    /// </summary>
    internal static class VideoCircleExport
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PSMOVID1");

        /// <summary>Это блоб видео-кружка (а не обычный видеофайл)?</summary>
        public static bool IsCircle(byte[] data)
        {
            if (data == null || data.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (data[i] != Magic[i]) return false;
            return true;
        }

        /// <summary>Переупаковывает кружок в AVI. null — если это не кружок или
        /// внутри не оказалось кадров.</summary>
        public static byte[] ToAvi(byte[] blob)
        {
            if (!IsCircle(blob)) return null;

            // ── Разбор PSMOVID1 (JPEG берём СЫРЫМИ, не декодируя в Bitmap) ──
            var frames = new List<byte[]>();
            byte[] wav;
            int fps;
            using (var ms = new MemoryStream(blob))
            using (var br = new BinaryReader(ms))
            {
                br.ReadBytes(8);                       // magic
                int audioLen = br.ReadInt32();
                int frameCount = br.ReadInt32();
                fps = br.ReadInt32();
                if (fps <= 0) fps = 10;
                wav = audioLen > 0 ? br.ReadBytes(audioLen) : Array.Empty<byte>();
                for (int i = 0; i < frameCount; i++)
                {
                    int len = br.ReadInt32();
                    if (len <= 0 || ms.Position + len > ms.Length) break;
                    frames.Add(br.ReadBytes(len));
                }
            }
            if (frames.Count == 0) return null;

            // Размер кадра — из первого JPEG (декодируем ровно один).
            int width, height;
            using (var fs = new MemoryStream(frames[0]))
            using (var img = Image.FromStream(fs)) { width = img.Width; height = img.Height; }

            var au = ParseWav(wav);   // null, если звука нет либо WAV непонятный

            // ── Сборка AVI ──────────────────────────────────────────────
            using var outMs = new MemoryStream();
            using var w = new BinaryWriter(outMs);

            int streams = au != null ? 2 : 1;
            int maxJpeg = 0; foreach (var f in frames) if (f.Length > maxJpeg) maxJpeg = f.Length;

            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            long riffSizePos = outMs.Position; w.Write(0);
            w.Write(Encoding.ASCII.GetBytes("AVI "));

            // LIST hdrl
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            long hdrlSizePos = outMs.Position; w.Write(0);
            w.Write(Encoding.ASCII.GetBytes("hdrl"));

            // avih
            w.Write(Encoding.ASCII.GetBytes("avih"));
            w.Write(56);
            w.Write((int)(1000000.0 / fps));      // dwMicroSecPerFrame
            w.Write(maxJpeg * fps);               // dwMaxBytesPerSec (оценка)
            w.Write(0);                           // dwPaddingGranularity
            w.Write(0x10);                        // dwFlags = AVIF_HASINDEX
            w.Write(frames.Count);                // dwTotalFrames
            w.Write(0);                           // dwInitialFrames
            w.Write(streams);                     // dwStreams
            w.Write(maxJpeg);                     // dwSuggestedBufferSize
            w.Write(width);
            w.Write(height);
            for (int i = 0; i < 4; i++) w.Write(0);   // dwReserved[4]

            // ── Поток 0: видео ──
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            long vStrlPos = outMs.Position; w.Write(0);
            w.Write(Encoding.ASCII.GetBytes("strl"));

            w.Write(Encoding.ASCII.GetBytes("strh"));
            w.Write(56);
            w.Write(Encoding.ASCII.GetBytes("vids"));
            w.Write(Encoding.ASCII.GetBytes("MJPG"));
            w.Write(0);                 // dwFlags
            w.Write((short)0);          // wPriority
            w.Write((short)0);          // wLanguage
            w.Write(0);                 // dwInitialFrames
            w.Write(1);                 // dwScale
            w.Write(fps);               // dwRate  → fps = Rate/Scale
            w.Write(0);                 // dwStart
            w.Write(frames.Count);      // dwLength
            w.Write(maxJpeg);           // dwSuggestedBufferSize
            w.Write(-1);                // dwQuality
            w.Write(0);                 // dwSampleSize (0 = кадр переменной длины)
            w.Write((short)0); w.Write((short)0);
            w.Write((short)width); w.Write((short)height);

            w.Write(Encoding.ASCII.GetBytes("strf"));
            w.Write(40);                            // BITMAPINFOHEADER
            w.Write(40);                            // biSize
            w.Write(width);
            w.Write(height);
            w.Write((short)1);                      // biPlanes
            w.Write((short)24);                     // biBitCount
            w.Write(Encoding.ASCII.GetBytes("MJPG")); // biCompression
            w.Write(width * height * 3);            // biSizeImage
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);

            PatchSize(outMs, w, vStrlPos);

            // ── Поток 1: звук ──
            if (au != null)
            {
                w.Write(Encoding.ASCII.GetBytes("LIST"));
                long aStrlPos = outMs.Position; w.Write(0);
                w.Write(Encoding.ASCII.GetBytes("strl"));

                w.Write(Encoding.ASCII.GetBytes("strh"));
                w.Write(56);
                w.Write(Encoding.ASCII.GetBytes("auds"));
                w.Write(0);                    // fccHandler
                w.Write(0);                    // dwFlags
                w.Write((short)0); w.Write((short)0);
                w.Write(0);                    // dwInitialFrames
                w.Write(au.BlockAlign);        // dwScale
                w.Write(au.AvgBytesPerSec);    // dwRate
                w.Write(0);                    // dwStart
                w.Write(au.Data.Length / Math.Max(1, au.BlockAlign));  // dwLength в блоках
                w.Write(au.AvgBytesPerSec);    // dwSuggestedBufferSize
                w.Write(-1);                   // dwQuality
                w.Write(au.BlockAlign);        // dwSampleSize
                w.Write((short)0); w.Write((short)0); w.Write((short)0); w.Write((short)0);

                w.Write(Encoding.ASCII.GetBytes("strf"));
                w.Write(18);                       // WAVEFORMATEX
                w.Write((short)au.FormatTag);
                w.Write((short)au.Channels);
                w.Write(au.SampleRate);
                w.Write(au.AvgBytesPerSec);
                w.Write((short)au.BlockAlign);
                w.Write((short)au.BitsPerSample);
                w.Write((short)0);                 // cbSize

                PatchSize(outMs, w, aStrlPos);
            }

            PatchSize(outMs, w, hdrlSizePos);

            // ── LIST movi ───────────────────────────────────────────────
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            long moviSizePos = outMs.Position; w.Write(0);
            long moviDataStart = outMs.Position;      // отсюда считаются смещения в idx1
            w.Write(Encoding.ASCII.GetBytes("movi"));

            var index = new List<(string id, long off, int len)>();
            // Звук режем на порции «на кадр», чтобы картинка и звук шли вперемешку:
            // одним куском в начале часть плееров теряет синхронизацию.
            int audioPerFrame = 0;
            if (au != null)
            {
                audioPerFrame = au.AvgBytesPerSec / fps;
                audioPerFrame -= audioPerFrame % Math.Max(1, au.BlockAlign);
                if (audioPerFrame <= 0) audioPerFrame = au.BlockAlign;
            }
            int audioPos = 0;

            for (int i = 0; i < frames.Count; i++)
            {
                index.Add(("00dc", outMs.Position - moviDataStart, frames[i].Length));
                WriteChunk(w, outMs, "00dc", frames[i]);

                if (au != null && audioPos < au.Data.Length)
                {
                    int take = Math.Min(audioPerFrame, au.Data.Length - audioPos);
                    // Последнему кадру отдаём весь остаток, чтобы звук не обрезался.
                    if (i == frames.Count - 1) take = au.Data.Length - audioPos;
                    var part = new byte[take];
                    Buffer.BlockCopy(au.Data, audioPos, part, 0, take);
                    audioPos += take;
                    index.Add(("01wb", outMs.Position - moviDataStart, take));
                    WriteChunk(w, outMs, "01wb", part);
                }
            }

            PatchSize(outMs, w, moviSizePos);

            // ── idx1 ────────────────────────────────────────────────────
            w.Write(Encoding.ASCII.GetBytes("idx1"));
            w.Write(index.Count * 16);
            foreach (var (id, off, len) in index)
            {
                w.Write(Encoding.ASCII.GetBytes(id));
                w.Write(0x10);              // AVIIF_KEYFRAME — у MJPG каждый кадр ключевой
                w.Write((int)off);
                w.Write(len);
            }

            PatchSize(outMs, w, riffSizePos);
            return outMs.ToArray();
        }

        /// <summary>Пишет чанк с обязательным выравниванием на чётную границу.</summary>
        private static void WriteChunk(BinaryWriter w, MemoryStream ms, string id, byte[] data)
        {
            w.Write(Encoding.ASCII.GetBytes(id));
            w.Write(data.Length);
            w.Write(data);
            if ((data.Length & 1) != 0) w.Write((byte)0);
        }

        /// <summary>Проставляет размер блока, записанный «заглушкой» ранее.</summary>
        private static void PatchSize(MemoryStream ms, BinaryWriter w, long sizePos)
        {
            long end = ms.Position;
            ms.Position = sizePos;
            w.Write((int)(end - sizePos - 4));
            ms.Position = end;
        }

        private sealed class WavInfo
        {
            public int FormatTag = 1, Channels = 1, SampleRate = 44100;
            public int AvgBytesPerSec, BlockAlign = 2, BitsPerSample = 16;
            public byte[] Data = Array.Empty<byte>();
        }

        /// <summary>Достаёт формат и PCM-данные из WAV. null — звука нет/формат чужой.</summary>
        private static WavInfo ParseWav(byte[] wav)
        {
            if (wav == null || wav.Length < 44) return null;
            try
            {
                using var ms = new MemoryStream(wav);
                using var br = new BinaryReader(ms);
                // ReadBytes + ASCII, а НЕ ReadChars: у BinaryReader кодировка UTF-8, и
                // байт >127 в идентификаторе съел бы лишнее, сбив разбор с шага.
                string Fourcc() => Encoding.ASCII.GetString(br.ReadBytes(4));

                if (Fourcc() != "RIFF") return null;
                br.ReadInt32();
                if (Fourcc() != "WAVE") return null;

                var info = new WavInfo();
                bool haveFmt = false;
                while (ms.Position + 8 <= ms.Length)
                {
                    string id = Fourcc();
                    int size = br.ReadInt32();
                    if (size < 0 || ms.Position + size > ms.Length) break;

                    if (id == "fmt ")
                    {
                        long next = ms.Position + size;
                        info.FormatTag = br.ReadInt16();
                        info.Channels = br.ReadInt16();
                        info.SampleRate = br.ReadInt32();
                        info.AvgBytesPerSec = br.ReadInt32();
                        info.BlockAlign = br.ReadInt16();
                        info.BitsPerSample = br.ReadInt16();
                        haveFmt = true;
                        ms.Position = next;
                    }
                    else if (id == "data")
                    {
                        info.Data = br.ReadBytes(size);
                        if ((size & 1) != 0 && ms.Position < ms.Length) ms.Position++;
                    }
                    else
                    {
                        ms.Position += size + (size & 1);
                    }
                }

                if (!haveFmt || info.Data.Length == 0) return null;
                if (info.BlockAlign <= 0) info.BlockAlign = Math.Max(1, info.Channels * info.BitsPerSample / 8);
                if (info.AvgBytesPerSec <= 0) info.AvgBytesPerSec = info.SampleRate * info.BlockAlign;
                return info;
            }
            catch { return null; }
        }
    }
}
