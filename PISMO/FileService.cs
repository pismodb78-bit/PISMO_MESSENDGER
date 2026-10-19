using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using MySqlConnector;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PISMO
{
    /// <summary>
    /// Файловый сервис: вложение лежит на диске сервера, в сообщении — ссылка.
    /// </summary>
    /// <remarks>
    /// Зачем это вместо базы. Файл дописывался в LONGBLOB порциями через
    /// CONCAT, и на каждую порцию сервер переписывал весь накопленный блоб —
    /// работа росла квадратом от размера. Прогресс был виден только по
    /// границам порций, видео нельзя было перемотать, а клиент возил мегабайты
    /// по протоколу MySQL, который для этого не делали.
    ///
    /// Протокол тот же, что у tus и у черновика IETF «Resumable Uploads»:
    /// POST создаёт загрузку, HEAD говорит, сколько уже принято, PATCH
    /// дописывает с этого места. Обрыв стоит килобайтов, а не всей отправки.
    ///
    /// Сервиса может не быть — тогда всё работает по-старому, через базу:
    /// он ставится отдельно (ws-server/install-files.sh), и до этого момента
    /// программа обязана оставаться рабочей.
    /// </remarks>
    internal static class FileService
    {
        /// <summary>Мельче этого шлём по-старому: ради мелочи ходить некуда.</summary>
        public const int MinSize = 1024 * 1024;

        /// <summary>Сколько раз пробуем дослать файл, прежде чем сдаться.</summary>
        private const int MaxAttempts = 6;

        /// <summary>
        /// Сколько частей держим в полёте одновременно.
        /// </summary>
        /// <remarks>
        /// Так работают мессенджеры: пока одна часть летит, следующие уже в
        /// пути, и круг до сервера между ними не стоит простоем. На проводе
        /// этот круг дешёвый, на мобильном модеме — сотни миллисекунд, и
        /// именно он съедал время при живом канале.
        /// </remarks>
        private const int Lanes = 4;

        /// <summary>Границы размера части.</summary>
        private const int PartMin = 256 * 1024;
        private const int PartMax = 8 * 1024 * 1024;

        /// <summary>К какой длительности части стремимся.</summary>
        private const int PartTargetMs = 3000;

        /// <summary>Замеренная скорость отправки, байт в секунду. 0 — не знаем.</summary>
        private static long _speedBps;

        private static void RememberSpeed(long bytes, long ms)
        {
            if (bytes <= 0 || ms <= 0) return;
            long now = bytes * 1000L / ms;
            // Скользящее среднее: одна быстрая часть — ещё не признак канала.
            _speedBps = _speedBps == 0 ? now : (_speedBps * 3 + now) / 4;
        }

        /// <summary>
        /// Размер части под текущую скорость. Постоянный размер плох с обеих
        /// сторон: на быстром канале мелкие части заставляют ждать круг до
        /// сервера чаще, чем нужно, а на медленном крупная идёт минутами и
        /// теряется целиком при обрыве.
        /// </summary>
        private static int PartSize()
        {
            if (_speedBps <= 0) return 1024 * 1024;
            long want = _speedBps * PartTargetMs / 1000L;
            return (int)Math.Min(PartMax, Math.Max(PartMin, want));
        }

        private static readonly HttpClient Http = new HttpClient
        {
            // Заливка крупного файла идёт сколько нужно; потолок снимаем, его
            // роль выполняет отмена передачи.
            Timeout = Timeout.InfiniteTimeSpan
        };

        private static string _base;
        private static DateTime _probedAt = DateTime.MinValue;
        private static bool _alive;

        /// <summary>
        /// Адрес сервиса. Берётся из ip.txt — там же, где база, сигналинг и
        /// сервер звонков:
        ///
        ///   server=IP;port=3307;...;files=http://IP:8081
        ///
        /// Ключа нет — берём хост базы и порт 8081: сервис ставится на тот же
        /// VPS, и заставлять каждого дописывать строку ради значения по
        /// умолчанию незачем. «files=-» выключает сервис совсем — тогда
        /// вложения снова идут через базу.
        /// </summary>
        public static string Base
        {
            get
            {
                if (_base != null) return _base;
                _base = Probe();
                return _base;
            }
        }

        private static string Probe()
        {
            try
            {
                string line = ReadIpTxt();
                if (string.IsNullOrWhiteSpace(line)) return "";

                string host = null, files = null;
                if (line.IndexOf('=') >= 0)
                {
                    foreach (var part in line.Split(';'))
                    {
                        var seg = part.Trim();
                        int eq = seg.IndexOf('=');
                        if (eq <= 0) continue;
                        var key = seg.Substring(0, eq).Trim().ToLowerInvariant();
                        var val = seg.Substring(eq + 1).Trim();
                        if (val.Length == 0) continue;
                        if (key == "files") files = val;
                        else if (key == "server" || key == "host" || key == "data source") host = val;
                    }
                }
                else
                {
                    host = line.Split(':')[0].Trim();
                }

                if (files == "-") return "";                 // выключено вручную
                if (!string.IsNullOrEmpty(files))
                    return (files.Contains("://") ? files : "http://" + files).TrimEnd('/');
                if (!string.IsNullOrEmpty(host)) return "http://" + host + ":8081";
            }
            catch { }
            return "";
        }

        private static string ReadIpTxt()
        {
            string[] paths =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ip.txt"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "ip.txt"),
                "ip.txt"
            };
            foreach (var p in paths)
                if (File.Exists(p)) return File.ReadAllText(p).Trim();
            return null;
        }

        /// <summary>
        /// Жив ли сервис. Ответ держим пять минут: спрашивать перед каждым
        /// файлом незачем, а «его нет» не должно стоить таймаута на каждой
        /// отправке.
        /// </summary>
        public static bool Available()
        {
            if (string.IsNullOrEmpty(Base)) return false;
            if ((DateTime.UtcNow - _probedAt).TotalMinutes < 5) return _alive;
            _probedAt = DateTime.UtcNow;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var res = Http.GetAsync(Base + "/health", cts.Token).GetAwaiter().GetResult();
                _alive = res.IsSuccessStatusCode;
            }
            catch { _alive = false; }
            return _alive;
        }

        private static string Token()
        {
            try { return JwtAuth.Create(UserSession.EffectiveId, UserSession.EffectiveName ?? "user"); }
            catch { return ""; }
        }

        private static string Sha256(byte[] data)
        {
            using var sha = SHA256.Create();
            var sb = new StringBuilder(64);
            foreach (var b in sha.ComputeHash(data)) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string UrlOf(string sha, string name) =>
            Base + "/files/" + sha + "/" + Uri.EscapeDataString(name ?? "file");

        /// <summary>
        /// Заливает файл. Возвращает ссылку или null — тогда отправка пойдёт
        /// по-старому, через базу.
        /// </summary>
        public static string Upload(byte[] data, string name, Action<double> onProgress,
                                    CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(Base) || data == null || data.Length == 0) return null;
            name = string.IsNullOrWhiteSpace(name) ? "file" : name;

            try
            {
                string sha = Sha256(data);
                string nameB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));

                // 1. Создаём загрузку. Такой файл уже лежит — ответ придёт
                //    сразу с ключом, и по сети не уйдёт ни байта. А если эту
                //    отправку уже начинали и она оборвалась, сервер вернёт ту
                //    же загрузку и скажет, сколько у него есть: повтор
                //    продолжает с места обрыва, а не гонит файл заново.
                string id = null;
                long offset = 0;
                using (var req = new HttpRequestMessage(HttpMethod.Post, Base + "/files"))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token());
                    req.Headers.TryAddWithoutValidation("Upload-Length", data.Length.ToString());
                    req.Headers.TryAddWithoutValidation("Upload-Sha", sha);
                    req.Headers.TryAddWithoutValidation("Upload-Name", nameB64);
                    req.Content = new ByteArrayContent(Array.Empty<byte>());
                    using var res = Http.SendAsync(req, ct).GetAwaiter().GetResult();
                    if (!res.IsSuccessStatusCode) return null;
                    if (res.Headers.TryGetValues("File-Key", out var keys))
                    {
                        onProgress?.Invoke(1.0);
                        foreach (var k in keys) return UrlOf(k, name);
                    }
                    if (res.Headers.TryGetValues("Upload-Id", out var ids))
                        foreach (var v in ids) { id = v; break; }
                    if (res.Headers.TryGetValues("Upload-Offset", out var offs))
                        foreach (var v in offs) { long.TryParse(v, out offset); break; }
                }
                if (id == null) return null;
                if (offset < 0 || offset > data.Length) offset = 0;
                if (offset > 0) onProgress?.Invoke((double)offset / data.Length);

                // 2. Остаток шлём ЧАСТЯМИ И РАЗОМ.
                //
                // Раньше части уходили строго друг за другом, и после каждой
                // мы ждали ответа сервера. Этот круг на мобильном модеме —
                // сотни миллисекунд, и на файле из полусотни частей он
                // складывается в минуты простоя при живом канале. Сервис
                // принимает части вразнобой и сам следит, что дыр не
                // осталось, поэтому держим несколько в полёте.
                long sent = offset;
                int part = PartSize();
                var ranges = new System.Collections.Generic.List<(long from, long till)>();
                for (long at = offset; at < data.Length; at += part)
                    ranges.Add((at, Math.Min(at + part, data.Length)));

                string key = null;
                using (var gate = new System.Threading.SemaphoreSlim(Lanes))
                {
                    var tasks = new System.Collections.Generic.List<Task>();
                    foreach (var (from, till) in ranges)
                    {
                        gate.Wait(ct);
                        tasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                var got = SendPart(id, data, from, till,
                                    bytes =>
                                    {
                                        long done = System.Threading.Interlocked.Add(ref sent, bytes);
                                        onProgress?.Invoke(Math.Min(1.0, (double)done / data.Length));
                                    }, ct);
                                if (got != null) System.Threading.Volatile.Write(ref key, got);
                            }
                            finally { gate.Release(); }
                        }, ct));
                    }
                    try { Task.WaitAll(tasks.ToArray(), ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* причина уже учтена в самой части */ }
                }

                if (key != null) return UrlOf(key, name);

                // Все части ушли, а ключа нет — ответ на последнюю мог
                // потеряться. Спрашиваем сервер.
                try
                {
                    long? have = HeadOffset(id);
                    if (have != null)
                    {
                        string done = Patch(id, data, have.Value, have.Value, null, ct);
                        if (done != null) return UrlOf(done, name);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>
        /// Одна часть со всеми повторами. Возвращает ключ, если именно она
        /// оказалась последней недостающей.
        /// </summary>
        private static string SendPart(string id, byte[] data, long from, long till,
                                       Action<long> onBytes, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    string key = Patch(id, data, from, till, onBytes, ct);
                    started.Stop();
                    RememberSpeed(till - from, started.ElapsedMilliseconds);
                    return key;
                }
                catch (OperationCanceledException) { throw; }
                catch (OffsetMismatch)
                {
                    // Сервер не берёт это место — спорить незачем.
                    return null;
                }
                catch
                {
                    if (attempt + 1 > MaxAttempts) return null;
                    Thread.Sleep(1000 * (attempt + 1));
                }
            }
        }

        /// <summary>
        /// Одна попытка отправить часть [from, till). Возвращает ключ, если
        /// этой частью файл закончился.
        /// </summary>
        private static string Patch(string id, byte[] data, long from, long till,
                                    Action<long> onBytes, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), Base + "/files/" + id);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token());
            req.Headers.TryAddWithoutValidation("Upload-Offset", from.ToString());
            req.Content = new ProgressContent(data, from, till, onBytes, ct);
            req.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/offset+octet-stream");

            using var res = Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                .GetAwaiter().GetResult();
            // 409 — разошлись в том, с какого места продолжать. Сервер
            // присылает своё смещение: с него и пойдём.
            if (res.StatusCode == HttpStatusCode.Conflict)
            {
                long at = 0;
                if (res.Headers.TryGetValues("Upload-Offset", out var o))
                    foreach (var v in o) { long.TryParse(v, out at); break; }
                throw new OffsetMismatch(at);
            }
            if (!res.IsSuccessStatusCode) throw new IOException("patch " + (int)res.StatusCode);
            if (res.Headers.TryGetValues("File-Key", out var keys))
                foreach (var k in keys) return k;
            return null;
        }

        /// <summary>Сервер назвал своё смещение (409) — продолжаем с него.</summary>
        private sealed class OffsetMismatch : IOException
        {
            public readonly long Offset;
            public OffsetMismatch(long offset) : base("offset mismatch") { Offset = offset; }
        }

        /// <summary>
        /// Сколько байт у сервера без дыр. Нужно, когда все части ушли, а
        /// ключа нет: ответ на последнюю мог потеряться.
        /// </summary>
        private static long? HeadOffset(string id)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, Base + "/files/" + id);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token());
                using var res = Http.SendAsync(req).GetAwaiter().GetResult();
                if (!res.IsSuccessStatusCode) return null;
                if (res.Headers.TryGetValues("Upload-Offset", out var v))
                    foreach (var sVal in v) if (long.TryParse(sVal, out var at)) return at;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Качает файл по ссылке. Ссылку мог записать другой клиент со своим
        /// адресом сервера — берём из неё только путь, а основу свою: сервер
        /// мог переехать, а ссылки в переписке остались прежними.
        /// </summary>
        public static byte[] Download(string url, Action<double> onProgress,
                                      CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                using var res = Http.GetAsync(Resolve(url), HttpCompletionOption.ResponseHeadersRead, ct)
                                    .GetAwaiter().GetResult();
                if (!res.IsSuccessStatusCode) return null;
                long total = res.Content.Headers.ContentLength ?? 0;

                using var src = res.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var outMs = new MemoryStream(total > 0 ? (int)Math.Min(total, int.MaxValue) : 64 * 1024);
                var buf = new byte[64 * 1024];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    outMs.Write(buf, 0, n);
                    if (total > 0) onProgress?.Invoke((double)outMs.Length / total);
                }
                return outMs.ToArray();
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>Путь из чужой ссылки на своей основе.</summary>
        public static string Resolve(string url)
        {
            if (string.IsNullOrEmpty(Base) || string.IsNullOrWhiteSpace(url)) return url;
            int i = url.IndexOf("/files/", StringComparison.Ordinal);
            return i >= 0 ? Base + url.Substring(i) : url;
        }

        /// <summary>
        /// Тело запроса, которое само считает отправленное.
        /// </summary>
        /// <remarks>
        /// Готовый ByteArrayContent отдал бы прогресс одним скачком в конце —
        /// ровно та беда, от которой уходим: полоса минутами стоит на нуле.
        /// Здесь байты пишутся в сокет нами, и видно каждые 64 КБ.
        /// </remarks>
        private sealed class ProgressContent : HttpContent
        {
            private readonly byte[] _data;
            private readonly long _from;
            private readonly long _till;
            private readonly Action<long> _onBytes;
            private readonly CancellationToken _ct;

            public ProgressContent(byte[] data, long from, long till,
                                   Action<long> onBytes, CancellationToken ct)
            {
                _data = data; _from = from; _till = till; _onBytes = onBytes; _ct = ct;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                const int Step = 64 * 1024;
                long off = _from;
                while (off < _till)
                {
                    _ct.ThrowIfCancellationRequested();
                    int len = (int)Math.Min(Step, _till - off);
                    await stream.WriteAsync(_data, (int)off, len, _ct).ConfigureAwait(false);
                    off += len;
                    // Отдаём именно сколько ушло: доля считается снаружи, на
                    // все дорожки сразу — по отдельной части она бессмысленна.
                    _onBytes?.Invoke(len);
                }
                await stream.FlushAsync(_ct).ConfigureAwait(false);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _till - _from;
                return true;
            }
        }
        // ---------------------------------------------------------------
        //  Сторона базы: в сообщении вместо тела файла — ссылка
        // ---------------------------------------------------------------

        private static int _columns = -1;

        /// <summary>
        /// Есть ли в базе колонки file_url/file_size (миграция 26).
        /// </summary>
        /// <remarks>
        /// Миграции накатывает администратор, и на части машин программа
        /// работает со старой базой. Поэтому спрашиваем один раз и дальше
        /// держим ответ: без колонок всё идёт по-старому, через file_data,
        /// а не падает с «Unknown column».
        /// </remarks>
        internal static bool ColumnsExist(MySqlConnection conn)
        {
            if (_columns >= 0) return _columns == 1;
            try
            {
                using var cmd = new MySqlCommand("SELECT file_url, file_size FROM messages LIMIT 0", conn);
                cmd.ExecuteNonQuery();
                _columns = 1;
            }
            catch { _columns = 0; }
            return _columns == 1;
        }

        /// <summary>«Есть вложение» — тело в базе ИЛИ ссылка на сервис.</summary>
        internal static string HasFileSql(string alias, MySqlConnection conn)
        {
            string a = string.IsNullOrEmpty(alias) ? "" : alias + ".";
            return ColumnsExist(conn)
                ? $"({a}file_data IS NOT NULL OR {a}file_url IS NOT NULL)"
                : $"({a}file_data IS NOT NULL)";
        }

        /// <summary>Колонки ссылки и размера — или заглушки на старой базе.</summary>
        internal static string FileColsSql(string alias, MySqlConnection conn)
        {
            string a = string.IsNullOrEmpty(alias) ? "" : alias + ".";
            return ColumnsExist(conn)
                ? $"{a}file_url, {a}file_size"
                : "NULL AS file_url, NULL AS file_size";
        }

        /// <summary>Ссылка на вложение сообщения или null.</summary>
        internal static string UrlOfMessage(MySqlConnection conn, string table, long id)
        {
            if (!ColumnsExist(conn)) return null;
            try
            {
                using var cmd = new MySqlCommand($"SELECT file_url FROM {table} WHERE id=@id", conn);
                cmd.Parameters.AddWithValue("@id", id);
                var o = cmd.ExecuteScalar();
                var s = (o == null || o == DBNull.Value) ? null : o.ToString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            catch { return null; }
        }

        /// <summary>
        /// Пробует взять вложение с сервиса. null — ссылки нет или не вышло,
        /// тогда зовущий читает file_data как раньше.
        /// </summary>
        internal static byte[] TryFetch(MySqlConnection conn, string table, long id,
                                        Action<double> onProgress = null,
                                        CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(Base)) return null;
            var url = UrlOfMessage(conn, table, id);
            return url == null ? null : Download(url, onProgress, ct);
        }
    }
}
