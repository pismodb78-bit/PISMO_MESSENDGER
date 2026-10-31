using System;
using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using System.Threading;

namespace PISMO
{
    /// <summary>
    /// Чтение переписки ЧЕРЕЗ РЕЛЕЙ — по тому же сокету, что и сигналы.
    /// </summary>
    /// <remarks>
    /// ПОЧЕМУ ЭТО БЫСТРЕЕ. Программа ходит в MySQL напрямую, и дорого здесь
    /// не количество байт, а количество КРУГОВ до сервера. Протокол MySQL
    /// разговорчив: открыть соединение — рукопожатие и аутентификация, потом
    /// запрос и ответ. До VPS круг около шестидесяти миллисекунд, а на
    /// мобильном модеме — сотни; заход в чат складывается из нескольких таких
    /// кругов.
    ///
    /// Релей же стоит рядом с базой: круг до неё — доли миллисекунды. Значит
    /// дешевле спросить его по уже открытому сокету: один обмен вместо
    /// нескольких. Так устроены все мессенджеры — у клиента одно соединение
    /// со своим сервером, а в базу ходит сервер.
    ///
    /// ЭТО НЕ ЗАМЕНА, А КОРОТКАЯ ДОРОГА. Сокета нет, релей старый, ответ не
    /// поспел — возвращаем null, и вызывающий идёт в базу сам, как раньше.
    /// Поэтому программа работает и со старым сервером.
    /// </remarks>
    internal static class RelayData
    {
        /// <summary>Сколько ждём ответа. Дольше — дешевле спросить базу самим.</summary>
        private const int TimeoutMs = 5000;

        /// <summary>
        /// Столько промахов подряд — и короткая дорога выключается до
        /// переподключения: релей этой работы не умеет (старая версия), и
        /// ждать его ответа каждый раз незачем.
        /// </summary>
        private const int StrikesOff = 3;

        private static int _strikes;
        private static long _nextId;

        private static readonly ConcurrentDictionary<string, TaskCompletionSourceBox> Waiting = new();

        private sealed class TaskCompletionSourceBox
        {
            public readonly ManualResetEventSlim Done = new(false);
            public string Json;
        }

        /// <summary>Сокет переподключился — даём релею ещё один шанс.</summary>
        public static void Reset() => Interlocked.Exchange(ref _strikes, 0);

        public static bool Available =>
            WebSocketSignalingClient.Instance.IsConnected && Volatile.Read(ref _strikes) < StrikesOff;

        /// <summary>Пришёл ответ — отдаём его тому, кто ждёт.</summary>
        public static void Deliver(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("qid", out var q)) return;
                string qid = q.GetString() ?? "";
                if (qid.Length == 0) return;
                if (Waiting.TryRemove(qid, out var box))
                {
                    box.Json = json;
                    box.Done.Set();
                }
            }
            catch { }
        }

        /// <summary>
        /// Страница сообщений от релея. null — короткой дороги нет.
        /// </summary>
        /// <param name="op">dm | group | channel</param>
        public static DataTable Page(string op, object args)
        {
            if (!Available) return null;

            string qid = "q" + Interlocked.Increment(ref _nextId);
            var box = new TaskCompletionSourceBox();
            Waiting[qid] = box;

            string msg = JsonSerializer.Serialize(new { type = "q", qid, op, args });
            if (!WebSocketSignalingClient.Instance.SendRaw(msg))
            {
                Waiting.TryRemove(qid, out _);
                return null;
            }

            if (!box.Done.Wait(TimeoutMs))
            {
                Waiting.TryRemove(qid, out _);
                Interlocked.Increment(ref _strikes);
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(box.Json);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() != "q_ok")
                {
                    Interlocked.Increment(ref _strikes);
                    return null;
                }
                Interlocked.Exchange(ref _strikes, 0);
                return ToTable(root.GetProperty("rows"));
            }
            catch
            {
                Interlocked.Increment(ref _strikes);
                return null;
            }
        }

        /// <summary>
        /// Строки от релея — в таблицу с теми же колонками, что приходят из
        /// базы: остальной код не должен замечать, откуда они приехали.
        /// </summary>
        private static DataTable ToTable(JsonElement rows)
        {
            var dt = new DataTable();
            dt.Columns.Add("id", typeof(int));
            dt.Columns.Add("sender_id", typeof(int));
            dt.Columns.Add("text", typeof(string));
            dt.Columns.Add("file_name", typeof(string));
            dt.Columns.Add("reply_to_id", typeof(int));
            dt.Columns.Add("is_deleted", typeof(int));
            dt.Columns.Add("edited_at", typeof(string));
            dt.Columns.Add("created_at", typeof(DateTime));
            dt.Columns.Add("age_sec", typeof(long));
            dt.Columns.Add("is_read", typeof(int));
            dt.Columns.Add("has_img", typeof(int));
            dt.Columns.Add("has_audio", typeof(int));
            dt.Columns.Add("has_video", typeof(int));
            dt.Columns.Add("has_file", typeof(int));
            dt.Columns.Add("sender_name", typeof(string));
            // «nm» — то же имя отправителя, только под тем названием, под
            // которым его ждут каналы сервера (их запрос зовёт колонку так).
            // Без неё сборка ленты канала падала на первой же строке — и
            // канал открывался ПУСТЫМ, потому что данные приехали релеем, а
            // не из базы.
            dt.Columns.Add("nm", typeof(string));
            dt.Columns.Add("login", typeof(string));

            long nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var r in rows.EnumerateArray())
            {
                var row = dt.NewRow();
                row["id"] = Int(r, "id");
                row["sender_id"] = Int(r, "sender_id");
                row["text"] = Str(r, "text") ?? "";
                row["file_name"] = (object)Str(r, "file_name") ?? DBNull.Value;
                row["reply_to_id"] = Int(r, "reply_to_id");
                row["is_deleted"] = Int(r, "is_deleted");
                string edited = Str(r, "edited_at");
                row["edited_at"] = string.IsNullOrEmpty(edited) ? (object)DBNull.Value : edited;

                // Время приходит отметкой в секундах — так оно не зависит ни от
                // часового пояса сервера, ни от настроек соединения. Кладём его
                // временем сервера, как в строках из базы: показ сам переводит
                // его в местное. Местным оно уже было — и сдвигалось дважды.
                long ts = Long(r, "created_ts");
                row["created_at"] = ts > 0
                    ? MainForm.UnixToDbTime(ts)
                    : (object)DBNull.Value;
                row["age_sec"] = ts > 0 ? Math.Max(0, nowTs - ts) : 0;

                row["is_read"] = Int(r, "is_read");
                row["has_img"] = Int(r, "has_img");
                row["has_audio"] = Int(r, "has_audio");
                row["has_video"] = Int(r, "has_video");
                row["has_file"] = Int(r, "has_file");
                row["sender_name"] = Str(r, "sender_name") ?? "";
                row["nm"] = row["sender_name"];
                row["login"] = Str(r, "login") ?? "";
                dt.Rows.Add(row);
            }
            return dt;
        }

        private static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v)
                ? v.ValueKind switch
                {
                    JsonValueKind.Number => v.TryGetInt32(out var i) ? i : 0,
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 0,
                    JsonValueKind.String => int.TryParse(v.GetString(), out var s) ? s : 0,
                    _ => 0,
                }
                : 0;

        private static long Long(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? (v.TryGetInt64(out var l) ? l : 0)
                : 0;

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }
}
