# ============================================================
#  Проверка WS-сервера НА ПК2 (где запущен node server.js).
#  Запуск: правый клик → "Выполнить с помощью PowerShell"
#  (лучше от имени администратора — чтобы добавить правило фаервола).
# ============================================================
$port = 8080
Write-Host "== Проверка PISMO WS-сервера (порт $port) ==" -ForegroundColor Cyan

# 1) Процесс node
$node = Get-Process node -ErrorAction SilentlyContinue
if ($node) { Write-Host "[OK] node запущен (PID: $($node.Id -join ', '))" -ForegroundColor Green }
else { Write-Host "[X] node НЕ запущен — запусти start-ws-server.bat" -ForegroundColor Red }

# 2) Слушается ли порт
$listen = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
if ($listen) { Write-Host "[OK] Порт $port слушается" -ForegroundColor Green }
else { Write-Host "[X] Порт $port НЕ слушается (сервер не стартовал или другой порт)" -ForegroundColor Red }

# 3) Правило фаервола (вход)
$rule = Get-NetFirewallRule -DisplayName "PISMO WS $port" -ErrorAction SilentlyContinue
if (-not $rule) {
    Write-Host "[~] Добавляю правило фаервола для порта $port ..." -ForegroundColor Yellow
    try {
        New-NetFirewallRule -DisplayName "PISMO WS $port" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port | Out-Null
        Write-Host "[OK] Правило фаервола добавлено" -ForegroundColor Green
    } catch {
        Write-Host "[X] Не удалось (запусти PowerShell от АДМИНИСТРАТОРА): $($_.Exception.Message)" -ForegroundColor Red
    }
} else { Write-Host "[OK] Правило фаервола уже есть" -ForegroundColor Green }

# 4) IP этого ПК — его вписать в ip.txt на клиентах
Write-Host "`nIPv4-адреса этого ПК (впиши в ip.txt клиента):" -ForegroundColor Cyan
(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -ExpandProperty IPAddress) | ForEach-Object { Write-Host "    $_" }

Write-Host "`nГотово." -ForegroundColor Cyan
Read-Host "Enter для выхода"
