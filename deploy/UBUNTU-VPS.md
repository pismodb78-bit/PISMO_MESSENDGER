# Перенос базы и вебсокета на Ubuntu VPS

Что переезжает: **MySQL** (сейчас MAMP на ноутбуке, `85.174.248.59:3307`) и
**ws-сервер** (сейчас там же, порт 8080).

Что НЕ переезжает и трогать не нужно: **LiveKit** — он уже на VPS
(`5.181.23.167:7880`). Звонки к ноутбуку не привязаны.

После переезда белый IP от провайдера не нужен: клиенты ходят только на VPS.

---

## 0. Прежде чем начать

**MariaDB, а не MySQL 8.** Причина конкретная: Android-клиент собран с
драйвером `mysql-connector-java:5.1.49`, а он не умеет `caching_sha2_password`
— способ проверки пароля, который MySQL 8 включает по умолчанию. С MySQL 8
пришлось бы отдельно переключать пользователя на старый плагин, а в MySQL 8.4
его уже нет вовсе. MariaDB использует `mysql_native_password` по умолчанию,
дамп из MySQL 5.7 принимает без правок, и оба клиента работают с ней как есть.

**Проверьте память VPS.** MySQL рядом с LiveKit и Node на дешёвом тарифе — это
обычно 1–2 ГБ. Если меньше двух, добавьте подкачку (шаг 3.1), иначе первый же
импорт дампа с вложениями упрётся в OOM.

**Пароль придётся сменить.** Нынешний `scent01` лежит в публичном репозитории
и в APK — то есть известен любому, кто откроет GitHub. Пока база стояла за
домашним роутером, это было полбеды; на VPS с открытым портом это приглашение.
Новый пароль ставим на шаге 4.

---

## 1. Снять дамп с MAMP (на ноутбуке, Windows)

В обычном `cmd`:

```bat
"C:\MAMP\bin\mysql\bin\mysqldump.exe" -h 127.0.0.1 -P 3307 -u root -p ^
  --default-character-set=utf8mb4 ^
  --single-transaction ^
  --routines ^
  --hex-blob ^
  --max-allowed-packet=512M ^
  bdauth > %USERPROFILE%\Desktop\bdauth.sql
```

Пароль root в MAMP по умолчанию `root`.

Что означают ключи, потому что каждый здесь по делу:

* `--hex-blob` — вложения (LONGBLOB) пишутся шестнадцатеричными числами, а не
  как строки. Без него дамп с картинками и голосовыми портится.
* `--single-transaction` — снимок целостный, MAMP при этом можно не гасить.
* `--routines` — в базе есть процедура `add_column_safe`, без ключа она
  потеряется.
* `--max-allowed-packet` — иначе дамп оборвётся на первом крупном вложении.

Проверьте размер файла: он должен быть сопоставим с размером папки данных
MAMP. Если получилось несколько килобайт — что-то пошло не так, не продолжайте.

Скопируйте файл на VPS:

```bat
scp %USERPROFILE%\Desktop\bdauth.sql root@ВАШ_IP:/root/
```

---

## 2. Подготовка сервера

```bash
sudo apt update && sudo apt upgrade -y
sudo timedatectl set-timezone Europe/Moscow    # чтобы время сообщений совпадало
```

### 2.1 Подкачка, если памяти меньше 2 ГБ

```bash
free -h                                        # посмотреть, сколько есть
sudo fallocate -l 2G /swapfile
sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

---

## 3. MariaDB

```bash
sudo apt install -y mariadb-server
sudo mysql_secure_installation
```

В `mysql_secure_installation`: пароль root задать, анонимных пользователей
удалить, удалённый вход root — **запретить** (приложение ходит под `user1`),
тестовую базу удалить.

### 3.1 Настройка

```bash
sudo nano /etc/mysql/mariadb.conf.d/60-pismo.cnf
```

```ini
[mysqld]
# Порт оставляем 3307 — такой же, как был у MAMP. Это и меньше правок в
# клиентах, и чуть тише: сканеры интернета долбятся в 3306.
port = 3307
bind-address = 0.0.0.0

# Вложения лежат в базе как LONGBLOB и доходят до 200 МБ. Пакет должен быть
# больше самого крупного вложения, иначе отправка обрывается посередине.
max_allowed_packet = 512M

# Под 2 ГБ памяти. Если на VPS 4 ГБ и больше — можно 512M.
innodb_buffer_pool_size = 256M
innodb_log_file_size = 128M

character-set-server = utf8mb4
collation-server = utf8mb4_unicode_ci

# Клиенты держат пул соединений и умеют переподключаться; полчаса простоя
# рвать смысла нет, но и вечные соединения копить незачем.
wait_timeout = 1800
max_connections = 100

[client]
max_allowed_packet = 512M
```

```bash
sudo systemctl restart mariadb
sudo systemctl status mariadb --no-pager
```

---

## 4. База и пользователь

```bash
sudo mysql
```

```sql
CREATE DATABASE bdauth CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

-- Пароль придумайте свой, длинный. Старый scent01 не используйте: он в
-- публичном репозитории.
CREATE USER 'user1'@'%' IDENTIFIED BY 'НОВЫЙ_ДЛИННЫЙ_ПАРОЛЬ';

-- ALL PRIVILEGES, а не только SELECT/INSERT/UPDATE/DELETE: приложение само
-- накатывает миграции схемы (DbMigrator), и без ALTER они молча не
-- применяются. На старом хостинге прав не хватало, и индексы приходилось
-- класть руками — здесь это станет ненужным.
GRANT ALL PRIVILEGES ON bdauth.* TO 'user1'@'%';
FLUSH PRIVILEGES;
EXIT;
```

### 4.1 Импорт дампа

```bash
sudo mysql --max-allowed-packet=512M bdauth < /root/bdauth.sql
```

Проверка, что доехало всё:

```bash
sudo mysql -e "
  SELECT table_name, table_rows
  FROM information_schema.tables
  WHERE table_schema='bdauth' ORDER BY table_rows DESC LIMIT 10;
  SELECT COUNT(*) AS messages FROM bdauth.messages;
  SELECT id, name FROM bdauth.schema_migrations ORDER BY id;"
