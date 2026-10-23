// ============================================================
//  VideoShapes — какой формы показывать ролик в ленте
// ============================================================
//
//  Любое видео показывалось в коробке 280×336: вертикальное, снятое
//  телефоном, занимало узкую полоску посреди чёрного поля, а широкое
//  прижималось к середине. В Telegram пузырь принимает форму ролика, и
//  теперь так же здесь и на телефоне (там это ui/chat/VideoShapes.kt).
//
//  Узнать форму заранее неоткуда: размеры кадра знает только сам файл, а он
//  лежит на сервере и в ленту не подгружается. Поэтому первый показ идёт в
//  обычных 4:3, а страница плеера, открыв ролик, присылает его размеры —
//  они запоминаются и живут в кеше пользователя, так что со следующего раза
//  коробка сразу правильная, даже после перезапуска.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PISMO
{
    internal static class VideoShapes
    {
        private const double Default = 4.0 / 3.0;
        private const double Min = 0.5;   // уже «девять на шестнадцать» не бывает
        private const double Max = 2.0;   // и шире «двух к одному» тоже

        private static readonly object Sync = new();
        private static Dictionary<string, double> _map;

        private static string FilePath
        {
            get
            {
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "PISMO", "cache", UserSession.EffectiveId.ToString());
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "video_shapes.txt");
                }
                catch { return null; }
            }
        }

        private static void EnsureLoaded()
        {
            if (_map != null) return;
            _map = new Dictionary<string, double>(StringComparer.Ordinal);
            try
            {
                string p = FilePath;
                if (p == null || !File.Exists(p)) return;
                foreach (var line in File.ReadAllLines(p))
                {
                    int tab = line.LastIndexOf('\t');
                    if (tab <= 0) continue;
                    if (double.TryParse(line.Substring(tab + 1), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out double r))
                        _map[line.Substring(0, tab)] = r;
                }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                string p = FilePath;
                if (p == null || _map == null) return;
                var sb = new StringBuilder();
                foreach (var kv in _map)
                    sb.Append(kv.Key).Append('\t')
                      .Append(kv.Value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(p, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Запомнить форму ролика — её присылает страница плеера.</summary>
        public static void Remember(string key, int width, int height)
        {
            if (string.IsNullOrEmpty(key) || width <= 0 || height <= 0) return;
            double r = Math.Min(Max, Math.Max(Min, width / (double)height));
            lock (Sync)
            {
                EnsureLoaded();
                if (_map.TryGetValue(key, out double old) && Math.Abs(old - r) < 0.01) return;
                _map[key] = r;
                Save();
            }
        }

        /// <summary>Коробка под ролик: ширина не больше <paramref name="maxW"/>.</summary>
        public static (int w, int h) Box(string key, int maxW)
        {
            double r;
            lock (Sync)
            {
                EnsureLoaded();
                r = key != null && _map.TryGetValue(key, out double v) ? v : Default;
            }
            // Вертикальному хватает и меньшей ширины: во всю ширину пузыря он
            // занял бы пол-ленты — ровно то, что было с коробкой 280×336.
            int w = r < 1.0 ? Math.Min(maxW, 340) : maxW;
            int h = Math.Max(120, (int)Math.Round(w / r));
            return (w, h);
        }
    }
}
