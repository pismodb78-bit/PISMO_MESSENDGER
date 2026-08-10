using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MySql.Data.MySqlClient;

namespace PISMO
{
    /// <summary>
    /// Поиск по каналу сервера (как 🔍 в мессенджере) + переход к дате 📅.
    /// Кнопки живут в заголовке канала; поиск идёт по уже загруженным сообщениям,
    /// а переход к дате при необходимости подтягивает ленту глубже.
    /// </summary>
    public sealed partial class ServersForm
    {
        private Button _srvBtnSearch, _srvBtnPrev, _srvBtnNext, _srvBtnCalendar;
        private TextBox _srvSearchBox;
        private Label _srvSearchCount;

        private readonly List<int> _srvSearchHits = new();   // id найденных сообщений
        private int _srvSearchIndex = -1;
        private int _srvPendingJumpId;                        // к какому id прокрутиться после отрисовки

        /// <summary>Создаёт строку поиска в заголовке канала. Вызывается из конструктора.</summary>
        private void BuildChannelSearch()
        {
            Button MkBtn(string text, int right, int width, string tip)
            {
                var b = new Button
                {
                    Text = text,
                    Font = new Font("Segoe UI Emoji", 9f),
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.FromArgb(200, 202, 208),
                    BackColor = Color.FromArgb(47, 49, 54),
                    Size = new Size(width, 26),
                    Location = new Point(Math.Max(0, _lblTitle.Width - right), 5),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                b.FlatAppearance.BorderSize = 0;
                new ToolTip().SetToolTip(b, tip);
                return b;
            }

            _srvBtnSearch   = MkBtn("🔍", 40, 30, "Поиск по каналу");
            _srvBtnNext     = MkBtn("▼", 76, 26, "Следующее совпадение (Enter)");
            _srvBtnPrev     = MkBtn("▲", 106, 26, "Предыдущее совпадение (Shift+Enter)");
            _srvBtnCalendar = MkBtn("📅", 140, 28, "Перейти к дате");
            _srvBtnNext.Visible = _srvBtnPrev.Visible = _srvBtnCalendar.Visible = false;

            _srvSearchCount = new Label
            {
                Visible = false, AutoSize = false, Size = new Size(58, 20),
                Location = new Point(Math.Max(0, _lblTitle.Width - 202), 8),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                ForeColor = Color.FromArgb(150, 152, 158), Font = new Font("Segoe UI", 8f),
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent
            };

            _srvSearchBox = new TextBox
            {
                Visible = false, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(30, 31, 34), ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f), PlaceholderText = "Поиск в канале…",
                Size = new Size(190, 26),
                Location = new Point(Math.Max(0, _lblTitle.Width - 396), 5),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            void SetVisible(bool show)
            {
                _srvSearchBox.Visible = show;
                _srvSearchCount.Visible = show;
                _srvBtnPrev.Visible = show;
                _srvBtnNext.Visible = show;
                _srvBtnCalendar.Visible = show;
                if (show) _srvSearchBox.Focus();
                else { _srvSearchBox.Clear(); RunChannelSearch(""); }
            }

            _srvBtnSearch.Click += (s, e) => SetVisible(!_srvSearchBox.Visible);
            _srvSearchBox.TextChanged += (s, e) => RunChannelSearch(_srvSearchBox.Text);
            _srvSearchBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { SetVisible(false); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter) { GoToChannelMatch(e.Shift ? -1 : +1); e.SuppressKeyPress = true; }
            };
            _srvBtnPrev.Click += (s, e) => GoToChannelMatch(-1);
            _srvBtnNext.Click += (s, e) => GoToChannelMatch(+1);
            _srvBtnCalendar.Click += (s, e) =>
                DatePickerPopup.Show(_srvBtnCalendar, DateTime.Today, JumpToChannelDate);

            // Позиции считаем от фактической ширины заголовка: на момент создания она
            // ещё не финальная, а Anchor запоминает исходное смещение — без пересчёта
            // после раскладки кнопки уехали бы.
            void Reposition()
            {
                int w = _lblTitle.Width;
                if (w <= 0) return;
                _srvBtnSearch.Location   = new Point(Math.Max(0, w - 40), 5);
                _srvBtnNext.Location     = new Point(Math.Max(0, w - 76), 5);
                _srvBtnPrev.Location     = new Point(Math.Max(0, w - 106), 5);
                _srvBtnCalendar.Location = new Point(Math.Max(0, w - 140), 5);
                _srvSearchCount.Location = new Point(Math.Max(0, w - 202), 8);
                // Ширину поля подгоняем под узкое окно, иначе оно наезжает на имя канала.
                const int titleMin = 150;
                int boxRight = w - 150;                       // дальше идут 📅 ▲ ▼ 🔍
                int boxLeft = Math.Max(titleMin, w - 396);
                int boxW = Math.Max(70, boxRight - boxLeft);
                _srvSearchBox.Bounds = new Rectangle(boxLeft, 5, boxW, 26);
            }
            _lblTitle.Resize += (s, e) => Reposition();
            _lblTitle.HandleCreated += (s, e) => Reposition();

            // Заголовок канала — Label с Dock=Top; кладём элементы поиска в него,
            // чтобы не перестраивать раскладку центра.
            _lblTitle.Controls.Add(_srvSearchBox);
            _lblTitle.Controls.Add(_srvSearchCount);
            _lblTitle.Controls.Add(_srvBtnCalendar);
            _lblTitle.Controls.Add(_srvBtnPrev);
            _lblTitle.Controls.Add(_srvBtnNext);
            _lblTitle.Controls.Add(_srvBtnSearch);
            Reposition();
        }

