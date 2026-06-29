@echo off
chcp 65001 >nul
rem ============================================================
rem  Запуск PISMO WebSocket-сервера (ПК2 — где БД и вебсокет).
rem  Порт 8080. Секрет по умолчанию совпадает с PISMO/JwtAuth.cs,
rem  поэтому отдельно задавать JWT_SECRET НЕ нужно.
rem  Если меняешь секрет в JwtAuth.cs — задай тот же и здесь:
rem     set JWT_SECRET=ТВОЙ_СЕКРЕТ
rem ============================================================
cd /d "%~dp0\..\ws-server"

where node >nul 2>nul
if errorlevel 1 (
    echo [!] Node.js не установлен. Скачай: https://nodejs.org
    pause
    exit /b 1
)

if not exist node_modules (
    echo [*] Устанавливаю зависимости (npm install)...
    call npm install
)

set PORT=8080
rem set REQUIRE_JWT=1   (включить только когда ВСЕ клиенты >= 1.0.31)
echo [*] Запускаю WS-сервер на порту %PORT% ...
node server.js
pause
