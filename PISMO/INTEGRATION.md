# PISMO v2 — Инструкция по интеграции

## Новые файлы (добавить в проект)

| Файл | Что делает |
|------|-----------|
| `CallSessionInfo.cs` | Модель данных звонка |
| `CallManager.cs` | UDP движок: аудио + видео + экран |
| `CallForm.cs` | Окно звонка (Discord-стиль) |
| `IncomingCallForm.cs` | Всплывающий входящий звонок |
| `MainForm_MessageActions.cs` | Partial class: edit/delete/reply/forward/calls |
| `pismo_v2_migration.sql` | SQL для phpMyAdmin |

---

## Шаг 1 — База данных

Выполнить `pismo_v2_migration.sql` в phpMyAdmin.

---

## Шаг 2 — NuGet пакеты

Уже должны быть (из предыдущих версий):
- `AForge.Video.DirectShow` — камера
- `NAudio` — аудио
- `MySqlConnector` — MySQL/MariaDB (драйвер без пинга при выдаче соединения из пула)

---

## Шаг 3 — MainForm.cs: 3 вставки

### 3.1 В конструкторе `MainForm()` добавить вызов:

```csharp
public MainForm()
{
    InitializeComponent();
    DeviceSettings.Load();
    SetupPolling();
    this.Load += MainForm_Load;
    
    // ← ДОБАВИТЬ:
    InitMessageActions();
}
```

### 3.2 В `btnSend_Click` — добавить проверки ПЕРЕД основной логикой:

```csharp
private void btnSend_Click(object sender, EventArgs e)
{
    // ← ДОБАВИТЬ первыми строками:
    if (TrySaveEdit()) return;      // режим редактирования
    if (TrySendForward()) return;   // режим пересылки

    // ... остальной код без изменений
    
    // ← ДОБАВИТЬ перед LoadMessages()/LoadGroupMessages():
    if (_currentGroupId >= 0)
        ApplyReplyToLastMessage(isGroup: true);
    else
        ApplyReplyToLastMessage(isGroup: false);
}
```

### 3.3 В `BuildBubble` — добавить вызов контекстного меню и цитату ответа:

В конец метода `BuildBubble`, перед `return bubble;`:

```csharp
// ── Цитата ответа (если есть reply_to_id) ────────────────────
// Добавить параметр в сигнатуру: int replyToId = -1, bool isGroup = false
// и вставить ПОСЛЕ блока имени отправителя:
if (replyToId > 0)
    innerY += BuildReplyQuote(bubble, replyToId, isGroup, isMine, innerY, innerW, PAD);

// ── Пометка «изменено» ─────────────────────────────────────────
// Добавить параметр: bool isEdited = false
// Рядом с lblTime:
if (isEdited)
{
    var lblEdited = new Label
    {
        Text      = "изменено",
        Font      = new Font("Segoe UI", 7f, FontStyle.Italic),
        ForeColor = Color.FromArgb(114, 118, 125),
        AutoSize  = true,
        Location  = new Point(PAD + 50, innerY)
    };
    bubble.Controls.Add(lblEdited);
}

// ── Контекстное меню (правый клик) ────────────────────────────
// Добавить параметры: int msgId, bool isGroup
AttachBubbleContextMenu(bubble, msgId, isGroup, isMine, text, senderName);
```

### 3.4 В `LoadMessages` и `LoadGroupMessages` — передать новые параметры:

```csharp
// В SELECT добавить: m.reply_to_id, m.edited_at, m.is_deleted, m.id
// Пример:
const string sql = @"
    SELECT m.id, m.sender_id, m.text, m.image_data, m.audio_data, m.video_data,
           m.created_at, m.reply_to_id, m.edited_at, m.is_deleted,
           TRIM(CONCAT(u.Name,' ',u.Surname)) AS sender_name, u.login
    FROM messages m
    JOIN users u ON u.id = m.sender_id
    WHERE (m.sender_id=@me AND m.receiver_id=@them)
       OR (m.sender_id=@them AND m.receiver_id=@me)
    ORDER BY m.created_at ASC";

// При вызове BuildBubble:
int    msgId    = Convert.ToInt32(row["id"]);
int    replyId  = row["reply_to_id"] == DBNull.Value ? -1 : Convert.ToInt32(row["reply_to_id"]);
bool   isEdited = row["edited_at"] != DBNull.Value;

var bubble = BuildBubble(sname, time, text, img, audio, isMine, video,
    msgId: msgId, isGroup: false, replyToId: replyId, isEdited: isEdited);
```

### 3.5 В `AddUserCard` — добавить правый клик:

```csharp
// В конце AddUserCard:
AttachConversationContextMenu(pnl, uid, name);
```

### 3.6 В `AddGroupCard` — добавить правый клик:

```csharp
// В конце AddGroupCard:
AttachGroupContextMenu(pnl, gid, name);
```

---

## Шаг 4 — Сигнатура BuildBubble

Добавить параметры:

```csharp
private Panel BuildBubble(
    string senderName, string time, string text,
    byte[] imgBytes, byte[] audioBytes, bool isMine,
    byte[] videoBytes = null,
    int    msgId      = -1,
    bool   isGroup    = false,
    int    replyToId  = -1,
    bool   isEdited   = false)
```

---

## Итог — что появляется

| Функция | Где |
|---------|-----|
| ↩ Ответить на сообщение | ПКМ по пузырю |
| ↪ Переслать сообщение | ПКМ по пузырю |
| ✏ Редактировать | ПКМ по пузырю (только своё) |
| 🗑 Удалить сообщение | ПКМ по пузырю |
| 🗑 Удалить переписку | ПКМ по карточке в сайдбаре (admin) |
| 🗑 Удалить группу | ПКМ по группе в сайдбаре (создатель/admin) |
| 📞 Голосовой звонок | Кнопка в заголовке чата / ПКМ по карточке |
| 📹 Видеозвонок | Кнопка в заголовке чата / ПКМ по карточке |
| 🖥 Демонстрация экрана | Кнопка в окне звонка |
| Входящий звонок | Всплывающее окно (polling 1.5 сек) |
| Цитата ответа в пузыре | Автоматически при загрузке |
| Пометка «изменено» | Рядом со временем |
