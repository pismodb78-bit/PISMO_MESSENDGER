# Перенос PISMO на Ubuntu VPS

Порядок сплошной: сверху вниз, ничего не пропуская. Всё делается на
`5.181.23.167` — там же, где уже работает LiveKit.

**Что переезжает:** MySQL (сейчас MAMP на ноутбуке, `85.174.248.59:3307`) и
ws-сервер (там же, порт 8080).

**Что не трогаем:** LiveKit — он уже на этом VPS, порт 7880. Звонки от
ноутбука не зависят.

**Итог:** белый IP от провайдера больше не нужен, ноутбук можно гасить.

**Адреса после переезда** — всё на одном хосте:

| Что | Адрес |
|---|---|
| База | `5.181.23.167:3307` |
| Сигналинг | `ws://5.181.23.167:8080/` |
| LiveKit | `ws://5.181.23.167:7880` |
| phpMyAdmin | `https://5.181.23.167:47821/` |

---

## 1. Дамп с ноутбука

На ноутбуке, в обычном `cmd`:

```bat
"C:\MAMP\bin\mysql\bin\mysqldump.exe" -h 127.0.0.1 -P 3307 -u root -p ^
  --default-character-set=utf8mb4 --single-transaction --routines ^
  --hex-blob --max-allowed-packet=512M ^
  bdauth > %USERPROFILE%\Desktop\bdauth.sql
```

Пароль root в MAMP по умолчанию `root`.

Ключи не для красоты: `--hex-blob` иначе испортит вложения (они лежат в базе
как LONGBLOB), `--routines` иначе потеряет процедуру `add_column_safe`,
`--max-allowed-packet` иначе оборвёт дамп на первом крупном файле,
`--single-transaction` даёт целостный снимок не гася MAMP.

**Проверьте размер файла.** Он должен быть сопоставим с папкой данных MAMP.
Пара килобайт — дамп не удался, дальше идти нельзя.

Отправьте на сервер:

```bat
scp %USERPROFILE%\Desktop\bdauth.sql root@5.181.23.167:/root/
```

---

## 2. Подготовка сервера

```bash
ssh root@5.181.23.167

apt update && apt upgrade -y
timedatectl set-timezone Europe/Moscow
free -h
```

Если памяти меньше 2 ГБ — подкачка, иначе импорт с вложениями упрётся в OOM:

```bash
fallocate -l 2G /swapfile && chmod 600 /swapfile
mkswap /swapfile && swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
```

---

## 3. MariaDB

MariaDB, а не MySQL 8: Android-клиент собран с драйвером
`mysql-connector-java:5.1.49`, а он не умеет `caching_sha2_password` —
проверку пароля, которую MySQL 8 включает по умолчанию. В MySQL 8.4 старый
способ убрали совсем. MariaDB работает со старым, дамп из вашей 5.7
принимает как есть.

### 3.1 Установка

```bash
apt install -y mariadb-server
mysql_secure_installation
```

В диалоге: пароль root задать, анонимных удалить, удалённый вход root
**запретить**, тестовую базу удалить.

### 3.2 Настройка

```bash
cat > /etc/mysql/mariadb.conf.d/60-pismo.cnf <<'EOF'
[mysqld]
port = 3307
bind-address = 0.0.0.0

# Вложения доходят до 200 МБ. Пакет должен быть больше самого крупного,
# иначе отправка файла обрывается посередине.
max_allowed_packet = 512M

# Под 2 ГБ памяти. На 4 ГБ и больше можно поставить 512M.
innodb_buffer_pool_size = 256M
innodb_log_file_size = 128M

character-set-server = utf8mb4
collation-server = utf8mb4_unicode_ci
wait_timeout = 1800
max_connections = 100

[client]
max_allowed_packet = 512M
EOF
```

### 3.3 Автозапуск

```bash
systemctl enable mariadb
systemctl restart mariadb
systemctl status mariadb --no-pager
```

---

## 4. База, пользователи, данные

### 4.1 Создание

```bash
mysql
```

