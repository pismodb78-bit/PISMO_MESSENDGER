using System;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    public partial class RegisterForm : Form
    {
        public RegisterForm()
        {
            InitializeComponent();
            this.Load += (s, e) => { try { Theme.Apply(this); } catch { } };
        }

        private void btnRegister_Click(object sender, EventArgs e)
        {
            string name    = txtName.Text.Trim();
            string surname = txtSurname.Text.Trim();
            string login   = txtLogin.Text.Trim();
            string pass    = txtPass.Text;

            if (string.IsNullOrWhiteSpace(name)   ||
                string.IsNullOrWhiteSpace(surname) ||
                string.IsNullOrWhiteSpace(login)   ||
                string.IsNullOrWhiteSpace(pass))
            {
                ShowError("Заполните все поля!");
                return;
            }
            if (login.ToLower() == "admin")
            {
                ShowError("Этот логин зарезервирован.");
                return;
            }
            if (pass.Length < 8)
            {
                ShowError("Пароль минимум 8 символов.");
                return;
            }
            if (pass == "12345678" || pass == "87654321")
            {
                ShowError("Пароль слишком предсказуем!");
                return;
            }

            try
            {
                using (var conn = DBHelper.OpenConnection())
                {
                    using (var chk = new MySqlCommand(
                        "SELECT COUNT(*) FROM users WHERE login=@l", conn))
                    {
                        chk.Parameters.AddWithValue("@l", login);
                        if (Convert.ToInt32(chk.ExecuteScalar()) > 0)
                        {
                            ShowError("Этот логин уже занят.");
                            return;
                        }
                    }

                    const string sql =
                        "INSERT INTO users (login, password, Name, Surname, role) " +
                        "VALUES (@l, @p, @n, @s, 'teacher')";
                    using (var cmd = new MySqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@l", login);
                        cmd.Parameters.AddWithValue("@p", PasswordHasher.Hash(pass)); // bcrypt, не открытым текстом
                        cmd.Parameters.AddWithValue("@n", name);
                        cmd.Parameters.AddWithValue("@s", surname);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError("Ошибка БД: " + ex.Message);
                return;
            }

            MessageBox.Show("Аккаунт создан! Теперь войдите.", "PISMO",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            this.Close();
        }

        private void ShowError(string msg)
        {
            lblError.Text    = msg;
            lblError.Visible = true;
        }

        private void lnkBack_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
            => this.Close();
    }
}
