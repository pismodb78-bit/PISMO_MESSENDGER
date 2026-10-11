using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using MySqlConnector;

namespace PISMO
{
    /// <summary>
    /// Настройки сервера: название, значок (картинка или цвет) и номер
    /// для приглашения.
    /// </summary>
    /// <remarks>
    /// До этого на ПК из настроек сервера была только надпись с номером —
    /// не нажимаемая. Название, оформление и смена номера жили либо нигде,
    /// либо только на телефоне; окно собирает всё это в одном месте, как в
    /// Android-версии.
    /// </remarks>
    public sealed partial class ServersForm
    {
        private void ServerSettings()
        {
            if (_serverId <= 0) return;

            string nameWas = _serverName;
            string colorWas = ReadServerColor(_serverId);
            string color = colorWas;
            bool hasPicture = ServerHasAvatar(_serverId);
            int sid = _serverId;

            // Колонок оформления может не быть: у учётной записи приложения на
            // этом хостинге нет прав ALTER, и схему кладёт администратор из
            // sql/. Раньше окно в таком случае выглядело рабочим, но ни цвет,
            // ни картинка не сохранялись и ничего об этом не говорили.
            bool lookOk = LookColumnsExist();

            using var f = new Form
            {
                Text = "Настройки сервера",
                ClientSize = new Size(430, 400),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(47, 49, 54),
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lblN = new Label
            {
                Text = "Название:", ForeColor = Color.White,
                Location = new Point(12, 14), AutoSize = true
            };
            var txtN = new TextBox
            {
                Location = new Point(12, 34), Size = new Size(406, 24),
                Text = nameWas, Enabled = _canManage,
                BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            // ── Значок ────────────────────────────────────────────────────
            var preview = new Panel
            {
                Location = new Point(12, 72), Size = new Size(64, 64),
                BackColor = Color.Transparent
            };
            preview.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, 63, 63);
                var back = ServerLook.ParseColor(color) ?? Color.FromArgb(88, 101, 242);
                using var path = RoundedSquare(rect, 18);
                using (var br = new SolidBrush(back)) g.FillPath(br, path);

                var img = hasPicture ? ServerLook.Cached(sid) : null;
                if (img != null)
                {
                    var st = g.Save();
                    g.SetClip(path);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, rect);
                    g.Restore(st);
                }
                else
                {
                    string ini = (txtN.Text ?? "").Trim();
                    ini = ini.Length >= 2 ? ini.Substring(0, 2).ToUpperInvariant()
                        : ini.ToUpperInvariant();
                    using var fnt = new Font("Segoe UI Semibold", 16f, FontStyle.Bold);
                    TextRenderer.DrawText(g, ini, fnt, rect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            };
            txtN.TextChanged += (s, e) => preview.Invalidate();

            var btnPick = MakeDialogButton(hasPicture ? "🖼 Сменить картинку" : "🖼 Загрузить картинку",
                new Point(88, 76), new Size(170, 28));
            var btnDrop = MakeDialogButton("Убрать картинку", new Point(264, 76), new Size(154, 28));
            btnDrop.Visible = hasPicture;

            var lblHint = new Label
            {
                Text = lookOk
                    ? "Значок видят все участники сервера."
                    : "Значку нужна миграция: sql/2026-10-11_servers_avatar.sql",
                ForeColor = lookOk ? Color.FromArgb(160, 164, 170) : Color.FromArgb(230, 160, 120),
                Location = new Point(88, 108), AutoSize = true,
                MaximumSize = new Size(330, 0)
            };
            btnPick.Enabled = lookOk;
            if (!lookOk) btnDrop.Visible = false;

            btnPick.Click += (s, e) =>
            {
                var png = PickServerIcon(f);
                if (png == null) return;
                if (!SaveServerAvatar(sid, png)) return;
                hasPicture = true;
                ServerLook.Remember(sid, ServerLook.Decode(png));
                btnPick.Text = "🖼 Сменить картинку";
                btnDrop.Visible = true;
                preview.Invalidate();
                RefreshServerLookEverywhere();
            };

            btnDrop.Click += (s, e) =>
            {
                if (!SaveServerAvatar(sid, null)) return;
                hasPicture = false;
                ServerLook.Remember(sid, null);
                btnPick.Text = "🖼 Загрузить картинку";
                btnDrop.Visible = false;
                preview.Invalidate();
                RefreshServerLookEverywhere();
            };

            // ── Цвет значка ───────────────────────────────────────────────
            var lblC = new Label
            {
                Text = "Цвет значка:", ForeColor = Color.White,
                Location = new Point(12, 150), AutoSize = true
            };
            var txtC = new TextBox
            {
                Location = new Point(110, 146), Size = new Size(94, 24), Text = color,
                BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            txtC.TextChanged += (s, e) => { color = txtC.Text.Trim(); preview.Invalidate(); };
            txtC.Enabled = lookOk;

            // Та же палитра, что у цветов ролей: оба цвета лежат в базе строкой
            // и рисуются двумя клиентами — наборы должны совпадать.
            string[] palette =
            {
                "#1ABC9C", "#2ECC71", "#3498DB", "#9B59B6", "#E91E63", "#F1C40F",
                "#E67E22", "#E74C3C", "#95A5A6", "#607D8B", "#3BA55D", "#5865F2",
            };
            int px = 12, py = 180;
            var swatches = new List<Panel>();
            foreach (var hex in palette)
            {
                var sw = new Panel
                {
                    Location = new Point(px, py), Size = new Size(26, 26),
                    BackColor = ColorTranslator.FromHtml(hex),
                    Cursor = Cursors.Hand, BorderStyle = BorderStyle.FixedSingle
                };
                string hexCap = hex;
                sw.Enabled = lookOk;
                sw.Click += (s, e) => { txtC.Text = hexCap; };
                swatches.Add(sw);
                px += 32;
                if (px + 26 > 418) { px = 12; py += 32; }
            }

            var btnMore = MakeDialogButton("🎨 Ещё…", new Point(12, py + 36), new Size(100, 26));
            btnMore.Click += (s, e) =>
            {
                using var cd = new ColorDialog { FullOpen = true };
                try { cd.Color = ColorTranslator.FromHtml(txtC.Text.Trim()); } catch { }
                if (cd.ShowDialog(f) == DialogResult.OK)
                    txtC.Text = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
            };
            var btnPlain = MakeDialogButton("Цвет по умолчанию", new Point(120, py + 36), new Size(160, 26));
            btnPlain.Click += (s, e) => { txtC.Text = ""; };
            btnMore.Enabled = lookOk;
            btnPlain.Enabled = lookOk;

            // ── Номер сервера ─────────────────────────────────────────────
            int idTop = py + 76;
            var lblId = new Label
            {
                Text = "ID для приглашения: " + sid,
                ForeColor = Color.FromArgb(0, 168, 252),
                Location = new Point(12, idTop + 6), AutoSize = true
            };
            var btnCopy = MakeDialogButton("Скопировать", new Point(220, idTop), new Size(110, 26));
            btnCopy.Click += (s, e) =>
            {
                try { Clipboard.SetText(sid.ToString()); } catch { }
            };
            var btnNewId = MakeDialogButton("Новый ID", new Point(336, idTop), new Size(82, 26));
            btnNewId.Visible = _isOwner;
            btnNewId.Click += (s, e) =>
            {
                if (MessageBox.Show(
                        $"Сервер получит новый случайный номер. По старому ({sid}) войти " +
                        "больше не получится — разошлите новый тем, кого ждёте.\n\n" +
                        "Участники, каналы и переписка останутся на месте.",
                        "Сменить ID сервера?", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) != DialogResult.Yes) return;

                int fresh = ChangeServerId(sid);
                if (fresh <= 0)
                {
                    MessageBox.Show("Не удалось сменить ID.", "PISMO");
                    return;
                }
                MessageBox.Show($"Новый ID для приглашений: {fresh}", "PISMO");
                f.Tag = fresh;          // чтобы после закрытия перейти на новый номер
                f.DialogResult = DialogResult.Cancel;
                f.Close();
            };

            var ok = MakeDialogButton("Сохранить", new Point(236, idTop + 44), new Size(94, 30));
            ok.DialogResult = DialogResult.OK;
            var cancel = MakeDialogButton("Отмена", new Point(336, idTop + 44), new Size(82, 30));
            cancel.DialogResult = DialogResult.Cancel;

            f.Controls.AddRange(new Control[]
            {
                lblN, txtN, preview, btnPick, btnDrop, lblHint,
                lblC, txtC, btnMore, btnPlain, lblId, btnCopy, btnNewId, ok, cancel
            });
            foreach (var sw in swatches) f.Controls.Add(sw);
            f.AcceptButton = ok;
            f.CancelButton = cancel;

            var result = f.ShowDialog(this);

            // Номер сменили — уходим на сервер под новым номером.
            if (f.Tag is int newId && newId > 0)
            {
                ServerLook.Forget(sid);
                LoadServers();
                SelectServer(newId, nameWas, _isOwner);
                RefreshServerLookEverywhere();
                return;
            }

            if (result != DialogResult.OK) return;

            string nameNow = (txtN.Text ?? "").Trim();
            string colorNow = (txtC.Text ?? "").Trim();
            bool renamed = _canManage && nameNow.Length > 0 && nameNow != nameWas;
            bool recolored = _canManage && colorNow != colorWas;

            if (renamed) SaveServerName(sid, nameNow);
            if (recolored && !SaveServerColor(sid, colorNow)) return;
            if (!renamed && !recolored) return;

            LoadServers();
            SelectServer(sid, renamed ? nameNow : nameWas, _isOwner);
            RefreshServerLookEverywhere();
        }

        /// <summary>Кнопка в стиле диалогов сервера — чтобы не повторять настройки.</summary>
        private static Button MakeDialogButton(string text, Point at, Size size)
        {
            var b = new Button
            {
                Text = text, Location = at, Size = size,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(64, 68, 75),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private static GraphicsPath RoundedSquare(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// Выбор файла значка. Читаем БАЙТАМИ и разбираем через WIC при нужде:
        /// картинка могла прийти с телефона, а там это мог быть и WebP, который
        /// GDI+ не открывает.
        /// </summary>
        private byte[]? PickServerIcon(IWin32Window owner)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Значок сервера",
                Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.heic;*.avif|Все файлы|*.*"
            };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
            try
            {
                var raw = File.ReadAllBytes(dlg.FileName);
                using var src = ServerLook.Decode(raw);
                if (src == null)
                {
                    MessageBox.Show("Не удалось открыть это изображение.", "PISMO");
                    return null;
                }
                using var crop = new AvatarCropForm(src);
                if (crop.ShowDialog(owner) == DialogResult.OK && crop.ResultPng != null)
                    return crop.ResultPng;
                // Обрезку отменили — значок не меняем.
                return null;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка изображения: " + ex.Message, "PISMO");
                return null;
            }
        }

        // ── Чтение и запись оформления ────────────────────────────────────

        private string ReadServerColor(int serverId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT avatar_color FROM servers WHERE id=@s", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                var o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? "" : o.ToString() ?? "";
            }
            catch { return ""; }
        }

        private bool ServerHasAvatar(int serverId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT avatar_data IS NOT NULL FROM servers WHERE id=@s", conn);
                cmd.Parameters.AddWithValue("@s", serverId);
                var o = cmd.ExecuteScalar();
                return o != null && o != DBNull.Value && Convert.ToInt32(o) != 0;
            }
            catch { return false; }
        }

        private bool SaveServerAvatar(int serverId, byte[]? png)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("UPDATE servers SET avatar_data=@a WHERE id=@s", conn);
                cmd.Parameters.AddWithValue("@a", (object?)png ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@s", serverId);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex) { ReportLookFailure(ex); return false; }
        }

        private bool SaveServerColor(int serverId, string hex)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("UPDATE servers SET avatar_color=@c WHERE id=@s", conn);
                cmd.Parameters.AddWithValue("@c", hex);
                cmd.Parameters.AddWithValue("@s", serverId);
                cmd.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex) { ReportLookFailure(ex); return false; }
        }

