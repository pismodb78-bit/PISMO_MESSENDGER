using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    // ДИЗАЙН форм (построение интерфейса) — вынесен из логики по образцу
    // MainForm.Designer.cs.
    public partial class GroupMembersForm
    {
        // ════════════════════════════════════════════════════════════
        //  UI
        // ════════════════════════════════════════════════════════════
        private void BuildUi()
        {
            Text            = "PISMO — Участники группы";
            ClientSize      = new Size(400, 500);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(54, 57, 63);
            Font            = new Font("Segoe UI", 9.5f);

            _lblTitle = new Label
            {
                Text      = $"👥 {_groupName}",
                Font      = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(20, 16)
            };

            var lblHint = new Label
            {
                Text      = "УЧАСТНИКИ",
                Font      = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(185, 187, 190),
                AutoSize  = true,
                Location  = new Point(20, 54)
            };

            _pnlMembers = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents  = false,
                AutoScroll    = true,
                BackColor     = Color.FromArgb(47, 49, 54),
                Location      = new Point(20, 76),
                Size          = new Size(360, 350),
                Padding       = new Padding(4)
            };

            _btnAddMembers = new Button
            {
                Text      = "➕ Добавить участников",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                Location  = new Point(20, 440),
                Size      = new Size(360, 42),
                Cursor    = Cursors.Hand
            };
            _btnAddMembers.FlatAppearance.BorderSize = 0;
            _btnAddMembers.Click += BtnAddMembers_Click;
            _btnAddMembers.MouseEnter += (s, e) => _btnAddMembers.BackColor = Color.FromArgb(71, 82, 196);
            _btnAddMembers.MouseLeave += (s, e) => _btnAddMembers.BackColor = Color.FromArgb(88, 101, 242);

            Controls.AddRange(new Control[] { _lblTitle, lblHint, _pnlMembers, _btnAddMembers });
        }
    }

    public partial class AddGroupMembersForm
    {
        private void BuildUi()
        {
            Text            = "PISMO — Добавить участников";
            ClientSize      = new Size(360, 440);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(54, 57, 63);
            Font            = new Font("Segoe UI", 9.5f);

            var lblTitle = new Label
            {
                Text      = "Добавить участников",
                Font      = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize  = true,
                Location  = new Point(20, 16)
            };

            _clbUsers = new CheckedListBox
            {
                BackColor    = Color.FromArgb(32, 34, 37),
                ForeColor    = Color.FromArgb(220, 221, 222),
                BorderStyle  = BorderStyle.FixedSingle,
                Font         = new Font("Segoe UI", 10f),
                Location     = new Point(20, 56),
                Size         = new Size(320, 300),
                CheckOnClick = true
            };

            _lblError = new Label
            {
                ForeColor = Color.FromArgb(240, 71, 71),
                Font      = new Font("Segoe UI", 9f),
                Location  = new Point(20, 362),
                Size      = new Size(320, 20),
                Visible   = false
            };

            _btnAdd = new Button
            {
                Text      = "Добавить",
                BackColor = Color.FromArgb(88, 101, 242),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                Location  = new Point(20, 388),
                Size      = new Size(320, 42),
                Cursor    = Cursors.Hand
            };
            _btnAdd.FlatAppearance.BorderSize = 0;
            _btnAdd.Click += BtnAdd_Click;
            _btnAdd.MouseEnter += (s, e) => _btnAdd.BackColor = Color.FromArgb(71, 82, 196);
            _btnAdd.MouseLeave += (s, e) => _btnAdd.BackColor = Color.FromArgb(88, 101, 242);

            Controls.AddRange(new Control[] { lblTitle, _clbUsers, _lblError, _btnAdd });
        }
    }
}
