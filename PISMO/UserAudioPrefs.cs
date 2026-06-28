using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PISMO
{
    /// <summary>
    /// Сохраняемые пользовательские настройки звука для конкретных собеседников:
    /// индивидуальная громкость и «заглушить». Привязаны к pid (id участника),
    /// поэтому переживают перезапуск и повторные звонки.
    /// </summary>
    public static class UserAudioPrefs
    {
        private sealed class Entry { public float Volume { get; set; } = 1f; public bool Muted { get; set; } }

        private static readonly object _lock = new();
        private static Dictionary<string, Entry> _map;

        private static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PISMO");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "user_audio.json");
            }
        }

        private static void EnsureLoaded()
        {
            if (_map != null) return;
            lock (_lock)
            {
                if (_map != null) return;
                try
                {
                    if (File.Exists(FilePath))
                        _map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath))
                               ?? new Dictionary<string, Entry>();
                    else
                        _map = new Dictionary<string, Entry>();
                }
                catch { _map = new Dictionary<string, Entry>(); }
            }
        }

        private static void Save()
        {
            try
            {
                lock (_lock)
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(_map));
            }
            catch { }
        }

        /// <summary>Сохранённая громкость участника (1.0, если не задана).</summary>
        public static float GetVolume(string pid)
        {
            EnsureLoaded();
            return _map.TryGetValue(pid, out var e) ? e.Volume : 1f;
        }

        /// <summary>Сохранён ли «заглушить» для участника.</summary>
        public static bool GetMuted(string pid)
        {
            EnsureLoaded();
            return _map.TryGetValue(pid, out var e) && e.Muted;
        }

        /// <summary>Есть ли сохранённая настройка для участника.</summary>
        public static bool Has(string pid)
        {
            EnsureLoaded();
            return _map.ContainsKey(pid);
        }

        public static void SetVolume(string pid, float volume)
        {
            EnsureLoaded();
            if (!_map.TryGetValue(pid, out var e)) { e = new Entry(); _map[pid] = e; }
            e.Volume = volume;
            Save();
        }

        public static void SetMuted(string pid, bool muted)
        {
            EnsureLoaded();
            if (!_map.TryGetValue(pid, out var e)) { e = new Entry(); _map[pid] = e; }
            e.Muted = muted;
            Save();
        }
    }
}
