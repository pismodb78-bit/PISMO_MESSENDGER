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

const PORT = parseInt(process.env.PORT || '8080', 10);
const wss = new WebSocket.Server({ port: PORT });

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
            ws.userId = Number(msg.userId);
            addClient(ws.userId, ws);
            console.log(`[PISMO WS] register userId=${ws.userId} (онлайн: ${clients.size})`);
            return;
        }

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
