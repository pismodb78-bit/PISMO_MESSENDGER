using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Поле ввода, которое само рисует текст — ради ЦВЕТНЫХ эмодзи.
    ///
    /// Почему не обычное поле. TextBox рисуется средствами GDI, а они цветных
    /// шрифтов (COLR/CBDT) не знают: набранный эмодзи выходит монохромным
    /// контуром. RichTextBox не помог — WinForms берёт старую версию RichEdit.
    /// Текстовый движок WPF цветные глифы тоже не рисует: это видно по самому
    /// EmojiRender, который берёт его результат ТОЛЬКО если в нём нашёлся цвет,
    /// а иначе показывает силуэт и догружает картинку Twemoji.
    ///
    /// Поэтому текст рисуется здесь так же, как в пузырях: слова — обычной
    /// отрисовкой текста, эмодзи — картинками из того же кеша. А раз рисуем
    /// сами, то сами и ведём курсор, выделение, ввод с клавиатуры и буфер
    /// обмена — обычное поле всё это делало за нас.
    ///
    /// Чего здесь намеренно НЕТ: отмены (Ctrl+Z) и перетаскивания текста мышью.
    /// В поле ввода мессенджера они не нужны, а кода требуют заметно больше.
    /// </summary>
    public sealed class EmojiTextBox : Control
    {
        // ── состояние ─────────────────────────────────────────────────────
        private string _value = "";
        private int _caret;          // позиция курсора в строке
        private int _anchor;         // второй конец выделения
        private int _scrollY;        // сдвиг при переполнении
        private bool _caretOn = true;
        private bool _dragging;
        private readonly System.Windows.Forms.Timer _blink;

        /// <summary>Кусок раскладки: слово или эмодзи со своим местом на экране.</summary>
        private readonly struct Run
        {
            public readonly string Text; public readonly bool IsEmoji;
            public readonly int X, Y, W, H, Index, Len;
            public Run(string text, bool emoji, int x, int y, int w, int h, int index, int len)
            { Text = text; IsEmoji = emoji; X = x; Y = y; W = w; H = h; Index = index; Len = len; }
        }
        private readonly List<Run> _runs = new();
        private int _contentHeight;

        public EmojiTextBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.IBeam;

            _blink = new System.Windows.Forms.Timer { Interval = SystemInformation.CaretBlinkTime };
            _blink.Tick += (s, e) => { if (Focused) { _caretOn = !_caretOn; Invalidate(); } };
            _blink.Start();

            // Цветная картинка эмодзи могла ещё догружаться — перерисуемся, когда придёт.
            EmojiRender.Loaded += _ =>
            {
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Invalidate)); } catch { }
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _blink.Stop(); _blink.Dispose(); } catch { } }
            base.Dispose(disposing);
        }

        // ── свойства, которые ждёт остальной код ──────────────────────────

        public override string Text
        {
            get => _value;
            set
            {
                string v = value ?? "";
                if (v == _value) return;
                _value = v;
                _caret = _anchor = Math.Min(_caret, _value.Length);
                Relayout();
                OnTextChanged(EventArgs.Empty);
                Invalidate();
            }
        }

        /// <summary>Подсказка, пока поле пустое.</summary>
        public string PlaceholderText { get; set; } = "";

        /// <summary>Есть для совместимости с обычным полем — здесь всегда многострочно.</summary>
        public bool Multiline { get => true; set { } }

        /// <summary>Рамку не рисуем; свойство есть, чтобы не переписывать вызовы.</summary>
        public BorderStyle BorderStyle { get; set; } = BorderStyle.None;

        public int TextLength => _value.Length;

        public int SelectionStart
        {
            get => Math.Min(_caret, _anchor);
            set { _caret = _anchor = Clamp(value); EnsureCaretVisible(); Invalidate(); }
        }

        public int SelectionLength
        {
            get => Math.Abs(_caret - _anchor);
            set { _anchor = SelectionStart; _caret = Clamp(_anchor + Math.Max(0, value)); Invalidate(); }
        }

        public string SelectedText => _value.Substring(SelectionStart, SelectionLength);

        public void Clear() { Text = ""; _caret = _anchor = 0; _scrollY = 0; Invalidate(); }

        public void SelectAll() { _anchor = 0; _caret = _value.Length; Invalidate(); }

        private int Clamp(int i) => Math.Max(0, Math.Min(_value.Length, i));

        // ── раскладка ─────────────────────────────────────────────────────

        /// <summary>
        /// Разбирает текст на слова и эмодзи и расставляет их по строкам.
        /// Переносим по словам, как это делает обычное поле; слово длиннее
        /// строки переносится по границе, чтобы не уехать за край.
        /// </summary>
        private void Relayout()
        {
            _runs.Clear();
            _contentHeight = 0;
            if (Width <= 4) return;

            var font = Font;
            int lineH = Math.Max(font.Height, 1);
            int maxW = Math.Max(8, Width - 4);
            int x = 2, y = 2;

            int Measure(string s) => TextRenderer.MeasureText(
                s, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;

            void NewLine() { x = 2; y += lineH; }

            int i = 0;
            while (i < _value.Length)
            {
                if (_value[i] == '\n') { NewLine(); i++; continue; }

                int el = EmojiRender.EmojiClusterLength(_value, i);
                if (el > 0)
                {
                    int w = lineH;                       // эмодзи — квадрат ростом со строку
                    if (x > 2 && x + w > maxW) NewLine();
                    _runs.Add(new Run(_value.Substring(i, el), true, x, y, w, lineH, i, el));
                    x += w; i += el;
                    continue;
                }

                // Слово до пробела или до эмодзи включительно с хвостовым пробелом.
                int start = i;
                while (i < _value.Length && _value[i] != '\n'
                       && EmojiRender.EmojiClusterLength(_value, i) == 0)
                {
                    i++;
                    if (_value[i - 1] == ' ') break;     // пробел заканчивает слово
                }
                string word = _value[start..i];
                int ww = Measure(word);
                if (x > 2 && x + ww > maxW) NewLine();

                // Слово шире строки — режем по символам.
                while (ww > maxW && word.Length > 1)
                {
                    int cut = 1;
                    while (cut < word.Length && Measure(word[..(cut + 1)]) <= maxW) cut++;
                    _runs.Add(new Run(word[..cut], false, x, y, Measure(word[..cut]), lineH, start, cut));
                    start += cut; word = word[cut..]; ww = Measure(word);
                    NewLine();
                }
                _runs.Add(new Run(word, false, x, y, ww, lineH, start, word.Length));
                x += ww;
            }

            // Замыкающий пустой кусок: без него курсор в пустом поле и сразу
            // после перевода строки некуда поставить — он прилипал к концу
            // предыдущей строки.
            _runs.Add(new Run("", false, x, y, 0, lineH, _value.Length, 0));
            _contentHeight = y + lineH + 2;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Relayout(); Invalidate(); }

        // ── курсор ↔ координаты ───────────────────────────────────────────

        private Point CaretPoint()
        {
            var font = Font;
            foreach (var r in _runs)
            {
                if (_caret < r.Index || _caret > r.Index + r.Len) continue;
                if (r.IsEmoji) return new Point(_caret == r.Index ? r.X : r.X + r.W, r.Y);
                string prefix = r.Text[..(_caret - r.Index)];
                int w = TextRenderer.MeasureText(prefix, font,
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                return new Point(r.X + w, r.Y);
            }
            return new Point(2, 2);   // сюда попадаем только при пустой раскладке
        }

        private int IndexFromPoint(Point p)
        {
            var font = Font;
            int y = p.Y + _scrollY;

            Run? best = null;
            foreach (var r in _runs)
            {
                if (y < r.Y || y >= r.Y + r.H) continue;
                if (p.X < r.X) { best ??= r; continue; }
                if (p.X <= r.X + r.W) { best = r; break; }
                best = r;                                  // правее — берём последний в строке
            }
            if (best == null) return _runs.Count == 0 ? 0 : _value.Length;

            var run = best.Value;
            if (run.IsEmoji)
                return p.X > run.X + run.W / 2 ? run.Index + run.Len : run.Index;

            for (int k = 1; k <= run.Text.Length; k++)
            {
                int w = TextRenderer.MeasureText(run.Text[..k], font,
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                if (run.X + w > p.X) return run.Index + k - 1;
            }
            return run.Index + run.Len;
        }

        private void EnsureCaretVisible()
        {
            var pt = CaretPoint();
            int lineH = Math.Max(Font.Height, 1);
            if (pt.Y - _scrollY < 0) _scrollY = pt.Y;
            else if (pt.Y + lineH - _scrollY > Height) _scrollY = pt.Y + lineH - Height;
            _scrollY = ClampScroll(_scrollY);
        }

        // ── отрисовка ─────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var font = Font;
            int lineH = Math.Max(font.Height, 1);

            if (_value.Length == 0 && !string.IsNullOrEmpty(PlaceholderText))
            {
                TextRenderer.DrawText(g, PlaceholderText, font, new Point(2, 2),
                    Color.FromArgb(140, 143, 150), TextFormatFlags.NoPadding);
            }

            int selA = SelectionStart, selB = selA + SelectionLength;

            foreach (var r in _runs)
            {
                int y = r.Y - _scrollY;
                if (y + r.H < 0 || y > Height) continue;

                // Подсветка выделения — под текстом, кусками по пересечению.
                if (selB > selA && r.Index < selB && r.Index + r.Len > selA)
                {
                    int a = Math.Max(selA, r.Index) - r.Index;
                    int b = Math.Min(selB, r.Index + r.Len) - r.Index;
                    int x1 = r.IsEmoji ? (a == 0 ? r.X : r.X + r.W)
                        : r.X + TextRenderer.MeasureText(r.Text[..a], font,
                            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                    int x2 = r.IsEmoji ? (b == 0 ? r.X : r.X + r.W)
                        : r.X + TextRenderer.MeasureText(r.Text[..b], font,
                            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
                    using var sel = new SolidBrush(Color.FromArgb(70, 88, 101, 242));
                    g.FillRectangle(sel, x1, y, Math.Max(1, x2 - x1), r.H);
                }

                if (r.IsEmoji)
                {
                    var img = EmojiRender.Get(r.Text, lineH);
                    if (img != null) g.DrawImage(img, new Rectangle(r.X, y, r.W, r.H));
                    else TextRenderer.DrawText(g, r.Text, font, new Point(r.X, y), ForeColor,
                        TextFormatFlags.NoPadding);
                }
                else
                {
                    TextRenderer.DrawText(g, r.Text, font, new Point(r.X, y), ForeColor,
                        TextFormatFlags.NoPadding);
                }
            }

            if (Focused && _caretOn && SelectionLength == 0)
            {
                var c = CaretPoint();
                using var pen = new Pen(ForeColor);
                g.DrawLine(pen, c.X, c.Y - _scrollY, c.X, c.Y - _scrollY + lineH);
            }
        }

        // ── мышь ──────────────────────────────────────────────────────────

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            _caret = _anchor = IndexFromPoint(e.Location);
            _dragging = true;
            _caretOn = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            _caret = IndexFromPoint(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _dragging = false; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_contentHeight <= Height) return;
            _scrollY = ClampScroll(_scrollY - e.Delta / 120 * Math.Max(Font.Height, 1));
            Invalidate();
        }

        private int ClampScroll(int v) => Math.Max(0, Math.Min(v, Math.Max(0, _contentHeight - Height)));

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _caretOn = true; Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        // ── клавиатура ────────────────────────────────────────────────────

        /// <summary>Стрелки, Enter и Tab должны приходить нам, а не диалогу.</summary>
        protected override bool IsInputKey(Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            if (k is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End
                or Keys.Enter or Keys.Back or Keys.Delete) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);           // сначала внешние обработчики (Ctrl+V картинки и т.п.)
            if (e.Handled) return;

            bool shift = e.Shift, ctrl = e.Control;

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    // Отправку делает txtMessage_PreviewKeyDown: WinForms поднимает
                    // PreviewKeyDown на каждое нажатие ДО того, как оно дойдёт сюда,
                    // и там же поле очищается. Нам остаётся только перенос строки.
                    if (shift) Insert("\n");
                    e.Handled = true;
                    return;
                case Keys.Back:
                    if (SelectionLength > 0) DeleteSelection();
                    else if (_caret > 0)
                    {
                        int step = StepLeft(_caret);
                        Replace(_caret - step, step, "");
                    }
                    e.Handled = true; return;

                case Keys.Delete:
                    if (SelectionLength > 0) DeleteSelection();
                    else if (_caret < _value.Length) Replace(_caret, StepRight(_caret), "");
                    e.Handled = true; return;

                case Keys.Left:  Move(_caret - StepLeft(_caret), shift); e.Handled = true; return;
                case Keys.Right: Move(_caret + StepRight(_caret), shift); e.Handled = true; return;
                case Keys.Home:  Move(LineStart(_caret), shift); e.Handled = true; return;
                case Keys.End:   Move(LineEnd(_caret), shift); e.Handled = true; return;
                case Keys.Up:    MoveLine(-1, shift); e.Handled = true; return;
                case Keys.Down:  MoveLine(+1, shift); e.Handled = true; return;

                case Keys.A when ctrl: SelectAll(); e.Handled = true; return;
                case Keys.C when ctrl: CopySelection(); e.Handled = true; return;
                case Keys.X when ctrl: CopySelection(); DeleteSelection(); e.Handled = true; return;
                case Keys.V when ctrl:
                    // Картинку/файл из буфера разбирает внешний обработчик выше;
                    // сюда доходит только текст.
                    try { if (Clipboard.ContainsText()) Insert(Clipboard.GetText()); } catch { }
                    e.Handled = true; return;
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled || char.IsControl(e.KeyChar)) return;
            Insert(e.KeyChar.ToString());
            e.Handled = true;
        }

        /// <summary>Шаг влево с учётом суррогатных пар: эмодзи стирается целиком.</summary>
        private int StepLeft(int pos)
        {
            if (pos <= 0) return 0;
            if (pos >= 2 && char.IsLowSurrogate(_value[pos - 1]) && char.IsHighSurrogate(_value[pos - 2])) return 2;
            return 1;
        }

        private int StepRight(int pos)
        {
            if (pos >= _value.Length) return 0;
            if (pos + 1 < _value.Length && char.IsHighSurrogate(_value[pos]) && char.IsLowSurrogate(_value[pos + 1])) return 2;
            return 1;
        }

        private int LineStart(int pos)
        {
            int i = _value.LastIndexOf('\n', Math.Max(0, pos - 1));
            return i < 0 ? 0 : i + 1;
        }

        private int LineEnd(int pos)
        {
            int i = _value.IndexOf('\n', pos);
            return i < 0 ? _value.Length : i;
        }

        private void Move(int to, bool shift)
        {
            _caret = Clamp(to);
            if (!shift) _anchor = _caret;
            _caretOn = true;
            EnsureCaretVisible();
            Invalidate();
        }

        private void MoveLine(int dir, bool shift)
        {
            var pt = CaretPoint();
            int lineH = Math.Max(Font.Height, 1);
            Move(IndexFromPoint(new Point(pt.X, pt.Y - _scrollY + dir * lineH)), shift);
        }

        private void CopySelection()
        {
            if (SelectionLength == 0) return;
            try { Clipboard.SetText(SelectedText); } catch { }
        }

        private void DeleteSelection() => Replace(SelectionStart, SelectionLength, "");

        private void Insert(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            Replace(SelectionStart, SelectionLength, s);
        }

        /// <summary>Единственное место, где меняется текст: заменяет кусок и двигает курсор.</summary>
        private void Replace(int at, int len, string with)
        {
            at = Clamp(at);
            len = Math.Max(0, Math.Min(len, _value.Length - at));
            _value = _value[..at] + with + _value[(at + len)..];
            _caret = _anchor = at + with.Length;
            Relayout();
            EnsureCaretVisible();
            OnTextChanged(EventArgs.Empty);
            _caretOn = true;
            Invalidate();
        }
    }
}
