@echo off
rem Двойной клик — проверка связи с WS-сервером (ПК1 -> ПК2).
rem По умолчанию IP 85.174.248.59. Свой: test-ws.bat 1.2.3.4
setlocal
set SRV=%1
if "%SRV%"=="" set SRV=85.174.248.59
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-ws-from-client.ps1" -ServerHost %SRV%
echo.
echo ====== окно НЕ закроется, читай вывод выше ======
pause
