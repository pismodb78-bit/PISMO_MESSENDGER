// ============================================================
//  PISMO — файловый сервис (загрузка с докачкой + отдача с перемоткой)
//
//  ЗАЧЕМ. Вложения ездили в саму базу: клиент дописывал LONGBLOB порциями
//  через CONCAT. Каждая дозапись заставляет сервер перечитать и переписать
//  ВЕСЬ накопленный блоб — работа растёт квадратом от размера файла. Прогресс
//  при этом известен только по границам порций, видео нельзя перемотать
//  (блоб читается целиком), а телефон возит мегабайты по протоколу MySQL,
//  который для этого не делали.
//
//  Здесь файл кладётся на диск, а в базе остаётся ссылка. Протокол — тот же,
//  что у tus и у черновика IETF «Resumable Uploads»: три запроса.
//
//      POST  /files            создать загрузку     → Upload-Id
//      HEAD  /files/<id>       сколько уже принято  → Upload-Offset
//      PATCH /files/<id>       дописать с Upload-Offset
//      GET   /files/<sha>      отдать файл (с Range — перемотка видео)
//
//  ДЕДУПЛИКАЦИЯ. Файл хранится под своей sha256. Если прислали sha заранее и
//  такой файл уже лежит — POST сразу отвечает «готово», и по сети не уходит
//  ни байта: то же видео второму собеседнику отправляется мгновенно.
//
//  ДОСТУП. Заголовок Authorization: Bearer <jwt>, подпись HS256 тем же
//  секретом, что у релея (JWT_SECRET) — клиенты его уже умеют выписывать.
//  REQUIRE_JWT=0 отключает проверку (для отладки).
//
//  Запуск:  FILES_PORT=8081 FILES_DIR=/opt/pismo-ws/files node files.js
//  На VPS — службой, см. install-files.sh.
// ============================================================

const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const PORT = parseInt(process.env.FILES_PORT || '8081', 10);
const ROOT = process.env.FILES_DIR || '/opt/pismo-ws/files';
const TMP = path.join(ROOT, 'tmp');
const BLOBS = path.join(ROOT, 'blobs');
const JWT_SECRET = process.env.JWT_SECRET
    || 'uc5KT2e+qYwa6tb0HUXnLZwsC55VuB93szkSpkucr8i1BFjKA6RXbyIrjk0+ign9';
const REQUIRE_JWT = process.env.REQUIRE_JWT !== '0';
// Потолок размера. Без него один кривой клиент забьёт диск.
const MAX_SIZE = parseInt(process.env.FILES_MAX_SIZE || String(2 * 1024 * 1024 * 1024), 10);
// Сколько держать незавершённые загрузки. Докачка после обрыва — обычное
// дело, а вот брошенное через сутки уже никому не нужно.
const TMP_TTL_MS = 24 * 60 * 60 * 1000;

fs.mkdirSync(TMP, { recursive: true });
fs.mkdirSync(BLOBS, { recursive: true });

const log = (...a) => console.log('[FILES]', ...a);

