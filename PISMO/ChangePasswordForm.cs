using System;
using System.Linq;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    public partial class ChangePasswordForm : Form
    {
        public ChangePasswordForm()
        {
            InitializeComponent();
        }

        private void btnUpdate_Click(object sender, EventArgs e)
        {
            string oldP  = txtOldPass.Text;
            string newP  = txtNewPass.Text;
            string confP = txtConfirmPass.Text;

            if (newP.Length < 8)
            {
                ShowError("Пароль минимум 8 символов!");
                return;
            }
            if (newP == "12345678" || newP == "87654321")
            {
                ShowError("Пароль слишком предсказуем!");
                return;
            }
            if (string.IsNullOrWhiteSpace(newP) || newP.All(c => c == ' '))
            {
                ShowError("Пароль не может состоять из пробелов!");
                return;
            }
            if (newP != confP)
            {
                ShowError("Пароли не совпадают!");
                return;
            }

            try
            {
                using (var conn = DBHelper.OpenConnection())
                {
                    using (var chk = new MySqlCommand(
                        "SELECT COUNT(*) FROM users WHERE id=@uid AND password=@old", conn))
                    {
                        chk.Parameters.AddWithValue("@uid", UserSession.UserId);
                        chk.Parameters.AddWithValue("@old", oldP);
                        if (Convert.ToInt32(chk.ExecuteScalar()) == 0)
                        {
                            ShowError("Старый пароль неверен!");
                            return;
                        }
                    }

                    using (var upd = new MySqlCommand(
                        "UPDATE users SET password=@new WHERE id=@uid", conn))
                    {
                        upd.Parameters.AddWithValue("@new", newP);
                        upd.Parameters.AddWithValue("@uid", UserSession.UserId);
                        upd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError("Ошибка БД: " + ex.Message);
                return;
            }

            MessageBox.Show("Пароль успешно изменён!", "PISMO",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            this.Close();
        }

        private void ShowError(string msg)
        {
            lblError.Text    = msg;
            lblError.Visible = true;
        }
    }
}
