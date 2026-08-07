using System;

namespace PISMO.Native
{
    /// <summary>
    /// Программный «шумодав» для микрофона в НАТИВНОМ пути звонка (livekit_ffi).
    /// libwebrtc APM (NoiseSuppression на push-источнике NewAudioSource) фоновый
    /// шум практически не убирает, поэтому чистим PCM сами до отправки в FFI.
    ///
    /// Каскады:
    ///   1) Высокочастотный фильтр (2×~120 Гц) — срезает гул/рокот/DC/задувание.
    ///   2) Де-кликер + адаптивный шумовой гейт — оценивает уровень фонового шума
    ///      в паузах и плавно приглушает сигнал у шумового пола (мягкая экспандер-
    ///      кривая, без щелчков).
    ///   3) Look-ahead лимитер — упреждающе давит транзиенты (клики/удары).
    ///
    /// Работает на 16-бит моно PCM in-place. Дёшево, без сторонних зависимостей.
    /// </summary>
    internal sealed class MicDenoiser
    {
        private readonly int _sampleRate;

        // Высокочастотный фильтр — ДВА однополюсных каскада ~120 Гц (крутизна
        // 12 дБ/окт). Задувание/поп/ветер — это в основном энергия ниже ~150 Гц;
        // одиночный полюс её почти не срезал, поэтому «дуть в микрофон» пролезало.
        // Двойной каскад давит низ куда сильнее, а разборчивость голоса (основной
        // тон ~150–250 Гц + форманты выше) почти не страдает.
        private float _hpPrevIn, _hpPrevOut;    // 1-й каскад
        private float _hpPrevIn2, _hpPrevOut2;  // 2-й каскад
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

        // Де-кликер (работает ВСЕГДА, в т.ч. поверх открытого под речь гейта):
        // клик клавиатуры — резкий короткий ВЧ-всплеск, который «выпрыгивает» над
        // сглаженным уровнем голоса. Гейт его пропускает (во время речи он открыт),
        // поэтому давим точечно: сравниваем быструю огибающую с медленной и, если
        // всплеск кратно выше устойчивого уровня, приглушаем именно эти отсчёты.
        private float _dcFast, _dcSlow;

        // Look-ahead лимитер: звук задерживается на LA отсчётов, а решение о
        // приглушении принимается по «будущему» сэмплу — так всплеск громкости
        // (клавиатура/удар/начало выдува) глушится ДО того, как выйдет в эфир, а
        // не постфактум. Порог всплеска — относительно медленного «уровня голоса».
        private readonly int _la;
        private readonly float[] _laBuf;
        private int _laPos;
        private float _laGain = 1f;
        private float _voiceRef;
        private float _laMax;      // текущий максимум |сэмпла| в линии задержки (look-ahead окно)
        private int _laMaxAge;     // сколько сэмплов назад был установлен _laMax

        // Пересчёт максимума по всей линии задержки (когда прежний максимум «вышел»).
        private float RescanMax()
        {
            float m = 0f;
            for (int i = 0; i < _laBuf.Length; i++) { float v = _laBuf[i]; if (v < 0) v = -v; if (v > m) m = v; }
            return m;
        }

        public MicDenoiser(int sampleRate)
        {
            _sampleRate = sampleRate;
            // RC-коэффициент HPF: y = a*(y_prev + x - x_prev).
            float rc = 1f / (2f * (float)Math.PI * 120f);
            float dt = 1f / sampleRate;
            _hpCoef = rc / (rc + dt);
            _la = Math.Max(64, sampleRate / 200);   // ~5 мс упреждения
            _laBuf = new float[_la];
        }

        /// <summary>Обработать блок 16-бит PCM in-place.</summary>
        public void Process(byte[] buffer, int bytes) => Process(buffer, 0, bytes);