```sql
CREATE DATABASE bdauth CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

-- Администратор: phpMyAdmin и удалённые подключения.
CREATE USER 'root1'@'%' IDENTIFIED BY 'scent01!';
GRANT ALL PRIVILEGES ON *.* TO 'root1'@'%' WITH GRANT OPTION;

-- Приложение: ровно те права, что были на старом сервере.
CREATE USER 'user1'@'%' IDENTIFIED BY 'scent01';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE ON bdauth.* TO 'user1'@'%';

FLUSH PRIVILEGES;
EXIT;
```

FILE не переносим: приложению он не нужен (обычные выборки и вставки), право
глобальное и позволяет читать и писать файлы сервера от имени СУБД.

ALTER и INDEX — по желанию. Их не было, и поэтому миграции схемы, которые
приложение носит в себе, не применялись, а индексы вы вставляли руками.
Хотите, чтобы применялись само:

```sql
GRANT ALTER, INDEX ON bdauth.* TO 'user1'@'%';
```

### 4.2 Импорт

```bash
mysql --max-allowed-packet=512M bdauth < /root/bdauth.sql
```

### 4.3 Проверка

```bash
mysql -e "
  SELECT COUNT(*) AS messages FROM bdauth.messages;
  SELECT COUNT(*) AS users FROM bdauth.users;
  SELECT id, name FROM bdauth.schema_migrations ORDER BY id;"
```

Числа сверьте с тем, что было в phpMyAdmin на ноутбуке.

### 4.4 Индекс, снимающий нагрузку

Если он не приехал с дампом:

```bash
mysql bdauth -e "
  ALTER TABLE messages
  ADD INDEX idx_msg_recv_read (receiver_id, is_read, sender_id);"
```

Ошибка 1061 — уже стоит, всё в порядке.

---

## 5. ws-сервер

### 5.1 Установка

```bash
curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
apt install -y nodejs

mkdir -p /opt/pismo-ws && cd /opt/pismo-ws
B=claude/pismo-android-version-qd5fxr
curl -fsSLO "https://raw.githubusercontent.com/pismodb78-bit/PISMO_MESSENDGER/$B/ws-server/server.js"
curl -fsSLO "https://raw.githubusercontent.com/pismodb78-bit/PISMO_MESSENDGER/$B/ws-server/package.json"
npm install --omit=dev
```

### 5.2 Настройка

Секрет отдельным файлом, а не в юните: юниты читаются всеми, а это ключ
подписи токенов.

```bash
cat > /etc/pismo-ws.env <<'EOF'
PORT=8080
REQUIRE_JWT=0
JWT_SECRET=uc5KT2e+qYwa6tb0HUXnLZwsC55VuB93szkSpkucr8i1BFjKA6RXbyIrjk0+ign9
EOF
chmod 600 /etc/pismo-ws.env
```

`REQUIRE_JWT=0` — мягкий режим на время переезда: строгий отклоняет клиентов
без токена, а среди них могут быть ещё не обновлённые. Когда все перейдут,
поменяйте на `1` и перезапустите.

### 5.3 Автозапуск

```bash
cat > /etc/systemd/system/pismo-ws.service <<'EOF'
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

systemctl daemon-reload
systemctl enable --now pismo-ws
journalctl -u pismo-ws -n 20 --no-pager
```

Ждём в логе `[PISMO WS] Слушаю ws://0.0.0.0:8080`.

---

## 6. phpMyAdmin

### 6.1 Установка

```bash
apt install -y nginx php-fpm php-mysql php-mbstring php-zip php-gd php-curl php-xml unzip apache2-utils

cd /tmp
curl -fsSLO https://files.phpmyadmin.net/phpMyAdmin/5.2.1/phpMyAdmin-5.2.1-all-languages.zip
unzip -q phpMyAdmin-5.2.1-all-languages.zip
mv phpMyAdmin-5.2.1-all-languages /usr/share/phpmyadmin
mkdir -p /var/lib/phpmyadmin/tmp
chown -R www-data:www-data /var/lib/phpmyadmin
```

