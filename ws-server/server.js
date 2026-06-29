// ============================================================
//  PISMO — WebSocket сигнальный сервер (релей)
//
//  Назначение: мгновенная доставка сигналов между клиентами PISMO
//  (входящие звонки, статусы звонка, новые сообщения и т.п.) без задержки
//  опроса БД. Клиент (WebSocketSignalingClient.cs) подключается, шлёт
//  {type:'register', userId} и затем сообщения вида
//  {type, userId, targetUserId, sessionId, payload}. Сервер пересылает их
//  адресату (targetUserId) либо всем (если targetUserId == 0 — групповой
//  broadcast), не меняя содержимое.
//
//  Запуск:
//    npm install
//    node server.js            (порт 8080 по умолчанию, можно PORT=9000 node server.js)
//
//  На VPS лучше как systemd-сервис — см. ws-server/README.md.
// ============================================================

const WebSocket = require('ws');
const crypto = require('crypto');

const PORT = parseInt(process.env.PORT || '8080', 10);
const wss = new WebSocket.Server({ port: PORT });

// ── JWT (HS256) ─────────────────────────────────────────────────────
// Секрет ДОЛЖЕН совпадать с PISMO/JwtAuth.cs (Secret). Можно переопределить
// переменной окружения JWT_SECRET. REQUIRE_JWT=1 — отклонять клиентов без
// валидного токена (по умолчанию мягкий режим для совместимости со старыми).
// Должен совпадать с Secret в PISMO/JwtAuth.cs. Можно переопределить через env JWT_SECRET.
const JWT_SECRET = process.env.JWT_SECRET || 'uc5KT2e+qYwa6tb0HUXnLZwsC55VuB93szkSpkucr8i1BFjKA6RXbyIrjk0+ign9';
const REQUIRE_JWT = process.env.REQUIRE_JWT === '1';

function verifyJwt(token) {
    try {
        if (!token || typeof token !== 'string') return null;
        const parts = token.split('.');
        if (parts.length !== 3) return null;
        const signingInput = parts[0] + '.' + parts[1];
        const expected = crypto.createHmac('sha256', JWT_SECRET).update(signingInput).digest('base64url');
        // Сравнение в постоянном времени.
        const a = Buffer.from(expected), b = Buffer.from(parts[2]);
        if (a.length !== b.length || !crypto.timingSafeEqual(a, b)) return null;
        const payload = JSON.parse(Buffer.from(parts[1].replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8'));
        if (!payload.exp || Math.floor(Date.now() / 1000) >= payload.exp) return null; // истёк
        return payload; // {uid, login, iat, exp}
    } catch { return null; }
}

// userId -> Set<ws> (у пользователя может быть несколько окон/устройств)
const clients = new Map();

function addClient(userId, ws) {
    if (!clients.has(userId)) clients.set(userId, new Set());
    clients.get(userId).add(ws);
}
function removeClient(userId, ws) {
    const set = clients.get(userId);
    if (!set) return;
    set.delete(ws);
    if (set.size === 0) clients.delete(userId);
}

console.log(`[PISMO WS] Слушаю ws://0.0.0.0:${PORT}`);

wss.on('connection', (ws, req) => {
    ws.userId = null;
    ws.isAlive = true;
    ws.on('pong', () => { ws.isAlive = true; });

    ws.on('message', (data) => {
        const raw = typeof data === 'string' ? data : data.toString('utf8');
        let msg;
        try { msg = JSON.parse(raw); } catch { return; }

        if (msg.type === 'register') {
            const uid = Number(msg.userId);
            const claims = verifyJwt(msg.token);
            if (!claims || Number(claims.uid) !== uid) {
                // Токен невалиден/не совпал с userId.
                // СТРОГО отклоняем только при REQUIRE_JWT=1. В мягком режиме (по
                // умолчанию) пускаем по userId — иначе при несовпадении секрета
                // JWT_SECRET (сервер) и Secret (клиент) НИКТО не регистрируется и
                // сообщения/галочки не доходят.
                if (REQUIRE_JWT) {
                    console.log(`[PISMO WS] register ОТКЛОНЁН userId=${uid} (плохой JWT, строгий режим)`);
                    try { ws.send(JSON.stringify({ type: 'auth_error', payload: 'invalid token' })); } catch {}
                    try { ws.close(); } catch {}
                    return;
                }
                console.log(`[PISMO WS] register userId=${uid} (мягкий режим: токен не проверен)`);
            }
            ws.userId = uid;
            addClient(ws.userId, ws);
            console.log(`[PISMO WS] register userId=${ws.userId} (онлайн: ${clients.size})`);
            return;
        }

        // До регистрации ничего не релеим.
        if (ws.userId == null) return;

        const target = Number(msg.targetUserId || 0);
        if (target && target !== 0) {
            // Личная доставка адресату.
            const set = clients.get(target);
            if (set) for (const c of set) if (c.readyState === WebSocket.OPEN) { try { c.send(raw); } catch {} }
        } else {
            // Broadcast (группа/общее) — всем, кроме отправителя.
            for (const set of clients.values())
                for (const c of set)
                    if (c !== ws && c.readyState === WebSocket.OPEN) { try { c.send(raw); } catch {} }
        }
    });

    ws.on('close', () => { if (ws.userId != null) removeClient(ws.userId, ws); });
    ws.on('error', () => { if (ws.userId != null) removeClient(ws.userId, ws); });
});

// Keepalive: отсекаем «мертвые» соединения, чтобы Map не разрастался.
setInterval(() => {
    wss.clients.forEach((ws) => {
        if (ws.isAlive === false) { try { ws.terminate(); } catch {} return; }
        ws.isAlive = false;
        try { ws.ping(); } catch {}
    });
}, 30000);
