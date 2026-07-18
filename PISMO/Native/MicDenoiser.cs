using System;

namespace PISMO.Native
{
    /// <summary>
    /// Программный «шумодав» для микрофона в НАТИВНОМ пути звонка (livekit_ffi).
    /// libwebrtc APM (NoiseSuppression на push-источнике NewAudioSource) фоновый
    /// шум практически не убирает, поэтому чистим PCM сами до отправки в FFI.
    ///
    /// Два каскада:
    ///   1) Высокочастотный фильтр (~90 Гц) — срезает гул/рокот/DC-сдвиг.
    ///   2) Адаптивный шумовой гейт — оценивает уровень фонового шума в паузах и
    ///      плавно приглушает сигнал, когда энергия близка к шумовому полу
    ///      (мягкая экспандер-кривая, а не жёсткий обрыв — без щелчков).
    ///
    /// Работает на 16-бит моно PCM in-place. Дёшево, без сторонних зависимостей.
    /// </summary>
    internal sealed class MicDenoiser
    {
        private readonly int _sampleRate;

        // Высокочастотный фильтр (однополюсный, ~90 Гц).
        private float _hpPrevIn, _hpPrevOut;
        private readonly float _hpCoef;

        // Оценка шумового пола и огибающей сигнала.
        private float _noiseFloor = 200f;   // стартовая оценка (в отсчётах i16)
        private float _envelope;            // сглаженная энергия текущего блока
        private float _gain = 1f;           // текущее сглаженное усиление гейта

        // Транзиент-фильтр (часть единого режима «включён»): клики клавиатуры ГРОМКИЕ, но
        // короткие (<20мс) — обычный гейт от них наоборот открывается. Здесь
        // гейт открывает только УСТОЙЧИВАЯ громкость (~30мс подряд = начало
        // речи), а после речи держится hangover, чтобы не резать середину фраз.
        public bool TransientGuard;
        private int _loudRun;      // сколько блоков подряд «громко»
        private int _hangBlocks;   // осталось блоков удержания «идёт речь»

        public MicDenoiser(int sampleRate)
        {
            _sampleRate = sampleRate;
            // RC-коэффициент HPF: y = a*(y_prev + x - x_prev).
            float rc = 1f / (2f * (float)Math.PI * 90f);
            float dt = 1f / sampleRate;
            _hpCoef = rc / (rc + dt);
        }

        /// <summary>Обработать блок 16-бит PCM in-place.</summary>
        public void Process(byte[] buffer, int bytes) => Process(buffer, 0, bytes);

        /// <summary>Обработать блок 16-бит PCM in-place (с offset — для 10мс-кадров APM).</summary>
        public void Process(byte[] buffer, int offset, int bytes)
        {
            int n = bytes / 2;
            if (n <= 0) return;

            // 1) HPF + подсчёт RMS блока.
            double sumSq = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = offset + i * 2;
                short s = (short)(buffer[idx] | (buffer[idx + 1] << 8));
                float x = s;
                float y = _hpCoef * (_hpPrevOut + x - _hpPrevIn);
                _hpPrevIn = x;
                _hpPrevOut = y;
                // временно сохраняем отфильтрованное значение обратно.
                int v = (int)y;
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                buffer[idx] = (byte)(v & 0xFF);
                buffer[idx + 1] = (byte)((v >> 8) & 0xFF);
                sumSq += (double)y * y;
            }
            float rms = (float)Math.Sqrt(sumSq / n);

            // 2) Огибающая (быстрый подъём, медленный спад).
            _envelope = rms > _envelope ? rms * 0.5f + _envelope * 0.5f
                                        : rms * 0.05f + _envelope * 0.95f;

            // 3) Адаптация шумового пола: подтягиваем только когда тихо
            //    (иначе речь «обучила» бы гейт и он бы её резал).
            if (_envelope < _noiseFloor * 2.5f)
                _noiseFloor = _envelope * 0.05f + _noiseFloor * 0.95f;   // быстрее учим фон
            if (_noiseFloor < 30f) _noiseFloor = 30f;    // не даём уползти в ноль

            // 4) Целевое усиление: полный сигнал заметно выше шума, тишина — глушим.
            float openLevel = _noiseFloor * 4.0f;    // порог «речь»
            float closeLevel = _noiseFloor * 2.2f;   // порог «шум»
            float target;
            if (_envelope >= openLevel) target = 1f;
            else if (_envelope <= closeLevel) target = 0.02f;   // тише глушим фон между словами
            else target = 0.02f + 0.98f * (_envelope - closeLevel) / (openLevel - closeLevel);

            if (TransientGuard)
            {
                // Блоки по 10-20мс: считаем подряд идущие громкие блоки.
                int msPerBlock = Math.Max(1, n * 1000 / _sampleRate);
                int needBlocks = Math.Max(1, 30 / msPerBlock);          // ~30мс до открытия
                int hangTotal = Math.Max(1, 350 / msPerBlock);          // ~350мс удержания

                if (_envelope >= openLevel) _loudRun++; else _loudRun = 0;
                if (_loudRun >= needBlocks) _hangBlocks = hangTotal;    // речь подтверждена
                bool speech = _hangBlocks > 0;
                if (_hangBlocks > 0) _hangBlocks--;
                // Одиночный громкий всплеск (клик) без подтверждения речи — глушим.
                if (!speech) target = 0.10f;
            }

            // 5) Сглаживание усиления (без щелчков): быстрое открытие, мягкое закрытие.
            float coef = target > _gain ? 0.4f : 0.03f;
            for (int i = 0; i < n; i++)
            {
                _gain += (target - _gain) * coef;
                int idx = offset + i * 2;
                short s = (short)(buffer[idx] | (buffer[idx + 1] << 8));
                int v = (int)(s * _gain);
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                buffer[idx] = (byte)(v & 0xFF);
                buffer[idx + 1] = (byte)((v >> 8) & 0xFF);
            }
        }
    }
}
