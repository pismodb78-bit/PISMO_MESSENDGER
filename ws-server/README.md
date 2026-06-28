# PISMO — WebSocket сигнальный сервер

Мгновенная доставка сигналов (входящие звонки, статусы, новые сообщения)
между клиентами. Без него приложение работает, но через опрос БД (медленнее
и «не всегда приходит уведомление»). С ним — мгновенно.

## JWT-аутентификация

Клиент при подключении присылает подписанный JWT (выдаётся при входе).
Сервер проверяет подпись и совпадение `userId` с токеном.

- `JWT_SECRET` — секрет подписи. **Должен совпадать** с `PISMO/JwtAuth.cs`
  (по умолчанию `PISMO::jwt::secret::v1::change-me-please` — поменяйте в обоих
  местах на свой).
- `REQUIRE_JWT=1` — строгий режим: клиенты без валидного токена отклоняются.
  По умолчанию мягкий режим (старые клиенты без токена ещё пускаются), чтобы
  можно было обновить всех постепенно. После обновления всех — включите строгий:

```bash
JWT_SECRET='ваш-секрет' REQUIRE_JWT=1 node server.js
```

## Развёртывание на VPS (тот же, где LiveKit — 5.181.23.167)

```bash
# 1. Установить Node.js (если ещё нет)
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
sudo apt-get install -y nodejs

# 2. Скопировать папку ws-server на сервер, например в /opt/pismo-ws
mkdir -p /opt/pismo-ws && cd /opt/pismo-ws
#   (скопируйте сюда server.js и package.json)

# 3. Установить зависимости и проверить запуск
npm install
node server.js          # должно вывести: [PISMO WS] Слушаю ws://0.0.0.0:8080
# Ctrl+C после проверки

# 4. Открыть порт в фаерволе
sudo ufw allow 8080/tcp
```

## Развёртывание на Windows (машина с MySQL, 85.174.248.59)

```bat
:: 1. Установить Node.js LTS: https://nodejs.org (кнопка LTS, обычная установка)
::    Проверить в новом окне cmd:
node --version

:: 2. Создать папку, например C:\pismo-ws, и положить туда server.js и package.json

:: 3. В cmd перейти в папку и установить зависимости:
cd C:\pismo-ws
npm install

:: 4. Запустить (проверка):
node server.js
::    Должно вывести: [PISMO WS] Слушаю ws://0.0.0.0:8080
```

**Открыть порт 8080 в брандмауэре Windows** (cmd от администратора):

```bat
netsh advfirewall firewall add rule name="PISMO WS 8080" dir=in action=allow protocol=TCP localport=8080
```

**Автозапуск как служба Windows.**

> ⚠️ НЕ используйте pm2 на Windows с новым Node.js (v20+/v24): pm2 падает с
> `EPERM \\.\pipe\rpc.sock` (не может поднять свой демон). Используйте NSSM —
> он создаёт настоящую службу Windows и не зависит от версии Node.

**Вариант A — NSSM (рекомендуется):**
1. Скачать NSSM: https://nssm.cc/download (распаковать, взять `win64\nssm.exe`).
2. В cmd от администратора:
   ```bat
   nssm install PismoWS
   ```
   В открывшемся окне:
   - **Path**: `C:\Program Files\nodejs\node.exe`
   - **Startup directory**: `C:\pismo-ws`
   - **Arguments**: `server.js`
   Нажать **Install service**.
3. Запустить службу:
   ```bat
   nssm start PismoWS
   ```
   Управление: `nssm restart PismoWS`, `nssm stop PismoWS`, логи — вкладка I/O
   в `nssm edit PismoWS` (можно указать файл лога).

**Вариант B — Планировщик заданий (без скачиваний):**
```bat
schtasks /create /tn PismoWS /tr "\"C:\Program Files\nodejs\node.exe\" C:\pismo-ws\server.js" /sc onstart /ru SYSTEM /f
schtasks /run /tn PismoWS
```
Остановить: `schtasks /end /tn PismoWS`, удалить: `schtasks /delete /tn PismoWS /f`.

**Вариант C — просто проверить/погонять сейчас:** оставить открытым окно с
`node server.js` (работает, пока окно открыто).

> Если машина за роутером/NAT — пробросьте внешний порт 8080 на эту машину
> (как уже сделано для MySQL 3307). Если у машины публичный IP напрямую —
> достаточно правила брандмауэра выше.

## Автозапуск как systemd-сервис (Linux)

Создайте `/etc/systemd/system/pismo-ws.service`:

```ini
[Unit]
Description=PISMO WebSocket signaling server
After=network.target

[Service]
WorkingDirectory=/opt/pismo-ws
ExecStart=/usr/bin/node /opt/pismo-ws/server.js
Restart=always
Environment=PORT=8080

[Install]
WantedBy=multi-user.target
```

Затем:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now pismo-ws
sudo systemctl status pismo-ws
```

## Настройка клиента

Клиент берёт адрес WS из `ip.txt` (рядом с exe). Добавьте строку с явным
адресом WebSocket (точка с запятой — разделитель):

```
server=85.174.248.59;port=3307;uid=user1;password=scent01;database=bdauth;ws=ws://85.174.248.59:8080
```

Параметр `ws=` имеет приоритет. Если его нет — клиент берёт `server=` и порт 8080
(то есть для машины с MySQL `ws=` можно даже не указывать — адрес совпадёт).
Этот `ip.txt` должен быть у КАЖДОГО клиента (рядом с PISMO.exe), одинаковый.

## Проверка

С компьютера: `curl http://5.181.23.167:8080` вернёт ошибку «Upgrade Required»
(это нормально — порт слушает, просто это не HTTP). Главное — не таймаут.
В логах сервера при входе пользователей появятся строки `register userId=...`.
