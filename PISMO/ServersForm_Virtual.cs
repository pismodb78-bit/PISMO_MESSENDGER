using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Дальние сообщения канала — ВНЕ ленты.
    ///
    /// Каждое сообщение канала — окно Windows, и на каждом кадре прокрутки
    /// система сдвигает их все, а WinForms для каждого переспрашивает
    /// координаты. Чем глубже пролистан канал, тем больше окон и тем дороже
    /// кадр. В личной переписке пузыри стоят по своим координатам, и дальние
    /// там просто вынимаются. Лента канала — FlowLayoutPanel: место каждого
    /// сообщения считается от предыдущего, и вынутое сдвинуло бы всё, что
    /// ниже. Поэтому дальние сообщения сверху и снизу заменяются РАСПОРКАМИ —
    /// пустыми панелями той же высоты. Остальная лента стоит ровно там же, а
    /// окон в ней — только то, что рядом с экраном.
    ///
    ///   • вынимаем через треть секунды покоя, дальше трёх экранов;
    ///     возвращаем заранее, за два экрана, прямо на кадре прокрутки;
    ///   • сообщение с фокусом и с живым браузерным проигрывателем не трогаем;
    ///   • всё, что работает с сообщениями напрямую (переход к сообщению,
    ///     догрузка истории сверху, выделение), сначала возвращает всё на место;
    ///   • вернувшееся сообщение могло подрасти (доехала гифка) — тогда место
    ///     на экране держим по соседу, а не по распорке.
    /// </summary>
    public sealed partial class ServersForm
    {
        /// <summary>Пустая панель на месте вынутых сообщений.</summary>
        private sealed class FeedSpacer : Panel
        {
            public FeedSpacer(Color back)
            {
                Margin = Padding.Empty;
                Padding = Padding.Empty;
                Width = 1;
                Height = 0;
                BackColor = back;
                TabStop = false;
                Enabled = false;
            }
        }

        /// <summary>Одна распорка не выше этого: очень высокие окна Windows не любит.</summary>
        private const int SrvSpacerMax = 30000;
        /// <summary>На короткой ленте ничего не вынимаем.</summary>
        private const int SrvCullMinCount = 60;

        private readonly List<Control> _srvTopRun = new();      // вынутые сверху, по порядку ленты
        private readonly List<Control> _srvBottomRun = new();   // вынутые снизу, по порядку ленты
        private readonly Dictionary<Control, int> _srvSpan = new();   // сколько места занимал вынутый
        private readonly List<FeedSpacer> _srvTopSpacers = new();
        private readonly List<FeedSpacer> _srvBottomSpacers = new();
        private int _srvTopHeight, _srvBottomHeight;

        private System.Windows.Forms.Timer _srvCullTimer;
        private int _srvCullPos = int.MinValue;
        private DateTime _srvCullMoved = DateTime.MinValue;
        private bool _srvCullSettled = true;

        private static int SpanOf(Control c) => c.Height + c.Margin.Vertical;

        private bool SrvHasParked =>
            _srvTopRun.Count > 0 || _srvBottomRun.Count > 0
            || _srvTopSpacers.Count > 0 || _srvBottomSpacers.Count > 0;

        /// <summary>Есть ли в сообщении браузерный проигрыватель: его окно переносить нельзя.</summary>
        private static bool SrvHoldsBrowser(Control c)
        {
            foreach (Control ch in c.Controls)
            {
                if (ch.GetType().Name.StartsWith("WebView2", StringComparison.Ordinal)) return true;
                if (ch.HasChildren && SrvHoldsBrowser(ch)) return true;
            }
            return false;
        }

        private static bool SrvParkable(Control c) =>
            c != null && !c.IsDisposed && c is not FeedSpacer && !c.ContainsFocus && !SrvHoldsBrowser(c);

        /// <summary>Первое сообщение после верхних распорок — по нему держим место на экране.</summary>
        private Control SrvAnchor()
        {
            int i = _srvTopSpacers.Count;
            var ctrls = _pnlMessages.Controls;
            for (; i < ctrls.Count; i++)
                if (ctrls[i] is not FeedSpacer) return ctrls[i];
            return null;
        }

        /// <summary>Сообщение-якорь сдвинулось после раскладки — сдвигаем прокрутку за ним.</summary>
        private void SrvKeepAnchor(Control anchor, int topBefore)
        {
            if (anchor == null || anchor.IsDisposed || anchor.Parent != _pnlMessages) return;
            int delta = anchor.Top - topBefore;
            if (delta == 0) return;
            try
            {
                int cur = -_pnlMessages.AutoScrollPosition.Y;
                _pnlMessages.AutoScrollPosition = new Point(0, Math.Max(0, cur + delta));
                ChatScroll.ShiftBy(_pnlMessages, delta);
                _lastSrvTop = -_pnlMessages.AutoScrollPosition.Y;
            }
            catch { }
        }

        /// <summary>Высота верхних распорок; раскладку приостанавливает вызывающий.</summary>
        private void SetTopSpacers(int total)
        {
            _srvTopHeight = Math.Max(0, total);
            int need = (_srvTopHeight + SrvSpacerMax - 1) / SrvSpacerMax;
            while (_srvTopSpacers.Count > need)
            {
                var sp = _srvTopSpacers[_srvTopSpacers.Count - 1];
                _srvTopSpacers.RemoveAt(_srvTopSpacers.Count - 1);
                try { _pnlMessages.Controls.Remove(sp); sp.Dispose(); } catch { }
            }
            while (_srvTopSpacers.Count < need)
            {
                var sp = new FeedSpacer(_pnlMessages.BackColor);
                _pnlMessages.Controls.Add(sp);
                _pnlMessages.Controls.SetChildIndex(sp, _srvTopSpacers.Count);
                _srvTopSpacers.Add(sp);
            }
            int left = _srvTopHeight;
            foreach (var sp in _srvTopSpacers)
            {
                int h = Math.Min(SrvSpacerMax, left);
                if (sp.Height != h) sp.Height = h;
                left -= h;
            }
        }

        /// <summary>
        /// Высота нижних распорок. <paramref name="insertAt"/> — куда встать
        /// первой, если их ещё нет: сразу за последним оставшимся сообщением,
        /// а не в самый конец (туда дописываются новые сообщения).
        /// </summary>
        private void SetBottomSpacers(int total, int insertAt)
        {
            _srvBottomHeight = Math.Max(0, total);
            int need = (_srvBottomHeight + SrvSpacerMax - 1) / SrvSpacerMax;
            while (_srvBottomSpacers.Count > need)
            {
                var sp = _srvBottomSpacers[_srvBottomSpacers.Count - 1];
                _srvBottomSpacers.RemoveAt(_srvBottomSpacers.Count - 1);
                try { _pnlMessages.Controls.Remove(sp); sp.Dispose(); } catch { }
            }
            while (_srvBottomSpacers.Count < need)
            {
                int at = _srvBottomSpacers.Count > 0
                    ? _pnlMessages.Controls.GetChildIndex(_srvBottomSpacers[_srvBottomSpacers.Count - 1]) + 1
                    : insertAt;
                var sp = new FeedSpacer(_pnlMessages.BackColor);
                _pnlMessages.Controls.Add(sp);
                _pnlMessages.Controls.SetChildIndex(sp, Math.Min(at, _pnlMessages.Controls.Count - 1));
                _srvBottomSpacers.Add(sp);
            }
            int left = _srvBottomHeight;
            foreach (var sp in _srvBottomSpacers)
            {
                int h = Math.Min(SrvSpacerMax, left);
                if (sp.Height != h) sp.Height = h;
                left -= h;
            }
        }

        /// <summary>Кадр прокрутки: вернуть подъезжающее и ждать покоя.</summary>
        private void SrvFeedOnScrolled()
        {
            try
            {
                EnsureSrvCullTimer();
                SrvRevealNear();
            }
            catch { }
        }

        private void EnsureSrvCullTimer()
        {
            if (_srvCullTimer != null) return;
            _srvCullTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _srvCullTimer.Tick += (s, e) => { try { SrvCullTick(); } catch { } };
            _srvCullTimer.Start();
            FormClosed += (s, e) => { try { _srvCullTimer.Stop(); _srvCullTimer.Dispose(); } catch { } };
        }

        private void SrvCullTick()
        {
            if (IsDisposed || _pnlMessages == null || _pnlMessages.IsDisposed || !_pnlMessages.IsHandleCreated) return;
            int pos = -_pnlMessages.AutoScrollPosition.Y;
            if (pos != _srvCullPos)
            {
                _srvCullPos = pos;
                _srvCullMoved = DateTime.UtcNow;
                _srvCullSettled = false;
                SrvRevealNear();
                return;
            }
            if (_srvCullSettled) return;
            if ((DateTime.UtcNow - _srvCullMoved).TotalMilliseconds < 350) return;
            if (ChatScroll.IsScrolling(_pnlMessages)) return;
            _srvCullSettled = true;
            SrvParkFar();
        }

        /// <summary>Вернуть сообщения, к которым подъезжает экран.</summary>
        private void SrvRevealNear()
        {
            if (!SrvHasParked || _pnlMessages == null || _pnlMessages.IsDisposed) return;
            int vh = Math.Max(200, _pnlMessages.ClientSize.Height);
            int from = -vh * 2, to = vh * 3;
            var ctrls = _pnlMessages.Controls;

            // Сверху: нижний край распорок уже близко к экрану.
            if (_srvTopRun.Count > 0 && _srvTopSpacers.Count > 0)
            {
                var lastSp = _srvTopSpacers[_srvTopSpacers.Count - 1];
                int spacerBottom = lastSp.Top + lastSp.Height;
                if (spacerBottom > from)
                {
                    var back = new List<Control>();
                    int total = _srvTopHeight;
                    while (_srvTopRun.Count > 0 && spacerBottom > from)
                    {
                        var c = _srvTopRun[_srvTopRun.Count - 1];
                        _srvTopRun.RemoveAt(_srvTopRun.Count - 1);
                        int span = _srvSpan.TryGetValue(c, out int s) ? s : SpanOf(c);
                        _srvSpan.Remove(c);
                        total -= span;
                        spacerBottom -= span;
                        if (!c.IsDisposed) back.Insert(0, c);
                    }
                    var anchor = SrvAnchor();
                    int anchorTop = anchor?.Top ?? 0;
                    _pnlMessages.SuspendLayout();
                    try
                    {
                        if (_srvTopRun.Count == 0) total = 0;
                        SetTopSpacers(total);
                        int at = _srvTopSpacers.Count;
                        for (int i = 0; i < back.Count; i++)
                        {
                            ctrls.Add(back[i]);
                            ctrls.SetChildIndex(back[i], at + i);
                        }
                    }
                    finally { _pnlMessages.ResumeLayout(true); }
                    SrvKeepAnchor(anchor, anchorTop);
                    _srvCullSettled = false;
                }
            }

            // Снизу: верхний край распорок уже близко к экрану. Выше экрана
            // ничего не меняется, поэтому место держать не нужно.
            if (_srvBottomRun.Count > 0 && _srvBottomSpacers.Count > 0)
            {
                var firstSp = _srvBottomSpacers[0];
                int spacerTop = firstSp.Top;
                if (spacerTop < to)
                {
                    var back = new List<Control>();
                    int total = _srvBottomHeight;
                    while (_srvBottomRun.Count > 0 && spacerTop < to)
                    {
                        var c = _srvBottomRun[0];
                        _srvBottomRun.RemoveAt(0);
                        int span = _srvSpan.TryGetValue(c, out int s) ? s : SpanOf(c);
                        _srvSpan.Remove(c);
                        total -= span;
                        spacerTop += span;
                        if (!c.IsDisposed) back.Add(c);
                    }
                    _pnlMessages.SuspendLayout();
                    try
                    {
                        int at = ctrls.GetChildIndex(firstSp);
                        for (int i = 0; i < back.Count; i++)
                        {
                            ctrls.Add(back[i]);
                            ctrls.SetChildIndex(back[i], at + i);
                        }
                        if (_srvBottomRun.Count == 0) total = 0;
                        SetBottomSpacers(total, at + back.Count);
                    }
                    finally { _pnlMessages.ResumeLayout(true); }
                    _srvCullSettled = false;
                }
            }
        }

        /// <summary>Вынуть то, что далеко от экрана (когда прокрутка утихла).</summary>
        private void SrvParkFar()
        {
            if (_pnlMessages == null || _pnlMessages.IsDisposed) return;
            // Выделение работает со всеми сообщениями сразу; догрузка истории
            // сама вставляет сверху — не мешаем ей.
            if (_srvSelectMode) { SrvUnparkAll(); return; }
            if (_srvOlderBusy || _srvLoadingOlder) return;

            var ctrls = _pnlMessages.Controls;
            int startIdx = _srvTopSpacers.Count;
            int endIdx = _srvBottomSpacers.Count > 0 ? ctrls.GetChildIndex(_srvBottomSpacers[0]) : ctrls.Count;
            if (endIdx - startIdx + _srvTopRun.Count + _srvBottomRun.Count < SrvCullMinCount) return;

            int vh = Math.Max(200, _pnlMessages.ClientSize.Height);
            int from = -vh * 3, to = vh * 4;

            var topPark = new List<Control>();
            int t = startIdx;
            while (t < endIdx)
            {
                var c = ctrls[t];
                if (c.Bottom + c.Margin.Bottom >= from || !SrvParkable(c)) break;
                topPark.Add(c);
                t++;
            }
            var bottomPark = new List<Control>();
            int b = endIdx - 1;
            while (b > t)
            {
                var c = ctrls[b];
                if (c.Top - c.Margin.Top <= to || !SrvParkable(c)) break;
                bottomPark.Insert(0, c);
                b--;
            }
            // Хотя бы одно сообщение остаётся — по нему держим место.
            if (t >= endIdx) return;
            if (topPark.Count == 0 && bottomPark.Count == 0) return;

            var anchor = ctrls[t];
            int anchorTop = anchor.Top;
            var lastKept = ctrls[b];
            _pnlMessages.SuspendLayout();
            try
            {
                if (topPark.Count > 0)
                {
                    int add = 0;
                    foreach (var c in topPark)
                    {
                        int span = SpanOf(c);
                        _srvSpan[c] = span;
                        add += span;
                        ctrls.Remove(c);
                        _srvTopRun.Add(c);
                    }
                    SetTopSpacers(_srvTopHeight + add);
                }
                if (bottomPark.Count > 0)
                {
                    int add = 0;
                    foreach (var c in bottomPark)
                    {
                        int span = SpanOf(c);
                        _srvSpan[c] = span;
                        add += span;
                        ctrls.Remove(c);
                    }
                    // Они идут ПЕРЕД тем, что уже вынуто снизу.
                    _srvBottomRun.InsertRange(0, bottomPark);
                    SetBottomSpacers(_srvBottomHeight + add, ctrls.GetChildIndex(lastKept) + 1);
                }
            }
            finally { _pnlMessages.ResumeLayout(true); }
            SrvKeepAnchor(anchor, anchorTop);
        }

        /// <summary>Вернуть в ленту всё вынутое и убрать распорки.</summary>
        private void SrvUnparkAll()
        {
            if (!SrvHasParked || _pnlMessages == null || _pnlMessages.IsDisposed) return;
            var ctrls = _pnlMessages.Controls;
            var anchor = SrvAnchor();
            int anchorTop = anchor?.Top ?? 0;
            _pnlMessages.SuspendLayout();
            try
            {
                SetTopSpacers(0);
                for (int i = 0; i < _srvTopRun.Count; i++)
                {
                    var c = _srvTopRun[i];
                    if (c.IsDisposed) continue;
                    ctrls.Add(c);
                    ctrls.SetChildIndex(c, i);
                }
                int at = _srvBottomSpacers.Count > 0 ? ctrls.GetChildIndex(_srvBottomSpacers[0]) : ctrls.Count;
                for (int i = 0; i < _srvBottomRun.Count; i++)
                {
                    var c = _srvBottomRun[i];
                    if (c.IsDisposed) continue;
                    ctrls.Add(c);
                    ctrls.SetChildIndex(c, Math.Min(at + i, ctrls.Count - 1));
                }
                SetBottomSpacers(0, 0);
                _srvTopRun.Clear();
                _srvBottomRun.Clear();
                _srvSpan.Clear();
            }
            finally { _pnlMessages.ResumeLayout(true); }
            SrvKeepAnchor(anchor, anchorTop);
            _srvCullSettled = false;
        }

        /// <summary>Лента уходит целиком — вынутое уходит вместе с ней (распорки уберёт очистка ленты).</summary>
        private void SrvDropParked()
        {
            foreach (var c in _srvTopRun) { try { c.Dispose(); } catch { } }
            foreach (var c in _srvBottomRun) { try { c.Dispose(); } catch { } }
            _srvTopRun.Clear();
            _srvBottomRun.Clear();
            _srvSpan.Clear();
            _srvTopSpacers.Clear();
            _srvBottomSpacers.Clear();
            _srvTopHeight = _srvBottomHeight = 0;
            _srvCullSettled = true;
        }
    }
}
