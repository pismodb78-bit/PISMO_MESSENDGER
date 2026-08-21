# PISMO для Linux (Avalonia)

Нативный оконный клиент PISMO под Linux (тестируется на **CachyOS/Arch**).
Пишется с нуля на [Avalonia](https://avaloniaui.net) (кроссплатформенный C#/XAML
UI), но **вся логика — БД, шифрование, JWT, парольный хеш — переиспользуется**
из Windows-клиента (файлы линкуются в `PISMO.Linux.csproj`, без дублирования).
Сервер (ws-server, MySQL, LiveKit) общий и не меняется.

Статус: **срез 0.1 — вход в аккаунт.** Доказывает, что тулчейн, доступ к БД и
авторизация работают на Linux. Дальше по частям: список чатов → переписка →
звонки (через CEF) → остальное.

## Что нужно (любой дистрибутив с .NET 8)

Клиент кроссплатформенный — идёт на Arch/CachyOS, Ubuntu/Debian, Fedora и др.
Нужен только **.NET 8 SDK**; отличаются лишь команды установки:

```bash
# Arch / CachyOS
sudo pacman -S dotnet-sdk

# Ubuntu 22.04+/24.04, Debian 12+
sudo apt update && sudo apt install -y dotnet-sdk-8.0
#   если пакета нет в репозиториях — подключи фид Microsoft:
#   https://learn.microsoft.com/dotnet/core/install/linux-ubuntu

# Fedora
sudo dnf install dotnet-sdk-8.0
```

Проверь: `dotnet --version` → `8.x`. Больше для входа ничего не требуется —
Avalonia и MySQL-коннектор приедут через NuGet при первой сборке (нужен
интернет). GUI работает и на X11, и на Wayland (GNOME/KDE и пр.).

## Настройка подключения

Клиент читает адрес БД из `ip.txt` рядом с бинарником — тот же формат, что и в
Windows-версии. Положи файл `ip.txt` в папку `linux/PISMO.Linux/` (сборка сама
скопирует его к бинарнику):

```
server=АДРЕС;port=ПОРТ;uid=ПОЛЬЗОВАТЕЛЬ;password=ПАРОЛЬ;database=bdauth;ws=ws://АДРЕС:8080
```

(Возьми ровно такой же `ip.txt`, что лежит у Windows-клиента.)

По желанию — секрет JWT в `pismo.config` (`jwt_secret=…`); если файла нет,
используется вшитый дефолт, совпадающий с ws-сервером, — вход работает и без него.

> `ip.txt` и `pismo.config` намеренно **не** в гите — это адрес БД и секрет.

## Запуск

```bash
cd linux/PISMO.Linux
dotnet run
```

Первая сборка качает NuGet-пакеты (Avalonia, MySqlConnector) — займёт минуту.
Откроется окно входа. Введи логин/пароль существующего аккаунта PISMO — при
успехе откроется главное окно с «✓ Вход выполнен» и твоим именем/ролью.

## Сборка релиза (self-contained, без установки .NET у пользователя)

```bash
dotnet publish -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -o out
# бинарник: out/pismo   (рядом положи ip.txt)
./out/pismo
```

## Структура

| Путь | Назначение |
|------|------------|
| `Program.cs`, `App.axaml` | точка входа Avalonia |
| `Views/LoginWindow.*` | окно входа |
| `Views/MainWindow.*` | главное окно (пока заглушка после входа) |
| `Services/AuthService.cs` | логин (та же логика, что в Windows `LoginForm`) |
| `Compat/ConnectionGuard.cs` | Linux-заглушка WinForms-индикатора связи |
| `Shared/*` (линки) | переиспользуемый код Windows-клиента |

## Дорожная карта

- [x] 0.1 — вход в аккаунт (тулчейн + БД + JWT на Linux)
- [ ] 0.2 — список личных чатов + друзья
- [ ] 0.3 — переписка (текст), отправка/приём по WS
- [ ] 0.4 — медиа (картинки/файлы/голосовые)
- [ ] 0.5 — звонки и демонстрация экрана (LiveKit через CEF)
- [ ] 0.6 — серверы/каналы, профили, настройки
- [ ] упаковка в AppImage/Flatpak/AUR
