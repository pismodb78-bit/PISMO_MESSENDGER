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
    /// Оформление группы и баны — то же, что у серверов.
    /// </summary>
    /// <remarks>
    /// У группы был только цвет значка, и тот менялся лишь при создании.
    /// А выгнать можно было, не пустить обратно — нет: исключённого тут же
    /// добавляли снова.
    /// </remarks>
    public partial class GroupMembersForm
    {
        /// <summary>
        /// Распоряжается группой её создатель, админ группы (звёздочка) и
        /// системный администратор. Раньше исключать мог только последний —
        /// в своей же группе хозяин был бессилен.
        /// </summary>
        private bool CanManageGroup()
        {
            if (UserSession.Role == "admin") return true;
            int me = UserSession.EffectiveId;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var c = new MySqlCommand(
                           "SELECT created_by FROM group_chats WHERE id=@g", conn))
                {
                    c.Parameters.AddWithValue("@g", _groupId);
                    var o = c.ExecuteScalar();
                    if (o != null && o != DBNull.Value && Convert.ToInt32(o) == me) return true;
                }
                using var a = new MySqlCommand(
                    "SELECT COALESCE(MAX(is_admin),0) FROM group_members " +
                    "WHERE group_id=@g AND user_id=@u", conn);
                a.Parameters.AddWithValue("@g", _groupId);
                a.Parameters.AddWithValue("@u", me);
                return Convert.ToInt32(a.ExecuteScalar() ?? 0) != 0;
            }
            catch { return false; }
        }

        /// <summary>Сколько человек забанено в этой группе.</summary>
        private int BannedCount()
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM group_bans WHERE group_id=@g", conn);
                cmd.Parameters.AddWithValue("@g", _groupId);
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            catch { return 0; }   // таблицы ещё нет — миграцию кладёт администратор
        }

        /// <summary>Бан: выгнать и не пускать обратно.</summary>
        private void BanMember(int uid, string name)
        {
            if (uid == UserSession.EffectiveId)
            {
                MessageBox.Show("Себя банить незачем — есть «Покинуть группу».");
                return;
            }
            if (MessageBox.Show(
                    $"Забанить «{name}» в группе «{_groupName}»?\n\n" +
                    "Он выйдет из группы, и добавить его обратно будет нельзя, " +
                    "пока бан не снимут.",
                    "PISMO", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var b = new MySqlCommand(
                           "INSERT IGNORE INTO group_bans (group_id,user_id) VALUES (@g,@u)", conn))
                {
                    b.Parameters.AddWithValue("@g", _groupId);
                    b.Parameters.AddWithValue("@u", uid);
                    b.ExecuteNonQuery();
                }
                using var d = new MySqlCommand(
                    "DELETE FROM group_members WHERE group_id=@g AND user_id=@u", conn);
                d.Parameters.AddWithValue("@g", _groupId);
                d.Parameters.AddWithValue("@u", uid);
                d.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось забанить: " + ex.Message + "\n\n" +
                                "Если жалуется на group_bans — нужна миграция " +
                                "(sql/2026-10-12_group_avatar_bans.sql).");
                return;
            }

            AnnounceKick(uid, true);
            AnnounceMembers();
            Changed = true;
            LoadMembers();
        }

        /// <summary>
        /// Говорит выгнанному, что его выгнали. Без этого он узнавал бы об
        /// этом только при следующей загрузке списка чатов — то есть у
        /// открытого приложения никогда.
        /// </summary>
        private void AnnounceKick(int uid, bool ban)
        {
            try
            {
                WebSocketSignalingClient.Instance.SendMessage(
                    "group_kick", uid, _groupId, ban ? "ban" : "kick");
            }
            catch { }
        }

        /// <summary>
        /// Говорит остальным, что состав группы изменился.
        /// </summary>
        /// <remarks>
        /// Широковещательно: кто сейчас смотрит участников, клиент не знает.
        /// Адресное group_kick доходит только до выгнанного, а окно
        /// участников открыто у других — и до этого события оно обновлялось
        /// лишь при повторном открытии.
        /// </remarks>
        private void AnnounceMembers()
        {
            try
            {
                WebSocketSignalingClient.Instance.SendMessage(
                    "group_members", 0, _groupId, "members");
            }
            catch { }
        }

        /// <summary>
        /// Подписка на события о группе: состав и вид меняются у других, и
        /// окно обязано это показать, а не висеть со старым списком.
        /// </summary>
        private void HookGroupEvents()
        {
            _onWs = (type, senderId, sessionId, payload) =>
            {
                if (sessionId != _groupId) return;
                if (type != "group_members" && type != "group_look") return;
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(LoadMembers));
                }
                catch { }
            };
            WebSocketSignalingClient.Instance.OnMessageReceived += _onWs;
            FormClosed += (s, e) =>
            {
                try { WebSocketSignalingClient.Instance.OnMessageReceived -= _onWs; } catch { }
            };
        }

        private Action<string, int, int, string> _onWs;

        /// <summary>Говорит остальным, что группа сменила вид.</summary>
        private void AnnounceLook()
        {
            try
            {
                WebSocketSignalingClient.Instance.SendMessage("group_look", 0, _groupId, "look");
            }
            catch { }
        }

        /// <summary>Окно забаненных: кто забанен и снятие бана.</summary>
        private void ManageBans()
        {
            using var f = new Form
            {
                Text = "Забаненные в группе",
                ClientSize = new Size(420, 400),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(47, 49, 54),
                MaximizeBox = false,
                MinimizeBox = false
            };

            var list = new ListBox
            {
                Location = new Point(12, 12),
                Size = new Size(396, 300),
                BackColor = Color.FromArgb(40, 42, 46),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var ids = new List<int>();

            void Reload()
            {
                list.Items.Clear();
                ids.Clear();
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand(
                        "SELECT b.user_id, TRIM(CONCAT(u.Name,' ',u.Surname)) AS nm, u.login " +
                        "FROM group_bans b JOIN users u ON u.id=b.user_id " +
                        "WHERE b.group_id=@g ORDER BY u.login", conn);
                    cmd.Parameters.AddWithValue("@g", _groupId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string nm = r["nm"].ToString()?.Trim() ?? "";
                        string login = r["login"].ToString() ?? "";
                        if (nm.Length == 0) nm = login;
                        ids.Add(Convert.ToInt32(r["user_id"]));
                        list.Items.Add($"{nm}  @{login}");
                    }
                }
                catch { /* таблицы нет — список просто пуст */ }

                if (list.Items.Count == 0) list.Items.Add("— забаненных нет —");
            }
            Reload();

            var btnUnban = MakeButton("Разбанить", new Point(12, 324), new Size(140, 30));
            btnUnban.Click += (s, e) =>
            {
                int i = list.SelectedIndex;
                if (i < 0 || i >= ids.Count) return;
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    using var cmd = new MySqlCommand(
                        "DELETE FROM group_bans WHERE group_id=@g AND user_id=@u", conn);
                    cmd.Parameters.AddWithValue("@g", _groupId);
                    cmd.Parameters.AddWithValue("@u", ids[i]);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex) { MessageBox.Show("Ошибка: " + ex.Message); return; }

                // Вернуть человека в группу всё равно придётся вручную —
                // бан снят, значит, добавить его снова теперь можно.
                AnnounceMembers();
                Reload();
                LoadMembers();
            };

            var btnClose = MakeButton("Закрыть", new Point(326, 324), new Size(82, 30));
            btnClose.DialogResult = DialogResult.Cancel;

            f.Controls.AddRange(new Control[] { list, btnUnban, btnClose });
            f.CancelButton = btnClose;
            f.ShowDialog(this);
        }

        /// <summary>
        /// Оформление группы: название, картинка и цвет значка.
        /// </summary>
        /// <remarks>
        /// Как и у серверов, до «Сохранить» в базу не пишется ничего:
        /// выбранная картинка живёт в окне и видна в предпросмотре, «Отмена»
        /// отменяет её вместе с остальным.
        /// </remarks>
        private void EditLook()
        {
            string nameWas = _groupName;
            string colorWas = ReadGroupColor();
            string color = colorWas;
            bool hasPicture = GroupHasAvatar();

            byte[]? pendingPng = null;
            Image? pendingImage = null;
            bool dropPicture = false;

            using var f = new Form
            {
                Text = "Оформление группы",
                ClientSize = new Size(430, 330),
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
                Location = new Point(12, 34), Size = new Size(406, 24), Text = nameWas,
                BackColor = Color.FromArgb(40, 42, 46), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

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
                var back = Look.ParseColor(color) ?? Color.FromArgb(88, 101, 242);
                using (var br = new SolidBrush(back)) g.FillEllipse(br, rect);

                Image? img = pendingImage;
                if (img == null && hasPicture && !dropPicture) img = GroupLook.Cached(_groupId);
                if (img != null)
                {
                    var st = g.Save();
                    using var clip = new GraphicsPath();
                    clip.AddEllipse(rect);
                    g.SetClip(clip);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, rect);
                    g.Restore(st);
                }
                else
                {
                    string ini = (txtN.Text ?? "").Trim();
                    ini = ini.Length > 0 ? ini.Substring(0, 1).ToUpperInvariant() : "#";
                    using var fnt = new Font("Segoe UI Black", 18f, FontStyle.Bold);
                    TextRenderer.DrawText(g, ini, fnt, rect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            };
            txtN.TextChanged += (s, e) => preview.Invalidate();

            var btnPick = MakeButton(hasPicture ? "🖼 Сменить картинку" : "🖼 Загрузить картинку",
                new Point(88, 76), new Size(170, 28));
            var btnDrop = MakeButton("Убрать картинку", new Point(264, 76), new Size(154, 28));
            btnDrop.Visible = hasPicture;

            btnPick.Click += (s, e) =>
            {
                var png = PickPicture(f);
                if (png == null) return;
                pendingPng = png;
                pendingImage = Look.Decode(png);
                dropPicture = false;
                btnPick.Text = "🖼 Сменить картинку";
                btnDrop.Visible = true;
                preview.Invalidate();
            };
            btnDrop.Click += (s, e) =>
            {
                pendingPng = null;
                pendingImage = null;
                dropPicture = true;
                btnPick.Text = "🖼 Загрузить картинку";
                btnDrop.Visible = false;
                preview.Invalidate();
            };

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

            // Та же палитра, что у ролей и серверов: цвет лежит в базе строкой
            // и рисуется обоими клиентами, наборы обязаны совпадать.
            string[] palette =
            {
                "#1ABC9C", "#2ECC71", "#3498DB", "#9B59B6", "#E91E63", "#F1C40F",
                "#E67E22", "#E74C3C", "#95A5A6", "#607D8B", "#3BA55D", "#5865F2",
            };
            var swatches = new List<Panel>();
            int px = 12, py = 180;
            foreach (var hex in palette)
            {
                var sw = new Panel
                {
                    Location = new Point(px, py), Size = new Size(26, 26),
                    BackColor = ColorTranslator.FromHtml(hex),
                    Cursor = Cursors.Hand, BorderStyle = BorderStyle.FixedSingle
                };
                string hexCap = hex;
                sw.Click += (s, e) => { txtC.Text = hexCap; };
                swatches.Add(sw);
                px += 32;
                if (px + 26 > 418) { px = 12; py += 32; }
            }

            var btnMore = MakeButton("🎨 Ещё…", new Point(12, py + 36), new Size(100, 26));
            btnMore.Click += (s, e) =>
            {
                using var cd = new ColorDialog { FullOpen = true };
                try { cd.Color = ColorTranslator.FromHtml(txtC.Text.Trim()); } catch { }
                if (cd.ShowDialog(f) == DialogResult.OK)
                    txtC.Text = $"#{cd.Color.R:X2}{cd.Color.G:X2}{cd.Color.B:X2}";
            };

            var ok = MakeButton("Сохранить", new Point(236, py + 76), new Size(94, 30));
            ok.DialogResult = DialogResult.OK;
            var cancel = MakeButton("Отмена", new Point(336, py + 76), new Size(82, 30));
            cancel.DialogResult = DialogResult.Cancel;

            f.Controls.AddRange(new Control[]
            {
                lblN, txtN, preview, btnPick, btnDrop, lblC, txtC, btnMore, ok, cancel
            });
            foreach (var sw in swatches) f.Controls.Add(sw);
            f.AcceptButton = ok;
            f.CancelButton = cancel;

            if (f.ShowDialog(this) != DialogResult.OK) return;

            string nameNow = (txtN.Text ?? "").Trim();
            string colorNow = (txtC.Text ?? "").Trim();
            bool renamed = nameNow.Length > 0 && nameNow != nameWas;
            bool recolored = colorNow != colorWas;
            bool repictured = pendingPng != null || dropPicture;
            if (!renamed && !recolored && !repictured) return;

            try
            {
                using var conn = DBHelper.OpenConnection();
                if (renamed)
                {
                    using var c = new MySqlCommand("UPDATE group_chats SET name=@n WHERE id=@g", conn);
                    c.Parameters.AddWithValue("@n", nameNow);
                    c.Parameters.AddWithValue("@g", _groupId);
                    c.ExecuteNonQuery();
                }
                if (recolored)
                {
                    using var c = new MySqlCommand("UPDATE group_chats SET avatar_color=@c WHERE id=@g", conn);
                    c.Parameters.AddWithValue("@c", colorNow);
                    c.Parameters.AddWithValue("@g", _groupId);
                    c.ExecuteNonQuery();
                }
                if (repictured)
                {
                    using var c = new MySqlCommand("UPDATE group_chats SET avatar_data=@a WHERE id=@g", conn);
                    c.Parameters.AddWithValue("@a", (object?)pendingPng ?? DBNull.Value);
                    c.Parameters.AddWithValue("@g", _groupId);
                    c.ExecuteNonQuery();
                    GroupLook.Remember(_groupId,
                        pendingPng == null ? null : (pendingImage ?? Look.Decode(pendingPng)));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось сохранить: " + ex.Message + "\n\n" +
                                "Если жалуется на avatar_data — нужна миграция " +
                                "(sql/2026-10-12_group_avatar_bans.sql).");
                return;
            }

            AnnounceLook();
            Changed = true;
            if (renamed) _groupName = nameNow;
            LoadMembers();
            try { MainForm.Current?.ReloadChatList(); } catch { }
        }

        private string ReadGroupColor()
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT avatar_color FROM group_chats WHERE id=@g", conn);
                cmd.Parameters.AddWithValue("@g", _groupId);
                var o = cmd.ExecuteScalar();
                return o == null || o == DBNull.Value ? "" : o.ToString() ?? "";
            }
            catch { return ""; }
        }

        private bool GroupHasAvatar()
        {
            try
            {
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand(
                    "SELECT avatar_data IS NOT NULL FROM group_chats WHERE id=@g", conn);
                cmd.Parameters.AddWithValue("@g", _groupId);
                var o = cmd.ExecuteScalar();
                return o != null && o != DBNull.Value && Convert.ToInt32(o) != 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Выбор файла значка: читаем байтами и разбираем через WIC при нужде
        /// — картинка могла прийти с телефона, а там это мог быть и WebP.
        /// </summary>
        private byte[]? PickPicture(IWin32Window owner)
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Значок группы",
                Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.heic;*.avif|Все файлы|*.*"
            };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
            try
            {
                var raw = File.ReadAllBytes(dlg.FileName);
                using var src = Look.Decode(raw);
                if (src == null)
                {
                    MessageBox.Show("Не удалось открыть это изображение.", "PISMO");
                    return null;
                }
                using var crop = new AvatarCropForm(src);
                if (crop.ShowDialog(owner) == DialogResult.OK && crop.ResultPng != null)
                    return crop.ResultPng;
                return null;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка изображения: " + ex.Message, "PISMO");
                return null;
            }
        }

        private static Button MakeButton(string text, Point at, Size size)
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
    }
}
