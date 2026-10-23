using System.Drawing;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Лента сообщений, которая НЕ прыгает к нажатому.
    ///
    /// ЗАЧЕМ. Прокручиваемая панель WinForms сама подтягивает в видимую
    /// область тот элемент, который получил фокус. В ленте сообщений это
    /// оборачивается рывком на каждое нажатие: ткнул в проигрыватель, в
    /// кнопку карточки файла, в поле — и переписка уехала, подставив под
    /// палец другое место. Та же беда была и на телефоне, там её снимали
    /// запретом фокуса у элементов плеера.
    ///
    /// Поэтому автоматическую прокрутку к фокусу гасим, а НАМЕРЕННУЮ —
    /// переход к найденному сообщению, к дате, к концу ленты — оставляем:
    /// её зовут явно, через ScrollToExplicit.
    /// </summary>
    internal sealed class ChatFeedPanel : Panel
    {
        private bool _explicit;

        protected override Point ScrollToControl(Control activeControl) =>
            _explicit ? base.ScrollToControl(activeControl) : AutoScrollPosition;

        /// <summary>Намеренная прокрутка к элементу — единственная разрешённая.</summary>
        internal void ScrollToExplicit(Control target)
        {
            if (target == null) return;
            _explicit = true;
            try { ScrollControlIntoView(target); }
            finally { _explicit = false; }
        }
    }

    /// <summary>То же для ленты канала: она собрана на FlowLayoutPanel.</summary>
    internal sealed class ChatFeedFlowPanel : FlowLayoutPanel
    {
        private bool _explicit;

        protected override Point ScrollToControl(Control activeControl) =>
            _explicit ? base.ScrollToControl(activeControl) : AutoScrollPosition;

        internal void ScrollToExplicit(Control target)
        {
            if (target == null) return;
            _explicit = true;
            try { ScrollControlIntoView(target); }
            finally { _explicit = false; }
        }
    }
}
