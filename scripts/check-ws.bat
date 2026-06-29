@echo off
chcp 65001 >nul
rem Двойной клик на ПК2 — проверка WS-сервера (процесс, порт, фаервол, IP).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0check-ws-server.ps1"
