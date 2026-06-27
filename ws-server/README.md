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

**Автозапуск как служба Windows** (чтобы работал всегда, без открытого окна).
Проще всего через `pm2`:

```bat
npm install -g pm2 pm2-windows-startup
pm2-startup install
cd C:\pismo-ws
pm2 start server.js --name pismo-ws
pm2 save
```

Готово — теперь сервер сам поднимается при загрузке Windows.
Команды управления: `pm2 status`, `pm2 logs pismo-ws`, `pm2 restart pismo-ws`.

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
