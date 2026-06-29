@echo off
rem Двойной клик на ПК2 — проверка WS-сервера (процесс, порт, фаервол, IP).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-ws-server.ps1"
echo.
echo ====== окно НЕ закроется, читай вывод выше ======
pause
