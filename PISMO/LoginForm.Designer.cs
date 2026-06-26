namespace PISMO
{
    partial class LoginForm
    {
        private System.ComponentModel.IContainer components = null;

        private Panel     pnlCard;
        private Label     lblAppName;
        private Label     lblWelcome;
        private Label     lblLoginHint;
        private TextBox   txtLogin;
        private Label     lblPassHint;
        private TextBox   txtPass;
        private Label     lblError;
        private Button    btnLogin;
        private LinkLabel lnkRegister;
        private CheckBox  chkRemember;
        private Button    btnSaveCreds;
        private Button    btnClearCreds;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private Button GetBtnLogin()
        {
            return btnLogin;
        }

        private void InitializeComponent(Button btnLogin)
        {
            pnlCard      = new Panel();
            lblAppName   = new Label();
            lblWelcome   = new Label();
            lblLoginHint = new Label();
            txtLogin     = new TextBox();
            lblPassHint  = new Label();
            txtPass      = new TextBox();
            lblError     = new Label();
            btnLogin     = new Button();
            lnkRegister  = new LinkLabel();
            chkRemember  = new CheckBox();
            btnSaveCreds = new Button();
            btnClearCreds = new Button();

            pnlCard.SuspendLayout();
            SuspendLayout();

            // Form
            BackColor       = Color.FromArgb(54, 57, 63);
            ClientSize      = new Size(480, 610);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Text            = "PISMO — Войти";
            Font            = new Font("Segoe UI", 9.5f);

            // pnlCard
            pnlCard.BackColor = Color.FromArgb(47, 49, 54);
            pnlCard.Size      = new Size(400, 520);
            pnlCard.Location  = new Point(40, 45);
            pnlCard.Controls.AddRange(new Control[]
            {
                lblAppName, lblWelcome,
                lblLoginHint, txtLogin,
                lblPassHint, txtPass,
                lblError, chkRemember,
                btnSaveCreds, btnClearCreds,
                btnLogin, lnkRegister
            });

            // App name
            lblAppName.Text      = "PISMO";
            lblAppName.Font      = new Font("Segoe UI Black", 26f, FontStyle.Bold);
            lblAppName.ForeColor = Color.FromArgb(88, 101, 242);
            lblAppName.AutoSize  = true;
            lblAppName.Location  = new Point(145, 30);

            // Subtitle
            lblWelcome.Text      = "Рады видеть тебя снова!";
            lblWelcome.Font      = new Font("Segoe UI", 11f);
            lblWelcome.ForeColor = Color.FromArgb(185, 187, 190);
            lblWelcome.AutoSize  = true;
            lblWelcome.Location  = new Point(105, 84);

            // Login hint
            lblLoginHint.Text      = "ЛОГИН";
            lblLoginHint.Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
            lblLoginHint.ForeColor = Color.FromArgb(185, 187, 190);
            lblLoginHint.AutoSize  = true;
            lblLoginHint.Location  = new Point(30, 140);

            // txtLogin
            txtLogin.BackColor   = Color.FromArgb(32, 34, 37);
            txtLogin.ForeColor   = Color.FromArgb(220, 221, 222);
            txtLogin.BorderStyle = BorderStyle.FixedSingle;
            txtLogin.Font        = new Font("Segoe UI", 11f);
            txtLogin.Location    = new Point(30, 160);
            txtLogin.Size        = new Size(340, 36);

            // Pass hint
            lblPassHint.Text      = "ПАРОЛЬ";
            lblPassHint.Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
            lblPassHint.ForeColor = Color.FromArgb(185, 187, 190);
            lblPassHint.AutoSize  = true;
            lblPassHint.Location  = new Point(30, 215);

            // txtPass
            txtPass.BackColor             = Color.FromArgb(32, 34, 37);
            txtPass.ForeColor             = Color.FromArgb(220, 221, 222);
            txtPass.BorderStyle           = BorderStyle.FixedSingle;
            txtPass.Font                  = new Font("Segoe UI", 11f);
            txtPass.Location              = new Point(30, 235);
            txtPass.Size                  = new Size(340, 36);
            txtPass.UseSystemPasswordChar = true;
            txtPass.KeyDown              += txtPass_KeyDown;

            // Error
            lblError.ForeColor = Color.FromArgb(240, 71, 71);
            lblError.Font      = new Font("Segoe UI", 9f);
            lblError.Location  = new Point(30, 282);
            lblError.Size      = new Size(340, 20);
            lblError.Visible   = false;

            // chkRemember — галочка "Запомнить меня"
            chkRemember.Text      = "Запомнить меня";
            chkRemember.Font      = new Font("Segoe UI", 9.5f);
            chkRemember.ForeColor = Color.FromArgb(185, 187, 190);
            chkRemember.Location  = new Point(30, 308);
            chkRemember.AutoSize  = true;
            chkRemember.Cursor    = Cursors.Hand;

            // btnSaveCreds — сохранить данные вручную
            btnSaveCreds.Text      = "💾 Сохранить";
            btnSaveCreds.BackColor = Color.FromArgb(67, 181, 129);
            btnSaveCreds.ForeColor = Color.White;
            btnSaveCreds.FlatStyle = FlatStyle.Flat;
            btnSaveCreds.FlatAppearance.BorderSize = 0;
            btnSaveCreds.Font      = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            btnSaveCreds.Location  = new Point(30, 340);
            btnSaveCreds.Size      = new Size(160, 36);
            btnSaveCreds.Cursor    = Cursors.Hand;
            btnSaveCreds.Click    += btnSaveCreds_Click;
            btnSaveCreds.MouseEnter += (s, e) => btnSaveCreds.BackColor = Color.FromArgb(50, 160, 110);
            btnSaveCreds.MouseLeave += (s, e) => btnSaveCreds.BackColor = Color.FromArgb(67, 181, 129);

            // btnClearCreds — очистить сохранённые данные
            btnClearCreds.Text      = "🗑 Очистить";
            btnClearCreds.BackColor = Color.FromArgb(240, 71, 71);
            btnClearCreds.ForeColor = Color.White;
            btnClearCreds.FlatStyle = FlatStyle.Flat;
            btnClearCreds.FlatAppearance.BorderSize = 0;
            btnClearCreds.Font      = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            btnClearCreds.Location  = new Point(210, 340);
            btnClearCreds.Size      = new Size(160, 36);
            btnClearCreds.Cursor    = Cursors.Hand;
            btnClearCreds.Click    += btnClearCreds_Click;
            btnClearCreds.MouseEnter += (s, e) => btnClearCreds.BackColor = Color.FromArgb(200, 50, 50);
            btnClearCreds.MouseLeave += (s, e) => btnClearCreds.BackColor = Color.FromArgb(240, 71, 71);

            // btnLogin
            btnLogin.Text      = "Войти";
            btnLogin.BackColor = Color.FromArgb(88, 101, 242);
            btnLogin.ForeColor = Color.White;
            btnLogin.FlatStyle = FlatStyle.Flat;
            btnLogin.FlatAppearance.BorderSize = 0;
            btnLogin.Font      = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
            btnLogin.Location  = new Point(30, 395);
            btnLogin.Size      = new Size(340, 44);
            btnLogin.Cursor    = Cursors.Hand;
            btnLogin.Click    += btnLogin_Click;
            btnLogin.MouseEnter += (s, e) => btnLogin.BackColor = Color.FromArgb(71, 82, 196);
            btnLogin.MouseLeave += (s, e) => btnLogin.BackColor = Color.FromArgb(88, 101, 242);

            // Register link
            lnkRegister.Text            = "Нет аккаунта?  Зарегистрироваться →";
            lnkRegister.Font            = new Font("Segoe UI", 9.5f);
            lnkRegister.LinkColor       = Color.FromArgb(0, 176, 244);
            lnkRegister.ActiveLinkColor = Color.White;
            lnkRegister.Location        = new Point(80, 458);
            lnkRegister.AutoSize        = true;
            lnkRegister.LinkClicked    += lnkRegister_LinkClicked;

            Controls.Add(pnlCard);
            pnlCard.ResumeLayout(false);
            ResumeLayout(false);
        }
    }
}
