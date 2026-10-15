using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace PISMO
{
    /// <summary>
    /// Готовит переписки к работе без связи заранее — пока связь есть.
    /// </summary>
    /// <remarks>
    /// Кеш переписки наполнялся только тем, что человек открыл сам: зашёл в
    /// чат — страница осела на диске. Без связи это и выходило боком: один-два
    /// чата, куда успел заглянуть, открываются, а остальные встречают пустотой,
    /// хотя переписка в них есть.
    ///
    /// Поэтому последние сообщения всех чатов из списка подтягиваются фоном:
    /// запросы лёгкие (только метаданные, без вложений), идут по одному с
    /// паузой и начинаются не раньше, чем через полминуты после открытия
    /// списка — чтобы не мешать тому, чем человек занят прямо сейчас.
    ///
    /// Вложения сюда не входят намеренно: тянуть все фото и видео всех
    /// переписок «на всякий случай» — это гигабайты трафика и диска. Что
    /// открывали, то и останется доступным — у вложений свой кеш.
    /// </remarks>
    internal static class OfflinePrefetch
    {
        /// <summary>Сколько сообщений держим наготове на каждый чат.</summary>
        private const int Depth = 40;

        /// <summary>Пауза между чатами: это фоновая работа, а не гонка.</summary>
        private const int GapMs = 400;

        /// <summary>Сколько ждать после открытия списка, прежде чем начать.</summary>
        private const int DelayMs = 30_000;

        /// <summary>Как часто повторять обход. Чаще незачем: переписки не исчезают.</summary>
        private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

        private static readonly object Lock = new();
        private static DateTime _lastRun = DateTime.MinValue;
        private static bool _running;

        /// <summary>
        /// Поставить обход в очередь. Зовётся после удачной загрузки списка
        /// чатов; лишние вызовы отсекаются сами.
        /// </summary>
        public static void Schedule(int myId, List<int> partnerIds, List<int> groupIds)
        {
            if (myId <= 0) return;
            if (partnerIds == null && groupIds == null) return;

            lock (Lock)
            {
                if (_running) return;
                if (DateTime.UtcNow - _lastRun < Period) return;
                _running = true;
                _lastRun = DateTime.UtcNow;
            }

            var dms = partnerIds != null ? new List<int>(partnerIds) : new List<int>();
            var grps = groupIds != null ? new List<int>(groupIds) : new List<int>();

            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(DelayMs);
                    foreach (var id in dms)
                    {
                        if (ConnectionGuard.Offline) break;   // связь ушла — не мешаем
                        Grab(() => MainForm.LoadMessagesMetaOnly(myId, id, Depth),
                             MessageCache.DirectKey(myId, id), OfflineStore.DmKey(id));
                        Thread.Sleep(GapMs);
                    }
                    foreach (var gid in grps)
                    {
                        if (ConnectionGuard.Offline) break;
                        Grab(() => MainForm.LoadGroupMessagesMetaOnly(gid, Depth),
                             MessageCache.GroupKey(gid), OfflineStore.GroupKey(gid));
                        Thread.Sleep(GapMs);
                    }
                }
                catch { }
                finally { lock (Lock) _running = false; }
            });
        }

        private static readonly object ServersLock = new();
        private static DateTime _lastServers = DateTime.MinValue;
        private static bool _serversRunning;

        /// <summary>
        /// Подготовить серверы: списки их каналов. Сами сообщения каналов
        /// оседают при заходе на сервер — там это делается уже давно, одним
        /// обходом по всем его каналам. Не хватало ровно списка: без него
        /// сервер без связи выглядел пустым, даже когда сообщения лежали
        /// рядом на диске.
        /// </summary>
        public static void ScheduleServers(DataTable servers)
        {
            if (servers == null || servers.Rows.Count == 0) return;

            lock (ServersLock)
            {
                if (_serversRunning) return;
                if (DateTime.UtcNow - _lastServers < Period) return;
                _serversRunning = true;
                _lastServers = DateTime.UtcNow;
            }

            var ids = new List<int>();
            foreach (DataRow r in servers.Rows)
            {
                try { ids.Add(Convert.ToInt32(r["id"])); } catch { }
            }

            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(DelayMs);
                    foreach (var sid in ids)
                    {
                        if (ConnectionGuard.Offline) break;
                        try
                        {
                            using var conn = DBHelper.OpenConnection();
                            var dt = new DataTable();
                            try
                            {
                                using var cmd = new MySqlConnector.MySqlCommand(
                                    "SELECT id,name,type,user_limit FROM server_channels " +
                                    "WHERE server_id=@s ORDER BY position,id", conn);
                                cmd.Parameters.AddWithValue("@s", sid);
                                new MySqlConnector.MySqlDataAdapter(cmd).Fill(dt);
                            }
                            catch
                            {
                                // Миграции 14 может не быть — тогда без вместимости.
                                dt = new DataTable();
                                using var cmd2 = new MySqlConnector.MySqlCommand(
                                    "SELECT id,name,type FROM server_channels " +
                                    "WHERE server_id=@s ORDER BY position,id", conn);
                                cmd2.Parameters.AddWithValue("@s", sid);
                                new MySqlConnector.MySqlDataAdapter(cmd2).Fill(dt);
                            }
                            if (dt.Rows.Count > 0)
                                OfflineStore.Save(OfflineStore.ServerChannelsKey(sid), dt);
                        }
                        catch { }
                        Thread.Sleep(GapMs);
                    }
                }
                catch { }
                finally { lock (ServersLock) _serversRunning = false; }
            });
        }

        /// <summary>Одна переписка: забрать страницу и положить в оба кеша.</summary>
        private static void Grab(Func<DataTable> fetch, string cacheKey, string offlineKey)
        {
            try
            {
                var dt = fetch();
                if (dt == null || dt.Rows.Count == 0) return;
                try { MessageCache.Save(cacheKey, dt); } catch { }
                try { OfflineStore.Save(offlineKey, dt); } catch { }
            }
            catch { /* один неудачный чат не повод бросать обход */ }
        }
    }
}
