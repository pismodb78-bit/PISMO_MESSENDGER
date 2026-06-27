# PISMO — WebSocket сигнальный сервер

Мгновенная доставка сигналов (входящие звонки, статусы, новые сообщения)
между клиентами. Без него приложение работает, но через опрос БД (медленнее
и «не всегда приходит уведомление»). С ним — мгновенно.

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

## Автозапуск как systemd-сервис

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
server=85.174.248.59;port=3307;uid=user1;password=scent01;database=bdauth;ws=ws://5.181.23.167:8080
```

Параметр `ws=` имеет приоритет. Если его нет — клиент берёт `server=` и порт 8080.
Поставьте WS туда, где реально запущен этот сервер (рекомендуется VPS LiveKit
5.181.23.167, чтобы не нагружать машину с MySQL).

## Проверка

С компьютера: `curl http://5.181.23.167:8080` вернёт ошибку «Upgrade Required»
(это нормально — порт слушает, просто это не HTTP). Главное — не таймаут.
В логах сервера при входе пользователей появятся строки `register userId=...`.
