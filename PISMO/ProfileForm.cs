using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>Редактирование профиля: аватар (с обрезкой кружком), баннер-фон,
    /// имя, логин (с проверкой занятости), «о себе», ссылки и смена пароля.</summary>
    public sealed class ProfileForm : Form
    {
        private readonly int _uid;
        private byte[] _newAvatar;   // если пользователь выбрал новый
        private byte[] _newBanner;
        private Image _bannerImg;
        private bool _bannerChanged;

        private readonly Panel _banner;
        private readonly Panel _avatar;
        private readonly TextBox _txtName, _txtSurname, _txtLogin, _txtAbout, _txtLinks;
        private readonly Label _lblLoginStatus;
        private readonly TextBox _txtOldPass, _txtNewPass, _txtNewPass2;
        private readonly Label _lblStatus;

        public bool Saved { get; private set; }

        public ProfileForm(int uid)
        {
            _uid = uid;
            Text = "PISMO — Профиль";
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(47, 49, 54);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 680);
            Font = new Font("Segoe UI", 9.5f);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(47, 49, 54) };
            Controls.Add(scroll);

            // ── Баннер (фон) ────────────────────────────────────────────
            _banner = new Panel { Location = new Point(0, 0), Size = new Size(560, 150), BackColor = Color.FromArgb(59, 165, 93), Cursor = Cursors.Hand };
            _banner.Paint += BannerPaint;
            _banner.Click += (s, e) => ChangeBanner();
            var bannerHint = new Label { Text = "📷 Сменить фон", AutoSize = true, ForeColor = Color.White, BackColor = Color.FromArgb(120, 0, 0, 0), Font = new Font("Segoe UI", 8f), Location = new Point(440, 8), Padding = new Padding(4, 2, 4, 2) };
            bannerHint.Click += (s, e) => ChangeBanner();
            _banner.Controls.Add(bannerHint);
            scroll.Controls.Add(_banner);

            // ── Аватар (с обрезкой кружком) ─────────────────────────────
            _avatar = new Panel { Location = new Point(24, 96), Size = new Size(104, 104), BackColor = Color.Transparent, Cursor = Cursors.Hand };
            _avatar.Paint += AvatarPaint;
            _avatar.Click += (s, e) => ChangeAvatar();
            scroll.Controls.Add(_avatar);
            _avatar.BringToFront();

            int y = 214;
            Label Hint(string t, int yy)
            {
                var l = new Label { Text = t.ToUpper(), Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold), ForeColor = Color.FromArgb(160, 162, 168), AutoSize = true, Location = new Point(24, yy) };
                scroll.Controls.Add(l); return l;
            }
            TextBox Box(int yy, int h = 28, bool multiline = false)
            {
                var tb = new TextBox { Location = new Point(24, yy), Size = new Size(512, h), BackColor = Color.FromArgb(32, 34, 37), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10.5f), Multiline = multiline };
                scroll.Controls.Add(tb); return tb;
            }

            Hint("Имя", y); _txtName = Box(y + 16); y += 56;
            Hint("Фамилия", y); _txtSurname = Box(y + 16); y += 56;

            Hint("Логин", y); _txtLogin = Box(y + 16);
            _lblLoginStatus = new Label { AutoSize = true, Font = new Font("Segoe UI", 8f), ForeColor = Color.FromArgb(150, 152, 158), Location = new Point(360, y - 2) };
            scroll.Controls.Add(_lblLoginStatus);
            y += 56;

            Hint("О себе", y); _txtAbout = Box(y + 16, 70, true); y += 98;

            Hint("Ссылки (по строке: Название|https://...)", y); _txtLinks = Box(y + 16, 70, true); y += 98;

            // ── Смена пароля ────────────────────────────────────────────
            Hint("Смена пароля (необязательно)", y); y += 22;
            _txtOldPass = Box(y); _txtOldPass.UseSystemPasswordChar = true; _txtOldPass.PlaceholderText = "Текущий пароль"; y += 36;
            _txtNewPass = Box(y); _txtNewPass.UseSystemPasswordChar = true; _txtNewPass.PlaceholderText = "Новый пароль"; y += 36;
            _txtNewPass2 = Box(y); _txtNewPass2.UseSystemPasswordChar = true; _txtNewPass2.PlaceholderText = "Повторите новый пароль"; y += 44;

            _lblStatus = new Label { AutoSize = true, Font = new Font("Segoe UI", 9f), ForeColor = Color.FromArgb(240, 71, 71), Location = new Point(24, y), MaximumSize = new Size(512, 0) };
            scroll.Controls.Add(_lblStatus); y += 28;

            var btnSave = new Button { Text = "Сохранить", BackColor = Color.FromArgb(88, 101, 242), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold), Location = new Point(24, y), Size = new Size(512, 44), Cursor = Cursors.Hand };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += (s, e) => DoSave();
            scroll.Controls.Add(btnSave);

            // Проверка логина «на лету».
            _txtLogin.TextChanged += (s, e) => CheckLogin();

            LoadData();
        }

        private void LoadData()
        {
            var p = ProfileRepository.Load(_uid);
            _txtName.Text = p.Name;
            _txtSurname.Text = p.Surname;
            _txtLogin.Text = p.Login;
            _txtAbout.Text = p.About;
            _txtLinks.Text = p.SocialLinks;
            AvatarStore.EnsureLoaded(_uid);
            try { var b = ProfileRepository.LoadBanner(_uid); if (b != null && b.Length > 0) { _bannerImg = Image.FromStream(new MemoryStream(b)); _banner.Invalidate(); } } catch { }
            AvatarStore.AvatarLoaded += OnAvatarLoaded;
        }

        private void OnAvatarLoaded(int uid)
        {
            if (uid == _uid && !IsDisposed && IsHandleCreated)
                try { BeginInvoke(new Action(() => _avatar.Invalidate())); } catch { }
        }

        private void BannerPaint(object sender, PaintEventArgs e)
        {
            if (_bannerImg != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // cover-заполнение
                float r = Math.Max((float)_banner.Width / _bannerImg.Width, (float)_banner.Height / _bannerImg.Height);
                float w = _bannerImg.Width * r, h = _bannerImg.Height * r;
                e.Graphics.DrawImage(_bannerImg, (_banner.Width - w) / 2, (_banner.Height - h) / 2, w, h);
            }
        }

        private void AvatarPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int size = _avatar.Width - 8;
            int x = 4, y = 4;
            // обводка-«подложка» цветом формы
            using (var bg = new SolidBrush(Color.FromArgb(47, 49, 54)))
                g.FillEllipse(bg, 0, 0, _avatar.Width - 1, _avatar.Height - 1);

            Image av = null;
            if (_newAvatar != null) { try { av = Image.FromStream(new MemoryStream(_newAvatar)); } catch { } }
            else av = AvatarStore.Get(_uid);

            using var path = new GraphicsPath();
            path.AddEllipse(x, y, size, size);
            var oldClip = g.Clip; g.SetClip(path);
            if (av != null) g.DrawImage(av, x, y, size, size);
            else
            {
                using var br = new SolidBrush(Color.FromArgb(88, 101, 242));
                g.FillEllipse(br, x, y, size, size);
            }
            g.Clip = oldClip;
            if (_newAvatar != null) av?.Dispose();

            // иконка «изменить»
            using var camBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            g.FillEllipse(camBg, _avatar.Width - 30, _avatar.Height - 30, 26, 26);
            using var f = new Font("Segoe UI", 10f);
            g.DrawString("📷", f, Brushes.White, _avatar.Width - 30, _avatar.Height - 30);
        }

        private void ChangeAvatar()
        {
            using var dlg = new OpenFileDialog { Title = "Аватар", Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using var src = Image.FromFile(dlg.FileName);
                using var crop = new AvatarCropForm(src);
                if (crop.ShowDialog(this) == DialogResult.OK && crop.ResultPng != null)
                {
                    _newAvatar = crop.ResultPng;
                    _avatar.Invalidate();
                }
            }
            catch (Exception ex) { _lblStatus.Text = "Ошибка изображения: " + ex.Message; }
        }

        private void ChangeBanner()
        {
            using var dlg = new OpenFileDialog { Title = "Фон профиля", Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using (var src = (Bitmap)Image.FromFile(dlg.FileName))
                {
                    // Ужимаем до ширины ~1024, чтобы не раздувать БД.
                    int tw = Math.Min(1024, src.Width);
                    int th = (int)(src.Height * (tw / (double)src.Width));
                    using var dst = new Bitmap(tw, th);
                    using (var g = Graphics.FromImage(dst)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, tw, th); }
                    using var ms = new MemoryStream();
                    dst.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                    _newBanner = ms.ToArray();
                }
                _bannerImg?.Dispose();
                _bannerImg = Image.FromStream(new MemoryStream(_newBanner));
                _bannerChanged = true;
                _banner.Invalidate();
            }
            catch (Exception ex) { _lblStatus.Text = "Ошибка фона: " + ex.Message; }
        }

        private void CheckLogin()
        {
            string login = _txtLogin.Text.Trim();
            if (string.IsNullOrWhiteSpace(login)) { _lblLoginStatus.Text = ""; return; }
            bool free = ProfileRepository.IsLoginAvailable(login, _uid);
            _lblLoginStatus.Text = free ? "✓ свободен" : "✗ занят";
            _lblLoginStatus.ForeColor = free ? Color.FromArgb(87, 171, 90) : Color.FromArgb(240, 71, 71);
        }

        private void DoSave()
        {
            _lblStatus.ForeColor = Color.FromArgb(240, 71, 71);
            string login = _txtLogin.Text.Trim();
            if (string.IsNullOrWhiteSpace(login)) { _lblStatus.Text = "Логин не может быть пустым."; return; }
            if (!ProfileRepository.IsLoginAvailable(login, _uid)) { _lblStatus.Text = "Этот логин уже занят."; return; }

            // Смена пароля (если заполнено).
            if (_txtNewPass.Text.Length > 0 || _txtOldPass.Text.Length > 0)
            {
                if (_txtNewPass.Text != _txtNewPass2.Text) { _lblStatus.Text = "Новые пароли не совпадают."; return; }
                if (_txtNewPass.Text.Length < 3) { _lblStatus.Text = "Новый пароль слишком короткий."; return; }
                string perr = ProfileRepository.ChangePassword(_uid, _txtOldPass.Text, _txtNewPass.Text);
                if (perr != null) { _lblStatus.Text = perr; return; }
            }

            var p = new ProfileData
            {
                Id = _uid,
                Name = _txtName.Text.Trim(),
                Surname = _txtSurname.Text.Trim(),
                Login = login,
                About = _txtAbout.Text.Trim(),
                SocialLinks = _txtLinks.Text.Trim()
            };
            string err = ProfileRepository.Save(p);

            if (_newAvatar != null) AvatarStore.SaveMyAvatar(_uid, _newAvatar);
            if (_bannerChanged) ProfileRepository.SaveBanner(_uid, _newBanner);

            // Обновляем сессию (имя в шапке).
            try
            {
                string full = (p.Name + " " + p.Surname).Trim();
                if (string.IsNullOrWhiteSpace(full)) full = p.Login;
                UserSession.UserName = full;
            }
            catch { }

            Saved = true;
            if (err != null)
                MessageBox.Show("Сохранено. " + err, "PISMO", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { AvatarStore.AvatarLoaded -= OnAvatarLoaded; } catch { }
            try { _bannerImg?.Dispose(); } catch { }
            base.OnFormClosed(e);
        }
    }
}
