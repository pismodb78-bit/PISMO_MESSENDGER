#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Восстановление LiveKit-сервера для PISMO после переустановки ОС.
# Запускать НА СЕРВЕРЕ 5.181.23.167 (Linux) из-под root:
#     bash livekit-restore.sh
#
# ВАЖНО: ключи ниже ДОЛЖНЫ совпадать с PISMO/LiveKitSettings.cs
#   ApiKey    = APIkey5I8EkGBDSc4jdmI5QcVC
#   ApiSecret = Y3pIteGv4BxEEWSmIvE3P9YqDTBdc3nF7IzWNa51flCRS8Gx
# иначе сервер отвергнет JWT-токены клиента ("invalid token").
#
# Клиент подключается по ws://5.181.23.167:7880 (без TLS, голый IP).
# ─────────────────────────────────────────────────────────────────────────────
set -e

# 1) Docker (ставим, если его нет — после переустановки ОС обычно нет)
if ! command -v docker >/dev/null 2>&1; then
  echo "== ставлю Docker =="
  curl -fsSL https://get.docker.com | sh
fi

# 2) Конфиг LiveKit с КЛЮЧАМИ ИЗ КЛИЕНТА
cat > /root/livekit.yaml <<'YAML'
port: 7880
rtc:
  tcp_port: 7881
  port_range_start: 50000
  port_range_end: 60000
  use_external_ip: true
keys:
  APIkey5I8EkGBDSc4jdmI5QcVC: Y3pIteGv4BxEEWSmIvE3P9YqDTBdc3nF7IzWNa51flCRS8Gx
logging:
  level: info
YAML

# 3) Открыть порты в фаерволе (ufw). Если ufw не стоит — блок пропустится.
#    Нужны: 7880/tcp (сигналинг ws), 7881/tcp (RTC over TCP), 50000-60000/udp (медиа).
if command -v ufw >/dev/null 2>&1; then
  echo "== открываю порты в ufw =="
  ufw allow 7880/tcp  || true
  ufw allow 7881/tcp  || true
  ufw allow 50000:60000/udp || true
fi

# 4) Запуск в host-сети (рекомендация LiveKit для одного узла) + автоперезапуск
echo "== запускаю livekit-server =="
docker rm -f livekit 2>/dev/null || true
docker pull livekit/livekit-server:latest
docker run -d --name livekit --restart unless-stopped --network host \
  -v /root/livekit.yaml:/livekit.yaml \
  livekit/livekit-server --config /livekit.yaml

sleep 2
echo "== последние строки лога =="
docker logs --tail 30 livekit

echo
echo "Проверка здоровья:  curl http://127.0.0.1:7880   (должно вернуть OK)"
curl -s http://127.0.0.1:7880 || true
echo
echo "Готово. С ПК проверь: Test-NetConnection 5.181.23.167 -Port 7880  → True"