// ── Доступ ──────────────────────────────────────────────────────────
function verifyJwt(token) {
    try {
        if (!token || typeof token !== 'string') return null;
        const parts = token.split('.');
        if (parts.length !== 3) return null;
        const expected = crypto.createHmac('sha256', JWT_SECRET)
            .update(parts[0] + '.' + parts[1]).digest('base64url');
        const a = Buffer.from(expected), b = Buffer.from(parts[2]);
        if (a.length !== b.length || !crypto.timingSafeEqual(a, b)) return null;
        const payload = JSON.parse(Buffer.from(
            parts[1].replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8'));
        if (!payload.exp || Math.floor(Date.now() / 1000) >= payload.exp) return null;
        return payload;
    } catch { return null; }
}

function allowed(req) {
    if (!REQUIRE_JWT) return true;
    const h = req.headers['authorization'] || '';
    return !!verifyJwt(h.startsWith('Bearer ') ? h.slice(7).trim() : '');
}

// ── Мелочи ──────────────────────────────────────────────────────────
const isSha = s => typeof s === 'string' && /^[a-f0-9]{64}$/.test(s);
const isId = s => typeof s === 'string' && /^[a-f0-9]{32}$/.test(s);

function send(res, code, headers = {}, body = '') {
    res.writeHead(code, Object.assign({ 'Cache-Control': 'no-store' }, headers));
    res.end(body);
}

/** Имя файла для скачивания: без путей и управляющих символов. */
function safeName(raw) {
    if (!raw) return 'file';
    let s = String(raw).replace(/[\r\n"\\]/g, '').replace(/[/\\]/g, '_').trim();
    return s.slice(0, 120) || 'file';
}

function decodeName(req) {
    const b64 = req.headers['upload-name'];
    if (!b64) return 'file';
    try { return safeName(Buffer.from(String(b64), 'base64').toString('utf8')); }
    catch { return 'file'; }
}

const MIME = {
    jpg: 'image/jpeg', jpeg: 'image/jpeg', png: 'image/png', gif: 'image/gif',
    webp: 'image/webp', mp4: 'video/mp4', webm: 'video/webm', mov: 'video/quicktime',
    mp3: 'audio/mpeg', wav: 'audio/wav', ogg: 'audio/ogg', pdf: 'application/pdf',
    txt: 'text/plain; charset=utf-8', zip: 'application/zip',
};
const mimeOf = name => MIME[String(name).split('.').pop().toLowerCase()] || 'application/octet-stream';

const blobPath = sha => path.join(BLOBS, sha);
const tmpPath = id => path.join(TMP, id);
const metaPath = id => path.join(TMP, id + '.json');

function sizeOf(p) {
    try { return fs.statSync(p).size; } catch { return -1; }
}

/** sha256 готового файла — одним проходом по диску. */
function hashFile(p) {
    return new Promise((resolve, reject) => {
        const h = crypto.createHash('sha256');
        fs.createReadStream(p)
            .on('data', c => h.update(c))
            .on('end', () => resolve(h.digest('hex')))
            .on('error', reject);
    });
}

/**
 * Незавершённая загрузка того же файла, если она есть.
 *
 * Ищем перебором по каталогу незавершённых: их единицы (готовое сразу
 * уезжает в хранилище, брошенное чистится через сутки), а отдельный индекс
 * пришлось бы чинить после каждого падения.
 */
function findUnfinished(sha, total) {
    try {
        for (const f of fs.readdirSync(TMP)) {
            if (!f.endsWith('.json')) continue;
            const id = f.slice(0, -5);
            if (!isId(id)) continue;
            let meta;
            try { meta = JSON.parse(fs.readFileSync(path.join(TMP, f), 'utf8')); }
            catch { continue; }
            if (meta.sha !== sha || meta.total !== total) continue;
            const have = sizeOf(tmpPath(id));
            if (have < 0 || have >= total) continue;   // файла нет или он уже целый
            return id;
        }
    } catch {}
    return null;
}

// ── POST /files — создать загрузку ──────────────────────────────────
function createUpload(req, res) {
    const total = parseInt(req.headers['upload-length'] || '0', 10);
    if (!Number.isFinite(total) || total <= 0) return send(res, 400, {}, 'Upload-Length');
    if (total > MAX_SIZE) return send(res, 413, {}, 'too large');

    const sha = String(req.headers['upload-sha'] || '').toLowerCase();
    const name = decodeName(req);

    // Уже лежит — отправлять нечего.
    if (isSha(sha) && sizeOf(blobPath(sha)) === total) {
        log('дедупликация', sha.slice(0, 12), name);
        return send(res, 200, {
            'Upload-Offset': String(total),
            'Upload-Length': String(total),
            'File-Key': sha,
        });
    }

    // Эту отправку уже начинали, и она оборвалась — продолжаем её, а не
    // заводим новую.
    //
    // Без этого повтор после обрыва начинался с нуля: клиент спрашивал новую
    // загрузку, получал новый id, и уже принятые мегабайты оставались лежать
    // мусором. На мобильной связи, где обрыв — обычное дело, большой файл так
    // мог не доехать вовсе: каждая попытка начиналась заново.
    if (isSha(sha)) {
        const found = findUnfinished(sha, total);
        if (found) {
            const have = sizeOf(tmpPath(found));
            log('продолжаем загрузку', found, name, have + '/' + total);
            return send(res, 200, {
                'Upload-Id': found,
                'Upload-Offset': String(have),
                'Upload-Length': String(total),
                'Location': '/files/' + found,
            });
        }
    }

    const id = crypto.randomBytes(16).toString('hex');
    fs.writeFileSync(tmpPath(id), '');
    fs.writeFileSync(metaPath(id), JSON.stringify({
        total, name, sha: isSha(sha) ? sha : '', at: Date.now(),
    }));
    log('создана загрузка', id, name, total);
    send(res, 201, {
        'Upload-Id': id,
        'Upload-Offset': '0',
        'Upload-Length': String(total),
        'Location': '/files/' + id,
    });
}

// ── HEAD /files/<id> — сколько уже принято ──────────────────────────
function headUpload(res, id) {
    const have = sizeOf(tmpPath(id));
    if (have < 0) return send(res, 404);
    let total = 0;
    try { total = JSON.parse(fs.readFileSync(metaPath(id), 'utf8')).total || 0; } catch {}
    send(res, 200, { 'Upload-Offset': String(have), 'Upload-Length': String(total) });
}

// ── PATCH /files/<id> — дописать ────────────────────────────────────
function patchUpload(req, res, id) {
    const file = tmpPath(id);
    const have = sizeOf(file);
    if (have < 0) return send(res, 404);

    let meta = {};
    try { meta = JSON.parse(fs.readFileSync(metaPath(id), 'utf8')); } catch {}
    const total = meta.total || 0;

    const offset = parseInt(req.headers['upload-offset'] || '-1', 10);
    // Клиент и сервер обязаны сойтись в том, с какого места продолжаем:
    // иначе дозапись легла бы поверх дырки или поверх уже принятого.
    if (offset !== have) {
        return send(res, 409, { 'Upload-Offset': String(have) }, 'offset mismatch');
    }

    const out = fs.createWriteStream(file, { flags: 'a' });
    let written = 0;
    let failed = false;

    req.on('data', chunk => {
        written += chunk.length;
        if (have + written > total) {       // прислали больше, чем обещали
            failed = true;
            req.destroy();
        }
    });
    req.on('error', () => { failed = true; });
    // Обрыв видно в журнале: «докуда дошли» — первое, что нужно знать, когда
    // отправка ходит по кругу.
    req.on('aborted', () => log('обрыв', id, sizeOf(file) + '/' + total));

    req.pipe(out);

    out.on('error', () => { failed = true; send(res, 500, {}, 'write failed'); });
    out.on('close', async () => {
        if (failed) { try { res.destroy(); } catch {} return; }
        const now = sizeOf(file);
        if (now < total) {
            return send(res, 204, { 'Upload-Offset': String(now) });
        }

        // Дошло до конца — проверяем отпечаток и переносим в хранилище.
        try {
            const sha = await hashFile(file);
            if (meta.sha && meta.sha !== sha) {
                // Содержимое не то, что обещали. Чаще всего это следствие
                // обрыва, после которого докачали не с того места.
                fs.rmSync(file, { force: true });
                fs.rmSync(metaPath(id), { force: true });
                return send(res, 422, {}, 'sha mismatch');
            }
            const dest = blobPath(sha);
            if (sizeOf(dest) === total) fs.rmSync(file, { force: true });  // уже есть
            else fs.renameSync(file, dest);
            fs.rmSync(metaPath(id), { force: true });
            log('принят', sha.slice(0, 12), meta.name, total);
            send(res, 200, {
                'Upload-Offset': String(total),
                'Upload-Length': String(total),
                'File-Key': sha,
            });
        } catch (e) {
            log('ошибка завершения', e.message);
            send(res, 500, {}, 'finish failed');
        }
    });
}

// ── GET /files/<sha> — отдать, с перемоткой ─────────────────────────
function serveFile(req, res, sha, name, headOnly) {
    const p = blobPath(sha);
    const size = sizeOf(p);
    if (size < 0) return send(res, 404);

    const type = mimeOf(name);
    const common = {
        'Content-Type': type,
        'Accept-Ranges': 'bytes',
        // Содержимое по этой ссылке неизменно — она и есть его отпечаток.
        'Cache-Control': 'public, max-age=31536000, immutable',
        'Content-Disposition': 'inline; filename="' + safeName(name) + '"',
    };

    const range = req.headers['range'];
    const m = range && /^bytes=(\d*)-(\d*)$/.exec(String(range).trim());
    if (m) {
        let start = m[1] === '' ? null : parseInt(m[1], 10);
        let end = m[2] === '' ? null : parseInt(m[2], 10);
        if (start === null && end === null) return send(res, 416, { 'Content-Range': `bytes */${size}` });
        if (start === null) { start = Math.max(0, size - end); end = size - 1; }   // последние N байт
        if (end === null || end >= size) end = size - 1;
        if (start > end || start >= size) {
            return send(res, 416, { 'Content-Range': `bytes */${size}` });
        }
        const headers = Object.assign({}, common, {
            'Content-Range': `bytes ${start}-${end}/${size}`,
            'Content-Length': String(end - start + 1),
        });
        res.writeHead(206, headers);
        if (headOnly) return res.end();
        return fs.createReadStream(p, { start, end }).pipe(res);
    }

    res.writeHead(200, Object.assign({}, common, { 'Content-Length': String(size) }));
    if (headOnly) return res.end();
    fs.createReadStream(p).pipe(res);
}

// ── Маршрутизация ───────────────────────────────────────────────────
const server = http.createServer((req, res) => {
    let url;
    try { url = new URL(req.url, 'http://x'); } catch { return send(res, 400); }
    const parts = url.pathname.split('/').filter(Boolean);   // files/<key>[/<name>]

    if (parts[0] === 'health') return send(res, 200, { 'Content-Type': 'text/plain' }, 'ok');
    if (parts[0] !== 'files') return send(res, 404);

    const key = parts[1] || '';
    const name = parts[2] ? safeName(decodeURIComponent(parts[2])) : (url.searchParams.get('name') || key);

    // Скачивание пускаем по ссылке без токена: ссылка и есть отпечаток
    // содержимого, угадать её нельзя. Загрузку — только своим.
    const reading = req.method === 'GET' || (req.method === 'HEAD' && isSha(key));
    if (!reading && !allowed(req)) return send(res, 401, {}, 'unauthorized');

    if (req.method === 'POST' && !key) return createUpload(req, res);
    if (req.method === 'HEAD' && isId(key)) return headUpload(res, key);
    if (req.method === 'PATCH' && isId(key)) return patchUpload(req, res, key);
    if ((req.method === 'GET' || req.method === 'HEAD') && isSha(key)) {
        return serveFile(req, res, key, name, req.method === 'HEAD');
    }
    send(res, 404);
});

// Соединение держится на всё время заливки порции — потолок должен быть
// щедрым, иначе сервер сам оборвёт медленную мобильную отправку.
server.requestTimeout = 0;
server.headersTimeout = 60_000;
server.setTimeout(0);

server.listen(PORT, () => log('слушаю :' + PORT + ', хранилище ' + ROOT));

// ── Уборка брошенных загрузок ───────────────────────────────────────
setInterval(() => {
    try {
        for (const f of fs.readdirSync(TMP)) {
            const p = path.join(TMP, f);
            const st = fs.statSync(p);
            if (Date.now() - st.mtimeMs > TMP_TTL_MS) {
                fs.rmSync(p, { force: true });
                log('убрал брошенное', f);
            }
        }
    } catch {}
}, 60 * 60 * 1000);
