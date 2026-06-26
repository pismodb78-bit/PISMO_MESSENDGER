#!/usr/bin/env bash
set -euo pipefail

CONF="/etc/turnserver.conf"
SERVER="${1:-85.174.248.59}"
PORT="${2:-3478}"
TTL="${3:-3600}"
BASEUSER="${4:-pismo}"

if [[ ! -f "$CONF" ]]; then
  echo "Конфиг не найден: $CONF" >&2
  exit 1
fi

# извлечь static-auth-secret
SECRET_LINE=$(sudo grep -Ei '^\s*static-auth-secret\s*=' "$CONF" || true)
if [[ -z "$SECRET_LINE" ]]; then
  echo "static-auth-secret не найден в $CONF" >&2
  exit 2
fi

SECRET=$(echo "$SECRET_LINE" | sed -E 's/^[[:space:]]*static-auth-secret[[:space:]]*=[[:space:]]*//I' | tr -d '"' | tr -d "'")
if [[ -z "$SECRET" ]]; then
  echo "Не удалось извлечь значение static-auth-secret" >&2
  exit 3
fi

echo "Использую secret из конфига (скрыто). Генерирую username/password..."

EXPIRY=$(( $(date +%s) + TTL ))
USERNAME="${EXPIRY}:${BASEUSER}"

# HMAC-SHA1 (использует openssl с hex key)
PASSWORD=$(printf "%s" "$USERNAME" | openssl dgst -sha1 -mac HMAC -macopt hexkey:"$SECRET" -binary | base64)

echo ""
echo "CREDS:"
echo "  username: $USERNAME"
echo "  password: $PASSWORD"
echo "  expires : $(date -d "@$EXPIRY" --utc +'%Y-%m-%dT%H:%M:%SZ') (UTC)"
echo ""

# Проверка доступности порта TCP/UDP
echo "Проверка подключения TCP $SERVER:$PORT ..."
if command -v nc >/dev/null 2>&1; then
  nc -vz -w 3 "$SERVER" "$PORT" || echo "TCP connect failed (порт может быть закрыт)."
else
  echo "nc не установлен — пропускаю TCP check."
fi

# Попытка использовать turnutils_uclient если установлена
if command -v turnutils_uclient >/dev/null 2>&1; then
  echo ""
  echo "Выполняю пробный аутентифицированный запрос через turnutils_uclient (TCP)..."
  echo "Если утилита висит — прервите (Ctrl+C)."
  turnutils_uclient -u "$USERNAME" -w "$PASSWORD" -t tcp -p "$PORT" "$SERVER" || true
else
  echo ""
  echo "turnutils_uclient не установлен. Чтобы установить и протестировать:"
  echo "  sudo apt update && sudo apt install -y coturn"
  echo "Затем запустите этот скрипт снова."
fi

echo ""
echo "Если тест с клиентской утилитой не прошёл — посмотрите логи coturn:"
echo "  sudo journalctl -u coturn -n 100 --no-pager"
echo "  sudo tail -n 200 /var/log/turnserver/turnserver.log"

exit 0