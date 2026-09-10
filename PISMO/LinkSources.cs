using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Откуда ссылка: короткая пометка и цвет службы.
    ///
    /// Настоящих логотипов не качаем и с собой не носим. Тянуть иконку с
    /// самого сайта значит сообщить ему, что человек открыл переписку, ещё
    /// до того, как он нажал на ссылку; носить чужие логотипы в приложении —
    /// отдельный разговор про права. Поэтому рисуем сами: цвет службы и
    /// одна-две буквы, ровно как уже сделаны бейджи типов файлов.
    /// </summary>
    internal static class LinkSources
    {
        internal readonly record struct Source(string Mark, Color Color, string Title);

        private static readonly (string[] Hosts, Source Src)[] Known =
        {
            (new[] { "youtube.com", "youtu.be", "youtube-nocookie.com" }, new Source("YT", Color.FromArgb(255, 0, 0), "YouTube")),
            (new[] { "steampowered.com", "steamcommunity.com" },         new Source("ST", Color.FromArgb(27, 40, 56), "Steam")),
            (new[] { "instagram.com", "instagr.am" },                    new Source("IG", Color.FromArgb(225, 48, 108), "Instagram")),
            (new[] { "t.me", "telegram.org", "telegram.me" },            new Source("TG", Color.FromArgb(42, 171, 238), "Telegram")),
            (new[] { "vk.com", "vk.ru" },                                new Source("VK", Color.FromArgb(0, 119, 255), "ВКонтакте")),
            (new[] { "github.com", "gist.github.com" },                  new Source("GH", Color.FromArgb(36, 41, 46), "GitHub")),
            (new[] { "x.com", "twitter.com" },                           new Source("X",  Color.FromArgb(20, 23, 26), "X")),
            (new[] { "tiktok.com" },                                     new Source("TT", Color.FromArgb(1, 1, 1), "TikTok")),
            (new[] { "discord.com", "discord.gg", "discordapp.com" },    new Source("DC", Color.FromArgb(88, 101, 242), "Discord")),
            (new[] { "twitch.tv" },                                      new Source("TW", Color.FromArgb(145, 70, 255), "Twitch")),
            (new[] { "reddit.com", "redd.it" },                          new Source("RD", Color.FromArgb(255, 69, 0), "Reddit")),
            (new[] { "spotify.com" },                                    new Source("SP", Color.FromArgb(29, 185, 84), "Spotify")),
            (new[] { "music.yandex.ru", "yandex.ru", "ya.ru" },          new Source("Я",  Color.FromArgb(252, 63, 29), "Яндекс")),
            (new[] { "google.com", "google.ru" },                        new Source("G",  Color.FromArgb(66, 133, 244), "Google")),
            (new[] { "wikipedia.org", "wikimedia.org" },                 new Source("W",  Color.FromArgb(99, 100, 102), "Википедия")),
            (new[] { "rutube.ru" },                                      new Source("RT", Color.FromArgb(20, 25, 31), "RUTUBE")),
            (new[] { "ok.ru" },                                          new Source("OK", Color.FromArgb(238, 130, 8), "Одноклассники")),
            (new[] { "pikabu.ru" },                                      new Source("PK", Color.FromArgb(0, 160, 70), "Пикабу")),
            (new[] { "habr.com" },                                       new Source("H",  Color.FromArgb(98, 159, 208), "Хабр")),
            (new[] { "mail.ru" },                                        new Source("MR", Color.FromArgb(0, 95, 249), "Mail.ru")),
            (new[] { "avito.ru" },                                       new Source("AV", Color.FromArgb(0, 170, 255), "Авито")),
            (new[] { "wildberries.ru" },                                 new Source("WB", Color.FromArgb(123, 31, 162), "Wildberries")),
            (new[] { "ozon.ru" },                                        new Source("OZ", Color.FromArgb(0, 91, 255), "Ozon")),
        };

        /// <summary>Домен без www — то, что показываем рядом со значком.</summary>
        internal static string DomainOf(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            int i = url.IndexOf("://", StringComparison.Ordinal);
            string rest = i >= 0 ? url[(i + 3)..] : url;
            int cut = rest.IndexOfAny(new[] { '/', '?', '#' });
            string host = cut >= 0 ? rest[..cut] : rest;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            return host.ToLowerInvariant();
        }

        internal static Source Of(string url)
        {
            string host = DomainOf(url);
            foreach (var (hosts, src) in Known)
                foreach (var h in hosts)
                    if (host == h || host.EndsWith("." + h, StringComparison.Ordinal))
                        return src;

            // Незнакомый адрес — первая буква домена на сером. Так строка всё
            // равно читается как «источник», а не как безликая полоска.
            string letter = "?";
            foreach (var c in host)
                if (char.IsLetterOrDigit(c)) { letter = char.ToUpperInvariant(c).ToString(); break; }
            return new Source(letter, Color.FromArgb(110, 118, 129), host);
        }

        /// <summary>
        /// Карточка ссылки: значок и название службы, а если сайт уже отдавал
        /// разметку — ещё заголовок, описание и картинка.
        ///
        /// Карточка собирается ТОЛЬКО из уже готового: разметку тянет фоновая
        /// задача, и дорисовывать её в уже размеченный пузырь значило бы
        /// двигать всё, что под ним. Поэтому в первый раз показывается строка
        /// со значком, а полная карточка появляется при следующей отрисовке
        /// переписки — она и так происходит при каждом обновлении.
        /// </summary>
        /// <param name="bubbleBack">
        /// Цвет пузыря, в котором карточка живёт. Нужен затем, что своего
        /// цвета у неё быть не должно: раньше стоял постоянный тёмно-серый, и
        /// в СВОЁМ, синем пузыре карточка выглядела чужой заплаткой. Телефон
        /// делает то же самое — кладёт полупрозрачную черноту поверх пузыря.
        /// </param>
        internal static Panel MakeCard(string url, int width, Color bubbleBack)
        {
            var ready = LinkPreviews.Cached(url);
            if (ready == null)
            {
                LinkPreviews.QueueFetch(url);
                return MakeRow(url, width);
            }

            var src = Of(url);
            var card = new Panel
            {
                Width = Math.Max(120, width),
                BackColor = Darken(bubbleBack, 0.13f),
                Cursor = Cursors.Hand,
                // По метке карточку потом находят в пузыре, чтобы заменить её
                // на месте, когда сайт ответит, — вместо перерисовки всей
                // переписки.
                Tag = TagOf(url),
            };
            int y = 6;

            var badge = new Label
            {
                Text = src.Mark,
                BackColor = src.Color,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", src.Mark.Length > 1 ? 6.5f : 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(18, 18),
                Location = new Point(8, y),
                Cursor = Cursors.Hand,
            };
            card.Controls.Add(badge);

            bool playable = VideoLinks.Of(url).Kind != VideoLinks.Kind.None;
            var site = new Label
            {
                Text = (string.IsNullOrWhiteSpace(ready.Site) ? src.Title : ready.Site)
                       + (playable ? "  ▶" : ""),
                ForeColor = Color.FromArgb(0, 176, 244),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(32, y + 3),
                Cursor = Cursors.Hand,
            };
            card.Controls.Add(site);
            y += 24;

            int textW = card.Width - 16;
            if (!string.IsNullOrWhiteSpace(ready.Title))
            {
                var t = new Label
                {
                    Text = ready.Title,
                    ForeColor = Color.FromArgb(235, 236, 240),
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    AutoSize = false,
                    Size = new Size(textW, TextRenderer.MeasureText(ready.Title,
                        new Font("Segoe UI", 9f, FontStyle.Bold), new Size(textW, 0),
                        TextFormatFlags.WordBreak).Height),
                    Location = new Point(8, y),
                    Cursor = Cursors.Hand,
                };
                card.Controls.Add(t);
                y += t.Height + 3;
            }

            if (!string.IsNullOrWhiteSpace(ready.Description))
            {
                string desc = ready.Description.Length > 220
                    ? ready.Description[..220] + "…" : ready.Description;
                var font = new Font("Segoe UI", 8.5f);
                var d = new Label
                {
                    Text = desc,
                    ForeColor = Color.FromArgb(160, 165, 175),
                    Font = font,
                    AutoSize = false,
                    Size = new Size(textW, TextRenderer.MeasureText(desc, font,
                        new Size(textW, 0), TextFormatFlags.WordBreak).Height),
                    Location = new Point(8, y),
                    Cursor = Cursors.Hand,
                };
                card.Controls.Add(d);
                y += d.Height + 4;
            }

            if (!string.IsNullOrEmpty(ready.ImagePath) && File.Exists(ready.ImagePath))
            {
                try
                {
                    // Читаем через поток и копируем: PictureBox из Image.FromFile
                    // держит файл открытым, и кеш потом не почистить.
                    Image img;
                    using (var fs = File.OpenRead(ready.ImagePath))
                    using (var src2 = Image.FromStream(fs))
                        img = new Bitmap(src2);

                    int w = textW;
                    int h = Math.Max(1, (int)(img.Height * (w / (double)img.Width)));
                    if (h > 220) { h = 220; w = Math.Max(1, (int)(img.Width * (h / (double)img.Height))); }
                    var pic = new PictureBox
                    {
                        Image = img,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Size = new Size(w, h),
                        Location = new Point(8, y),
                        Cursor = Cursors.Hand,
                    };
                    pic.Disposed += (s, e) => { try { pic.Image?.Dispose(); } catch { } };
                    card.Controls.Add(pic);
                    y += h + 4;
                }
                catch { }
            }

            card.Height = y + 4;

            Hook(card, url);
            return card;
        }

        /// <summary>Метка карточки: по ней её находят в пузыре.</summary>
        internal static string TagOf(string url) => "link:" + url;

        /// <summary>Цвет чуть темнее исходного — ровно как чернота поверх пузыря.</summary>
        private static Color Darken(Color c, float k) => Color.FromArgb(
            (int)(c.R * (1 - k)), (int)(c.G * (1 - k)), (int)(c.B * (1 - k)));

        /// <summary>
        /// Нажатия на карточку ссылки: левой кнопкой — открыть, правой — меню.
        ///
        /// Раньше здесь висел Click, и это оказалось не тем событием: WinForms
        /// поднимает Click на ЛЮБУЮ кнопку мыши, а не только на левую. Из-за
        /// этого правая кнопка одновременно открывала меню сообщения И уводила
        /// по ссылке — то есть посмотреть, что за ссылка, было нельзя: она
        /// открывалась от самой попытки спросить.
        ///
        /// Меню то же, что на телефоне по долгому нажатию на ссылку.
        /// </summary>
        private static void Hook(Panel root, string url)
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(24, 25, 28),
                ForeColor = Color.FromArgb(220, 221, 222),
                Font = new Font("Segoe UI", 9.5f),
            };
            // «Смотреть здесь» — только для того, что умеем проиграть: обещать
            // и открыть пустое окно хуже, чем не обещать.
            if (VideoLinks.Of(url).Kind != VideoLinks.Kind.None)
            {
                menu.Items.Add("▶ Смотреть здесь", null,
                    (s, e) => LinkVideoForm.TryPlay(root.FindForm(), url));
            }
            menu.Items.Add("Открыть в браузере", null, (s, e) => MainForm.OpenLink(url));
            menu.Items.Add("Копировать ссылку", null, (s, e) =>
            {
                try { Clipboard.SetText(url); } catch { }
            });
            root.Disposed += (s, e) => { try { menu.Dispose(); } catch { } };

            void Attach(Control c)
            {
                c.ContextMenuStrip = menu;
                c.MouseUp += (s, e) =>
                {
                    // Мышь захвачена тем, кто получил нажатие: без проверки
                    // границ ссылка открывалась бы и после «увёл и отпустил».
                    if (e.Button != MouseButtons.Left) return;
                    if (!c.ClientRectangle.Contains(e.Location)) return;
                    // Видео открываем прямо здесь, остальное — в браузере.
                    // Уходить из переписки ради ролика не нужно.
                    if (!LinkVideoForm.TryPlay(root.FindForm(), url)) MainForm.OpenLink(url);
                };
            }

            Attach(root);
            foreach (Control c in root.Controls) Attach(c);
        }

        /// <summary>Строка «откуда ссылка»: цветной значок и название службы.</summary>
        internal static Panel MakeRow(string url, int width)
        {
            var src = Of(url);
            var row = new Panel
            {
                Size = new Size(Math.Max(60, width), 20),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = TagOf(url),
            };

            var badge = new Label
            {
                Text = src.Mark,
                BackColor = src.Color,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", src.Mark.Length > 1 ? 6.5f : 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(18, 18),
                Location = new Point(0, 1),
                Cursor = Cursors.Hand,
            };

            var title = new Label
            {
                Text = src.Title + (VideoLinks.Of(url).Kind != VideoLinks.Kind.None ? "  ▶" : ""),
                ForeColor = Color.FromArgb(160, 165, 175),
                Font = new Font("Segoe UI", 8f),
                AutoSize = true,
                Location = new Point(24, 4),
                Cursor = Cursors.Hand,
            };

            row.Controls.Add(badge);
            row.Controls.Add(title);
            Hook(row, url);
            return row;
        }
    }
}
