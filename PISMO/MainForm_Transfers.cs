// ============================================================
//  MainForm — кнопка передач файлов (partial)
// ============================================================
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    public partial class MainForm
    {
        // ── Кнопка передач ───────────────────────────────────────────────
        //
        // Порт кружка с телефона. Показывает, сколько файлов идёт прямо
        // сейчас — в обе стороны, — и даёт их отменить, не возвращаясь в тот
        // чат, откуда всё началось.
        //
        // Место выбрано в шапке СПИСКА ЧАТОВ, а не над перепиской: передача
        // переживает переход в другой чат, и значок обязан быть виден оттуда
        // же. Плавающий поверх экрана кружок пробовали на телефоне — он
        // закрывал имена собеседников, и его убрали в шапку.
        //
        // Рисуем сами (см. TransferUi.PaintBadge), а не берём обычную кнопку:
        // по плоскому «⇅ 3» нельзя было понять ни направления, ни того, как
        // далеко ушла передача, ни того, что что-то сорвалось.

        private Panel _btnTransfers;
        private bool _transfersHover;
        private Form _transfersPopup;
        private Panel _transfersList;
        private System.Windows.Forms.Timer _transfersTimer;

        private const int TransferRowsMax = 8;

        private void BuildTransfersButton()
        {
            try
            {
                _btnTransfers = new Panel
                {
                    Dock = DockStyle.Right,
                    Width = 58,
                    BackColor = Color.Transparent,
                    Cursor = Cursors.Hand,
                    Visible = false,
                };
                EnableDoubleBuffer(_btnTransfers);
                _btnTransfers.Paint += (s, e) =>
                {
                    var box = new Rectangle(6, 0, _btnTransfers.Width - 12, _btnTransfers.Height);
                    TransferUi.PaintBadge(e.Graphics, box, Transfers.Snapshot(), _transfersHover);
                };
                _btnTransfers.MouseEnter += (s, e) => { _transfersHover = true; _btnTransfers.Invalidate(); };
                _btnTransfers.MouseLeave += (s, e) => { _transfersHover = false; _btnTransfers.Invalidate(); };
                new ToolTip().SetToolTip(_btnTransfers, "Передачи файлов: отправка, загрузка, отменённые");
                _btnTransfers.Click += (s, e) => ShowTransfersPopup();
                pnlSidebarHeader.Controls.Add(_btnTransfers);
                _btnTransfers.BringToFront();

                // Опрос вместо событий: они приходили бы с потоков передач, и
                // каждую порцию пришлось бы переводить на поток окна. Четыре
                // раза в секунду прочитать список из памяти — ничто.
                _transfersTimer = new System.Windows.Forms.Timer { Interval = 250 };
                _transfersTimer.Tick += (s, e) => RefreshTransfers();
                _transfersTimer.Start();

                BuildChatTransferStrip();
            }
            catch { }
        }

        /// <summary>
        /// Полоса передач ПОД ПЕРЕПИСКОЙ — как на телефоне.
        ///
        /// Кнопка в шапке показывает всё разом и переживает переход между
        /// чатами, но пока файл уходит из открытого чата, следить за ним и
        /// отменять удобнее прямо здесь. Полоса сама знает, какой чат открыт,
        /// и сама появляется и исчезает.
        /// </summary>
        private TransferStrip _chatStrip;

        private void BuildChatTransferStrip()
        {
            try
            {
                _chatStrip = new TransferStrip(() =>
                    _currentGroupId > 0 ? Transfers.ChatKey(true, _currentGroupId)
                    : _currentChatPartnerId > 0 ? Transfers.ChatKey(false, _currentChatPartnerId)
                    : "");
                _chatStrip.BackColor = Theme.Map(Color.FromArgb(54, 57, 63));
                pnlMain.Controls.Add(_chatStrip);

                // Полоса идёт НАД превью вложения и полем ввода, но ПОД лентой
                // сообщений. Порядок стыковки в WinForms задаётся z-порядком:
                // стыкуется сначала тот, кто дальше в списке. Лента заполняет
                // остаток и обязана быть первой, поэтому полосе место сразу за
                // ней — иначе она либо накроет переписку, либо уедет под поле
                // ввода.
                pnlMain.Controls.SetChildIndex(_chatStrip, 1);
            }
            catch { }
        }

        private void RefreshTransfers()
        {
            try
            {
                // Законченные строки живут недолго — кто-то должен их убирать,
                // а своего таймера у списка нет намеренно.
                Transfers.Expire();

                int n = Transfers.Rows;

                if (_btnTransfers != null)
                {
                    bool show = n > 0;
                    if (_btnTransfers.Visible != show) _btnTransfers.Visible = show;
                    if (show) _btnTransfers.Invalidate();
                }

                if (_transfersPopup is { IsDisposed: false } && _transfersList != null)
                {
                    if (n == 0) { _transfersPopup.Close(); return; }
                    int h = TransferUi.HeightFor(Math.Min(n, TransferRowsMax)) + 2;
                    if (_transfersList.Height != h) _transfersList.Height = h;
                    int ph = h + TransfersHeaderH;
                    if (_transfersPopup.ClientSize.Height != ph)
                        _transfersPopup.ClientSize = new Size(_transfersPopup.ClientSize.Width, ph);
                    _transfersList.Invalidate();
                }
            }
            catch { }
        }

        private const int TransfersHeaderH = 34;

        private void ShowTransfersPopup()
        {
            if (_transfersPopup is { IsDisposed: false })
            {
                try { _transfersPopup.Activate(); } catch { }
                return;
            }
            int n = Transfers.Rows;
            if (n == 0) return;

            int listH = TransferUi.HeightFor(Math.Min(n, TransferRowsMax)) + 2;

            var pop = new Form
            {
                Text = "Передачи файлов",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.Manual,
                ClientSize = new Size(440, listH + TransfersHeaderH),
                BackColor = Color.FromArgb(49, 51, 56),
                ShowInTaskbar = false,
            };

            // Шапка: что это и чем убрать законченное. Раньше законченные
            // строки просто исчезали, и убирать было нечего — зато и повторить
            // сорванную отправку было невозможно.
            var head = new Panel { Dock = DockStyle.Top, Height = TransfersHeaderH, BackColor = Color.FromArgb(43, 45, 49) };
            var lbl = new Label
            {
                Text = "Отправка и загрузка",
                Dock = DockStyle.Left,
                Width = 260,
                ForeColor = Color.FromArgb(200, 203, 209),
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0),
            };
            var clear = new Button
            {
                Text = "Очистить",
                Dock = DockStyle.Right,
                Width = 92,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(57, 60, 66),
                ForeColor = Color.FromArgb(210, 213, 219),
                Font = new Font("Segoe UI", 8.5f),
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            clear.FlatAppearance.BorderSize = 0;
            new ToolTip().SetToolTip(clear, "Убрать законченные строки");
            clear.Click += (s, e) => { Transfers.DismissEnded(); RefreshTransfers(); };
            head.Controls.Add(lbl);
            head.Controls.Add(clear);

            var list = new Panel
            {
                Dock = DockStyle.Top,
                Height = listH,
                BackColor = Color.FromArgb(49, 51, 56),
                Cursor = Cursors.Hand,
            };
            EnableDoubleBuffer(list);
            list.Paint += (s, e) => TransferUi.PaintRows(
                e.Graphics, list.ClientSize.Width, Transfers.Snapshot(), TransferRowsMax);
            list.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (TransferUi.Click(e.Location, list.ClientSize.Width,
                                     Transfers.Snapshot(), TransferRowsMax))
                    RefreshTransfers();
            };

            pop.Controls.Add(list);
            pop.Controls.Add(head);

            _transfersPopup = pop;
            _transfersList = list;
            pop.FormClosed += (s, e) => { _transfersPopup = null; _transfersList = null; };

            // Под кнопкой, а если координаты почему-то не считаются — по центру.
            try
            {
                var p = _btnTransfers.PointToScreen(new Point(0, _btnTransfers.Height));
                pop.Location = new Point(Math.Max(0, p.X - 40), p.Y + 4);
            }
            catch { pop.StartPosition = FormStartPosition.CenterParent; }

            pop.Show(this);
        }
    }
}