### 6.2 Настройка phpMyAdmin

```bash
cat > /usr/share/phpmyadmin/config.inc.php <<EOF
<?php
\$cfg['blowfish_secret'] = '$(openssl rand -base64 24 | cut -c1-32)';
\$i = 1;
\$cfg['Servers'][\$i]['auth_type'] = 'cookie';
\$cfg['Servers'][\$i]['host'] = '127.0.0.1';
\$cfg['Servers'][\$i]['port'] = '3307';
\$cfg['Servers'][\$i]['AllowNoPassword'] = false;
\$cfg['Servers'][\$i]['AllowDeny']['order'] = 'deny,allow';
\$cfg['Servers'][\$i]['AllowDeny']['rules'] = ['allow root1 from all'];
\$cfg['TempDir'] = '/var/lib/phpmyadmin/tmp';
\$cfg['ExecTimeLimit'] = 0;
\$cfg['MemoryLimit'] = '512M';
EOF
chown www-data:www-data /usr/share/phpmyadmin/config.inc.php
chmod 640 /usr/share/phpmyadmin/config.inc.php
```

`AllowDeny` пускает только `root1`. Без него в панель вошёл бы `user1`, чей
пароль лежит в публичном репозитории и в APK.

### 6.3 Лимиты PHP

По умолчанию PHP не примет файл больше двух мегабайт — для импорта дампов
бесполезно:

```bash
PHPVER=$(php -r 'echo PHP_MAJOR_VERSION.".".PHP_MINOR_VERSION;')
cat > /etc/php/$PHPVER/fpm/conf.d/99-pismo.ini <<'EOF'
upload_max_filesize = 1024M
post_max_size = 1024M
memory_limit = 512M
max_execution_time = 0
EOF
systemctl restart php$PHPVER-fpm
```

### 6.4 Пароль на входе и сертификат

```bash
htpasswd -c /etc/nginx/.htpasswd pismo

openssl req -x509 -nodes -days 3650 -newkey rsa:2048 \
  -keyout /etc/ssl/private/pma.key -out /etc/ssl/certs/pma.crt \
  -subj "/CN=5.181.23.167"
chmod 600 /etc/ssl/private/pma.key
```

### 6.5 nginx и автозапуск

Порт придумайте свой пятизначный и запомните. Ниже для примера 47821.

```bash
PMAPORT=47821
PHPVER=$(php -r 'echo PHP_MAJOR_VERSION.".".PHP_MINOR_VERSION;')
cat > /etc/nginx/sites-available/phpmyadmin <<EOF
server {
    listen $PMAPORT ssl;
    server_name _;

    ssl_certificate     /etc/ssl/certs/pma.crt;
    ssl_certificate_key /etc/ssl/private/pma.key;
    ssl_protocols TLSv1.2 TLSv1.3;

    auth_basic "PISMO";
    auth_basic_user_file /etc/nginx/.htpasswd;

    root /usr/share/phpmyadmin;
    index index.php;
    client_max_body_size 1024M;
    server_tokens off;

    location / { try_files \$uri \$uri/ =404; }

    location ~ \.php\$ {
        include snippets/fastcgi-php.conf;
        fastcgi_pass unix:/run/php/php$PHPVER-fpm.sock;
        fastcgi_read_timeout 3600;
    }
}
EOF
ln -sf /etc/nginx/sites-available/phpmyadmin /etc/nginx/sites-enabled/
rm -f /etc/nginx/sites-enabled/default
nginx -t

systemctl enable nginx php$PHPVER-fpm
systemctl restart nginx php$PHPVER-fpm
```

---

## 7. Фаервол

**Сначала 22, потом `enable`** — иначе выкинет из ssh.

```bash
PMAPORT=47821
ufw allow 22/tcp
ufw allow 3307/tcp
ufw allow 8080/tcp
ufw allow $PMAPORT/tcp
ufw allow 7880/tcp
ufw allow 7881/tcp
ufw allow 50000:60000/udp
ufw --force enable
ufw status numbered
```

