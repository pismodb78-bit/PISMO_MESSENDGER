#!/usr/bin/env bash
set -euo pipefail

CONF="/etc/turnserver.conf"
if [[ ! -f "$CONF" ]]; then
  echo "Файл не найден: $CONF" >&2
  exit 1
fi

sudo cp "$CONF" "${CONF}.bak.$(date +%s)"
echo "Backup saved: ${CONF}.bak.*"

# Добавить строку lt-cred-mech, если отсутствует
if ! sudo grep -Ei '^\s*lt-cred-mech\s*$' "$CONF" >/dev/null 2>&1; then
  echo "Добавляю lt-cred-mech в $CONF"
  sudo bash -c "echo '' >> '$CONF'; echo 'lt-cred-mech' >> '$CONF'"
else
  echo "lt-cred-mech уже присутствует в конфиге."
fi

# Перезапускаем службу coturn/turnserver
if systemctl list-unit-files | grep -q '^coturn\.service'; then
  sudo systemctl restart coturn.service
  sudo systemctl status coturn.service --no-pager
elif systemctl list-unit-files | grep -q '^turnserver\.service'; then
  sudo systemctl restart turnserver.service
  sudo systemctl status turnserver.service --no-pager
else
  echo "Unit coturn/turnserver не найден. Запустите вручную: sudo turnserver -c $CONF"
fi

# Покажем, что слушает порт 3478
echo ""
echo "Состояние слушающих портов (TCP/UDP 3478 и диапазон relay):"
sudo ss -tunlp | grep -E '3478|4915[0-9]|4918[0-9]' || true

echo ""
echo "Последние строки лога turnserver:"
sudo journalctl -u coturn --no-pager -n 40 || sudo tail -n 40 /var/log/turnserver/turnserver.log || true

exit 0