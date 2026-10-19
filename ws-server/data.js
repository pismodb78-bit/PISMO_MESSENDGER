// ============================================================
//  PISMO — чтение переписки ЧЕРЕЗ РЕЛЕЙ
//
//  ЗАЧЕМ. Телефон ходит в MySQL напрямую, и на мобильной связи это дорого
//  не байтами, а КРУГАМИ до сервера. Протокол MySQL разговорчив: открыть
//  соединение — несколько обменов с рукопожатием и аутентификацией, затем
//  запрос и ответ. При круге в 100–300 мс (а на 4G это норма) один заход в
//  чат стоит секунд, и никакое ускорение канала тут не помогает.
//
//  Релей же стоит РЯДОМ с базой: круг до неё — доли миллисекунды. Поэтому
//  телефон спрашивает не базу, а релей, по уже открытому сокету: один обмен
//  вместо десятка. Так устроены все мессенджеры — у клиента одно соединение
//  со своим сервером, а в базу ходит сервер.
//
//  ЧТО МОЖНО СПРОСИТЬ. Только перечисленные здесь операции и только за себя:
//  сокет уже зарегистрирован под конкретным userId, и именно он подставляется
//  в запросы. Произвольный SQL клиент прислать не может — иначе это была бы
//  дыра размером с базу.
//
//  Работает, только если релею дали доступ к базе (PISMO_DB) и установлен
//  mysql2. Нет — модуль молчит, а клиенты продолжают ходить в базу сами.
// ============================================================

let mysql = null;
let pool = null;
let ready = false;

function init() {
    const dbUrl = process.env.PISMO_DB;
    if (!dbUrl) {
        console.log('[DATA] выключен: нет PISMO_DB');
        return;
    }
    try {
        mysql = require('mysql2/promise');
    } catch {
        console.log('[DATA] выключен: не установлен mysql2');
        return;
    }
    try {
        pool = mysql.createPool(dbUrl + '?connectionLimit=8');
        ready = true;
        console.log('[DATA] включён: чтение переписки идёт через релей');
    } catch (e) {
        console.log('[DATA] выключен: ' + e.message);
    }
}

const isReady = () => ready;

const num = (v, def = 0) => {
    const n = Number(v);
    return Number.isFinite(n) ? n : def;
};

/** Сколько строк отдаём за раз. Больше страницы клиенту не нужно. */
const capLimit = (v, def = 40) => Math.min(200, Math.max(1, num(v, def)));

/**
 * Выборка сообщений с одной оговоркой: колонки file_url может не быть.
 *
 * Её приносит миграция 26, и накатывают её администраторы, а не мы. Пока её
 * нет, «есть вложение» определяется только по телу в базе — иначе запрос
 * падал бы с «Unknown column», и переписка не открылась бы вовсе.
 */
let fileUrlOk = true;

async function selectMessages(sqlTemplate, params, alias) {
    if (fileUrlOk) {
        try {
            const [rows] = await pool.query(
                sqlTemplate.replace('%HASFILE%',
                    `(${alias}.file_data IS NOT NULL OR ${alias}.file_url IS NOT NULL)`),
                params);
            return rows;
        } catch (e) {
            if (!/file_url/i.test(e.message || '')) throw e;
            fileUrlOk = false;      // миграции нет — дальше без неё
        }
    }
    const [rows] = await pool.query(
        sqlTemplate.replace('%HASFILE%', `(${alias}.file_data IS NOT NULL)`), params);
    return rows;
}

// ── Операции ────────────────────────────────────────────────────────
//
// Каждая получает (uid, args) и возвращает массив строк. uid — ТОЛЬКО из
// зарегистрированного сокета: что бы клиент ни прислал в args, спросить он
// может лишь за себя.

