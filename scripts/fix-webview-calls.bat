@echo off
REM ============================================================
REM  PISMO — восстановление движка звонков (WebView2).
REM  Лечит ошибку 0x8007139F "Группа или ресурс не находятся
REM  в нужном состоянии" при заходе в звонок.
REM  Запускать ОТ ИМЕНИ АДМИНИСТРАТОРА (ПКМ -> Запуск от имени администратора).
REM ============================================================
echo.
echo === PISMO: восстановление движка звонков (WebView2) ===
echo.

echo [1/4] Закрываю PISMO и зависшие процессы WebView2...
taskkill /F /IM PISMO.exe        >nul 2>&1
taskkill /F /IM msedgewebview2.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/4] Чищу папку данных транспорта звонков...
rmdir /S /Q "%LOCALAPPDATA%\PISMO\webview-rtc"      >nul 2>&1
REM запасные папки с уникальным суффиксом (от ретраев)
for /d %%d in ("%LOCALAPPDATA%\PISMO\webview-rtc-*") do rmdir /S /Q "%%d" >nul 2>&1

echo [3/4] Чищу папки WebView2 рядом с приложением...
for /d /r "%~dp0.." %%d in (*.WebView2) do rmdir /S /Q "%%d" >nul 2>&1

echo [4/4] Проверяю WebView2 Runtime...
reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv >nul 2>&1
if %errorlevel%==0 (
    echo     WebView2 Runtime установлен.
) else (
    echo     WebView2 Runtime НЕ НАЙДЕН или повреждён!
    echo     Установи его: winget install --id Microsoft.EdgeWebView2Runtime -e
    echo     или скачай "Evergreen Standalone Installer" с сайта Microsoft WebView2.
)

echo.
echo Готово. Запусти PISMO и попробуй зайти в звонок.
echo Если ошибка осталась — переустанови WebView2 Runtime (см. выше).
echo.
pause
