# ============================================================
#  Проверка ДОСТУПНОСТИ WS-сервера С ПК1 (клиент).
#  Запуск:
#     powershell -ExecutionPolicy Bypass -File test-ws-from-client.ps1 -ServerHost 192.168.1.50
#  (ServerHost = IP ПК2, который показал check-ws-server.ps1)
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$ServerHost,
    [int]$Port = 8080
)

Write-Host "== Проверка связи с WS-сервером $ServerHost`:$Port ==" -ForegroundColor Cyan

# 1) TCP-доступность порта
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $iar = $tcp.BeginConnect($ServerHost, $Port, $null, $null)
    if ($iar.AsyncWaitHandle.WaitOne(4000) -and $tcp.Connected) {
        Write-Host "[OK] TCP: порт $Port на $ServerHost открыт" -ForegroundColor Green
        $tcp.Close()
    } else {
        Write-Host "[X] TCP: не достучаться до $ServerHost`:$Port (фаервол/сервер не запущен/не тот IP)" -ForegroundColor Red
        Read-Host "Enter"; exit 1
    }
} catch {
    Write-Host "[X] TCP FAIL: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host "Enter"; exit 1
}

# 2) WebSocket-рукопожатие + тестовый register (мягкий режим, без токена)
try {
    $uri = [Uri]"ws://${ServerHost}:${Port}/"
    $ws  = New-Object System.Net.WebSockets.ClientWebSocket
    $cts = New-Object System.Threading.CancellationTokenSource
    $cts.CancelAfter(5000)
    $ws.ConnectAsync($uri, $cts.Token).Wait()
    Write-Host "[OK] WebSocket рукопожатие прошло (State: $($ws.State))" -ForegroundColor Green

    $reg = '{"type":"register","userId":999999,"token":""}'
    $buf = [System.Text.Encoding]::UTF8.GetBytes($reg)
    $seg = New-Object System.ArraySegment[byte] (,$buf)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).Wait()
    Start-Sleep -Milliseconds 700

    if ($ws.State -eq 'Open') {
        Write-Host "[OK] Сервер принял подключение — WS РАБОТАЕТ. Впиши в ip.txt:" -ForegroundColor Green
        Write-Host "       ws=ws://${ServerHost}:${Port}/" -ForegroundColor Yellow
    } else {
        Write-Host "[X] Соединение закрылось после register (State: $($ws.State)) — вероятно отклонён JWT. Проверь, что JWT_SECRET на сервере = Secret в JwtAuth.cs" -ForegroundColor Red
    }
    try { $ws.Dispose() } catch {}
} catch {
    Write-Host "[X] WS FAIL: $($_.Exception.Message)" -ForegroundColor Red
}

Read-Host "`nEnter для выхода"
