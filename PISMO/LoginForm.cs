using System;
using System.Data;
using System.IO;
using System.Text;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    public partial class LoginForm : Form
    {
        // Файл хранится рядом с exe, в папке AppData чтобы не мешать установке
        private static readonly string CredsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PISMO", "saved_login.dat");

        public LoginForm()
        {
            InitializeComponent(GetBtnLogin());
            LoadSavedCredentials();
            // Авто-вход по JWT здесь НЕ делаем: он выполняется в заставке
            // (SplashForm) ДО показа окна — иначе форма зависала недорисованной.
        }

        /// <summary>Восстановление сессии по сохранённому JWT (вызывается из
        /// заставки в фоне, БЕЗ UI). true — UserSession заполнена, можно
        /// открывать главное окно, минуя окно входа.</summary>
        public static bool TryRestoreSession()
        {
            try
            {
                if (!File.Exists(CredsFile)) return false;
                var lines = File.ReadAllLines(CredsFile, Encoding.UTF8);
                if (lines.Length < 4 || lines[2] != "1") return false; // нет токена / «запомнить» снято
                string token = lines[3];
                var claims = JwtAuth.Validate(token);
                if (claims == null || claims.Uid <= 0) return false;   // истёк/повреждён

                // Подтягиваем актуальные данные пользователя по uid из токена.
                using var conn = DBHelper.OpenConnection();
                using var cmd = new MySqlCommand("SELECT id, Name, Surname, role, login FROM users WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", claims.Uid);
                var dt = new DataTable();
                new MySqlDataAdapter(cmd).Fill(dt);
                if (dt.Rows.Count == 0) return false;
                var row = dt.Rows[0];
                UserSession.UserId = Convert.ToInt32(row["id"]);
                UserSession.UserName = $"{row["Name"]} {row["Surname"]}".Trim();
                UserSession.Role = row["role"].ToString().ToLower();
                if (string.IsNullOrWhiteSpace(UserSession.UserName))
                    UserSession.UserName = row["login"].ToString();
                UserSession.AuthToken = token;
                return true;
            }
            catch { return false; /* при любой ошибке — обычный вход */ }
        }

        /// <summary>Открыть главное окно после восстановления сессии из заставки
        /// (окно входа при этом остаётся скрытым и появится только при выходе).</summary>
        public void OpenMainAfterRestore() => OpenMainForm();

        /// <summary>Стирает сохранённый JWT (при «Выйти из аккаунта»), чтобы при
        /// следующем запуске не происходил автовход. Логин/пароль остаются.</summary>
        public static void InvalidateSavedToken()
        {
            try
            {
                if (!File.Exists(CredsFile)) return;
                var lines = File.ReadAllLines(CredsFile, Encoding.UTF8);
                if (lines.Length < 4) return;
                lines[3] = "";
                File.WriteAllLines(CredsFile, lines, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Открывает главное окно и прячет окно входа.</summary>
        private void OpenMainForm()
        {
            var main = new MainForm();
            main.Show();
            this.Hide();

            main.FormClosed += (s, _) =>
            {
                if (UserSession.UserId == 0)
                {
                    txtLogin.Clear();
                    txtPass.Clear();
                    lblError.Visible = false;
                    LoadSavedCredentials();
                    this.Show();
                }
                else
                {
                    this.Close();
                }
            };
        }

        // ────────────────────────────────────────────
        //  Загрузка сохранённых данных при старте
        // ────────────────────────────────────────────
        private void LoadSavedCredentials()
        {
            try
            {
                if (!File.Exists(CredsFile)) return;

                string[] lines = File.ReadAllLines(CredsFile, Encoding.UTF8);
                if (lines.Length < 3) return;

                string savedLogin = lines[0];
                string savedPass  = DecodePassword(lines[1]);
                bool   remember   = lines[2] == "1";

                txtLogin.Text    = savedLogin;
                txtPass.Text     = savedPass;
                chkRemember.Checked = remember;
            }
            catch
            {
                // Файл повреждён — просто игнорируем
            }
        }

        /// <summary>Удаляет файл сохранённого входа (когда галочка снята).</summary>
        private void ClearSavedCredentials()
        {
            try { if (File.Exists(CredsFile)) File.Delete(CredsFile); }
            catch { }
        }

        // ────────────────────────────────────────────
        //  Вход
        // ────────────────────────────────────────────
        private void btnLogin_Click(object sender, EventArgs e)
        {
            string login = txtLogin.Text.Trim();
            string pass  = txtPass.Text;

            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(pass))
            {
                ShowError("Заполните логин и пароль.");
                return;
            }

            try
            {
                using (var conn = DBHelper.OpenConnection())
                {
                    // Пароль больше НЕ сверяем в SQL (там мог быть открытый текст).
                    // Берём хеш по логину и проверяем в коде (bcrypt / legacy-plaintext).
                    const string sql =
                        "SELECT id, Name, Surname, role, password FROM users WHERE login=@l";
                    DataRow row;
                    using (var cmd = new MySqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@l", login);
                        var dt = new DataTable();
                        new MySqlDataAdapter(cmd).Fill(dt);

                        if (dt.Rows.Count == 0)
                        {
                            ShowError("Неверный логин или пароль.");
                            return;
                        }
                        row = dt.Rows[0];
                    }

                    string stored = row["password"]?.ToString() ?? "";
                    if (!PasswordHasher.Verify(pass, stored))
                    {
                        ShowError("Неверный логин или пароль.");
                        return;
                    }

                    UserSession.UserId   = Convert.ToInt32(row["id"]);
                    UserSession.UserName = $"{row["Name"]} {row["Surname"]}".Trim();
                    UserSession.Role     = row["role"].ToString().ToLower();
                    if (string.IsNullOrWhiteSpace(UserSession.UserName))
                        UserSession.UserName = login;

                    // Миграция: старый открытый пароль перехешируем в bcrypt.
                    if (PasswordHasher.NeedsUpgrade(stored))
                    {
                        try
                        {
                            using var upd = new MySqlCommand(
                                "UPDATE users SET password=@p WHERE id=@id", conn);
                            upd.Parameters.AddWithValue("@p", PasswordHasher.Hash(pass));
                            upd.Parameters.AddWithValue("@id", UserSession.UserId);
                            upd.ExecuteNonQuery();
                        }
                        catch { /* миграция не критична для входа */ }
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError("Ошибка БД: " + ex.Message);
                return;
            }

            // Выдаём JWT сессии (uid/login/срок) — используется WS-сервером и
            // для авто-входа «Запомнить меня».
            UserSession.AuthToken = JwtAuth.Create(UserSession.UserId, login);

            // «Запомнить меня» полностью управляет сохранением данных входа:
            // отмечена — сохраняем (логин + токен), снята — удаляем сохранённое.
            if (chkRemember.Checked)
                SaveCredentials(login, pass, true);
            else
                ClearSavedCredentials();

            OpenMainForm();
        }

        // ────────────────────────────────────────────
        //  Вспомогательные методы
        // ────────────────────────────────────────────
        private void SaveCredentials(string login, string pass, bool remember)
        {
            try
            {
                string dir = Path.GetDirectoryName(CredsFile);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllLines(CredsFile, new[]
                {
                    login,
                    EncodePassword(pass),
                    remember ? "1" : "0",
                    UserSession.AuthToken ?? ""   // 4-я строка: JWT для авто-входа
                }, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                ShowError("Не удалось сохранить данные: " + ex.Message);
            }
        }

        // Простая обфускация пароля (Base64) — не шифрование,
        // но защищает от случайного прочтения в блокноте.
        private static string EncodePassword(string pass)
            => Convert.ToBase64String(Encoding.UTF8.GetBytes(pass));

        private static string DecodePassword(string encoded)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
            catch { return string.Empty; }
        }

        private void ShowError(string msg)
        {
            lblError.ForeColor = Color.FromArgb(240, 71, 71);
            lblError.Text      = msg;
            lblError.Visible   = true;
        }

        private void lnkRegister_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            new RegisterForm().ShowDialog(this);
        }

        private void txtPass_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) btnLogin_Click(sender, e);
        }

        private void LoginForm_Load(object sender, EventArgs e)
        {
            // Проверка БД — в фоне, чтобы окно отрисовалось сразу (иначе при
            // недоступной БД форма висела «белыми прямоугольниками» до таймаута).
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var conn = DBHelper.OpenConnection();
                    System.Diagnostics.Debug.WriteLine("[TEST] ✓ БД подключена успешно!");
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (!IsDisposed && IsHandleCreated)
                            BeginInvoke(new Action(() =>
                                ShowError("Нет соединения с базой данных: " + ex.Message)));
                    }
                    catch { }
                }
            });
        }
    }
}
