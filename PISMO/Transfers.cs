using System;
using System.Collections.Generic;
using System.Threading;

namespace PISMO
{
    /// <summary>
    /// Идущие передачи файлов: что сейчас отправляется и что скачивается.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНЫЙ СПИСОК. Раньше отправка файла жила внутри модального
    /// окна: пока файл шёл, приложением нельзя было пользоваться, а сама
    /// передача обрывалась вместе с окном. Теперь она живёт здесь, отдельно от
    /// любого окна, — поэтому переход в другой чат её не трогает, а следить за
    /// ней и отменять можно из одного места, из кружка в шапке списка чатов.
    ///
    /// ЗАКОНЧЕННЫЕ ОСТАЮТСЯ В СПИСКЕ. Отмена и срыв раньше просто вычёркивали
    /// строку, и со стороны это выглядело как «нажал, и ничего»: ни следа, ни
    /// причины, ни возможности повторить. Теперь строка остаётся с исходом —
    /// «Отменено», «Готово» или текст ошибки — и уходит либо по крестику, либо
    /// сама, когда на неё перестанут смотреть (см. [Expire]).
    ///
    /// Список нарочно простой и живёт только в памяти: он про «прямо сейчас».
    /// Восстанавливать передачу после перезапуска приложения нечем — байты
    /// файла держит та самая задача, которая его и льёт.
    ///
    /// Событий об изменениях НЕТ намеренно: они приходили бы с чужих потоков и
    /// требовали бы перевода на поток окна на каждую порцию. Полосы просто
    /// перечитывают список несколько раз в секунду — это дешевле и не может
    /// разъехаться.
    /// </summary>
    internal static class Transfers
    {
        internal sealed class Item
        {
            public int Id;
            public string Name = "";

            /// <summary>
            /// Чей это файл — ключ переписки вида «dm:5», «group:3», «chan:7».
            ///
            /// Нужен, чтобы показать полосу там, где человек её ждёт: в самом
            /// чате, под перепиской. Кружок в шапке показывает всё разом и
            /// переживает переход между чатами, но пока отправка идёт из
            /// открытого чата, смотреть на неё удобнее прямо здесь.
            /// </summary>
            public string Where = "";

            /// <summary>Сорвалось: причина для полосы, иначе null.</summary>
            public string Error;

            /// <summary>true — отправка (⬆), false — скачивание (⬇).</summary>
            public bool Upload;

            public long Done;

            /// <summary>Размер целиком. 0 — неизвестен.</summary>
            public long Total;

            /// <summary>
            /// Доля не отслеживается. Так у скачивания: файл читается ОДНИМ
            /// запросом (чтение кусками через SUBSTRING перечитывало весь blob
            /// на каждый кусок и потому росло квадратично). Обещать проценты,
            /// которых нет, хуже, чем честно крутить полоску.
            /// </summary>
            public bool Indeterminate;

            /// <summary>Передача закончилась — строка осталась как запись об исходе.</summary>
            public bool Ended;

            /// <summary>Закончилась отменой (а не успехом и не ошибкой).</summary>
            public bool WasCancelled;

            /// <summary>Когда закончилась — по ней считается самоочистка.</summary>
            public DateTime EndedAt;

            internal Action CancelAction;
            internal int CancelFlag;

            /// <summary>
            /// Чем запустить заново. Держит те же байты вложения, поэтому
            /// «Повторить» ничего не спрашивает; живёт до закрытия строки.
            /// </summary>
            internal Action RetryAction;

            public bool Cancelled => CancelFlag != 0;

            public bool CanRetry => Ended && RetryAction != null && (WasCancelled || Error != null);

            /// <summary>Доля 0..1, либо -1, если её не знаем.</summary>
            public double Fraction =>
                Indeterminate || Total <= 0 ? -1 : Math.Min(1.0, (double)Done / Total);

            /// <summary>Исход одной строкой — для полосы и для списка.</summary>
            public string Status
            {
                get
                {
                    if (Ended)
                    {
                        if (Error != null) return Error;
                        return WasCancelled ? "Отменено" : "Готово";
                    }
                    if (Cancelled) return "Отмена…";
                    double f = Fraction;
                    string what = Upload ? "Отправка" : "Загрузка";
                    if (f < 0) return Total > 0 ? $"{what} · {Human(Total)}" : what + "…";
                    return $"{what} · {(int)Math.Round(f * 100)} % · " +
                           $"{Human(Done)} из {Human(Total)}";
                }
            }

            private static string Human(long bytes)
            {
                if (bytes <= 0) return "0 Б";
                string[] u = { "Б", "КБ", "МБ", "ГБ" };
                double v = bytes; int i = 0;
                while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
                return (i == 0 ? v.ToString("0") : v.ToString("0.#")) + " " + u[i];
            }
        }

