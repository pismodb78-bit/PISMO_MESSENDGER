using System;

namespace PISMO.Native
{
    /// <summary>
    /// Частотный шумодав микрофона (STFT + винеровская фильтрация) — чистый C#,
    /// без нативных зависимостей. В отличие от гейта, реально снижает ПОСТОЯННЫЙ
    /// фон (кулер, шипение, гул) даже во время речи, оценивая спектр шума и
    /// прижимая бины с низким SNR.
    ///
    /// N=512, HOP=256, окно Ханна, 50% перекрытие (COLA=1, анализ-Ханн +
    /// синтез-прямоугольник). Задержка ~ один кадр (512 сэмплов ≈ 10.7 мс).
    /// Вход/выход — 16-бит моно PCM 48к, обрабатывается in-place кадрами любой
    /// длины (внутри FIFO). Выход задержан ровно на N сэмплов (FIFO приморожен
    /// нулями на старте), поэтому длина выхода == длине входа на каждый вызов.
    /// </summary>
    internal sealed class SpectralDenoiser
    {
        private const int N = 512;
        private const int HOP = 256;
        private const int BINS = N / 2 + 1;

        private readonly float[] _hann = new float[N];
        private readonly float[] _in = new float[N];        // скользящее окно анализа
        private readonly float[] _overlap = new float[N];   // накопитель overlap-add
        private readonly float[] _noisePow = new float[BINS];
        private bool _noiseInit;

        // FIFO вход/выход (короткие очереди, ~кадр).
        private short[] _inFifo = new short[N * 4];
        private int _inCount;
        private short[] _outFifo = new short[N * 4];
        private int _outHead, _outCount;

        // Рабочие буферы FFT.
        private readonly float[] _re = new float[N];
        private readonly float[] _im = new float[N];
        private readonly int[] _rev = new int[N];

        /// <summary>0..1: агрессивность (0 = мягко, 1 = сильно). По умолчанию ~0.85.</summary>
        public float Strength = 0.85f;

        public SpectralDenoiser()
        {
            for (int n = 0; n < N; n++) _hann[n] = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * n / N);
            // Битовая перестановка для итеративного FFT.
            int bits = 9; // 2^9 = 512
            for (int i = 0; i < N; i++)
            {
                int x = i, r = 0;
                for (int b = 0; b < bits; b++) { r = (r << 1) | (x & 1); x >>= 1; }
                _rev[i] = r;
            }
            // Приморозка выхода: N нулей — чтобы FIFO выхода никогда не иссякал.
            for (int i = 0; i < N; i++) PushOut(0);
        }

        /// <summary>Обработать блок 16-бит PCM in-place (offset/len в БАЙТАХ).</summary>
        public void Process(byte[] buffer, int offset, int len)
        {
            int m = len / 2;
            if (m <= 0) return;

            // 1) вход → FIFO
            EnsureInCap(_inCount + m);
            for (int i = 0; i < m; i++)
            {
                int idx = offset + i * 2;
                _inFifo[_inCount++] = (short)(buffer[idx] | (buffer[idx + 1] << 8));
            }

            // 2) обрабатываем целыми HOP-шагами
            int consumed = 0;
            while (_inCount - consumed >= HOP)
            {
                ProcessHop(consumed);
                consumed += HOP;
            }
            if (consumed > 0)
            {
                int rem = _inCount - consumed;
                Array.Copy(_inFifo, consumed, _inFifo, 0, rem);
                _inCount = rem;
            }

            // 3) выход ← FIFO (ровно m сэмплов; FIFO всегда впереди из-за приморозки)
            for (int i = 0; i < m; i++)
            {
                short s = PopOut();
                int idx = offset + i * 2;
                buffer[idx] = (byte)(s & 0xFF);
                buffer[idx + 1] = (byte)((s >> 8) & 0xFF);
            }
        }

