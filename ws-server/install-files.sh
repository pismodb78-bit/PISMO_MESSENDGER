#!/usr/bin/env bash
#
# Файловый сервис PISMO. Запускать на VPS от root:
#
#   bash install-files.sh
#
# Что делает: кладёт files.js рядом с релеем, заводит службу pismo-files,
# открывает порт и проверяет, что всё поднялось. Повторный запуск ничего не
# ломает — можно звать после каждого обновления.
#
# ЗАЧЕМ ОН. Вложения ездили в саму базу (LONGBLOB порциями через CONCAT):
# сервер переписывал весь накопленный блоб на каждую порцию, видео нельзя
# было перемотать, а прогресс был виден только по границам порций. Теперь
# файлы лежат на диске, в базе — ссылка.

set -euo pipefail

DIR=${DIR:-/opt/pismo-ws}
WS_SERVICE=${WS_SERVICE:-pismo-ws}
SERVICE=${SERVICE:-pismo-files}
PORT=${PORT:-8081}
STORE=${STORE:-$DIR/files}
BRANCH=claude/pismo-android-version-qd5fxr
RAW="https://raw.githubusercontent.com/pismodb78-bit/pismo_messendger/$BRANCH/ws-server"

say() { printf '\n\033[1m%s\033[0m\n' "$*"; }
die() { printf '\n\033[31m%s\033[0m\n' "$*" >&2; exit 1; }

[ "$(id -u)" = 0 ] || die "Нужен root: sudo bash $0"
[ -d "$DIR" ] || die "Нет каталога $DIR. Если сервер лежит в другом месте: DIR=/путь bash $0"
command -v node >/dev/null || die "Нет node. Поставьте Node.js 18+."

# ─── 1. Код ──────────────────────────────────────────────────────────
say "Беру files.js из репозитория"
curl -fsSL "$RAW/files.js" -o "$DIR/files.js" || die "не скачался files.js"
node --check "$DIR/files.js" || die "files.js не разбирается — обновите node"

# ─── 2. От кого работает ─────────────────────────────────────────────
# Тем же пользователем, что и релей: иначе служба не прочитает свой же
# каталог, а файлы окажутся чужими. Ровно на этом спотыкался push.
RUN_USER=$(systemctl show -p User --value "$WS_SERVICE" 2>/dev/null || true)
[ -n "$RUN_USER" ] || RUN_USER=root
say "Работать будет от: $RUN_USER"

install -d -o "$RUN_USER" -g "$RUN_USER" "$STORE" "$STORE/tmp" "$STORE/blobs"

# ─── 3. Секрет подписи ───────────────────────────────────────────────
# Тот же, что у релея: клиенты уже умеют выписывать такой токен. Берём его
# из настроек релея, если он там переопределён.
JWT=$(systemctl show -p Environment --value "$WS_SERVICE" 2>/dev/null \
      | tr ' ' '\n' | sed -n 's/^JWT_SECRET=//p' | head -1 || true)

# ─── 4. Служба ───────────────────────────────────────────────────────
say "Завожу службу $SERVICE"
cat > "/etc/systemd/system/${SERVICE}.service" <<EOF
[Unit]
Description=PISMO file service (resumable upload + range download)
After=network.target

[Service]
Type=simple
User=$RUN_USER
WorkingDirectory=$DIR
ExecStart=$(command -v node) $DIR/files.js
Environment=FILES_PORT=$PORT
Environment=FILES_DIR=$STORE
${JWT:+Environment=JWT_SECRET=$JWT}
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl restart "$SERVICE"

# ─── 5. Порт ─────────────────────────────────────────────────────────
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
    say "Открываю порт $PORT в ufw"
    ufw allow "$PORT"/tcp >/dev/null || true
fi

# ─── 6. Проверка ─────────────────────────────────────────────────────
sleep 1
say "Проверяю"
if curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null; then
    printf '\033[32m[FILES] служба отвечает на :%s\033[0m\n' "$PORT"
else
    systemctl --no-pager -l status "$SERVICE" | tail -20
    die "[FILES] не отвечает — смотрите вывод выше"
fi

cat <<EOF

Готово.

  Хранилище:   $STORE/blobs
  Логи:        journalctl -u $SERVICE -f
  Снаружи:     http://<адрес сервера>:$PORT/health

Клиентам адрес прописывается в ip.txt:  files=http://<адрес сервера>:$PORT
Без этой строки они продолжат слать вложения в базу, как раньше.
EOF
