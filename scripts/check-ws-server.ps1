# WS server check ON PC2 (where node server.js runs). ASCII-only (PowerShell 5.1 safe).
# Run: check-ws.bat (better as Administrator for the firewall rule).
$ErrorActionPreference = "Continue"
$port = 8080
Write-Host ("== PISMO WS server, port {0} ==" -f $port) -ForegroundColor Cyan

$node = Get-Process node -ErrorAction SilentlyContinue
if ($node) { Write-Host ("[OK] node running (PID: {0})" -f ($node.Id -join ', ')) -ForegroundColor Green }
if (-not $node) { Write-Host "[X] node NOT running - launch start-ws-server.bat" -ForegroundColor Red }

$listen = Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue
if ($listen.TcpTestSucceeded) { Write-Host ("[OK] Port {0} is listening locally" -f $port) -ForegroundColor Green }
if (-not $listen.TcpTestSucceeded) { Write-Host ("[X] Port {0} NOT listening" -f $port) -ForegroundColor Red }

$ruleName = "PISMO WS " + $port
$rule = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if (-not $rule) { New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -ErrorAction SilentlyContinue | Out-Null }
$rule2 = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
if ($rule2) { Write-Host "[OK] Firewall rule present" -ForegroundColor Green }
if (-not $rule2) { Write-Host "[X] Firewall rule missing (run as Administrator)" -ForegroundColor Red }

Write-Host "IPv4 addresses of this PC (put into client ip.txt as ws=ws://IP:8080/):" -ForegroundColor Cyan
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -ExpandProperty IPAddress

Read-Host "Press Enter to exit"