        /// <summary>Обработать блок 16-бит PCM in-place (с offset — для 10мс-кадров APM).</summary>
        public void Process(byte[] buffer, int offset, int bytes)
        {
            int n = bytes / 2;
            if (n <= 0) return;

            // 1) HPF + подсчёт RMS блока (и «сырого» RMS до фильтра — для детектора выдува).
            double sumSq = 0, sumSqRaw = 0;
            for (int i = 0; i < n; i++)
            {
                int idx = offset + i * 2;
                short s = (short)(buffer[idx] | (buffer[idx + 1] << 8));
                float x = s;
                sumSqRaw += (double)x * x;
                float y = _hpCoef * (_hpPrevOut + x - _hpPrevIn);
                _hpPrevIn = x;
                _hpPrevOut = y;
                // 2-й каскад HPF (крутизна ×2) — добивает низкочастотное задувание.
                float y2 = _hpCoef * (_hpPrevOut2 + y - _hpPrevIn2);
                _hpPrevIn2 = y;
                _hpPrevOut2 = y2;
                y = y2;

                // Де-кликер: гоняем две огибающие ВЧ-сигнала — быструю (реагирует
                // на всплеск за доли мс) и медленную (устойчивый уровень голоса).
                // Клик = fast кратно выше slow. Тогда прижимаем всплеск к slow —
                // голос почти не меняется (его огибающие идут вровень), а щелчок
                // срезается даже посреди фразы.
                if (TransientGuard)
                {
                    float a = y < 0 ? -y : y;
                    _dcFast += (a - _dcFast) * (a > _dcFast ? 0.6f : 0.05f);
                    _dcSlow += (a - _dcSlow) * (a > _dcSlow ? 0.002f : 0.0008f);
                    if (_dcFast > 350f && _dcFast > _dcSlow * 4f)
                    {
                        float duck = (_dcSlow * 1.5f) / (a + 1f);   // тянем пик к устойчивому уровню
                        if (duck < 0.12f) duck = 0.12f;             // глубже, чем раньше (0.25) — клик слышно меньше
                        if (duck < 1f) y *= duck;
                    }
                }

                // временно сохраняем отфильтрованное значение обратно.
                int v = (int)y;
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                buffer[idx] = (byte)(v & 0xFF);
                buffer[idx + 1] = (byte)((v >> 8) & 0xFF);
                sumSq += (double)y * y;
            }
            float rms = (float)Math.Sqrt(sumSq / n);
            float rmsRaw = (float)Math.Sqrt(sumSqRaw / n);
            // Детектор задувания/попа: у выдува почти вся энергия ниже ~120 Гц, поэтому
            // после HPF она обваливается (rms ≪ rmsRaw). У голоса энергия в основном
            // выше HPF — отношение близко к 1. Громкий блок с малым отношением = выдув.
            bool blowing = rmsRaw > 1500f && rms < rmsRaw * 0.5f;

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
            else if (_envelope <= closeLevel) target = 0.008f;  // глубже глушим фон между словами
            else target = 0.008f + 0.992f * (_envelope - closeLevel) / (openLevel - closeLevel);

            if (TransientGuard)
            {
                // Блоки по 10-20мс: считаем подряд идущие громкие блоки.
                int msPerBlock = Math.Max(1, n * 1000 / _sampleRate);
                int needBlocks = Math.Max(1, 30 / msPerBlock);          // ~30мс до открытия
                int hangTotal = Math.Max(1, 180 / msPerBlock);          // ~180мс удержания (быстрее закрывается)

                if (_envelope >= openLevel) _loudRun++; else _loudRun = 0;
                if (_loudRun >= needBlocks) _hangBlocks = hangTotal;    // речь подтверждена
                bool speech = _hangBlocks > 0;
                if (_hangBlocks > 0) _hangBlocks--;
                // Одиночный громкий всплеск (клик) без подтверждения речи — глушим сильнее.
                if (!speech) target = 0.02f;
                // Выдув маскируется под «речь» (сустейн-громкость), поэтому давим его
                // ЖЁСТКО отдельно, и сбрасываем счётчик речи, чтобы он не «открыл» гейт.
                if (blowing) { target = 0.02f; _loudRun = 0; _hangBlocks = 0; }
            }

            // 5) Сглаживание усиления: быстрое открытие + БЫСТРОЕ закрытие (0.12),
            // чтобы фон/шум давился сразу, а не «постепенно и долго».
            float coef = target > _gain ? 0.4f : 0.12f;
            for (int i = 0; i < n; i++)
            {
                _gain += (target - _gain) * coef;
                int idx = offset + i * 2;
                short s = (short)(buffer[idx] | (buffer[idx + 1] << 8));

                // Look-ahead лимитер (только при TransientGuard/высокой силе):
                // сэмпл кладём в линию задержки, на выход берём задержанный, а
                // усиление считаем по максимуму «будущих» сэмплов в линии — так
                // всплеск приглушается ДО выхода и держится, пока не покинет буфер.
                float outSample;
                if (TransientGuard)
                {
                    float a = s < 0 ? -s : s;
                    _voiceRef += (a - _voiceRef) * (a > _voiceRef ? 0.0015f : 0.0006f);
                    float thr = _voiceRef * 2.5f + 450f;           // порог всплеска

                    // Кладём текущий сэмпл в линию задержки, забираем задержанный.
                    float delayed = _laBuf[_laPos];
                    _laBuf[_laPos] = s;
                    if (++_laPos >= _la) _laPos = 0;

                    // НАСТОЯЩИЙ look-ahead: усиление считаем по МАКСИМУМУ во ВСЕЙ линии
                    // задержки (в ней лежат _la «будущих» относительно выхода сэмплов).
                    // Значит пик клика виден и приглушён ЗАРАНЕЕ, а приглушение само
                    // держится, пока пик не выйдет из буфера. Старый код смотрел лишь
                    // на текущий сэмпл и успевал восстановиться до выхода пика —
                    // отсюда «первый клик слышно». bufMax обновляем инкрементально.
                    if (a >= _laMax) { _laMax = a; _laMaxAge = 0; }
                    else if (++_laMaxAge >= _la) { _laMax = RescanMax(); _laMaxAge = 0; }  // старый максимум вышел — пересканируем
                    float laTarget = _laMax > thr ? thr / _laMax : 1f;
                    _laGain += (laTarget - _laGain) * (laTarget < _laGain ? 0.6f : 0.01f);  // быстро вниз, медленно вверх

                    outSample = delayed * _gain * _laGain;
                }
                else
                {
                    outSample = s * _gain;
                }

                int v = (int)outSample;
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                buffer[idx] = (byte)(v & 0xFF);
                buffer[idx + 1] = (byte)((v >> 8) & 0xFF);
            }
        }
    }
}
