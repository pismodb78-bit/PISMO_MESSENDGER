namespace PISMO
{
    partial class ChangePasswordForm
    {
        private System.ComponentModel.IContainer components = null;

        private Panel   pnlCard;
        private Label   lblTitle;
        private Label   lblOldHint;
        private TextBox txtOldPass;
        private Label   lblNewHint;
        private TextBox txtNewPass;
        private Label   lblConfirmHint;
        private TextBox txtConfirmPass;
        private Label   lblError;
        private Button  btnUpdate;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            pnlCard        = new Panel();
            lblTitle       = new Label();
            lblOldHint     = new Label();
            txtOldPass     = new TextBox();
            lblNewHint     = new Label();
            txtNewPass     = new TextBox();
            lblConfirmHint = new Label();
            txtConfirmPass = new TextBox();
            lblError       = new Label();
            btnUpdate      = new Button();

            pnlCard.SuspendLayout();
            SuspendLayout();

            BackColor       = Color.FromArgb(54, 57, 63);
            ClientSize      = new Size(420, 440);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            Text            = "PISMO — Смена пароля";
            Font            = new Font("Segoe UI", 9.5f);

            pnlCard.BackColor = Color.FromArgb(47, 49, 54);
            pnlCard.Size      = new Size(360, 370);
            pnlCard.Location  = new Point(30, 35);
            pnlCard.Controls.AddRange(new Control[]
            {
                lblTitle,
                lblOldHint, txtOldPass,
                lblNewHint, txtNewPass,
                lblConfirmHint, txtConfirmPass,
                lblError, btnUpdate
            });

            lblTitle.Text      = "Сменить пароль";
            lblTitle.Font      = new Font("Segoe UI Semibold", 16f, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.AutoSize  = true;
            lblTitle.Location  = new Point(70, 24);

            int lx = 24, fw = 312, ly = 72;

            void AddField(Label lbl, string hint, TextBox tb, ref int y)
            {
                lbl.Text      = hint;
                lbl.Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
                lbl.ForeColor = Color.FromArgb(185, 187, 190);
                lbl.AutoSize  = true;
                lbl.Location  = new Point(lx, y);

                tb.BackColor              = Color.FromArgb(32, 34, 37);
                tb.ForeColor              = Color.FromArgb(220, 221, 222);
                tb.BorderStyle            = BorderStyle.FixedSingle;
                tb.Font                   = new Font("Segoe UI", 11f);
                tb.UseSystemPasswordChar  = true;
                tb.Location               = new Point(lx, y + 18);
                tb.Size                   = new Size(fw, 36);

                y += 66;
            }

            AddField(lblOldHint,     "СТАРЫЙ ПАРОЛЬ",     txtOldPass,     ref ly);
            AddField(lblNewHint,     "НОВЫЙ ПАРОЛЬ",      txtNewPass,     ref ly);
            AddField(lblConfirmHint, "ПОДТВЕРДИТЕ ПАРОЛЬ",txtConfirmPass, ref ly);

            lblError.ForeColor = Color.FromArgb(240, 71, 71);
            lblError.Font      = new Font("Segoe UI", 9f);
            lblError.Location  = new Point(lx, ly);
            lblError.Size      = new Size(fw, 20);
            lblError.Visible   = false;
            ly += 26;

            btnUpdate.Text      = "Сохранить пароль";
            btnUpdate.BackColor = Color.FromArgb(88, 101, 242);
            btnUpdate.ForeColor = Color.White;
            btnUpdate.FlatStyle = FlatStyle.Flat;
            btnUpdate.FlatAppearance.BorderSize = 0;
            btnUpdate.Font      = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
            btnUpdate.Location  = new Point(lx, ly);
            btnUpdate.Size      = new Size(fw, 42);
            btnUpdate.Cursor    = Cursors.Hand;
            btnUpdate.Click    += btnUpdate_Click;
            btnUpdate.MouseEnter += (s, e) => btnUpdate.BackColor = Color.FromArgb(71, 82, 196);
            btnUpdate.MouseLeave += (s, e) => btnUpdate.BackColor = Color.FromArgb(88, 101, 242);

            Controls.Add(pnlCard);
            pnlCard.ResumeLayout(false);
            ResumeLayout(false);
        }
    }
}
