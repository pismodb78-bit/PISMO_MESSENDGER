using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Тема оформления (2.0). Тёмная тема — исходный вид приложения (цвета в коде
    /// уже тёмные), поэтому в тёмном режиме Theme НИЧЕГО не трогает — нулевой риск
    /// регрессий. Светлая тема реализована как рантайм-перекраска: рекурсивно
    /// проходим дерево контролов и заменяем известные НЕЙТРАЛЬНЫЕ тёмные цвета их
    /// светлыми аналогами. Насыщенные акценты (блёрпл, зелёный, красный) и чистый
    /// белый текст на кнопках оставляем как есть — они одинаково смотрятся в обеих
    /// темах.
    ///
    /// Чтобы динамически создаваемые контролы (пузыри сообщений, карточки чатов)
    /// тоже красились, Apply вешает обработчик ControlAdded на каждый контейнер —
    /// любой новый ребёнок автоматически проходит перекраску.
    /// </summary>
    public static class Theme
    {
        // Тема ФИКСИРУЕТСЯ при первом обращении (на старте приложения). Иначе
        // смена галочки на лету давала «полусветлый» интерфейс: новые окна
        // открывались светлыми, а уже открытые оставались тёмными. Теперь смена
        // настройки вступает в силу после перезапуска (UI сам его предлагает).
        private static bool? _lightAtStartup;

        public static bool IsLight
        {
            get
            {
                _lightAtStartup ??= string.Equals(DeviceSettings.ThemeMode, "light", StringComparison.OrdinalIgnoreCase);
                return _lightAtStartup.Value;
            }
        }

        // Карта «тёмный нейтральный → светлый нейтральный». Ключ — упакованный ARGB.
        private static readonly Dictionary<int, Color> _map = BuildMap();

        private static Dictionary<int, Color> BuildMap()
        {
            var m = new Dictionary<int, Color>();
            void Add(int r, int g, int b, int lr, int lg, int lb) =>
                m[Color.FromArgb(255, r, g, b).ToArgb()] = Color.FromArgb(lr, lg, lb);

            // ── Фоны: тёмные → СЕРЫЕ (не чисто-белые: слепило, просили серее
            //    и с контурами). Градация сохранена: что темнее в тёмной теме,
            //    то темнее и в светлой. ──
            Add(20, 21, 23, 212, 214, 218);   Add(20, 21, 24, 212, 214, 218);
            Add(24, 25, 28, 220, 222, 226);   Add(28, 29, 34, 220, 222, 226);
            Add(30, 31, 34, 224, 226, 230);   Add(32, 34, 37, 224, 226, 230);
            Add(36, 37, 42, 214, 216, 220);   // шапка сайдбара («Все пользователи») — была не в карте
            Add(40, 42, 46, 232, 234, 238);   Add(43, 45, 49, 230, 232, 236);
            Add(47, 49, 54, 226, 228, 232);   Add(49, 51, 56, 222, 224, 228);
            Add(54, 57, 63, 216, 218, 222);   Add(56, 58, 64, 216, 218, 222);
            Add(60, 63, 68, 208, 211, 215);   // «пилюля» рейла серверов
            Add(64, 68, 75, 204, 207, 212);   Add(65, 68, 75, 204, 207, 212);
            Add(79, 84, 92, 188, 191, 196);   Add(80, 82, 88, 188, 191, 196);
            Add(80, 80, 80, 188, 191, 196);
            Add(35, 36, 41, 228, 230, 234);   // карточка футера (плашка профиля)

            // ── Текст (светло-серый на тёмном → тёмный на светлом) ──
            Add(220, 221, 222, 46, 48, 53);   Add(230, 231, 232, 46, 48, 53);
            Add(200, 202, 208, 60, 63, 69);   Add(185, 187, 190, 79, 84, 92);
            Add(150, 152, 158, 96, 101, 108); Add(142, 146, 151, 96, 101, 108);
            Add(140, 142, 148, 96, 101, 108); Add(114, 118, 125, 110, 114, 120);
            return m;
        }

        /// <summary>Светлый аналог цвета (или сам цвет, если он не в карте или тема
        /// тёмная). Можно вызывать из кастомных Paint-обработчиков.</summary>
        public static Color Map(Color c)
        {
            if (!IsLight) return c;
            return _map.TryGetValue(Color.FromArgb(255, c.R, c.G, c.B).ToArgb(), out var l) ? l : c;
        }

        /// <summary>Перекрасить форму/контрол и всё его поддерево под текущую тему,
        /// а также подписаться на добавление новых детей.</summary>
        public static void Apply(Control root)
        {
            if (root == null || !IsLight) return;   // тёмная тема — оставляем как есть
            try { Recolor(root); } catch { }
        }

        private static void Recolor(Control c)
        {
            // Не трогаем контролы, которые сами рисуют текст на насыщенном фоне и
            // где белый должен остаться (кнопки с явным акцентным BackColor
            // перекрасятся фоном, но их ForeColor=White оставляем — Map(White)=White).
            var nb = Map(c.BackColor);
            if (nb != c.BackColor) c.BackColor = nb;
            var nf = Map(c.ForeColor);
            if (nf != c.ForeColor) c.ForeColor = nf;

            // Контуры (просили «серый с контурами потемнее»): плоским НЕакцентным
            // кнопкам в светлой теме включаем тонкую серую рамку — элементы не
            // сливаются с серым фоном. Акцентные (блёрпл/зелёные/красные) не трогаем.
            if (c is Button b && b.FlatStyle == FlatStyle.Flat)
            {
                var bc = b.BackColor;
                bool accent = bc.A > 0 &&
                    (Math.Max(bc.R, Math.Max(bc.G, bc.B)) - Math.Min(bc.R, Math.Min(bc.G, bc.B))) > 28;
                if (!accent && b.FlatAppearance.BorderSize == 0)
                {
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.BorderColor = Color.FromArgb(176, 179, 185);
                }
            }

            // Подписка на будущих детей (один раз на контейнер).
            if (!_hooked.Contains(c))
            {
                _hooked.Add(c);
                c.ControlAdded += (s, e) => { try { Recolor(e.Control); } catch { } };
                c.Disposed += (s, e) => { try { _hooked.Remove(c); } catch { } };
            }

            foreach (Control child in c.Controls) Recolor(child);
        }

        // Слабый учёт уже подписанных контейнеров, чтобы не плодить обработчики.
        private static readonly HashSet<Control> _hooked = new();
    }
}
