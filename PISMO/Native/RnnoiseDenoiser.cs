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
        private int _st;        // DenoiseState* (проход 1)
        private int _st2;       // DenoiseState* (проход 2 — отдельное состояние, как node2 в тесте)
        private int _inPtr;     // float[480] в wasm-памяти
        private int _outPtr;    // float[480]
        private bool _ok;

        // FIFO вход/выход (i16 сэмплы). Выход впереди на FRAME (приморозка нулями).
        private short[] _inFifo = new short[FRAME * 4];
        private int _inCount;
        private short[] _outFifo = new short[FRAME * 4];
        private int _outHead, _outCount;

        public bool IsReady => _ok;

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
            _st2 = _rnCreate(0);   // второе состояние для второго прохода
            if (_st == 0 || _st2 == 0) throw new InvalidOperationException("rnnoise_create failed");
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

                // Два прохода RNNoise (как в тесте: node -> node2), КАЖДЫЙ со своим
                // состоянием — сильнее давит клавиатуру/мышь без порчи pitch-модели.
                _rnProcess(_st, _outPtr, _inPtr);      // проход 1: in -> out
                _rnProcess(_st2, _outPtr, _outPtr);    // проход 2: out -> out

                // память могла переехать при вызове — берём span заново
                var span2 = _memory.GetSpan<byte>(0);
                var outF = MemoryMarshal.Cast<byte, float>(span2.Slice(_outPtr, FRAME * 4));
                for (int i = 0; i < FRAME; i++)
                {
                    float v = outF[i];
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