Последние три правила — LiveKit, он на этом же сервере. Без них звонки
отвалятся, если `ufw` раньше не был включён.

---

## 8. Защита от перебора

```bash
apt install -y fail2ban
cat > /etc/fail2ban/jail.d/pismo.conf <<'EOF'
[nginx-http-auth]
enabled = true
port    = 47821
logpath = /var/log/nginx/error.log
maxretry = 5
bantime = 3600

[mysqld-auth]
enabled = true
port    = 3307
logpath = /var/log/mysql/error.log
maxretry = 5
bantime = 3600
EOF

systemctl enable fail2ban
systemctl restart fail2ban
fail2ban-client status
```

Две клетки, а не одна: порт базы тоже виден всему интернету, и `root1` на нём
отвечает.

---

## 9. Резервные копии

```bash
cat > /etc/cron.daily/pismo-backup <<'EOF'
#!/bin/sh
mysqldump --single-transaction --routines --hex-blob --max-allowed-packet=512M \
  bdauth | gzip > /root/backup-bdauth-$(date +\%F).sql.gz
find /root -name 'backup-bdauth-*.sql.gz' -mtime +7 -delete
EOF
chmod +x /etc/cron.daily/pismo-backup

/etc/cron.daily/pismo-backup && ls -lh /root/backup-*.gz
```

Хранится неделя. Раз в месяц копию стоит уносить с сервера — на диске VPS
она не переживёт самого VPS.

---

## 10. Проверка

Всё ли поднялось и включено в автозапуск:

```bash
systemctl is-enabled mariadb pismo-ws nginx fail2ban
systemctl is-active  mariadb pismo-ws nginx fail2ban
ss -tlnp | grep -E '3307|8080|47821|7880'
```

База отвечает снаружи (с ноутбука):

```bat
"C:\MAMP\bin\mysql\bin\mysql.exe" -h 5.181.23.167 -P 3307 -u user1 -p bdauth -e "SELECT COUNT(*) FROM messages;"
```

Панель: `https://5.181.23.167:47821/` → окно nginx (логин из `htpasswd`) →
форма phpMyAdmin, `root1` / `scent01!`. Браузер один раз предупредит про
самоподписанный сертификат: «Дополнительно» → «Перейти».

Приложение: войти, открыть чат, отправить сообщение и вложение, позвонить.
Уведомление о новом сообщении должно приходить мгновенно — это признак, что
вебсокет подключился.

Перезагрузка сервера — проверка автозапуска:

```bash
reboot
# через минуту
systemctl is-active mariadb pismo-ws nginx fail2ban
```

---

## 11. Файлы подключения

### ПК

`ip.txt` рядом с `PISMO.exe`, одна строка (адрес уже обновлён в репозитории):

```
server=5.181.23.167;port=3307;database=bdauth;uid=user1;pwd=scent01;ws=ws://5.181.23.167:8080/
```

Тонкость: `DBHelper` подменяет порт на 3306, если хост — `localhost`,
`127.0.0.1` или адрес локальной подсети. У VPS адрес внешний, поэтому 3307
останется как указано.

### Android

Настройки → «Подключение к базе данных»:

| Поле | Значение |
|---|---|
| Хост | 5.181.23.167 |
| Порт | 3307 |
| База | bdauth |
| Пользователь | user1 |
| Пароль | scent01 |

Поле «Сигналинг» оставить пустым — он собирается сам как `ws://<хост>:8080/`.
В сборке эти значения уже стоят по умолчанию.

---

## 12. Откат

MAMP не выключайте ещё пару дней. Если что-то пойдёт не так — верните в
`ip.txt` и в настройках Android прежние `85.174.248.59:3307`, данные там
останутся нетронутыми.

Помните только: сообщения, написанные на VPS, в старую базу не попадут.
Сводить две базы потом руками — работа на вечер.

Когда убедитесь, что всё работает: гасите MAMP и отказывайтесь от белого IP.
