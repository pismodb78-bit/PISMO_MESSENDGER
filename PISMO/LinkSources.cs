using System;
using System.Collections.Generic;
using System.Drawing;
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

        /// <summary>Строка «откуда ссылка»: цветной значок и название службы.</summary>
        internal static Panel MakeRow(string url, int width)
        {
            var src = Of(url);
            var row = new Panel
            {
                Size = new Size(Math.Max(60, width), 20),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
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
                Text = src.Title,
                ForeColor = Color.FromArgb(160, 165, 175),
                Font = new Font("Segoe UI", 8f),
                AutoSize = true,
                Location = new Point(24, 4),
                Cursor = Cursors.Hand,
            };

            void Open(object s, EventArgs e) => MainForm.OpenLink(url);
            row.Click += Open; badge.Click += Open; title.Click += Open;

            row.Controls.Add(badge);
            row.Controls.Add(title);
            return row;
        }
    }
}