```

Число сообщений должно совпасть с тем, что было в phpMyAdmin.

### 4.2 Индекс, который снимает нагрузку

Если он ещё не переехал вместе с дампом — приложение теперь создаст его само
(права есть). Можно и руками, файл лежит в `sql/2026-08-20_feed_indexes.sql`:

```bash
sudo mysql bdauth -e "
  ALTER TABLE messages
  ADD INDEX idx_msg_recv_read (receiver_id, is_read, sender_id);"
```

Ошибка 1061 — уже стоит, всё в порядке.

---

## 5. ws-сервер

```bash
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
sudo apt install -y nodejs

sudo mkdir -p /opt/pismo-ws
# скопировать в /opt/pismo-ws файлы ws-server/server.js и ws-server/package.json
cd /opt/pismo-ws && sudo npm install --omit=dev
```

Секрет — отдельным файлом, а не в юните: юниты читаются всеми, а это ключ
подписи токенов.

```bash
sudo tee /etc/pismo-ws.env >/dev/null <<'EOF'
PORT=8080
REQUIRE_JWT=0
JWT_SECRET=uc5KT2e+qYwa6tb0HUXnLZwsC55VuB93szkSpkucr8i1BFjKA6RXbyIrjk0+ign9
EOF
sudo chmod 600 /etc/pismo-ws.env
```

```bash
sudo tee /etc/systemd/system/pismo-ws.service >/dev/null <<'EOF'
[Unit]
Description=PISMO WebSocket signaling
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/pismo-ws
EnvironmentFile=/etc/pismo-ws.env
ExecStart=/usr/bin/node server.js
Restart=always
RestartSec=3
User=nobody
Group=nogroup
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now pismo-ws
sudo systemctl status pismo-ws --no-pager
journalctl -u pismo-ws -n 20 --no-pager
```

В логе должно быть `[PISMO WS] Слушаю ws://0.0.0.0:8080`.

`REQUIRE_JWT=0` оставлен на время переезда: строгий режим отклоняет клиентов
без токена, а среди них могут оказаться ещё не обновлённые. Когда все перейдут
— поменяйте на `1` и `sudo systemctl restart pismo-ws`.

---

## 6. Фаервол

```bash
sudo ufw allow 22/tcp
sudo ufw allow 3307/tcp
sudo ufw allow 8080/tcp
sudo ufw allow 7880/tcp          # LiveKit, если он на этом же VPS
sudo ufw allow 7881/tcp
sudo ufw allow 50000:60000/udp   # медиа LiveKit
sudo ufw enable
sudo ufw status numbered
```

И заведите fail2ban — порт базы теперь виден всему интернету:

```bash
sudo apt install -y fail2ban
sudo systemctl enable --now fail2ban
```

---

## 7. Файлы подключения

### ПК

Рядом с `PISMO.exe` лежит `ip.txt`. Одна строка:

```
server=ВАШ_IP;port=3307;database=bdauth;uid=user1;pwd=НОВЫЙ_ПАРОЛЬ;ws=ws://ВАШ_IP:8080/
```

Тонкость: `DBHelper` подменяет порт на 3306, если хост — `localhost`,
`127.0.0.1` или адрес из локальной подсети. У VPS адрес внешний, поэтому
указанный 3307 останется как есть.

### Android

Настройки → «Подключение к базе данных»:

| Поле | Значение |
|---|---|
| Хост | ВАШ_IP |
| Порт | 3307 |
| База | bdauth |
| Пользователь | user1 |
| Пароль | НОВЫЙ_ПАРОЛЬ |

Вебсокет собирается сам как `ws://<хост>:8080/`, отдельно указывать не нужно —
поле «Сигналинг» оставьте пустым.

Значения по умолчанию зашиты в сборку (`Prefs.kt`), так что после переезда их
стоит поменять и там — иначе на новом телефоне придётся вводить руками.

---

## 8. Проверка

```bash
# С ноутбука — база отвечает?
"C:\MAMP\bin\mysql\bin\mysql.exe" -h ВАШ_IP -P 3307 -u user1 -p bdauth -e "SELECT COUNT(*) FROM messages;"
```

Дальше в приложении: войти, открыть чат, отправить сообщение и вложение,
позвонить. Отдельно проверьте, что уведомление о новом сообщении приходит
мгновенно — это и есть признак, что вебсокет подключился.

---

## 9. Откат

MAMP не выключайте ещё пару дней. Если что-то пойдёт не так, верните в `ip.txt`
и в настройках Android прежние `85.174.248.59:3307` — данные там останутся
нетронутыми. Помните только, что сообщения, написанные на VPS, в старой базе не
появятся: сводить две базы потом руками — работа на вечер.

Когда убедитесь, что всё работает: погасите MAMP, откажитесь от белого IP и
настройте резервные копии на VPS:

```bash
sudo tee /etc/cron.daily/pismo-backup >/dev/null <<'EOF'
#!/bin/sh
mysqldump --single-transaction --routines --hex-blob --max-allowed-packet=512M \
  bdauth | gzip > /root/backup-bdauth-$(date +\%F).sql.gz
find /root -name 'backup-bdauth-*.sql.gz' -mtime +7 -delete
EOF
sudo chmod +x /etc/cron.daily/pismo-backup
```
