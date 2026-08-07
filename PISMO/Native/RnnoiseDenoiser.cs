using System;
using System.IO;
using System.Runtime.InteropServices;
using Wasmtime;

namespace PISMO.Native
{
    /// <summary>
    /// НАСТОЯЩИЙ RNNoise в нативном пути звонка — тот же нейросетевой шумодав, что
    /// работает в «Тесте микрофона» (папка noise/rnnoise*.wasm), но крутится прямо
    /// в процессе PISMO через WASM-рантайм Wasmtime, БЕЗ WebView2/Chromium.
    /// Поэтому в звонке давит клавиатуру/мышь/фон так же, как в тесте, и не боится
    /// 0x8007139F под активным VR (никакого Chromium не поднимается).
    ///
    /// Модель зашита в сам wasm (rnnoise_create(0) = встроенная). Кадр RNNoise —
    /// ровно 480 сэмплов (10 мс @48к моно), совпадает с 10мс-кадром APM. Вход/выход
    /// — float в ДИАПАЗОНЕ i16 (−32768..32767), не нормированный [−1,1].
    ///
    /// Обрабатывает блоки любой длины in-place через FIFO с постоянной задержкой
    /// (выход приморожен 480 нулями). Если wasm/рантайм не поднялся — IsReady=false,
    /// и вызывающий откатывается на программный SpectralDenoiser.
    /// </summary>
    internal sealed class RnnoiseDenoiser : IDisposable
    {
        private const int FRAME = 480;   // rnnoise_get_frame_size() @48к

        private readonly object _lock = new();
        private Engine _engine;
        private Module _module;
        private Store _store;
        private Instance _instance;
        private Memory _memory;
        private Func<int, int> _rnCreate;
        private Func<int, int, int, float> _rnProcess;
        private Func<int, int> _malloc;
        private int _st;        // DenoiseState* (один проход)
        private int _inPtr;     // float[480] в wasm-памяти
        private int _outPtr;    // float[480]
        private bool _ok;

        // FIFO вход/выход (i16 сэмплы). Выход впереди на FRAME (приморозка нулями).
        private short[] _inFifo = new short[FRAME * 4];
        private int _inCount;
        private short[] _outFifo = new short[FRAME * 4];
        private int _outHead, _outCount;

        // VAD-затухание остаточного шума: rnnoise_process_frame возвращает
        // вероятность речи (0..1). В паузах дожимаем фон ещё на ~-16дБ, в речи —
        // gain=1. КЛЮЧЕВОЕ: VAD RNNoise ПРОВАЛИВАЕТСЯ на глухих согласных (с, ф, т,
        // к, п) — если глушить по нему напрямую, согласные срезаются и голос «режет
        // по ушам». Поэтому держим hangover: после речевого кадра оставляем gain=1
        // ещё ~250мс, чтобы короткие провалы VAD внутри слов НЕ приглушались; в
        // затухание уходим только на настоящей паузе (устойчивый низкий VAD).
        // Порог речи по VAD: эталонный голосовой RNNoise (werman) рекомендует 85–95%.
        // Держим 0.6 как компромисс — с grace-периодом ниже это надёжно ловит речь и
        // не рубит тихие/глухие согласные (их VAD-провалы закрывает hangover).
        private const float VAD_SPEECH = 0.6f;    // выше — речь → взводим hangover
        private const int HANG_FRAMES = 30;       // 30*10мс = 300мс grace (не режем концы слов)
        private int _vadHang;
        private float _gain = 1f;

        // Плавный вход: первые кадры RNNoise со свежим состоянием дают всплеск-
        // «скрежет». Прогоняем их через рампу 0→1 (~150мс), чтобы не резало по ушам.
        private int _fadeLeft = 15;   // 15 кадров * 10мс = 150мс

        public bool IsReady => _ok;

        /// <summary>Сила шумодава 0..1 = ГЛУБИНА глушения фона В ПАУЗАХ (как VAD-гейт
        /// эталонного голосового RNNoise werman/noise-suppression-for-voice). Ядро
        /// RNNoise всегда работает на полную и чистит стационарный шум во время речи;
        /// ползунок задаёт, насколько давить остаточный фон/клаву между словами:
        /// 1 = тишина в паузах, 0.5 = ~-6 дБ, и т.д. Голос (пока идёт речь) не
        /// трогается вовсе — поэтому нет искажений/«рации».</summary>
        public volatile float Strength = 1f;

