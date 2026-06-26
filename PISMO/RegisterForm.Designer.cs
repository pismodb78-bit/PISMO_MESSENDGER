namespace PISMO
{
    partial class RegisterForm
    {
        private System.ComponentModel.IContainer components = null;

        private Panel     pnlCard;
        private Label     lblTitle;
        private Label     lblNameHint;
        private TextBox   txtName;
        private Label     lblSurnameHint;
        private TextBox   txtSurname;
        private Label     lblLoginHint;
        private TextBox   txtLogin;
        private Label     lblPassHint;
        private TextBox   txtPass;
        private Label     lblError;
        private Button    btnRegister;
        private LinkLabel lnkBack;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            pnlCard        = new Panel();
            lblTitle       = new Label();
            lblNameHint    = new Label();
            txtName        = new TextBox();
            lblSurnameHint = new Label();
            txtSurname     = new TextBox();
            lblLoginHint   = new Label();
            txtLogin       = new TextBox();
            lblPassHint    = new Label();
            txtPass        = new TextBox();
            lblError       = new Label();
            btnRegister    = new Button();
            lnkBack        = new LinkLabel();

            pnlCard.SuspendLayout();
            SuspendLayout();

            BackColor       = Color.FromArgb(54, 57, 63);
            ClientSize      = new Size(480, 600);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Text            = "PISMO — Регистрация";
            Font            = new Font("Segoe UI", 9.5f);

            pnlCard.BackColor = Color.FromArgb(47, 49, 54);
            pnlCard.Size      = new Size(420, 510);
            pnlCard.Location  = new Point(30, 45);
            pnlCard.Controls.AddRange(new Control[]
            {
                lblTitle,
                lblNameHint, txtName,
                lblSurnameHint, txtSurname,
                lblLoginHint, txtLogin,
                lblPassHint, txtPass,
                lblError, btnRegister, lnkBack
            });

            lblTitle.Text      = "Создать аккаунт";
            lblTitle.Font      = new Font("Segoe UI Semibold", 18f, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.AutoSize  = true;
            lblTitle.Location  = new Point(90, 28);

            int lx = 30, fw = 360, ly = 82;

            void AddField(Label lbl, string hint, TextBox tb, ref int y)
            {
                lbl.Text      = hint;
                lbl.Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
                lbl.ForeColor = Color.FromArgb(185, 187, 190);
                lbl.AutoSize  = true;
                lbl.Location  = new Point(lx, y);

                tb.BackColor   = Color.FromArgb(32, 34, 37);
                tb.ForeColor   = Color.FromArgb(220, 221, 222);
                tb.BorderStyle = BorderStyle.FixedSingle;
                tb.Font        = new Font("Segoe UI", 11f);
                tb.Location    = new Point(lx, y + 18);
                tb.Size        = new Size(fw, 36);

                y += 68;
            }

            AddField(lblNameHint,    "ИМЯ",                   txtName,    ref ly);
            AddField(lblSurnameHint, "ФАМИЛИЯ",                txtSurname, ref ly);
            AddField(lblLoginHint,   "ЛОГИН",                  txtLogin,   ref ly);
            AddField(lblPassHint,    "ПАРОЛЬ (мин. 8 симв.)",  txtPass,    ref ly);
            txtPass.UseSystemPasswordChar = true;

            lblError.ForeColor = Color.FromArgb(240, 71, 71);
            lblError.Font      = new Font("Segoe UI", 9f);
            lblError.Location  = new Point(lx, ly);
            lblError.Size      = new Size(fw, 20);
            lblError.Visible   = false;
            ly += 24;

            btnRegister.Text      = "Зарегистрироваться";
            btnRegister.BackColor = Color.FromArgb(88, 101, 242);
            btnRegister.ForeColor = Color.White;
            btnRegister.FlatStyle = FlatStyle.Flat;
            btnRegister.FlatAppearance.BorderSize = 0;
            btnRegister.Font      = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
            btnRegister.Location  = new Point(lx, ly);
            btnRegister.Size      = new Size(fw, 44);
            btnRegister.Cursor    = Cursors.Hand;
            btnRegister.Click    += btnRegister_Click;
            btnRegister.MouseEnter += (s, e) => btnRegister.BackColor = Color.FromArgb(71, 82, 196);
            btnRegister.MouseLeave += (s, e) => btnRegister.BackColor = Color.FromArgb(88, 101, 242);
            ly += 54;

            lnkBack.Text         = "← Уже есть аккаунт? Войти";
            lnkBack.Font         = new Font("Segoe UI", 9.5f);
            lnkBack.LinkColor    = Color.FromArgb(0, 176, 244);
            lnkBack.AutoSize     = true;
            lnkBack.Location     = new Point(lx + 80, ly);
            lnkBack.LinkClicked += lnkBack_LinkClicked;

            Controls.Add(pnlCard);
            pnlCard.ResumeLayout(false);
            ResumeLayout(false);
        }
    }
}
