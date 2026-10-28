using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PISMO
{
    /// <summary>
    /// Цитаты ответов в каналах.
    ///
    /// Страница канала чаще всего приезжает через релей, а у релея в строках
    /// нет ни имени автора исходного сообщения, ни его текста — только номер.
    /// Цитата получалась «↩ : »: ни кто, ни что. Теперь источник ищется на
    /// самой странице (обычно отвечают на то, что рядом), а чего там нет,
    /// догружается в фоне одним запросом и подставляется в уже нарисованную
    /// цитату — лента ради этого не пересобирается.
    /// </summary>
    public sealed partial class ServersForm
    {
        /// <summary>Номер сообщения → строка цитаты и автор. Общая на все
        /// каналы: номера сообщений сервера не повторяются.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string text, string sender)>
            _srvQuotes = new();

        /// <summary>Цитаты, которые ждут источник из базы.</summary>
        private readonly Dictionary<int, List<WeakReference<QuoteBlock>>> _srvQuoteWait = new();
        private bool _srvQuoteBusy;

        /// <summary>Цитата для ответа на <paramref name="replyToId"/>.</summary>
        private QuoteBlock MakeServerQuote(int replyToId, DataRow r, DataTable dt, int maxW, Color back)
        {
            (string text, string sender)? q = null;
            if (_srvQuotes.TryGetValue(replyToId, out var cached)) q = cached;
            else
            {
                q = ServerQuoteFromPage(replyToId, dt) ?? ServerQuoteFromRow(r);
                if (q != null) _srvQuotes[replyToId] = q.Value;
            }

            // Пока источник едет — «загрузка…» с местом под имя: высота цитаты
            // не меняется, когда он приедет, и лента под ней не прыгает.
            var block = new QuoteBlock(q?.sender ?? "…", q?.text ?? "загрузка…", maxW, back,
                Color.FromArgb(129, 140, 248), Color.FromArgb(88, 101, 242));
            if (q == null)
            {
                if (!_srvQuoteWait.TryGetValue(replyToId, out var list))
                    _srvQuoteWait[replyToId] = list = new List<WeakReference<QuoteBlock>>();
                list.Add(new WeakReference<QuoteBlock>(block));
                RequestServerQuotes();
            }
            return block;
        }

        /// <summary>Источник есть среди строк страницы — цитата готова сразу.</summary>
        private static (string text, string sender)? ServerQuoteFromPage(int id, DataTable dt)
        {
            if (dt == null) return null;
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                var t = dt.Rows[i];
                if (t["id"] == DBNull.Value || Convert.ToInt32(t["id"]) != id) continue;

                string who = ColText(t, "nm");
                if (string.IsNullOrWhiteSpace(who)) who = ColText(t, "sender_name");
                if (string.IsNullOrWhiteSpace(who)) who = ColText(t, "login");
                string text = "";
                try { text = Crypto.Dec(t["text"] == DBNull.Value ? "" : t["text"].ToString()); } catch { }
                bool img = Flag(t, "has_img"), aud = Flag(t, "has_audio"), vid = Flag(t, "has_video");
                string fn = ColText(t, "file_name");
                if (string.IsNullOrEmpty(fn)) fn = null;

                // Альбом — если за снимком идёт продолжение пачки того же автора.
                bool tile = img || MediaPlayerForm.IsVideo(
                    Path.GetExtension(fn ?? "").TrimStart('.').ToLowerInvariant());
                bool album = tile && Albums.IsPart(text);
                if (tile && !album)
                {
                    int sender = Convert.ToInt32(t["sender_id"]);
                    for (int k = i + 1; k < dt.Rows.Count; k++)
                    {
                        var n = dt.Rows[k];
                        if (Convert.ToInt32(n["sender_id"]) != sender) continue;
                        string nt = "";
                        try { nt = Crypto.Dec(n["text"] == DBNull.Value ? "" : n["text"].ToString()); } catch { }
                        album = Albums.IsPart(nt);
                        break;
                    }
                }
                return (QuoteText.Describe(text, img, aud, vid, fn, album), who);
            }
            return null;
        }

        /// <summary>Из подзапросов r_sender/r_text — когда страница пришла из базы.</summary>
        private static (string text, string sender)? ServerQuoteFromRow(DataRow r)
        {
            var t = r.Table;
            if (!t.Columns.Contains("r_text") && !t.Columns.Contains("r_sender")) return null;
            string who = ColText(r, "r_sender");
            if (string.IsNullOrWhiteSpace(who)) who = ColText(r, "r_login");
            string text = "";
            try { text = Crypto.Dec(ColText(r, "r_text")); } catch { }
            text = QuoteText.Describe(text, false, false, false, null);
            // Пустой текст — это вложение без подписи: чем оно было, отсюда
            // не видно. Пусть скажет база.
            if (string.IsNullOrWhiteSpace(who) || string.IsNullOrWhiteSpace(text)) return null;
            return (text, who);
        }

        private static bool Flag(DataRow r, string col)
        {
            if (!r.Table.Columns.Contains(col) || r[col] == DBNull.Value) return false;
            try { return Convert.ToInt64(r[col]) != 0; } catch { return false; }
        }

        /// <summary>Догрузить ждущие цитаты одним запросом, в фоне.</summary>
        private void RequestServerQuotes()
        {
            if (_srvQuoteBusy || _srvQuoteWait.Count == 0 || IsDisposed) return;
            _srvQuoteBusy = true;
            // Отложенно: сборка страницы ещё идёт и добавит своих ждущих.
            try
            {
                BeginInvoke(new Action(() =>
                {
                    var ids = new List<int>(_srvQuoteWait.Keys);
                    bool media = MediaColumnsExist();
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        Dictionary<int, (string text, string sender)> got = null;
                        try
                        {
                            using var conn = DBHelper.OpenConnection();
                            got = QuoteText.Load(conn, "server_messages", "x.channel_id = m.channel_id",
                                                 ids, hasDeleted: false, media: media);
                        }
                        catch { }
                        try
                        {
                            if (IsDisposed || !IsHandleCreated) return;
                            BeginInvoke(new Action(() => ApplyServerQuotes(ids, got)));
                        }
                        catch { }
                    });
                }));
            }
            catch { _srvQuoteBusy = false; }
        }

        private void ApplyServerQuotes(List<int> ids, Dictionary<int, (string text, string sender)> got)
        {
            _srvQuoteBusy = false;
            if (got == null)
            {
                // База не ответила — ждущие остаются ждать: следующая сборка
                // страницы попросит их снова.
                return;
            }
            foreach (int id in ids)
            {
                if (!got.TryGetValue(id, out var q)) continue;
                _srvQuotes[id] = q;
                if (!_srvQuoteWait.TryGetValue(id, out var list)) continue;
                _srvQuoteWait.Remove(id);
                foreach (var wr in list)
                    if (wr.TryGetTarget(out var b) && !b.IsDisposed)
                        try { b.SetContent(q.sender, q.text); } catch { }
            }
            // Пока ходили в базу, могли появиться новые ждущие.
            if (_srvQuoteWait.Count > 0) RequestServerQuotes();
        }
    }
}