        private void ProcessHop(int inOffset)
        {
            // сдвигаем окно анализа влево на HOP, дописываем HOP новых сэмплов
            Array.Copy(_in, HOP, _in, 0, N - HOP);
            for (int i = 0; i < HOP; i++) _in[N - HOP + i] = _inFifo[inOffset + i];

            // окно Ханна → FFT
            for (int i = 0; i < N; i++) { _re[i] = _in[i] * _hann[i]; _im[i] = 0f; }
            Fft(false);

            // винеровское усиление по бинам
            for (int k = 0; k < BINS; k++)
            {
                float pow = _re[k] * _re[k] + _im[k] * _im[k];
                float noise = _noisePow[k];
                if (!_noiseInit) noise = _noisePow[k] = pow;                 // первый кадр = стартовая оценка
                else if (pow < noise) _noisePow[k] = noise = pow;           // быстрый спад к минимуму
                else _noisePow[k] = noise = noise * 0.995f + pow * 0.005f;  // медленный подъём (трекинг фона)

                float snr = noise > 1e-6f ? pow / noise - 1f : 10f;
                if (snr < 0f) snr = 0f;
                float gain = snr / (snr + Strength);        // Wiener (Strength — «сколько шума»)
                if (gain < 0.06f) gain = 0.06f;             // пол — без «музыкального» шума
                _re[k] *= gain; _im[k] *= gain;
            }
            // сопряжённая симметрия для вещественного сигнала (Re чёт, Im нечёт)
            for (int k = 1; k < N / 2; k++) { _re[N - k] = _re[k]; _im[N - k] = -_im[k]; }
            _im[0] = 0f; _im[N / 2] = 0f;
            _noiseInit = true;

            Fft(true);   // обратный

            // overlap-add: анализ-окно Ханна @ 50% → COLA=1 (синтез-окно = 1)
            for (int i = 0; i < N; i++) _overlap[i] += _re[i] / N;
            for (int i = 0; i < HOP; i++)
            {
                float v = _overlap[i];
                if (v > 32767f) v = 32767f; else if (v < -32768f) v = -32768f;
                PushOut((short)v);
            }
            Array.Copy(_overlap, HOP, _overlap, 0, N - HOP);
            Array.Clear(_overlap, N - HOP, HOP);
        }

        // Итеративный радикс-2 FFT (in-place на _re/_im). inverse=false — прямое.
        private void Fft(bool inverse)
        {
            for (int i = 0; i < N; i++)
            {
                int j = _rev[i];
                if (j > i) { (_re[i], _re[j]) = (_re[j], _re[i]); (_im[i], _im[j]) = (_im[j], _im[i]); }
            }
            for (int len = 2; len <= N; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                float wRe = (float)Math.Cos(ang), wIm = (float)Math.Sin(ang);
                for (int i = 0; i < N; i += len)
                {
                    float curRe = 1f, curIm = 0f;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        float tRe = _re[b] * curRe - _im[b] * curIm;
                        float tIm = _re[b] * curIm + _im[b] * curRe;
                        _re[b] = _re[a] - tRe; _im[b] = _im[a] - tIm;
                        _re[a] += tRe; _im[a] += tIm;
                        float nRe = curRe * wRe - curIm * wIm;
                        curIm = curRe * wIm + curIm * wRe; curRe = nRe;
                    }
                }
            }
        }

        // ── FIFO helpers ──
        private void EnsureInCap(int need) { if (_inFifo.Length < need) Array.Resize(ref _inFifo, Math.Max(need, _inFifo.Length * 2)); }
        private void PushOut(short s)
        {
            if (_outCount == _outFifo.Length)
            {
                var bigger = new short[_outFifo.Length * 2];
                for (int i = 0; i < _outCount; i++) bigger[i] = _outFifo[(_outHead + i) % _outFifo.Length];
                _outFifo = bigger; _outHead = 0;
            }
            _outFifo[(_outHead + _outCount) % _outFifo.Length] = s; _outCount++;
        }
        private short PopOut()
        {
            if (_outCount == 0) return 0;
            short s = _outFifo[_outHead];
            _outHead = (_outHead + 1) % _outFifo.Length; _outCount--;
            return s;
        }
    }
}
