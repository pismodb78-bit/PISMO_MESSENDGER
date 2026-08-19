using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    // ДИЗАЙН формы (построение интерфейса). Вынесен из логики по образцу
    // MainForm.Designer.cs: здесь ТОЛЬКО создание/раскладка контролов.
    public partial class CreateGroupForm
    {
        private void BuildUi()
        {
            Text            = "PISMO — Новая группа";
            ClientSize      = new Size(380, 460);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(54, 57, 63);
            Font            = new Font("Segoe UI", 9.5f);

            var lblTitle = new Label
            {
                Text      = "Новая группа",
                Font      = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(20, 18)
            };

            var lblNameHint = new Label
            {
                Text      = "НАЗВАНИЕ ГРУППЫ",
                Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize  = true,
                Location  = new Point(20, 58)
            };

            _txtName = new TextBox
            {
                BackColor   = Color.FromArgb(32, 34, 37),
                ForeColor   = Color.FromArgb(220, 221, 222),
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Segoe UI", 11f),
                Location    = new Point(20, 76),
                Size        = new Size(340, 32)
            };

            var lblUsersHint = new Label
            {
                Text      = "УЧАСТНИКИ",
                Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize  = true,
                Location  = new Point(20, 120)
            };

            _clbUsers = new CheckedListBox
            {
                BackColor   = Color.FromArgb(32, 34, 37),
                ForeColor   = Color.FromArgb(220, 221, 222),
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Segoe UI", 10f),
                Location    = new Point(20, 140),
                Size        = new Size(340, 220),
                CheckOnClick = true
            };

            _lblError = new Label
            {
                ForeColor = Color.FromArgb(240, 71, 71),
                Font      = new Font("Segoe UI", 9f),
                Location  = new Point(20, 368),
                Size      = new Size(340, 20),
                Visible   = false
            };

            _btnCreate = new Button
            {
                Text      = "Создать группу",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                Location  = new Point(20, 396),
                Size      = new Size(340, 42),
                Cursor    = Cursors.Hand
            };
            _btnCreate.FlatAppearance.BorderSize = 0;
            _btnCreate.Click += BtnCreate_Click;
            _btnCreate.MouseEnter += (s, e) => _btnCreate.BackColor = Color.FromArgb(71, 82, 196);
            _btnCreate.MouseLeave += (s, e) => _btnCreate.BackColor = Color.FromArgb(88, 101, 242);

            Controls.AddRange(new Control[]
            {
                lblTitle, lblNameHint, _txtName,
                lblUsersHint, _clbUsers, _lblError, _btnCreate
            });
        }
    }
}
