using System;

namespace PISMO.Native
{
    /// <summary>
    /// Лёгкий транзиент-лимитер: давит КОРОТКИЕ громкие пики (клики клавиш/мыши,
    /// удары), которые «выпрыгивают» над огибающей голоса, — даже если пик ГРОМЧЕ
    /// самого голоса (чувствительный микрофон). Ровную речь не трогает (её огибающая
    /// поднимается, порог всплеска ползёт за ней), поэтому не даёт «рации».
    ///
    /// Работает in-place на 16-бит моно PCM. Look-ahead (~5мс): решение о приглушении
    /// принимается по максимуму в линии задержки, поэтому пик глушится ДО выхода, а
    /// приглушение держится, пока пик не покинет буфер. Ставится ПОСЛЕ RNNoise.
    /// </summary>
    internal sealed class TransientLimiter
    {
        private readonly int _la;
        private readonly float[] _buf;
        private int _pos;
        private float _gain = 1f;
        private float _voiceRef;
        private float _max;
        private int _maxAge;

        /// <summary>0..1 — агрессивность (0 = выкл, 1 = максимум подавления пиков).</summary>
        public float Strength = 1f;

        public TransientLimiter(int sampleRate)
        {
            _la = Math.Max(32, sampleRate / 200);   // ~5 мс упреждения
            _buf = new float[_la];
        }

        private float RescanMax()
        {
            float m = 0f;
            for (int i = 0; i < _buf.Length; i++) { float v = _buf[i]; if (v < 0) v = -v; if (v > m) m = v; }
            return m;
        }

        public void Process(byte[] data, int offset, int len)
        {
            float str = Strength; if (str <= 0f) return; if (str > 1f) str = 1f;
            int n = len / 2;
            for (int i = 0; i < n; i++)
            {
                int idx = offset + i * 2;
                short s = (short)(data[idx] | (data[idx + 1] << 8));
                float a = s < 0 ? -s : s;

                // Медленная огибающая «уровня голоса»: короткий клик её почти не
                // поднимает (медленный attack), устойчивая речь — поднимает.
                _voiceRef += (a - _voiceRef) * (a > _voiceRef ? 0.0008f : 0.0004f);

                // Линия задержки: на выход берём задержанный сэмпл.
                float delayed = _buf[_pos];
                _buf[_pos] = s;
                if (++_pos >= _la) _pos = 0;

                // Скользящий максимум по всей линии (look-ahead): пик виден заранее.
                if (a >= _max) { _max = a; _maxAge = 0; }
                else if (++_maxAge >= _la) { _max = RescanMax(); _maxAge = 0; }

                // Порог всплеска: заметно выше уровня голоса. Пик выше — прижимаем к порогу.
                float thr = _voiceRef * 2.0f + 400f;
                float target = _max > thr ? thr / _max : 1f;
                target = 1f - (1f - target) * str;   // сила регулирует глубину подавления
                _gain += (target - _gain) * (target < _gain ? 0.6f : 0.02f);

                float outv = delayed * _gain;
                int v = (int)outv;
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                data[idx] = (byte)(v & 0xFF);
                data[idx + 1] = (byte)((v >> 8) & 0xFF);
            }
        }
    }
}