        private void SaveServerName(int serverId, string name)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("UPDATE servers SET name=@n WHERE id=@s", conn);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@s", serverId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex) { ShowDbError(ex); }
        }

        private static bool _lookColumnsKnown;

        /// <summary>
        /// Есть ли в servers колонки оформления (миграция 24).
        /// </summary>
        /// <remarks>
        /// Запоминаем только утвердительный ответ: отрицательный лечится
        /// запуском файла из sql/ прямо во время работы программы, и
        /// перезапускать её ради этого незачем. Запрос дешёвый и делается
        /// только при открытии окна настроек.
        /// </remarks>
        private static bool LookColumnsExist()
        {
            if (_lookColumnsKnown) return true;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT avatar_data, avatar_color FROM servers LIMIT 0", conn);
                cmd.ExecuteScalar();
                _lookColumnsKnown = true;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Говорит, почему оформление не сохранилось. Молчать здесь нельзя:
        /// именно так выглядела «кнопка ничего не делает».
        /// </summary>
        private static void ReportLookFailure(Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[SERVERS] " + ex.Message);
            bool noColumn = ex is MySqlException m && m.Number == 1054;
            MessageBox.Show(
                noColumn
                    ? "В таблице servers нет колонок значка.\n\n" +
                      "Выполните sql/2026-10-11_servers_avatar.sql (или pismo-all.sql) " +
                      "под учётной записью с правами ALTER — у приложения их нет."
                    : "Не удалось сохранить: " + ex.Message,
                "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>Перерисовать значки там, где они видны, — в рейле главного окна.</summary>
        private static void RefreshServerLookEverywhere()
        {
            // Запись удалась — значит, колонки на месте. Рейл мог решить
            // обратное при запуске (до того, как администратор прогнал sql/),
            // и без этого остался бы с буквенными значками до перезапуска.
            try { MainForm.NoteServerLookColumnsExist(); } catch { }
            try { MainForm.Current?.LoadServerRailItems(); } catch { }
        }

        /// <summary>
        /// Выдаёт серверу новый случайный номер. Возвращает его или 0.
        /// </summary>
        /// <remarks>
        /// Номер — это приглашение. Если он разошёлся шире, чем хотелось,
        /// сменить его — единственный способ закрыть вход: отзывать
        /// приглашения по одному в проекте нечем.
        ///
        /// Каналы, роли, баны и участники привязаны к серверу этим же номером,
        /// поэтому правим его во ВСЕХ таблицах, где есть колонка server_id.
        /// Список таблиц спрашиваем у базы, а не держим в коде: появится новая
        /// таблица с server_id — она подхватится сама.
        ///
        /// Всё одной транзакцией: сервер без каналов или каналы без сервера
        /// хуже, чем несменённый номер. Если база не даст сменить сам номер
        /// (например, на него есть внешний ключ), не меняется вообще ничего.
        /// </remarks>
        private int ChangeServerId(int serverId)
        {
            try
            {
                using var conn = DBHelper.OpenConnection();

                int fresh = FreeServerId(conn);
                if (fresh == 0) return 0;

                var tables = new List<string>();
                using (var cmd = new MySqlCommand(
                           "SELECT table_name FROM information_schema.columns " +
                           "WHERE table_schema = DATABASE() AND column_name = 'server_id'", conn))
                using (var rd = cmd.ExecuteReader())
                {
                    while (rd.Read())
                    {
                        string t = rd.GetString(0);
                        if (IsPlainIdentifier(t)) tables.Add(t);
                    }
                }

                using var tx = conn.BeginTransaction();
                try
                {
                    using (var cmd = new MySqlCommand(
                               "UPDATE servers SET id=@n WHERE id=@o", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@n", fresh);
                        cmd.Parameters.AddWithValue("@o", serverId);
                        if (cmd.ExecuteNonQuery() <= 0) { tx.Rollback(); return 0; }
                    }
                    foreach (var t in tables)
                    {
                        using var cmd = new MySqlCommand(
                            $"UPDATE {t} SET server_id=@n WHERE server_id=@o", conn, tx);
                        cmd.Parameters.AddWithValue("@n", fresh);
                        cmd.Parameters.AddWithValue("@o", serverId);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                    return fresh;
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    return 0;
                }
            }
            catch (Exception ex) { ShowDbError(ex); return 0; }
        }

        /// <summary>Имя таблицы без кавычек — его подставляем в запрос напрямую.</summary>
        private static bool IsPlainIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }
    }
}
