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
        public static void MicOn()    => PlayBytes(ToneWav(700, 80));
        public static void MicOff()   => PlayBytes(ToneWav(340, 100));
        public static void CameraOn() => PlayBytes(ToneWav(660, 90));
        public static void CameraOff()=> PlayBytes(ToneWav(330, 110));
        public static void ScreenOn() => PlayBytes(ToneWav(620, 90));
        public static void ScreenOff()=> PlayBytes(ToneWav(320, 110));
        public static void Message()  => PlayBytes(ToneWav(880, 70));
        public static void Hangup()   => PlayBytes(TwoTone(500, 300, 120));
        public static void CallConnected() => PlayBytes(TwoTone(523, 784, 110));
        public static void UserJoined() => PlayBytes(TwoTone(523, 698, 90));  // восходящий — зашёл
        public static void UserLeft()   => PlayBytes(TwoTone(587, 392, 100)); // нисходящий — вышел

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

        /// <summary>WAV одиночного тона с плавным нарастанием/затуханием.</summary>
        private static byte[] ToneWav(double freq, int ms, double vol = 0.4)
            => BuildWav(samples => FillTone(samples, freq, 0, ms, vol));

        /// <summary>WAV из двух тонов подряд (нисходящий/восходящий сигнал).</summary>
        private static byte[] TwoTone(double f1, double f2, int eachMs)
        {
            int total = eachMs * 2;
            return BuildWav(samples =>
            {
                FillTone(samples, f1, 0, eachMs, 0.4);
                FillTone(samples, f2, eachMs, eachMs, 0.4);
            }, total);
        }

        /// <summary>Рингтон: «бринь-бринь» + пауза, чтобы PlayLooping звучал как телефон.</summary>
        private static byte[] RingtoneWav()
        {
            int total = 2600; // мс на цикл
            return BuildWav(samples =>
            {
                // Два коротких двухтоновых звонка, затем тишина.
                FillTone(samples, 520, 0,   350, 0.45);
                FillTone(samples, 660, 350, 350, 0.45);
                FillTone(samples, 520, 850, 350, 0.45);
                FillTone(samples, 660, 1200,350, 0.45);
                // 850мс..2600мс — тишина (не заполняем).
            }, total);
        }

        private static void FillTone(short[] samples, double freq, int startMs, int durMs, double vol)
        {
            int start = startMs * SampleRate / 1000;
            int len = durMs * SampleRate / 1000;
            int fade = Math.Min(len / 4, SampleRate / 200); // мягкие края, без щелчков
            for (int i = 0; i < len; i++)
            {
                int idx = start + i;
                if (idx < 0 || idx >= samples.Length) continue;
                double env = 1.0;
                if (i < fade) env = (double)i / fade;
                else if (i > len - fade) env = (double)(len - i) / fade;
                double s = Math.Sin(2 * Math.PI * freq * i / SampleRate) * vol * env;
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