const ops = {
    /** Страница личной переписки. args: { with, limit, before } */
    async dm(uid, a) {
        const them = num(a.with);
        const limit = capLimit(a.limit);
        const before = num(a.before);
        if (them <= 0) return [];

        // Две ветки по направлениям переписки, а не OR: каждая ложится на
        // индекс (sender_id, receiver_id, id) и берёт ровно свой хвост. С OR
        // сервер собирал бы всю переписку и сортировал в файле — на длинной
        // истории это десятки тысяч широких строк на каждый заход в чат.
        const cursor = before > 0 ? 'AND id < ?' : '';
        const keys = `
            (SELECT id FROM messages WHERE sender_id=? AND receiver_id=? ${cursor}
              ORDER BY id DESC LIMIT ${limit})
            UNION
            (SELECT id FROM messages WHERE sender_id=? AND receiver_id=? ${cursor}
              ORDER BY id DESC LIMIT ${limit})`;
        const params = before > 0
            ? [uid, them, before, them, uid, before]
            : [uid, them, them, uid];

        const rows = await selectMessages(
            `SELECT m.id, m.sender_id, m.text, m.file_name, m.reply_to_id,
                    m.is_deleted, m.edited_at, m.is_read,
                    UNIX_TIMESTAMP(m.created_at) AS created_ts,
                    TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender_name, u.login,
                    (m.image_data IS NOT NULL) AS has_img,
                    (m.audio_data IS NOT NULL) AS has_audio,
                    (m.video_data IS NOT NULL) AS has_video,
                    %HASFILE% AS has_file
               FROM (${keys}) k
               JOIN messages m ON m.id = k.id
               JOIN users u ON u.id = m.sender_id
              ORDER BY m.id DESC LIMIT ${limit}`, params, 'm');
        return rows.reverse();
    },

    /** Страница групповой переписки. args: { group, limit, before } */
    async group(uid, a) {
        const gid = num(a.group);
        const limit = capLimit(a.limit);
        const before = num(a.before);
        if (gid <= 0) return [];
        // Чужие группы не отдаём: спрашивать можно только то, где состоишь.
        const [member] = await pool.query(
            'SELECT 1 FROM group_members WHERE group_id=? AND user_id=? LIMIT 1', [gid, uid]);
        if (member.length === 0) return [];
        const cursor = before > 0 ? 'AND gm.id < ?' : '';
        const params = before > 0 ? [gid, before] : [gid];
        const rows = await selectMessages(
            `SELECT gm.id, gm.sender_id, gm.text, gm.file_name, gm.reply_to_id,
                    gm.is_deleted, gm.edited_at,
                    UNIX_TIMESTAMP(gm.created_at) AS created_ts,
                    TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender_name, u.login,
                    (gm.image_data IS NOT NULL) AS has_img,
                    (gm.audio_data IS NOT NULL) AS has_audio,
                    (gm.video_data IS NOT NULL) AS has_video,
                    %HASFILE% AS has_file
               FROM group_messages gm
               LEFT JOIN users u ON u.id = gm.sender_id
              WHERE gm.group_id=? ${cursor}
              ORDER BY gm.id DESC LIMIT ${limit}`, params, 'gm');
        return rows.reverse();
    },

    /** Страница канала сервера. args: { channel, limit, before } */
    async channel(uid, a) {
        const cid = num(a.channel);
        const limit = capLimit(a.limit);
        const before = num(a.before);
        if (cid <= 0) return [];
        // Канал чужого сервера — не наше дело.
        const [member] = await pool.query(
            `SELECT 1 FROM server_channels sc
               JOIN server_members sm ON sm.server_id = sc.server_id
              WHERE sc.id=? AND sm.user_id=? LIMIT 1`, [cid, uid]);
        if (member.length === 0) return [];
        const cursor = before > 0 ? 'AND sm2.id < ?' : '';
        const params = before > 0 ? [cid, before] : [cid];
        const rows = await selectMessages(
            `SELECT sm2.id, sm2.sender_id, sm2.text, sm2.file_name, sm2.reply_to_id,
                    UNIX_TIMESTAMP(sm2.created_at) AS created_ts,
                    TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender_name, u.login,
                    (sm2.image_data IS NOT NULL) AS has_img,
                    (sm2.audio_data IS NOT NULL) AS has_audio,
                    (sm2.video_data IS NOT NULL) AS has_video,
                    %HASFILE% AS has_file
               FROM server_messages sm2
               LEFT JOIN users u ON u.id = sm2.sender_id
              WHERE sm2.channel_id=? ${cursor}
              ORDER BY sm2.id DESC LIMIT ${limit}`, params, 'sm2');
        return rows.reverse();
    },

    /**
     * Сводка для опроса: непрочитанное по собеседникам и последние номера
     * в группах и каналах. Это самый частый запрос приложения — он уходит
     * каждые несколько секунд, и раньше каждый такой тик стоил телефону
     * отдельного похода в базу.
     */
    async poll(uid) {
        const [unread] = await pool.query(
            `SELECT sender_id AS id, COUNT(*) AS n
               FROM messages WHERE receiver_id=? AND is_read=0
              GROUP BY sender_id`, [uid]);
        const [groups] = await pool.query(
            `SELECT gm.group_id AS id, MAX(gm.id) AS last
               FROM group_messages gm
               JOIN group_members m ON m.group_id = gm.group_id AND m.user_id = ?
              GROUP BY gm.group_id`, [uid]);
        const [channels] = await pool.query(
            `SELECT sm.channel_id AS id, MAX(sm.id) AS last
               FROM server_messages sm
               JOIN server_channels sc ON sc.id = sm.channel_id
               JOIN server_members mm ON mm.server_id = sc.server_id AND mm.user_id = ?
              GROUP BY sm.channel_id`, [uid]);
        return [{ unread, groups, channels }];
    },
};

/**
 * Выполнить операцию. Возвращает массив строк либо бросает — вызывающий
 * отвечает клиенту ошибкой, и тот спокойно идёт в базу сам.
 */
async function run(uid, op, args) {
    if (!ready) throw new Error('data off');
    const fn = ops[op];
    if (!fn) throw new Error('unknown op');
    if (!(uid > 0)) throw new Error('not registered');
    return await fn(uid, args || {});
}

module.exports = { init, isReady, run };
