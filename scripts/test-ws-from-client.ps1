# Проверка доступности WS-сервера С КЛИЕНТА (ПК1). Плоский скрипт без блоков.
# Запуск: двойной клик по test-ws.bat  ИЛИ
#   powershell -ExecutionPolicy Bypass -File test-ws-from-client.ps1 -ServerHost 85.174.248.59
param([string]$ServerHost = "85.174.248.59", [int]$Port = 8080)
$ErrorActionPreference = "Continue"

Write-Host ("== Проверка WS {0}:{1} ==" -f $ServerHost, $Port) -ForegroundColor Cyan

# 1) TCP-доступность
$tcp = Test-NetConnection -ComputerName $ServerHost -Port $Port -WarningAction SilentlyContinue
if ($tcp.TcpTestSucceeded) { Write-Host ("[OK] TCP {0}:{1} открыт" -f $ServerHost, $Port) -ForegroundColor Green }
if (-not $tcp.TcpTestSucceeded) { Write-Host "[X] TCP закрыт: порт недоступен (фаервол/сервер не запущен/не тот IP)" -ForegroundColor Red }
if (-not $tcp.TcpTestSucceeded) { Read-Host "Enter"; exit 1 }

# 2) WebSocket-рукопожатие + тестовый register без токена (мягкий режим)
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$cts = New-Object System.Threading.CancellationTokenSource
$cts.CancelAfter(5000)
$uri = [Uri]("ws://{0}:{1}/" -f $ServerHost, $Port)
$ok = $true
try { $ws.ConnectAsync($uri, $cts.Token).Wait() } catch { $ok = $false; Write-Host ("[X] WS не подключился: {0}" -f $_.Exception.Message) -ForegroundColor Red }

if ($ok) { Write-Host ("[OK] WebSocket подключён (State: {0})" -f $ws.State) -ForegroundColor Green }
if ($ok) {
  $reg = '{"type":"register","userId":999999,"token":""}'
  $buf = [System.Text.Encoding]::UTF8.GetBytes($reg)
  $seg = New-Object System.ArraySegment[byte] (,$buf)
  try { $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait() } catch {}
  Start-Sleep -Milliseconds 800
}
if ($ok -and $ws.State -eq 'Open') { Write-Host "[OK] Сервер принял подключение — WS РАБОТАЕТ" -ForegroundColor Green }
if ($ok -and $ws.State -eq 'Open') { Write-Host ("     Строка для ip.txt:  ws=ws://{0}:{1}/" -f $ServerHost, $Port) -ForegroundColor Yellow }
if ($ok -and $ws.State -ne 'Open') { Write-Host ("[X] Закрылось после register (State: {0}) — сервер отклонил (старый server.js или JWT)" -f $ws.State) -ForegroundColor Red }

try { $ws.Dispose() } catch {}
Read-Host "Enter для выхода"
