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
    /// Список нарочно простой и живёт только в памяти: он про «прямо сейчас».
    /// Восстанавливать передачу после перезапуска приложения нечем — байты
    /// файла держит та самая задача, которая его и льёт.
    ///
    /// Событий об изменениях НЕТ намеренно: они приходили бы с чужих потоков и
    /// требовали бы перевода на поток окна на каждую порцию. Кружок просто
    /// перечитывает список несколько раз в секунду — это дешевле и не может
    /// разъехаться.
    /// </summary>
    internal static class Transfers
    {
        internal sealed class Item
        {
            public int Id;
            public string Name = "";

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

            internal Action CancelAction;
            internal int CancelFlag;

            public bool Cancelled => CancelFlag != 0;

            /// <summary>Доля 0..1, либо -1, если её не знаем.</summary>
            public double Fraction =>
                Indeterminate || Total <= 0 ? -1 : Math.Min(1.0, (double)Done / Total);
        }

        private static readonly object Lock = new();
        private static readonly List<Item> Items = new();
        private static int _next;

        public static int Count { get { lock (Lock) return Items.Count; } }

        public static Item[] Snapshot() { lock (Lock) return Items.ToArray(); }

        public static Item Begin(string name, bool upload, long total, Action cancel,
                                 bool indeterminate = false)
        {
            var it = new Item
            {
                Id = Interlocked.Increment(ref _next),
                Name = string.IsNullOrWhiteSpace(name) ? "файл" : name,
                Upload = upload,
                Total = total,
                Indeterminate = indeterminate,
                CancelAction = cancel,
            };
            lock (Lock) Items.Add(it);
            return it;
        }

        public static void Progress(Item it, long done)
        {
            if (it == null) return;
            it.Done = done;
        }

        public static void Finish(Item it)
        {
            if (it == null) return;
            lock (Lock) Items.Remove(it);
        }

        /// <summary>Отменяет передачу. Повторные нажатия ничего не делают.</summary>
        public static void Cancel(Item it)
        {
            if (it == null) return;
            if (Interlocked.Exchange(ref it.CancelFlag, 1) != 0) return;
            try { it.CancelAction?.Invoke(); } catch { }
        }
    }
}
