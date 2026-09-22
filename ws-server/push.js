// ============================================================
//  PISMO — отправка push тем, кого нет на связи
//
//  ЗАЧЕМ. Пока приложение на телефоне открыто или держит фоновую службу,
//  события приходят по сокету, и push не нужен. Но фоновая служба стоит
//  постоянного уведомления в шторке, а выгруженное приложение не получает
//  вообще ничего. Push закрывает и то и другое: систему будит Google, а не
//  наша служба.
//
//  ЧТО ЗДЕСЬ. Релей и так видит каждое событие и знает, кто сейчас
//  подключён. Значит, ему же и решать: адресат на связи — ничего не делаем,
//  адресата нет — шлём push.
//
//  ЧЕГО ЗДЕСЬ НЕТ. Текста сообщения. Он лежит в базе зашифрованным, ключ
//  есть только у клиентов, и расшифровать его здесь было бы можно — но
//  тогда сервер начал бы читать переписку. Push несёт только «кто и куда
//  написал»; за содержимым клиент идёт в базу сам.
//
//  ЧТО НУЖНО, ЧТОБЫ ВКЛЮЧИЛОСЬ (без этого модуль молча спит):
//    npm install firebase-admin mysql2
//    FIREBASE_CREDENTIALS=/opt/pismo-ws/firebase.json   ← ключ сервисного аккаунта
//    PISMO_DB=mysql://user:pass@host:3307/bdauth        ← та же база, что у клиентов
// ============================================================

let admin = null;
let mysql = null;
let pool = null;
let ready = false;

function init() {
    const credPath = process.env.FIREBASE_CREDENTIALS;
    const dbUrl = process.env.PISMO_DB;

    if (!credPath || !dbUrl) {
        console.log('[PUSH] выключен: нет FIREBASE_CREDENTIALS или PISMO_DB');
        return;
    }

    try {
        admin = require('firebase-admin');
        mysql = require('mysql2/promise');
    } catch (e) {
        // Пакетов нет — это не поломка, а просто «push не настроен».
        console.log('[PUSH] выключен: не установлены firebase-admin / mysql2');
        return;
    }

    try {
        admin.initializeApp({ credential: admin.credential.cert(require(credPath)) });
        pool = mysql.createPool(dbUrl + '?connectionLimit=4');
        ready = true;
        console.log('[PUSH] включён');
    } catch (e) {
        console.log('[PUSH] выключен: ' + e.message);
    }
}

/** Адреса устройств этого человека. Пусто — слать некуда. */
async function tokensFor(userId) {
    if (!ready) return [];
    try {
        const [rows] = await pool.query(
            'SELECT token FROM device_tokens WHERE user_id = ?', [userId]);
        return rows.map(r => r.token);
    } catch (e) {
        console.log('[PUSH] не прочитали токены: ' + e.message);
        return [];
    }
}

/** Имя отправителя — чтобы в шторке было видно, от кого. */
async function nameOf(userId) {
    if (!ready) return '';
    try {
        const [rows] = await pool.query(
            "SELECT TRIM(CONCAT(COALESCE(Name,''),' ',COALESCE(Surname,''))) AS nm, login " +
            'FROM users WHERE id = ?', [userId]);
        if (!rows.length) return '';
        return (rows[0].nm || '').trim() || rows[0].login || '';
    } catch { return ''; }
}

/**
 * Шлёт push. data-сообщение, БЕЗ блока notification: иначе при свёрнутом
 * приложении шторку рисует сама система, и клиент не может ни выбрать
 * текст, ни промолчать, когда этот чат уже открыт.
 */
async function send(userId, data) {
    if (!ready) return;
    const tokens = await tokensFor(userId);
    if (!tokens.length) {
        console.log(`[PUSH] ${userId}: адресов нет — приложение не регистрировалось`);
        return;
    }

    const message = {
        data: Object.fromEntries(Object.entries(data).map(([k, v]) => [k, String(v)])),
        android: { priority: 'high' },
        tokens,
    };

    try {
        const res = await admin.messaging().sendEachForMulticast(message);
        console.log(`[PUSH] ${userId}: доставлено ${res.successCount} из ${tokens.length}`);
        // Причину отказа печатаем — без неё «не пришло» неотличимо от
        // «отправили, но Google не взял».
        res.responses.forEach((r) => {
            if (!r.success && r.error) console.log('[PUSH] отказ: ' + r.error.code);
        });
        // Протухшие адреса убираем сразу: иначе список растёт вечно, и
        // каждая отправка тратится на устройства, которых давно нет.
        const dead = [];
        res.responses.forEach((r, i) => {
            if (r.success) return;
            const code = r.error && r.error.code;
            if (code === 'messaging/registration-token-not-registered' ||
                code === 'messaging/invalid-argument') dead.push(tokens[i]);
        });
        if (dead.length) {
            await pool.query('DELETE FROM device_tokens WHERE token IN (?)', [dead]);
            console.log(`[PUSH] убрали ${dead.length} протухших адресов`);
        }
    } catch (e) {
        console.log('[PUSH] не отправили: ' + e.message);
    }
}

/**
 * Событие с релея. isOnline(userId) говорит, есть ли у человека живой сокет.
 * Шлём только тем, кого нет: у кого приложение открыто, тот уже всё получил.
 */
async function onEvent(msg, isOnline) {
    if (!ready) return;
    if (msg.type !== 'new_message') return;

    const from = Number(msg.userId || 0);
    if (!from) return;

    // Адресат едет в sessionId, а НЕ в targetUserId.
    //
    // Клиенты шлют new_message широковещательно:
    //
    //   send("new_message", /*targetUserId*/ 0, /*sessionId*/ кому, payload)
    //
    // Релею так и надо — он рассылает всем, а каждый клиент сам решает, его
    // ли это событие. Значит targetUserId здесь всегда ноль, и читать из
    // него адресата бессмысленно: push не уходил вообще никогда.
    const chat = Number(msg.sessionId || 0);
    if (!chat) return;

    // Разбираем только то, что клиенты действительно шлют: "direct",
    // "group", "server" (см. вызовы SendMessage/send в обоих клиентах).
    // Незнакомое молча пропускаем — гадать, кому это адресовано, нельзя:
    // в chat лежит id канала, группы или человека, и перепутать их значит
    // разбудить постороннего.
    const payload = String(msg.payload || '');
    if (payload !== 'direct' && payload !== 'group') return;

    console.log(`[PUSH] событие: от ${from}, кому ${chat}, вид ${payload}`);

    const name = await nameOf(from);

    if (payload === 'group') {
        // Группа: состав знает база.
        try {
            const [rows] = await pool.query(
                'SELECT user_id FROM group_members WHERE group_id = ? AND user_id <> ?',
                [chat, from]);
            for (const r of rows) {
                if (isOnline(r.user_id)) continue;
                await send(r.user_id, { kind: 'group', group: chat, from, name });
            }
        } catch (e) { console.log('[PUSH] группа: ' + e.message); }
        return;
    }

    // Личное сообщение. Сам себе push не шлём: это своё же второе
    // устройство, ему релей и так всё отдал.
    if (chat === from) return;
    if (isOnline(chat)) return;
    await send(chat, { kind: 'message', from, name });
}

module.exports = { init, onEvent };
