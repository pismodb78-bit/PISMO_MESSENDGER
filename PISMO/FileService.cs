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

                // 2. Досылаем остаток. Обрыв на плохой связи — обычное дело,
                //    поэтому пробуем несколько раз и каждый раз продолжаем
                //    ровно с того места, которое назвал сервер.
                for (int attempt = 0; attempt <= MaxAttempts; )
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        string key = Patch(id, data, offset, onProgress, ct);
                        if (key != null) return UrlOf(key, name);
                        // Приняли, но файл ещё не целый — спросим, сколько есть.
                        if (++attempt > MaxAttempts) return null;
                        long? have = ResolveOffset(id, ct);
                        if (have == null) return null;
                        offset = have.Value;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (OffsetMismatch mm)
                    {
                        // Сервер назвал своё смещение. Это сверка, а не сбой:
                        // попытку не тратим, но и крутиться на месте не даём.
                        if (mm.Offset == offset && ++attempt > MaxAttempts) return null;
                        offset = Math.Max(0, Math.Min(mm.Offset, data.Length));
                        onProgress?.Invoke((double)offset / data.Length);
                    }
                    catch
                    {
                        if (++attempt > MaxAttempts) return null;
                        // Связь не возвращается мгновенно: ждём дольше с
                        // каждым разом, а не долбимся в один и тот же обрыв.
                        Thread.Sleep(2000 * attempt);
                        long? have = ResolveOffset(id, ct);
                        if (have == null) return null;
                        offset = have.Value;
                        onProgress?.Invoke((double)offset / data.Length);
                    }
                }
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>Одна попытка дозаписи. Возвращает ключ, когда дошли до конца.</summary>
        private static string Patch(string id, byte[] data, long from,
                                    Action<double> onProgress, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(new HttpMethod("PATCH"), Base + "/files/" + id);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token());
            req.Headers.TryAddWithoutValidation("Upload-Offset", from.ToString());
            req.Content = new ProgressContent(data, from, onProgress, ct);
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
        /// Сколько байт у сервера. Спрашиваем с повторами: один неудачный
        /// вопрос — не повод бросать отправку, на которую уже ушли минуты.
        /// </summary>
        private static long? ResolveOffset(string id, CancellationToken ct)
        {
            for (int i = 0; i < 3; i++)
            {
                ct.ThrowIfCancellationRequested();
                var have = HeadOffset(id);
                if (have != null) return have;
                Thread.Sleep(1500 * (i + 1));
            }
            return HeadOffset(id);
        }

        private static long? HeadOffset(string id)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, Base + "/files/" + id);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token());
                using var res = Http.SendAsync(req).GetAwaiter().GetResult();
                if (!res.IsSuccessStatusCode) return null;
                if (res.Headers.TryGetValues("Upload-Offset", out var v))
                    foreach (var s in v) return long.Parse(s);
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
            private readonly Action<double> _onProgress;
            private readonly CancellationToken _ct;

            public ProgressContent(byte[] data, long from, Action<double> onProgress, CancellationToken ct)
            {
                _data = data; _from = from; _onProgress = onProgress; _ct = ct;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                const int Step = 64 * 1024;
                long off = _from;
                while (off < _data.Length)
                {
                    _ct.ThrowIfCancellationRequested();
                    int len = (int)Math.Min(Step, _data.Length - off);
                    await stream.WriteAsync(_data, (int)off, len, _ct).ConfigureAwait(false);
                    off += len;
                    _onProgress?.Invoke((double)off / _data.Length);
                }
                await stream.FlushAsync(_ct).ConfigureAwait(false);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _data.Length - _from;
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
