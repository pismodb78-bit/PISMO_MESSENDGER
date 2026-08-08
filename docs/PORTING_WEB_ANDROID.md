# PISMO — стек для веб- и Android-версии

Справочник по тому, что используется в десктоп-клиенте (WinForms/.NET), чтобы
повторить в вебе и на Android. Всё — по фактам из кода.

## 1. Звонки и видео/демонстрация

- **Транспорт: LiveKit (SFU).** Полностью заменил самописный WebRTC (coturn +
  offer/answer/ICE). Комната = строковый id call-сессии — все участники звонка
  в одной комнате.
- **Сервер LiveKit:** `ws://5.181.23.167:7880` (см. `LiveKitSettings.cs`,
  `livekitsettings.json`). Для веба с HTTPS нужен **wss://** (иначе mixed-content).
- **Десктоп:** нативный `livekit_ffi.dll` (Rust/libwebrtc) через P/Invoke
  (`Native/LiveKitFfi.cs`, `Native/NativeCallTransport.cs`).
  - **Веб:** `livekit-client` (JS SDK).
  - **Android:** `io.livekit:livekit-android` SDK.
- **Токен доступа:** JWT **HS256**, подписывается `ApiKey`/`ApiSecret` LiveKit
  (`LiveKitSettings.CreateToken`). Payload: `iss=ApiKey`, `sub=identity`(=id
  пользователя), `video` grant (`roomJoin, room, canPublish, canSubscribe,
  canPublishData, canUpdateOwnMetadata`), `nbf/exp`.
  - ⚠️ Сейчас токен генерит **клиент** (секрет зашит в приложении). Для веба/
    Android так НЕЛЬЗЯ — токен должен выдавать **бэкенд** (секрет только на сервере).
- **Кодеки:**
  - Видео/демка: **AV1** и **H.264** (переключаемо; LiveKit также умеет VP8/VP9).
    Кодек фиксируется на публикации и на всю сессию (Pion залипает на кодеке
    первого трека) — смена только при перезаходе.
  - Аудио: **Opus**, `MaxBitrate=128000`, `DTX=true`, `RED=true`.
- **Захват экрана:** десктоп — Windows Graphics Capture (`Native/WgcCapturer.cs`),
  BGRA-кадры → FFI VideoSource. Веб — `getDisplayMedia`. Android — `MediaProjection`.
- **Индикатор «говорит»:** LiveKit `ActiveSpeakersChanged` (удалённые) +
  локальный детектор по уровню отправляемого кадра (см. `UpdateLocalSpeaking`).

## 2. Обработка звука (шумодав/усиление)

Тракт микрофона (в порядке применения), см. `Native/NativeCallTransport.cs`
`ProcessAndSendFrame`:

1. **APM libwebrtc** (через FFI): AEC (эхоподавление). AGC и HPF — выключены.
2. **Порог регистрации (voice gate):** ниже порога (dBFS) кадр не передаётся;
   hangover ~150 мс. Диапазон −90..0 дБ (`ApplyVoiceGate`).
3. **Шумодав:**
   - **RNNoise** — нейросетевой, `noise/rnnoise.wasm`, крутится через Wasmtime
     (`Native/RnnoiseDenoiser.cs`). Кадр 480 сэмплов (10 мс) @ 48к моно.
     Сила = глубина глушения фона в паузах (VAD-гейт + grace ~260 мс).
   - **TransientLimiter** (`Native/TransientLimiter.cs`) — давит клики клавиш
     громче голоса (look-ahead по максимуму линии задержки).
   - Fallback без RNNoise: `SpectralDenoiser` (Wiener/STFT, чистый C#).
4. **Усиление на выходе:** до **500%**, кривая `SoftGainSample` — линейно до 0.9
   полной шкалы, мягкий лимитер (tanh) у потолка.

Звук ДЕМОНСТРАЦИИ идёт отдельным трактом — только AEC, без шумодава/порога.

**Порт:**
- Веб: тот же `rnnoise.wasm` работает в браузере (AudioWorklet), либо LiveKit
  Krisp-плагин. AEC/AGC — штатные в браузерном WebRTC.
- Android: WebRTC built-in NS/AEC, либо RNNoise (JNI).
- Частота строго **48000 Гц**.

## 3. Отправка файлов

- Файлы хранятся **прямо в БД как LONGBLOB**: колонки `image_data`, `audio_data`,
  `video_data`, `file_data` + `file_name` в таблицах `messages`,
  `group_messages`, `server_messages` (см. `scripts/schema.sql`,
  `pismo_messenger_migration.sql`).
- Клиент пишет/читает BLOB напрямую через MySQL-соединение.
- **Порт:** для веба/Android так делать не стоит (тяжёлые BLOB через прямой
  доступ к БД). Нужен HTTP-эндпоинт загрузки/скачивания (или объектное
  хранилище + ссылка в сообщении). На переходный период можно оставить BLOB, но
  отдавать через API.

## 4. Шифрование

- **Пароли:** PBKDF2-**SHA256**, **100 000** итераций, соль 16 байт, ключ 32
  байта. Формат хранения: `pbkdf2$100000$<base64 salt>$<base64 key>`
  (`PasswordHasher.cs`). Есть чтение legacy: bcrypt `$2…` (из веб-версии) и
  открытый текст (миграция при входе). **Веб/Android должны считать тот же
  PBKDF2-SHA256/100k**, чтобы хэши были совместимы.
- **Сообщения:** **AES-256-GCM** (`Crypto.cs`). Формат `enc:v2:` +
  base64(nonce(12) + tag(16) + ciphertext). Legacy `enc:v1:` = AES-256-CBC
  (только чтение). Ключ = SHA-256 от зашитой фразы.
  - ⚠️ Ключ **общий и зашит в клиенте** — это защита «не прочитать прямо в БД»,
    **не** end-to-end. В вебе зашитый ключ виден в JS — если нужна реальная
    защита, делать E2E или шифровать на сервере. Для совместимости чтения нужен
    тот же ключ и формат.

## 5. Инфраструктура

- **БД:** MySQL/MariaDB. Сейчас клиент **напрямую** коннектится к БД
  (`DBHelper`). Для веба/Android прямой доступ к MySQL невозможен/небезопасен —
  **нужен бэкенд-API** (REST/gRPC) поверх той же схемы.
- **Сигналинг/уведомления:** единый **WebSocket-сервер** (`ws-server/`,
  `WebSocketSignalingClient.cs`) — звонки, typing, live-правки/удаления,
  агрегированные уведомления. Годится и для веба (тот же WS), для Android —
  WS + FCM для пуш-уведомлений в фоне.
- **Схема БД:** `scripts/schema.sql` — таблицы `users, messages, group_chats,
  group_members, group_messages, friends, user_prefs, user_blocks, servers,
  server_roles, server_members, server_bans, server_channels, server_messages,
  call_sessions, call_participants, voice_presence`.
- **Присутствие:** `users.last_seen` / `last_active`; статусы в сети/бездействует/
  не в сети по порогам 40с/90с (heartbeat ~15с).

## 6. Что обязательно вынести на бэкенд при порте

1. Генерацию **LiveKit-токенов** (секрет не должен попадать в браузер/APK).
2. **Доступ к БД** — через API, а не прямым MySQL-коннектом.
3. **Загрузку файлов** — через HTTP-эндпоинт/хранилище.
4. Пересмотреть **шифрование сообщений** (общий зашитый ключ в вебе виден).
