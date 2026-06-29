# Проверка WS-сервера НА ПК2 (где node server.js). Плоский скрипт без блоков.
# Запуск: двойной клик по check-ws.bat (лучше от админа — для правила фаервола).
$ErrorActionPreference = "Continue"
$port = 8080
Write-Host ("== PISMO WS-сервер, порт {0} ==" -f $port) -ForegroundColor Cyan

$node = Get-Process node -ErrorAction SilentlyContinue
if ($node) { Write-Host ("[OK] node запущен (PID: {0})" -f ($node.Id -join ', ')) -ForegroundColor Green }
if (-not $node) { Write-Host "[X] node НЕ запущен — запусти start-ws-server.bat" -ForegroundColor Red }

$listen = Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue
if ($listen.TcpTestSucceeded) { Write-Host ("[OK] Порт {0} слушается локально" -f $port) -ForegroundColor Green }
if (-not $listen.TcpTestSucceeded) { Write-Host ("[X] Порт {0} НЕ слушается" -f $port) -ForegroundColor Red }

$rule = Get-NetFirewallRule -DisplayName ("PISMO WS {0}" -f $port) -ErrorAction SilentlyContinue
if (-not $rule) { New-NetFirewallRule -DisplayName ("PISMO WS {0}" -f $port) -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -ErrorAction SilentlyContinue | Out-Null }
$rule2 = Get-NetFirewallRule -DisplayName ("PISMO WS {0}" -f $port) -ErrorAction SilentlyContinue
if ($rule2) { Write-Host "[OK] Правило фаервола на месте" -ForegroundColor Green }
if (-not $rule2) { Write-Host "[X] Правило фаервола не создано (запусти от АДМИНИСТРАТОРА)" -ForegroundColor Red }

Write-Host "IPv4-адреса этого ПК (впиши в ip.txt клиента, ws=ws://IP:8080/):" -ForegroundColor Cyan
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -ExpandProperty IPAddress

Read-Host "Enter для выхода"
