#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# PISMO — перезапуск LiveKit SFU.
# Запускать НА СЕРВЕРЕ 5.181.23.167 (там, где крутится LiveKit), под sudo.
#   scp scripts/restart-livekit.sh root@5.181.23.167:/root/
#   ssh root@5.181.23.167 'bash /root/restart-livekit.sh'
#
# Скрипт сам определяет способ развёртывания (systemd / docker / docker-compose /
# бинарь) и перезапускает, затем проверяет, что порт 7880 снова слушается.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

PORT="${LIVEKIT_PORT:-7880}"
SUDO=""
[[ "$(id -u)" -ne 0 ]] && SUDO="sudo"

echo "=== PISMO: перезапуск LiveKit (порт ${PORT}) ==="

restarted=0

# 1) systemd (livekit-server.service / livekit.service)
for unit in livekit-server livekit; do
  if systemctl list-unit-files 2>/dev/null | grep -q "^${unit}\.service"; then
    echo "→ systemd: перезапуск ${unit}.service"
    $SUDO systemctl restart "${unit}.service"
    sleep 2
    $SUDO systemctl status "${unit}.service" --no-pager -l | head -n 12 || true
    restarted=1
    break
  fi
done

# 2) docker-compose (если есть compose-файл с livekit)
if [[ "$restarted" -eq 0 ]] && command -v docker >/dev/null 2>&1; then
  for dir in /root /opt/livekit /etc/livekit "$HOME"; do
    for f in docker-compose.yml docker-compose.yaml compose.yml; do
      if [[ -f "${dir}/${f}" ]] && grep -qi livekit "${dir}/${f}"; then
        echo "→ docker compose: перезапуск в ${dir}/${f}"
        ( cd "$dir" && ($SUDO docker compose restart livekit 2>/dev/null \
            || $SUDO docker-compose restart livekit 2>/dev/null \
            || $SUDO docker compose restart 2>/dev/null \
            || $SUDO docker-compose restart) )
        restarted=1
        break 2
      fi
    done
  done
fi

# 3) одиночный docker-контейнер (имя или образ содержит livekit)
if [[ "$restarted" -eq 0 ]] && command -v docker >/dev/null 2>&1; then
  cid="$($SUDO docker ps --format '{{.ID}} {{.Image}} {{.Names}}' 2>/dev/null \
        | grep -i livekit | awk '{print $1}' | head -n1)"
  if [[ -n "${cid:-}" ]]; then
    echo "→ docker: перезапуск контейнера ${cid}"
    $SUDO docker restart "$cid"
    restarted=1
  fi
fi

# 4) голый бинарь livekit-server
if [[ "$restarted" -eq 0 ]]; then
  if pgrep -x livekit-server >/dev/null 2>&1; then
    echo "→ бинарь: убиваю livekit-server (перезапусти его своим способом запуска)"
    $SUDO pkill -x livekit-server || true
    echo "  ВНИМАНИЕ: автозапуск бинаря не настроен — запусти LiveKit как обычно:"
    echo "    livekit-server --config /etc/livekit.yaml &"
  else
    echo "!! LiveKit не найден ни как systemd, ни docker, ни бинарь."
    echo "   Проверь вручную: systemctl | grep livekit ; docker ps | grep livekit"
    exit 2
  fi
fi

# ── Проверка, что порт снова слушается ───────────────────────────────────────
echo "→ жду поднятия порта ${PORT}…"
ok=0
for i in $(seq 1 15); do
  if command -v ss >/dev/null 2>&1;   then ss -ltn  2>/dev/null | grep -q ":${PORT} " && { ok=1; break; }; fi
  if command -v netstat >/dev/null 2>&1; then netstat -ltn 2>/dev/null | grep -q ":${PORT} " && { ok=1; break; }; fi
  sleep 1
done

if [[ "$ok" -eq 1 ]]; then
  echo "✓ LiveKit слушает порт ${PORT} — перезапуск успешен."
else
  echo "✗ Порт ${PORT} не поднялся за 15с. Смотри логи:"
  echo "    journalctl -u livekit-server -n 50 --no-pager"
  echo "    docker logs \$(docker ps | grep -i livekit | awk '{print \$1}') --tail 50"
  exit 1
fi
