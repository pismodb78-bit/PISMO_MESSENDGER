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
            const meta = loadMeta(id);
            if (!meta) continue;
            if (meta.sha !== sha || meta.total !== total) continue;
            if (sizeOf(tmpPath(id)) < 0) continue;      // файла нет
            if (solidPrefix(meta) >= total) continue;   // он уже целый
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
            const have = solidPrefix(loadMeta(found) || {});
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
    // Файл нужного размера заводим сразу, «дырявым»: места на диске он пока
    // не занимает, зато в него можно писать с любого места. Это и позволяет
    // клиенту слать несколько частей разом, не дожидаясь ответа на
    // предыдущую — именно так загружают файлы мессенджеры, и именно это
    // убирает круг до сервера между частями.
    fs.writeFileSync(tmpPath(id), '');
    try { fs.truncateSync(tmpPath(id), total); } catch {}
    saveMeta(id, { total, name, sha: isSha(sha) ? sha : '', at: Date.now(), ranges: [] });
    log('создана загрузка', id, name, total);
    send(res, 201, {
        'Upload-Id': id,
        'Upload-Offset': '0',
        'Upload-Length': String(total),
        'Location': '/files/' + id,
    });
}

/**
 * Учёт принятого по кускам.
 *
 * Пока части шли строго по очереди, «сколько принято» равнялось размеру
 * файла. С параллельной отправкой это уже не так: файл заведён во всю длину
 * заранее, а куски приезжают вразнобой. Поэтому принятое держим списком
 * непересекающихся отрезков, сливая соседние.
 *
 * Клиенту при этом отвечаем не суммой, а СПЛОШНЫМ НАЧАЛОМ — до первой дыры:
 * возобновлять отправку можно только оттуда, и это же число понимают старые
 * клиенты, которые шлют строго последовательно.
 */
/**
 * Учёт загрузок держим В ПАМЯТИ, а на диск пишем следом.
 *
 * Части приходят параллельно, и каждый запрос раньше читал учёт из файла,
 * дополнял своим куском и записывал обратно. Пока один это делал, другой
 * успевал прочитать прежнее — и записывал поверх, теряя чужие части. Файл
 * оставался правильным, а учёт — нет: сервер считал принятым только
 * последний кусок. Теперь источник правды один на процесс, а файл нужен,
 * чтобы пережить перезапуск.
 */
const metaCache = new Map();

function loadMeta(id) {
    const cached = metaCache.get(id);
    if (cached) return cached;
    try {
        const meta = JSON.parse(fs.readFileSync(metaPath(id), 'utf8'));
        if (!Array.isArray(meta.ranges)) meta.ranges = [];
        metaCache.set(id, meta);
        return meta;
    } catch { return null; }
}

function saveMeta(id, meta) {
    metaCache.set(id, meta);
    try { fs.writeFileSync(metaPath(id), JSON.stringify(meta)); } catch {}
}

function forgetMeta(id) {
    metaCache.delete(id);
    fs.rmSync(metaPath(id), { force: true });
}

function addRange(meta, from, to) {
    const list = (meta.ranges || []).concat([[from, to]]);
    list.sort((a, b) => a[0] - b[0]);
    const merged = [];
    for (const r of list) {
        const last = merged[merged.length - 1];
        if (last && r[0] <= last[1]) last[1] = Math.max(last[1], r[1]);
        else merged.push([r[0], r[1]]);
    }
    meta.ranges = merged;
    return merged;
}

/** Докуда файл принят без дыр. */
function solidPrefix(meta) {
    const list = meta.ranges || [];
    return list.length > 0 && list[0][0] === 0 ? list[0][1] : 0;
}

/** Всё ли принято. */
function complete(meta) {
    return solidPrefix(meta) >= (meta.total || 0) && (meta.total || 0) > 0;
}

// ── HEAD /files/<id> — сколько уже принято ──────────────────────────
function headUpload(res, id) {
    if (sizeOf(tmpPath(id)) < 0) return send(res, 404);
    const meta = loadMeta(id) || {};
    send(res, 200, {
        'Upload-Offset': String(solidPrefix(meta)),
        'Upload-Length': String(meta.total || 0),
    });
}

// ── PATCH /files/<id> — дописать ────────────────────────────────────
function patchUpload(req, res, id) {
    const file = tmpPath(id);
    if (sizeOf(file) < 0) return send(res, 404);

    const meta = loadMeta(id);
    if (!meta) return send(res, 404);
    const total = meta.total || 0;
    const solid = solidPrefix(meta);

    const offset = parseInt(req.headers['upload-offset'] || '-1', 10);
    // Писать можно с любого места в пределах файла: части приходят
    // параллельно и вразнобой. Отказываем только бессмыслице — отрицательному
    // смещению и выходу за конец.
    if (!Number.isFinite(offset) || offset < 0 || offset > total) {
        return send(res, 409, { 'Upload-Offset': String(solid) }, 'offset mismatch');
    }

    // Пишем ровно с указанного места, не трогая остальное.
    const out = fs.createWriteStream(file, { flags: 'r+', start: offset });
    let written = 0;
    let failed = false;

    req.on('data', chunk => {
        written += chunk.length;
        if (offset + written > total) {     // прислали больше, чем обещали
            failed = true;
            req.destroy();
        }
    });
    req.on('error', () => { failed = true; });
    req.on('aborted', () => log('обрыв', id, offset + '+' + written + '/' + total));

    req.pipe(out);

    out.on('error', () => { failed = true; send(res, 500, {}, 'write failed'); });
    out.on('close', async () => {
        if (failed) {
            // Часть доехала не целиком — засчитываем ровно принятое, чтобы
            // повтор не вёз заново то, что уже легло на диск.
            if (written > 0) {
                addRange(meta, offset, offset + written);
                saveMeta(id, meta);
            }
            try { res.destroy(); } catch {}
            return;
        }

        addRange(meta, offset, offset + written);
        saveMeta(id, meta);

        if (!complete(meta)) {
            // Каждая принятая часть — строка в журнале. Когда отправка «не
            // идёт», первый вопрос всегда один: растёт принятое или стоит.
            log('порция', id, solidPrefix(meta) + '/' + total);
            return send(res, 204, { 'Upload-Offset': String(solidPrefix(meta)) });
        }

        // Дошло до конца — проверяем отпечаток и переносим в хранилище.
        //
        // Отпечаток здесь не формальность: при параллельной отправке он
        // единственный, кто подтверждает, что все части легли на свои места.
        try {
            const sha = await hashFile(file);
            if (meta.sha && meta.sha !== sha) {
                fs.rmSync(file, { force: true });
                forgetMeta(id);
                return send(res, 422, {}, 'sha mismatch');
            }
            const dest = blobPath(sha);
            if (sizeOf(dest) === total) fs.rmSync(file, { force: true });  // уже есть
            else fs.renameSync(file, dest);
            forgetMeta(id);
            log('принят', sha.slice(0, 12), meta.name, total);
            send(res, 200, {
                'Upload-Offset': String(total),
                'Upload-Length': String(total),
                'File-Key': sha,
            });
        } catch (e) {
            log('ошибка завершения', id, e && e.message);
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
