#!/usr/bin/env bash
#
# Включение push на ws-сервере. Запускать на VPS от root:
#
#   bash install-push.sh
#
# Что делает: обновляет server.js и push.js из репозитория, ставит пакеты,
# прописывает настройки в systemd и перезапускает службу. Всё повторяемо —
# запуск второй раз ничего не ломает.
#
# ЧЕГО НЕ ДЕЛАЕТ: не создаёт ключ сервисного аккаунта. Его надо скачать в
# консоли Firebase (Настройки проекта → Сервисные аккаунты → Создать
# закрытый ключ) и положить в /opt/pismo-ws/firebase.json.

set -euo pipefail

DIR=${DIR:-/opt/pismo-ws}
SERVICE=${SERVICE:-pismo-ws}
BRANCH=claude/pismo-android-version-qd5fxr
RAW="https://raw.githubusercontent.com/pismodb78-bit/pismo_messendger/$BRANCH/ws-server"

# ─── Доступ к базе ───────────────────────────────────────────────────
# Взято из ip.txt. Если на сервере пароль другой — поправьте ЗДЕСЬ, иначе
# push не сможет узнать, кому и куда слать.
DB_USER=user1
DB_PASS=scent01
DB_HOST=5.181.23.167
DB_PORT=3307
DB_NAME=bdauth

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
die() { printf '\n\033[31m%s\033[0m\n' "$*" >&2; exit 1; }

[ "$(id -u)" = 0 ] || die "Нужен root: sudo bash $0"
[ -d "$DIR" ] || die "Нет каталога $DIR. Если сервер лежит в другом месте: DIR=/путь bash $0"

systemctl list-unit-files | grep -q "^${SERVICE}\.service" \
  || die "Нет службы ${SERVICE}.service. Если она называется иначе: SERVICE=имя bash $0"

# ─── 1. Ключ сервисного аккаунта ─────────────────────────────────────
if [ ! -s "$DIR/firebase.json" ]; then
    die "Нет $DIR/firebase.json — это ключ сервисного аккаунта из Firebase.
Скачайте его (Настройки проекта → Сервисные аккаунты → Создать закрытый
ключ), положите на сервер и запустите снова:

  cp ваш-ключ.json $DIR/firebase.json
  bash $0"
fi

# Проверяем, что это ТОТ файл. Похожих два, и перепутать их легко:
# google-services.json — настройки приложения, они идут в APK; ключ
# сервисного аккаунта — для сервера. У второго есть "type":"service_account",
# у первого нет. Без проверки ошибка вылезла бы потом и невнятно.
grep -q '"type"[[:space:]]*:[[:space:]]*"service_account"' "$DIR/firebase.json" || die \
"$DIR/firebase.json — не ключ сервисного аккаунта.

Похоже, туда попал google-services.json (это настройки ПРИЛОЖЕНИЯ, их место
в секрете GitHub, а не на сервере). Нужен второй файл: Настройки проекта →
Сервисные аккаунты → Создать закрытый ключ. Внутри него есть строка
\"type\": \"service_account\"."

# Права. Служба работает не от root, а файл, положенный через sudo,
# принадлежит root с правами 600 — и node его не открывает:
#
#   [PUSH] выключен: EACCES: permission denied, open '/opt/pismo-ws/firebase.json'
#
# Поэтому владельцем делаем того, от кого работает служба, и только потом
# закрываем права. Пустой User= в юните означает root.
SVC_USER=$(systemctl show -p User --value "$SERVICE" 2>/dev/null || true)
SVC_USER=${SVC_USER:-root}
chown "$SVC_USER" "$DIR/firebase.json"
chmod 600 "$DIR/firebase.json"

# Проверяем, что служба и правда его прочтёт, а не узнаём об этом из лога
# после перезапуска.
if [ "$SVC_USER" != root ]; then
    su -s /bin/sh -c "head -c1 '$DIR/firebase.json' >/dev/null" "$SVC_USER" 2>/dev/null \
      || die "Пользователь $SVC_USER всё равно не читает $DIR/firebase.json.
Проверьте права на сам каталог: ls -ld $DIR"
fi
say "Ключ на месте: $DIR/firebase.json (владелец $SVC_USER)"

# ─── 2. Свежий код релея ─────────────────────────────────────────────
say "Обновляю server.js и push.js"
cd "$DIR"
for f in server.js push.js; do
    # -f: на HTTP-ошибке curl падает, а не сохраняет страницу ошибки вместо
    # кода. Качаем во временный файл и подменяем только после успеха, чтобы
    # оборванная закачка не оставила сервер с обрубком.
    curl -fsSL "$RAW/$f" -o "$f.new" || die "Не скачался $f"
    [ -s "$f.new" ] || die "Пустой $f"
    mv "$f.new" "$f"
    echo "  $f — обновлён"
done

# ─── 3. Пакеты ───────────────────────────────────────────────────────
say "Ставлю firebase-admin и mysql2"
npm install --omit=dev firebase-admin mysql2 >/dev/null 2>&1 || npm install firebase-admin mysql2

# ─── 4. Настройки для systemd ────────────────────────────────────────
#
# Отдельным файлом-дополнением, а не правкой самого юнита: так настройки
# переживут его обновление, и не нужно ничего редактировать руками.
say "Прописываю настройки в systemd"
install -d "/etc/systemd/system/${SERVICE}.service.d"
cat > "/etc/systemd/system/${SERVICE}.service.d/push.conf" <<EOF
[Service]
Environment=FIREBASE_CREDENTIALS=$DIR/firebase.json
Environment=PISMO_DB=mysql://$DB_USER:$DB_PASS@$DB_HOST:$DB_PORT/$DB_NAME
EOF
# В файле пароль от базы — читать его посторонним незачем.
chmod 600 "/etc/systemd/system/${SERVICE}.service.d/push.conf"

# ─── 5. Перезапуск и проверка ────────────────────────────────────────
say "Перезапускаю $SERVICE"
systemctl daemon-reload
systemctl restart "$SERVICE"
sleep 2

say "Что в логе:"
journalctl -u "$SERVICE" -n 30 --no-pager | tail -20

echo
if journalctl -u "$SERVICE" -n 50 --no-pager | grep -q "\[PUSH\] включён"; then
    printf '\033[32m%s\033[0m\n' "Готово: push включён."
else
    printf '\033[33m%s\033[0m\n' "Push НЕ включился. Вот что сказал сам сервер:"
    journalctl -u "$SERVICE" -n 50 --no-pager | grep "\[PUSH\]" | tail -3
fi
