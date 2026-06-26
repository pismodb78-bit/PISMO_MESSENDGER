using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace PISMO
{
    /// <summary>
    /// Простой контейнер для видео-кружочков (аналог "круглых видео" Telegram).
    /// Формат:
    ///   8 байт  — magic "PSMOVID1"
    ///   4 байта — Int32 длина блока аудио (WAV) в байтах
    ///   4 байта — Int32 количество кадров
    ///   4 байта — Int32 FPS (кадров в секунду)
    ///   ...     — блок аудио (WAV, может быть нулевой длины)
    ///   для каждого кадра:
    ///     4 байта — Int32 длина JPEG
    ///     ...     — JPEG-данные
    /// Кодеки/контейнеры сторонних библиотек не используются — всё на
    /// чистых System.Drawing (JPEG) и NAudio (WAV), чтобы не тащить FFmpeg.
    /// </summary>
    internal static class VideoCircleCodec
    {
        private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("PSMOVID1");

        /// <summary>Упаковывает список кадров (Bitmap) и аудио (WAV-байты, может быть null) в один блоб.</summary>
        public static byte[] Encode(List<Bitmap> frames, byte[] wavAudio, int fps)
        {
            wavAudio ??= Array.Empty<byte>();

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(Magic);
            bw.Write(wavAudio.Length);
            bw.Write(frames.Count);
            bw.Write(fps);
            bw.Write(wavAudio);

            var jpegEncoder = GetJpegEncoder();
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);

            foreach (var frame in frames)
            {
                using var frameMs = new MemoryStream();
                frame.Save(frameMs, jpegEncoder, encParams);
                byte[] jpegBytes = frameMs.ToArray();

                bw.Write(jpegBytes.Length);
                bw.Write(jpegBytes);
            }

            return ms.ToArray();
        }

        /// <summary>Результат распаковки кружочка.</summary>
        public sealed class DecodedVideo
        {
            public List<Bitmap> Frames { get; } = new();
            public byte[] AudioWav { get; set; } = Array.Empty<byte>();
            public int Fps { get; set; } = 10;
        }

        public static DecodedVideo Decode(byte[] data)
        {
            var result = new DecodedVideo();

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            byte[] magic = br.ReadBytes(8);
            if (!MagicMatches(magic))
                throw new InvalidDataException("Неизвестный формат видео-кружочка.");

            int audioLen = br.ReadInt32();
            int frameCount = br.ReadInt32();
            int fps = br.ReadInt32();

            result.Fps = fps > 0 ? fps : 10;
            result.AudioWav = audioLen > 0 ? br.ReadBytes(audioLen) : Array.Empty<byte>();

            for (int i = 0; i < frameCount; i++)
            {
                int len = br.ReadInt32();
                byte[] jpeg = br.ReadBytes(len);

                using var frameMs = new MemoryStream(jpeg);
                result.Frames.Add(new Bitmap(frameMs));
            }

            return result;
        }

        private static bool MagicMatches(byte[] candidate)
        {
            if (candidate.Length != Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (candidate[i] != Magic[i]) return false;
            return true;
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                    return codec;
            throw new InvalidOperationException("JPEG-кодек не найден.");
        }
    }
}
