// ============================================================
//  ThumbCache — готовые миниатюры снимков для ленты
// ============================================================
//
//  Каждая сборка пузыря с картинкой раскодировала снимок заново: байты из
//  кеша → Image.FromStream → уменьшение до размера показа. Снимок с телефона
//  это двенадцать мегапикселей, и такая пара операций стоит десятки
//  миллисекунд. Догрузка приносит сразу сорок сообщений, и если среди них
//  десяток фотографий — вот она, та самая пауза при прокрутке вглубь.
//
//  Здесь лежит уже уменьшенная копия, ровно того размера, каким она
//  показывается. Повторная сборка (а она бывает часто: перерисовка ленты,
//  возврат в чат, догрузка) берёт готовое и не трогает декодер вовсе.
//
//  Память ограничена: миниатюра 452×340 — это около шестисот килобайт, и
//  двести штук укладываются в сто мегабайт. Вытесняем самые давние.
using System;
using System.Collections.Generic;
using System.Drawing;

namespace PISMO
{
    internal static class ThumbCache
    {
        private const int Max = 200;

        private static readonly object Sync = new();
        private static readonly Dictionary<int, Image> Map = new();
        private static readonly LinkedList<int> Order = new();

        /// <summary>Готовая миниатюра сообщения или null.</summary>
        public static Image Get(int msgId)
        {
            if (msgId <= 0) return null;
            lock (Sync)
            {
                if (!Map.TryGetValue(msgId, out var img)) return null;
                Order.Remove(msgId);
                Order.AddLast(msgId);
                return img;
            }
        }

        /// <summary>Положить миниатюру. Владелец изображения — кеш, а не пузырь.</summary>
        public static void Put(int msgId, Image img)
        {
            if (msgId <= 0 || img == null) return;
            lock (Sync)
            {
                if (Map.TryGetValue(msgId, out var old))
                {
                    if (ReferenceEquals(old, img)) { Order.Remove(msgId); Order.AddLast(msgId); return; }
                    try { old.Dispose(); } catch { }
                    Order.Remove(msgId);
                }
                Map[msgId] = img;
                Order.AddLast(msgId);

                while (Order.Count > Max)
                {
                    int oldest = Order.First.Value;
                    Order.RemoveFirst();
                    if (Map.TryGetValue(oldest, out var drop))
                    {
                        Map.Remove(oldest);
                        // Вытесненная миниатюра может прямо сейчас висеть в
                        // живом пузыре, поэтому НЕ освобождаем её: GDI+ отдал
                        // бы «Parameter is not valid» при следующей отрисовке.
                        // Сборщик заберёт её, когда пузырь умрёт.
                        _ = drop;
                    }
                }
            }
        }

        /// <summary>Забыть миниатюру — например, когда сообщение удалили.</summary>
        public static void Forget(int msgId)
        {
            lock (Sync)
            {
                if (!Map.Remove(msgId)) return;
                Order.Remove(msgId);
            }
        }

        /// <summary>Очистить всё (смена учётной записи).</summary>
        public static void Clear()
        {
            lock (Sync) { Map.Clear(); Order.Clear(); }
        }
    }
}
