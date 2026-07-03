using System;
using System.IO;
using System.Media;

namespace PISMO
{
    /// <summary>
    /// Короткие звуки событий и рингтон входящего звонка. Звуки генерируются
    /// в памяти (синус-тоны в WAV), внешние файлы не нужны.
    /// </summary>
    public static class Sounds
    {
        public static bool Enabled { get; set; } = true;

        private static SoundPlayer _ring;

        // ── Публичные события ───────────────────────────────────────────
        // Мягкие «колокольчики»: тихие, низкие, с экспоненциальным затуханием
        // (раньше были громкие плоские синусы — резали слух).
        public static void MicOn()    => PlayBytes(ToneWav(587, 140, 0.18));
        public static void MicOff()   => PlayBytes(ToneWav(392, 160, 0.18));
        public static void CameraOn() => PlayBytes(ToneWav(523, 140, 0.18));
        public static void CameraOff()=> PlayBytes(ToneWav(349, 160, 0.18));
        public static void ScreenOn() => PlayBytes(ToneWav(494, 140, 0.18));
        public static void ScreenOff()=> PlayBytes(ToneWav(330, 160, 0.18));
        public static void Message()  => PlayBytes(ToneWav(740, 150, 0.16));
        public static void Hangup()   => PlayBytes(TwoTone(440, 294, 170, 0.18));
        public static void CallConnected() => PlayBytes(TwoTone(392, 587, 160, 0.2));
        public static void UserJoined() => PlayBytes(TwoTone(440, 659, 140, 0.18)); // восходящий — зашёл
        public static void UserLeft()   => PlayBytes(TwoTone(587, 392, 150, 0.18)); // нисходящий — вышел

        // ── Рингтон входящего звонка (зацикленный) ──────────────────────
        public static void StartRingtone()
        {
            if (!Enabled) return;
            StopRingtone();
            try
            {
                _ring = new SoundPlayer(new MemoryStream(RingtoneWav()));
                _ring.PlayLooping();
            }
            catch { _ring = null; }
        }

        public static void StopRingtone()
        {
            try { _ring?.Stop(); _ring?.Dispose(); } catch { }
            _ring = null;
        }

        // ── Внутреннее ──────────────────────────────────────────────────
        private static void PlayBytes(byte[] wav)
        {
            if (!Enabled || wav == null) return;
            try
            {
                // Fire-and-forget: SoundPlayer.Play не блокирует UI.
                var sp = new SoundPlayer(new MemoryStream(wav));
                sp.Play();
            }
            catch { }
        }

        private const int SampleRate = 44100;

        /// <summary>WAV одиночного мягкого тона (атака + экспоненциальное затухание).</summary>
        private static byte[] ToneWav(double freq, int ms, double vol = 0.18)
            => BuildWav(samples => FillTone(samples, freq, 0, ms, vol), ms + 40);

        /// <summary>WAV из двух мягких тонов ВНАХЛЁСТ (перелив, а не стык).</summary>
        private static byte[] TwoTone(double f1, double f2, int eachMs, double vol = 0.18)
        {
            int overlap = eachMs / 3;                 // второй тон начинается до конца первого
            int total = eachMs * 2 - overlap + 60;
            return BuildWav(samples =>
            {
                FillTone(samples, f1, 0, eachMs, vol);
                FillTone(samples, f2, eachMs - overlap, eachMs, vol);
            }, total);
        }

        /// <summary>Рингтон: мягкое «бим-бом» ×2 + пауза (PlayLooping — как телефон).</summary>
        private static byte[] RingtoneWav()
        {
            int total = 2600; // мс на цикл
            return BuildWav(samples =>
            {
                // Два мягких двухтоновых звонка, затем тишина.
                FillTone(samples, 523, 0,    420, 0.26);
                FillTone(samples, 659, 300,  420, 0.26);
                FillTone(samples, 523, 850,  420, 0.26);
                FillTone(samples, 659, 1150, 420, 0.26);
                // ~1600мс..2600мс — тишина (не заполняем).
            }, total);
        }

        /// <summary>Мягкий «колокольчик»: атака ~8мс, экспоненциальное затухание,
        /// тёплый тембр (основной тон + тихая нижняя октава). Без резких краёв.</summary>
        private static void FillTone(short[] samples, double freq, int startMs, int durMs, double vol)
        {
            int start = startMs * SampleRate / 1000;
            int len = durMs * SampleRate / 1000;
            int attack = SampleRate * 8 / 1000;       // ~8мс плавного входа
            double decay = 3.0 / len;                 // экспоненциальный спад до конца
            for (int i = 0; i < len; i++)
            {
                int idx = start + i;
                if (idx < 0 || idx >= samples.Length) continue;
                double env = (i < attack ? (double)i / attack : 1.0) * Math.Exp(-decay * Math.Max(0, i - attack));
                double t = 2 * Math.PI * i / SampleRate;
                // Основной тон + нижняя октава (тепло) — без высоких гармоник.
                double s = (Math.Sin(t * freq) * 0.8 + Math.Sin(t * freq / 2) * 0.35) * vol * env;
                int v = (int)(s * short.MaxValue) + samples[idx];
                samples[idx] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
        }

        private static byte[] BuildWav(Action<short[]> fill, int totalMs = 0)
        {
            // По умолчанию длина = по самому позднему тону: используем 600мс если не задано.
            int ms = totalMs > 0 ? totalMs : 600;
            int n = ms * SampleRate / 1000;
            var samples = new short[n];
            fill(samples);

            using var ms2 = new MemoryStream();
            using var bw = new BinaryWriter(ms2);
            int byteRate = SampleRate * 2;
            int dataLen = n * 2;
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataLen);
            bw.Write(new[] { 'W', 'A', 'V', 'E' });
            bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16);            // fmt chunk size
            bw.Write((short)1);      // PCM
            bw.Write((short)1);      // mono
            bw.Write(SampleRate);
            bw.Write(byteRate);
            bw.Write((short)2);      // block align
            bw.Write((short)16);     // bits per sample
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(dataLen);
            foreach (var s in samples) bw.Write(s);
            bw.Flush();
            return ms2.ToArray();
        }
    }
}
