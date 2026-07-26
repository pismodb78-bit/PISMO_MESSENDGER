using System;
using NAudio.Wave;

namespace PISMO.Native
{
    /// <summary>
    /// Подмешивание звука, который проигрывает САМ PISMO (видео-кружки, голосовые
    /// сообщения), в дорожку демонстрации. process-loopback исключает наш процесс,
    /// поэтому эти звуки иначе не попадают в демку. Голоса звонка сюда НЕ идут — у
    /// них отдельный выход, — так что эхо не возникает.
    ///
    /// Плееры оборачивают свой источник в <see cref="TapWaveProvider"/>; тот на лету
    /// копирует проигрываемые сэмплы сюда (даунмикс в моно + ресемпл в 48кГц). Демо-
    /// путь (OnScreenAudioData при process-loopback) забирает их через Pull и
    /// подмешивает в исходящий звук.
    /// </summary>
    internal static class DemoMediaTap
    {
        public static volatile bool Active;   // true, когда демка идёт через process-loopback

        private const int SR = 48000;
        private static readonly object _lock = new();
        private static short[] _ring = new short[SR * 2];   // ~2с моно 48кГц
        private static int _read, _count;

        // Состояние линейного ресемплера между вызовами Push (одна дорожка за раз).
        private static int _lastRate;
        private static double _resCarry;
        private static float _resPrev;

        public static void Reset()
        {
            lock (_lock) { _read = 0; _count = 0; _lastRate = 0; _resCarry = 0; _resPrev = 0; }
        }

        /// <summary>Забрать n сэмплов (моно 48кГц) для подмешивания; недостаток —
        /// добивается тишиной. Возвращает, сколько реально было звука.</summary>
        public static int Pull(short[] dst, int n)
        {
            lock (_lock)
            {
                int got = Math.Min(n, _count);
                for (int i = 0; i < got; i++)
                {
                    dst[i] = _ring[_read];
                    _read = (_read + 1) % _ring.Length;
                }
                for (int i = got; i < n; i++) dst[i] = 0;
                _count -= got;
                return got;
            }
        }

        /// <summary>Приём PCM от плеера медиа (в формате его источника).</summary>
        public static void PushPcm(byte[] buf, int offset, int bytes, WaveFormat fmt)
        {
            if (!Active || bytes <= 0 || fmt == null) return;
            int ch = fmt.Channels < 1 ? 1 : fmt.Channels;
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
            int bps = isFloat ? 4 : 2;
            int frameBytes = bps * ch;
            int frames = bytes / frameBytes;
            if (frames <= 0) return;

            // Даунмикс в моно float.
            var mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float acc = 0f; int b = offset + f * frameBytes;
                for (int c = 0; c < ch; c++)
                {
                    int i = b + c * bps;
                    acc += isFloat ? BitConverter.ToSingle(buf, i)
                                   : (short)(buf[i] | (buf[i + 1] << 8)) / 32768f;
                }
                mono[f] = acc / ch;
            }

            int rate = fmt.SampleRate;
            lock (_lock)
            {
                if (rate == SR)
                {
                    for (int i = 0; i < frames; i++) Enqueue(F2S(mono[i]));
                    return;
                }

                // Линейный ресемпл в 48кГц с переносом дробной позиции между буферами.
                if (_lastRate != rate) { _lastRate = rate; _resCarry = 0; _resPrev = mono[0]; }
                double step = (double)rate / SR;
                double pos = _resCarry;
                while (pos < frames)
                {
                    int i1 = (int)Math.Floor(pos);
                    double frac = pos - i1;
                    float s0 = i1 < 0 ? _resPrev : mono[i1];
                    float s1 = (i1 + 1 < frames) ? mono[i1 + 1] : mono[frames - 1];
                    Enqueue(F2S((float)(s0 + (s1 - s0) * frac)));
                    pos += step;
                }
                _resCarry = pos - frames;
                _resPrev = mono[frames - 1];
            }
        }

        // Кладём один сэмпл в кольцо; при переполнении двигаем чтение (роняем старое).
        private static void Enqueue(short s)
        {
            _ring[(_read + _count) % _ring.Length] = s;
            if (_count < _ring.Length) _count++;
            else _read = (_read + 1) % _ring.Length;
        }

        private static short F2S(float v)
        {
            int s = (int)(v * 32767f);
            return (short)(s > 32767 ? 32767 : s < -32768 ? -32768 : s);
        }
    }

    /// <summary>Прозрачная обёртка источника воспроизведения: пропускает звук в плеер
    /// как есть и параллельно копирует его в <see cref="DemoMediaTap"/> для демки.</summary>
    internal sealed class TapWaveProvider : IWaveProvider
    {
        private readonly IWaveProvider _src;
        public TapWaveProvider(IWaveProvider src) { _src = src; }
        public WaveFormat WaveFormat => _src.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _src.Read(buffer, offset, count);
            if (read > 0 && DemoMediaTap.Active)
                try { DemoMediaTap.PushPcm(buffer, offset, read, _src.WaveFormat); } catch { }
            return read;
        }
    }
}
