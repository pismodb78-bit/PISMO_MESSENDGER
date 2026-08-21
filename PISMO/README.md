# PISMO — Мессенджер

Discord-стиль мессенджер на C# / WinForms + MySQL (MAMP).

---

## Шаг 1 — Обновить БД

Открой phpMyAdmin → выбери `bdauth` → вкладка **SQL** → выполни:

```sql
CREATE TABLE IF NOT EXISTS `messages` (
  `id`          INT(11) UNSIGNED NOT NULL AUTO_INCREMENT,
  `sender_id`   INT(10) UNSIGNED NOT NULL,
  `receiver_id` INT(10) UNSIGNED NOT NULL,
  `text`        TEXT             NOT NULL DEFAULT '',
  `image_data`  LONGBLOB                  DEFAULT NULL,
  `is_read`     TINYINT(1)       NOT NULL DEFAULT 0,
  `created_at`  DATETIME         NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`id`),
  KEY `idx_sender`   (`sender_id`),
  KEY `idx_receiver` (`receiver_id`),
  KEY `idx_conv`     (`sender_id`, `receiver_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

Файл `pismo_messenger_migration.sql` содержит тот же запрос.

---

## Шаг 2 — Настроить подключение

Открой `ip.txt` и укажи свой сервер:

```
server=192.168.0.15;port=3306;uid=user1;password=scent01;database=bdauth
```

> ⚠ Параметр должен быть `uid=`, а НЕ `username=` — ConnectorNet требует именно `uid`.

---

## Шаг 3 — Открыть проект

1. Открой `PISMO.csproj` в Visual Studio 2022 Community.
2. NuGet восстановится автоматически (`MySqlConnector 2.4.0`).
3. F5 — запуск.

---

## Роли

| Роль     | Что видит в сайдбаре                    | Действия                    |
|----------|-----------------------------------------|-----------------------------|
| `admin`  | Все зарегистрированные пользователи     | Войти за любого и писать    |
| `teacher`| Список диалогов (с кем переписывался)   | Писать любому пользователю  |

**Логин admin:** `admin` / `1234` (из вашего дампа)

---

## Возможности

- ✅ Личные сообщения (DM) между любыми пользователями
- ✅ Список диалогов с превью последнего сообщения
- ✅ Значок непрочитанных (красный badge)
- ✅ Отправка изображений (кнопка 📎 или Ctrl+V)
- ✅ Просмотр изображения в полном размере (клик)
- ✅ Enter → отправить, Shift+Enter → новая строка
- ✅ Admin: вход за любого пользователя
- ✅ Смена пароля
- ✅ Discord-дизайн: тёмная тема, blurple кнопки, аватары с буквами
- ✅ Скруглённые пузырьки сообщений

---

## Структура файлов

```
PISMO/
├── Program.cs                   — точка входа
├── UserSession.cs               — данные сессии (статика)
├── DBHelper.cs                  — соединение с MySQL
├── LoginForm.cs / .Designer.cs  — форма входа
├── RegisterForm.cs / .Designer.cs
├── ChangePasswordForm.cs / .Designer.cs
├── MainForm.cs                  — вся логика чата
├── MainForm.Designer.cs         — Discord-дизайн
├── ip.txt                       — строка подключения
├── PISMO.csproj
└── pismo_messenger_migration.sql
```
