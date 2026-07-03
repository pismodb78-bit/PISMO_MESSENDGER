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
        // Не «писки»-тоны, а мягкие «бупы» со скольжением частоты (глайд):
        // вкл — частота едет вверх («буп↑»), выкл — вниз («буп↓»). Похоже на
        // капли/поп-звуки Discord, слух не режет.
        public static void MicOn()    => PlayBytes(GlideWav(300, 560, 130, 0.2));
        public static void MicOff()   => PlayBytes(GlideWav(560, 280, 150, 0.2));
        public static void CameraOn() => PlayBytes(GlideWav(340, 620, 130, 0.2));
        public static void CameraOff()=> PlayBytes(GlideWav(620, 320, 150, 0.2));
        public static void ScreenOn() => PlayBytes(GlideWav(280, 500, 130, 0.2));
        public static void ScreenOff()=> PlayBytes(GlideWav(500, 260, 150, 0.2));
        public static void Message()  => PlayBytes(GlideWav(500, 880, 90, 0.15));   // «плип» капли
        public static void Hangup()   => PlayBytes(DoubleGlide(450, 260, 300, 170, 140, 0.2)); // «бу-бум» вниз
        public static void CallConnected() => PlayBytes(DoubleGlide(300, 520, 420, 660, 150, 0.2)); // вверх дважды
        public static void UserJoined() => PlayBytes(GlideWav(330, 640, 160, 0.18)); // «буп↑» — зашёл
        public static void UserLeft()   => PlayBytes(GlideWav(640, 300, 170, 0.18)); // «буп↓» — вышел

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

        /// <summary>WAV мягкого «бупа»: частота скользит f1→f2, атака + затухание.</summary>
        private static byte[] GlideWav(double f1, double f2, int ms, double vol = 0.2)
            => BuildWav(samples => FillGlide(samples, f1, f2, 0, ms, vol), ms + 40);

        /// <summary>Два «бупа» подряд (глайды g1: a1→a2, g2: b1→b2) с паузой-переливом.</summary>
        private static byte[] DoubleGlide(double a1, double a2, double b1, double b2, int eachMs, double vol = 0.2)
        {
            int gap = eachMs / 2;
            int total = eachMs * 2 + gap + 60;
            return BuildWav(samples =>
            {
                FillGlide(samples, a1, a2, 0, eachMs, vol);
                FillGlide(samples, b1, b2, eachMs + gap, eachMs, vol);
            }, total);
        }

        /// <summary>«Буп» со скольжением частоты (фазовое накопление — без щелчков):
        /// атака ~6мс, экспоненциальное затухание, тёплая нижняя октава.</summary>
        private static void FillGlide(short[] samples, double f1, double f2, int startMs, int durMs, double vol)
        {
            int start = startMs * SampleRate / 1000;
            int len = durMs * SampleRate / 1000;
            int attack = SampleRate * 6 / 1000;
            double decay = 3.0 / len;
            double phase = 0, phaseLow = 0;
            for (int i = 0; i < len; i++)
            {
                int idx = start + i;
                if (idx < 0 || idx >= samples.Length) continue;
                double k = (double)i / len;                          // 0..1
                double f = f1 * Math.Pow(f2 / f1, k);                // экспоненциальный глайд
                phase += 2 * Math.PI * f / SampleRate;
                phaseLow += Math.PI * f / SampleRate;                // нижняя октава (f/2)
                double env = (i < attack ? (double)i / attack : 1.0) * Math.Exp(-decay * Math.Max(0, i - attack));
                double s = (Math.Sin(phase) * 0.8 + Math.Sin(phaseLow) * 0.35) * vol * env;
                int v = (int)(s * short.MaxValue) + samples[idx];
                samples[idx] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
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