        /// <summary>Пытается поднять RNNoise на указанном .wasm. Бросков не делает —
        /// при любой ошибке IsReady останется false.</summary>
        public RnnoiseDenoiser(string wasmPath)
        {
            try { Init(wasmPath); _ok = true; }
            catch { _ok = false; Cleanup(); }
        }

        /// <summary>Ищет rnnoise wasm рядом с exe (папка noise) и поднимает движок.
        /// Возвращает null, если файла нет или движок не завёлся.</summary>
        public static RnnoiseDenoiser TryCreate()
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "noise");
                // Без-SIMD сборка — самая совместимая для интерпретатора/JIT Wasmtime.
                string wasm = Path.Combine(dir, "rnnoise.wasm");
                if (!File.Exists(wasm)) return null;
                var d = new RnnoiseDenoiser(wasm);
                return d.IsReady ? d : null;
            }
            catch { return null; }
        }

        private void Init(string wasmPath)
        {
            _engine = new Engine();
            _module = Module.FromFile(_engine, wasmPath);
            _store = new Store(_engine);
            var linker = new Linker(_engine);

            // env.emscripten_memcpy_big(dest, src, num) -> dest : memcpy в линейной памяти.
            linker.Define("env", "emscripten_memcpy_big",
                Function.FromCallback(_store, (Caller c, int dest, int src, int num) =>
                {
                    try
                    {
                        var mem = c.GetMemory("memory");
                        var span = mem.GetSpan<byte>(0);
                        if (num > 0 && src >= 0 && dest >= 0 &&
                            src + num <= span.Length && dest + num <= span.Length)
                            span.Slice(src, num).CopyTo(span.Slice(dest, num));
                    }
                    catch { }
                    return dest;
                }));

            // env.emscripten_resize_heap(requestedBytes) -> success(0/1).
            linker.Define("env", "emscripten_resize_heap",
                Function.FromCallback(_store, (Caller c, int requested) =>
                {
                    try
                    {
                        var mem = c.GetMemory("memory");
                        long curBytes = mem.GetLength();
                        long need = (uint)requested;
                        long delta = need - curBytes;
                        if (delta <= 0) return 1;
                        long pages = (delta + 65535) / 65536;
                        mem.Grow(pages);
                        return 1;
                    }
                    catch { return 0; }
                }));

            // env.__assert_fail(cond, file, line, func) -> noreturn.
            linker.Define("env", "__assert_fail",
                Function.FromCallback(_store, (Caller c, int a, int b, int d, int e) =>
                {
                    throw new InvalidOperationException("rnnoise assert");
                }));

            _instance = linker.Instantiate(_store, _module);
            _memory = _instance.GetMemory("memory");
            if (_memory == null) throw new InvalidOperationException("no memory export");

            // Инициализация emscripten-рантайма (конструкторы, стек) — до вызовов.
            try { _instance.GetAction("emscripten_stack_init")?.Invoke(); } catch { }
            try { _instance.GetAction("__wasm_call_ctors")?.Invoke(); } catch { }

            _rnCreate = _instance.GetFunction<int, int>("rnnoise_create");
            _rnProcess = _instance.GetFunction<int, int, int, float>("rnnoise_process_frame");
            _malloc = _instance.GetFunction<int, int>("malloc");
            if (_rnCreate == null || _rnProcess == null || _malloc == null)
                throw new InvalidOperationException("missing rnnoise exports");

            _st = _rnCreate(0);    // 0 = встроенная модель
            if (_st == 0) throw new InvalidOperationException("rnnoise_create failed");
            _inPtr = _malloc(FRAME * 4);
            _outPtr = _malloc(FRAME * 4);
            if (_inPtr == 0 || _outPtr == 0) throw new InvalidOperationException("malloc failed");

            // Приморозка выхода: FRAME нулей → выходной FIFO всегда впереди входного.
            for (int i = 0; i < FRAME; i++) PushOut(0);
        }

        /// <summary>Обработать блок 16-бит PCM in-place (offset/len в БАЙТАХ).</summary>
        public void Process(byte[] buffer, int offset, int len)
        {
            if (!_ok) return;
            int m = len / 2;
            if (m <= 0) return;
            lock (_lock)
            {
                if (!_ok) return;
                // вход → FIFO
                EnsureInCap(_inCount + m);
                for (int i = 0; i < m; i++)
                {
                    int idx = offset + i * 2;
                    _inFifo[_inCount++] = (short)(buffer[idx] | (buffer[idx + 1] << 8));
                }

                // целыми кадрами по 480
                int consumed = 0;
                while (_inCount - consumed >= FRAME)
                {
                    ProcessFrame(consumed);
                    consumed += FRAME;
                }
                if (consumed > 0)
                {
                    int rem = _inCount - consumed;
                    Array.Copy(_inFifo, consumed, _inFifo, 0, rem);
                    _inCount = rem;
                }

                // выход ← FIFO (ровно m; всегда есть из-за приморозки)
                for (int i = 0; i < m; i++)
                {
                    short s = PopOut();
                    int idx = offset + i * 2;
                    buffer[idx] = (byte)(s & 0xFF);
                    buffer[idx + 1] = (byte)((s >> 8) & 0xFF);
                }
            }
        }

        private void ProcessFrame(int inOffset)
        {
            try
            {
                var span = _memory.GetSpan<byte>(0);
                var floats = MemoryMarshal.Cast<byte, float>(span.Slice(_inPtr, FRAME * 4));
                for (int i = 0; i < FRAME; i++) floats[i] = _inFifo[inOffset + i];   // i16-диапазон как float

                // ОДИН проход RNNoise: два прохода переобрабатывают голос — он
                // становится «звонким/цифровым» (как дешёвый микрофон). Один проход
                // давит шум достаточно и сохраняет натуральность голоса. Возврат —
                // вероятность речи (VAD), ей дожимаем остаточный фон в паузах.
                float vad = _rnProcess(_st, _outPtr, _inPtr);   // in -> out

                // Речевой кадр взводит hangover (grace period); пока он не истёк —
                // держим полный сигнал (короткие провалы VAD на согласных не режутся).
                if (vad >= VAD_SPEECH) _vadHang = HANG_FRAMES;
                else if (_vadHang > 0) _vadHang--;
                // Глубина глушения паузы задаётся ползунком силы: 1 → тишина, меньше →
                // часть фона остаётся. Во время речи (hangover) всегда полный сигнал.
                float str = Strength; if (str < 0f) str = 0f; else if (str > 1f) str = 1f;
                float floor = 0.30f * (1f - str);            // сила 1 = 0 (тишина), 0.5 ≈ 0.15
                float target = _vadHang > 0 ? 1f : floor;
                // Быстрое открытие; закрытие ускорено (0.04→0.18): раньше в паузу
                // уходило ~0.5с и фон/клава «проскакивали». Grace-период (300мс)
                // защищает концы слов, поэтому быстрое закрытие их не режет.
                _gain += (target > _gain ? 0.6f : 0.25f) * (target - _gain);

                // Плавный вход первых кадров (рампа 0→1) — глушит стартовый «скрежет».
                float fade = 1f;
                if (_fadeLeft > 0) { fade = 1f - (_fadeLeft / 15f); _fadeLeft--; }
                float g = _gain * fade;

                // память могла переехать при вызове — берём span заново
                var span2 = _memory.GetSpan<byte>(0);
                var outF = MemoryMarshal.Cast<byte, float>(span2.Slice(_outPtr, FRAME * 4));
                for (int i = 0; i < FRAME; i++)
                {
                    float v = outF[i] * g;   // чистый выход RNNoise с VAD-гейтом паузы
                    if (v > 32767f) v = 32767f; else if (v < -32768f) v = -32768f;
                    PushOut((short)v);
                }
            }
            catch
            {
                // сбой rnnoise — отдаём вход как есть (без обработки), не роняем звонок
                for (int i = 0; i < FRAME; i++) PushOut(_inFifo[inOffset + i]);
            }
        }

        private void EnsureInCap(int need)
        {
            if (_inFifo.Length < need) Array.Resize(ref _inFifo, Math.Max(need, _inFifo.Length * 2));
        }

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

        private void Cleanup()
        {
            try { _store?.Dispose(); } catch { }
            try { _module?.Dispose(); } catch { }
            try { _engine?.Dispose(); } catch { }
            _store = null; _module = null; _engine = null;
            _instance = null; _memory = null;
            _rnCreate = null; _rnProcess = null; _malloc = null;
        }

        public void Dispose()
        {
            lock (_lock) { _ok = false; Cleanup(); }
        }
    }
}
