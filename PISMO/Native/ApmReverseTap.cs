using System;
using NAudio.Wave;

namespace PISMO.Native
{
    /// <summary>
    /// Прозрачная «врезка» в тракт воспроизведения: пропускает микс собеседников
    /// без изменений в колонки, а копию отдаёт эхоподавителю (APM ProcessReverse
    /// Stream) кадрами по 10 мс как дальний конец (референс эха).
    /// Формат микшера — float mono 48к; конвертируем в i16 для APM.
    /// </summary>
    internal sealed class ApmReverseTap : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly Action<byte[], int> _sink;
        private readonly int _frameSamples;         // 10 мс = 480 (48к моно)
        private readonly short[] _accum;
        private int _accumLen;
        private readonly byte[] _bytes;

        public ApmReverseTap(ISampleProvider src, int sampleRate, int channels, Action<byte[], int> sink)
        {
            _src = src;
            _sink = sink;
            _frameSamples = sampleRate / 100 * channels;
            _accum = new short[_frameSamples];
            _bytes = new byte[_frameSamples * 2];
        }

        public WaveFormat WaveFormat => _src.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _src.Read(buffer, offset, count);
            try
            {
                for (int i = 0; i < read; i++)
                {
                    float f = buffer[offset + i];
                    if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                    _accum[_accumLen++] = (short)(f * 32767f);
                    if (_accumLen == _frameSamples)
                    {
                        Buffer.BlockCopy(_accum, 0, _bytes, 0, _bytes.Length);
                        _sink(_bytes, _bytes.Length);
                        _accumLen = 0;
                    }
                }
            }
            catch { _accumLen = 0; }
            return read;
        }
    }
}