        /// <summary>
        /// Сколько живёт закончившаяся строка.
        ///
        /// Успех показываем мельком: он виден и так — файл открылся, сообщение
        /// появилось. Отмену и срыв держим дольше: именно их человек ищет
        /// глазами, и именно их хочется повторить.
        /// </summary>
        private static readonly TimeSpan KeepDone = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan KeepFailed = TimeSpan.FromMinutes(5);

        private static readonly object Lock = new();
        private static readonly List<Item> Items = new();
        private static int _next;

        /// <summary>Сколько передач ИДЁТ. Законченные не считаются: выходить из
        /// приложения они не мешают.</summary>
        public static int Count
        {
            get { lock (Lock) return Items.FindAll(i => !i.Ended).Count; }
        }

        /// <summary>Сколько строк всего, включая законченные.</summary>
        public static int Rows { get { lock (Lock) return Items.Count; } }

        public static Item[] Snapshot() { lock (Lock) return Items.ToArray(); }

        public static Item Begin(string name, bool upload, long total, Action cancel,
                                 bool indeterminate = false, string where = "",
                                 Action retry = null)
        {
            var it = new Item
            {
                Id = Interlocked.Increment(ref _next),
                Name = string.IsNullOrWhiteSpace(name) ? "файл" : name,
                Upload = upload,
                Total = total,
                Indeterminate = indeterminate,
                CancelAction = cancel,
                RetryAction = retry,
                Where = where ?? "",
            };
            lock (Lock) Items.Add(it);
            return it;
        }

        public static void Progress(Item it, long done)
        {
            if (it == null) return;
            it.Done = done;
        }

        /// <summary>
        /// Передача закончилась. Строка остаётся — с исходом: «Готово» или
        /// «Отменено». Срыв объявляет [Fail], он знает причину.
        /// </summary>
        public static void Finish(Item it)
        {
            if (it == null) return;
            if (it.Ended) return;
            it.WasCancelled = it.Cancelled && it.Error == null;
            if (!it.WasCancelled && it.Error == null && it.Total > 0) it.Done = it.Total;
            it.Ended = true;
            it.EndedAt = DateTime.UtcNow;
        }

        /// <summary>Ключи переписок — чтобы писать их в одном месте.</summary>
        public static string ChatKey(bool isGroup, int target) =>
            (isGroup ? "group:" : "dm:") + target;

        public static string ChannelKey(int channelId) => "chan:" + channelId;

        /// <summary>Передачи этой переписки, включая только что законченные.</summary>
        public static Item[] For(string where)
        {
            if (string.IsNullOrEmpty(where)) return Array.Empty<Item>();
            lock (Lock) return Items.FindAll(i => i.Where == where).ToArray();
        }

        /// <summary>
        /// Сорвалось. Полоса остаётся и показывает причину — молча убирать
        /// её нельзя: со стороны это выглядит как «нажал, и ничего».
        /// </summary>
        public static void Fail(Item it, string why)
        {
            if (it == null || it.Ended) return;
            it.Error = string.IsNullOrWhiteSpace(why) ? "Не удалось" : why.Trim();
            it.Ended = true;
            it.EndedAt = DateTime.UtcNow;
        }

        /// <summary>Отменяет передачу. Повторные нажатия ничего не делают.</summary>
        public static void Cancel(Item it)
        {
            if (it == null) return;
            if (Interlocked.Exchange(ref it.CancelFlag, 1) != 0) return;
            try { it.CancelAction?.Invoke(); } catch { }
        }

        /// <summary>Убирает законченную строку из списка (крестик).</summary>
        public static void Dismiss(Item it)
        {
            if (it == null) return;
            lock (Lock) Items.Remove(it);
        }

        /// <summary>Убирает все законченные строки разом («Очистить»).</summary>
        public static void DismissEnded()
        {
            lock (Lock) Items.RemoveAll(i => i.Ended);
        }

        /// <summary>
        /// Повторяет сорванную или отменённую передачу теми же байтами.
        /// Прежняя строка уходит, вместо неё появляется новая — с нуля.
        /// </summary>
        public static void Retry(Item it)
        {
            if (it == null || !it.CanRetry) return;
            var again = it.RetryAction;
            it.RetryAction = null;
            lock (Lock) Items.Remove(it);
            try { again?.Invoke(); } catch { }
        }

        /// <summary>
        /// Самоочистка законченных строк. Зовут полосы из своего таймера —
        /// своего у списка нет намеренно: он не должен зависеть ни от окна,
        /// ни от того, смотрит ли кто-нибудь на него.
        /// </summary>
        public static void Expire()
        {
            var now = DateTime.UtcNow;
            lock (Lock)
            {
                Items.RemoveAll(i => i.Ended &&
                    now - i.EndedAt > (i.Error == null && !i.WasCancelled ? KeepDone : KeepFailed));
            }
        }
    }
}