        /// <summary>Ищет по тексту и автору среди загруженных сообщений канала.</summary>
        private void RunChannelSearch(string query)
        {
            _srvSearchHits.Clear();
            _srvSearchIndex = -1;

            query = (query ?? "").Trim();
            if (query.Length > 0)
            {
                foreach (var kv in _srvMsgMeta)
                {
                    if (!_msgControls.TryGetValue(kv.Key, out var ctl) || ctl == null || ctl.IsDisposed) continue;
                    string hay = (kv.Value.text ?? "") + " " + (kv.Value.sender ?? "");
                    if (hay.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        _srvSearchHits.Add(kv.Key);
                }
                _srvSearchHits.Sort();          // по id = по времени
            }

            if (_srvSearchCount != null)
                _srvSearchCount.Text = query.Length == 0
                    ? ""
                    : (_srvSearchHits.Count == 0 ? "0 найд." : $"1/{_srvSearchHits.Count}");

            if (_srvSearchHits.Count > 0)
            {
                _srvSearchIndex = _srvSearchHits.Count - 1;   // начинаем с самого свежего
                ScrollToServerMessage(_srvSearchHits[_srvSearchIndex]);
                _srvSearchCount.Text = $"{_srvSearchIndex + 1}/{_srvSearchHits.Count}";
            }
        }

        private void GoToChannelMatch(int dir)
        {
            if (_srvSearchHits.Count == 0) return;
            _srvSearchIndex = (_srvSearchIndex + dir + _srvSearchHits.Count) % _srvSearchHits.Count;
            ScrollToServerMessage(_srvSearchHits[_srvSearchIndex]);
            if (_srvSearchCount != null)
                _srvSearchCount.Text = $"{_srvSearchIndex + 1}/{_srvSearchHits.Count}";
        }

        /// <summary>Переход к первому сообщению канала за выбранную дату. Если оно ещё не
        /// подгружено (лента постраничная) — расширяем страницу до нужной даты и
        /// прокручиваем после перерисовки.</summary>
        private void JumpToChannelDate(DateTime day)
        {
            if (_channelId <= 0) return;
            day = day.Date;

            int need = 0, targetId = 0;
            try
            {
                using var conn = DBHelper.OpenConnection();
                using (var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM server_messages WHERE channel_id=@c AND created_at >= @d", conn))
                {
                    cmd.Parameters.AddWithValue("@c", _channelId);
                    cmd.Parameters.AddWithValue("@d", day);
                    need = Convert.ToInt32(cmd.ExecuteScalar());
                }
                if (need > 0)
                {
                    using var cmd2 = new MySqlCommand(
                        "SELECT id FROM server_messages WHERE channel_id=@c AND created_at >= @d " +
                        "ORDER BY id ASC LIMIT 1", conn);
                    cmd2.Parameters.AddWithValue("@c", _channelId);
                    cmd2.Parameters.AddWithValue("@d", day);
                    targetId = Convert.ToInt32(cmd2.ExecuteScalar());
                }
            }
            catch { }

            if (need <= 0 || targetId <= 0)
            {
                MessageBox.Show(this, $"За {day:dd.MM.yyyy} и позже сообщений в канале нет.",
                    "Переход к дате", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_msgControls.ContainsKey(targetId))   // уже на экране
            {
                ScrollToServerMessage(targetId);
                return;
            }

            _srvPendingJumpId = targetId;
            _srvLimit = need + 5;
            _renderedKey = null; _renderedSig = null;   // форсим перерисовку с большей выборкой
            LoadMessages();
        }

        /// <summary>Вызывается после отрисовки ленты канала: если ждём переход к дате —
        /// прокручиваем к найденному сообщению.</summary>
        private void ApplySrvPendingJump()
        {
            if (_srvPendingJumpId <= 0) return;
            int id = _srvPendingJumpId;
            _srvPendingJumpId = 0;
            try { ScrollToServerMessage(id); } catch { }
        }
    }
}
