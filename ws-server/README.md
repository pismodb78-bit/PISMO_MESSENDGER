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

## Развёртывание на VPS (5.181.23.167 — там же LiveKit и MySQL)

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

## Про Windows

Раньше ws-сервер и MySQL стояли на домашнем ноутбуке (85.174.248.59), и для
этого провайдеру платили за белый IP. Всё переехало на VPS — инструкция в
`deploy/UBUNTU-VPS.md`. Раздел про запуск на Windows убран, чтобы никто не
поднял вторую копию сервера: клиенты, разошедшиеся по двум релеям, друг друга
не видят.

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
server=5.181.23.167;port=3307;uid=user1;password=ВАШ_ПАРОЛЬ;database=bdauth;ws=ws://5.181.23.167:8080;livekit=ws://5.181.23.167:7880
```

Параметр `ws=` имеет приоритет. Если его нет — клиент берёт `server=` и порт 8080
(то есть для машины с MySQL `ws=` можно даже не указывать — адрес совпадёт).
Этот `ip.txt` должен быть у КАЖДОГО клиента (рядом с PISMO.exe), одинаковый.

## Проверка

С компьютера: `curl http://5.181.23.167:8080` вернёт ошибку «Upgrade Required»
(это нормально — порт слушает, просто это не HTTP). Главное — не таймаут.
В логах сервера при входе пользователей появятся строки `register userId=...`.
