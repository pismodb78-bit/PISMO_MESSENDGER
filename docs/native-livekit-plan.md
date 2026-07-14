# Нативный клиент звонков (замена WebView2 + LiveKit-JS)

## Зачем
Сейчас звонок = WebView2 (Chromium) + livekit-client.js. Активный VR (виртуальный
дисплей-драйвер Virtual Desktop) корёжит системную графику, и `CreateCoreWebView2Controller`
падает `0x8007139F` — на любом Chromium. Уйти от бага можно только уйдя от браузера:
нативный WebRTC-клиент, который не зависит от Chromium.

## Технология
**LiveKit `client-sdk-ffi`** (Rust, обёртка над libwebrtc) — официальный нативный слой
LiveKit, на котором построены их Unity/Python/Node SDK. Отдаёт нативную библиотеку
(`livekit_ffi.dll`) с protobuf-протоколом (request/response + async-события). Подключается
к тому же LiveKit-серверу теми же JWT-токенами (`LiveKitSettings.CreateToken`).

Альтернативы отклонены: CEF/Electron = тот же Chromium (тот же баг); SIPSorcery = не
говорит на сигналинге LiveKit (пришлось бы переписывать протокол).

## Архитектура
```
CallForm (WinForms UI — плитки, кнопки — БЕЗ изменений)
   │
NativeCallTransport.cs   (новый; тот же публичный контракт, что у WebRtcTransport:
   │                       события ParticipantJoined/TileFrame/Connected и т.д.)
   │  P/Invoke + protobuf (LiveKit FFI protocol)
livekit_ffi.dll  (Rust client-sdk-ffi; сигналинг, ICE/TURN, SFU, кодеки)
   │
mic/cam/screen capture (NAudio/WASAPI, MediaFoundation/AForge, наш BitBlt/DXGI)
```
Публичный контракт `WebRtcTransport` (события камеры/демки/кадров/участников) переносим
1:1 в `NativeCallTransport`, чтобы `CallForm` и вся плиточная UI работали без переписывания.

## Медиа-маппинг (что было в браузере → чем заменяем)
- **Микрофон:** WASAPI/NAudio capture → PCM → LiveKit ffi audio source.
- **Воспроизведение:** ffi audio sink → PCM → NAudio playback (+ индивидуальная громкость/мьют).
- **Эхоподавление/шумодав:** libwebrtc APM внутри ffi (+ наш RNNoise как опция).
- **Голосовая активация:** уровень с mic-потока (у нас уже есть логика dB-порога).
- **Камера:** MediaFoundation/AForge capture → ffi video source; удалённая → ffi video sink → PictureBox.
- **Демонстрация экрана:** наш BitBlt/DXGI capture → ffi video source (нативно, без getDisplayMedia —
  и VR-виртуальные мониторы тут уже НЕ трогают Chromium). Аппаратный энкод — через libwebrtc/ffi.
- **Плитки участников/активный говорящий/пинг:** из событий ffi.

## Фазы (каждая — самостоятельно проверяемая)
- **Ф0. Прототип-биндинг.** Собрать/достать `livekit_ffi.dll` под win-x64, сгенерить C#
  из `.proto`, подключиться к комнате, зайти с одним аудио, услышать/быть услышанным.
  Валидирует весь биндинг. ← начинаем отсюда.
- **Ф1. Аудио-звонок.** Полный голос: mic+speaker, мьют, индивидуальная громкость,
  голосовая активация, шумодав, устройства ввода/вывода.
- **Ф2. Камера.** Публикация + подписка + рендер плиток.
- **Ф3. Демонстрация экрана.** Нативный захват (наш выбор монитора) → публикация;
  приём → рендер театра/плитки. Аппаратный энкод.
- **Ф4. Паритет фич.** Активные говорящие, пинг, смена устройств на лету, звуки событий.
- **Ф5. Снос WebView2 из звонка** (плееры GIF/видео могут остаться на WebView2 —
  их VR-баг не трогает, т.к. они не критичны; либо тоже перевести).

## Кто что делает
- **Я (в этой среде):** пишу C#-биндинг, генерацию protobuf, обёртки захвата/воспроизведения,
  интеграцию в CallForm, инструкции по сборке `livekit_ffi.dll` (cargo) или где взять прибилд.
- **Ты (Windows + VR):** собираешь/кладёшь нативную dll, гоняешь и тестируешь (в т.ч. при
  активном VR), присылаешь результат. Я не могу собрать Rust/натив/Windows у себя — итерации
  через тебя.

## Риски / открытые вопросы
- Достать/собрать `livekit_ffi.dll` под win-x64 (cargo build client-sdk-ffi, либо прибилд из
  релизов LiveKit-SDK).
- Аппаратный видео-энкод (NVENC/QuickSync) через libwebrtc в ffi — доступность/конфиг.
- Качество эхоподавления (libwebrtc APM vs браузерный).
- Объём: реалистично несколько недель и много итераций сборки на твоей стороне.

## ✅ Ф0 (частично сделано в этой сессии)
- **Артефакт найден и подтверждён.** Нативная `livekit_ffi.dll` (win-x64, C-ABI, ~24 МБ)
  + заголовок `livekit_ffi.h` лежат внутри Python-колеса LiveKit:
  ```
  pip download livekit --only-binary=:all: --platform win_amd64 --python-version 3.11 -d wheels
  # затем из livekit-*.whl: livekit/rtc/resources/livekit_ffi.dll  ->  положить рядом с PISMO.exe
  ```
  (Node-пакет `@livekit/rtc-node` даёт napi `.node` — для C# НЕ годится; нужен именно этот C-ABI.)
- **C-ABI (весь!) — 4 функции:**
  ```c
  FfiHandleId livekit_ffi_request(const uint8_t* data, size_t len,
                                  const uint8_t** res_ptr, size_t* res_len);
  bool        livekit_ffi_drop_handle(FfiHandleId handle);
  void        livekit_ffi_initialize(FfiCallback cb, bool captureLogs,
                                     const char* sdk, const char* version);
  void        livekit_ffi_dispose();
  // FfiCallback = void(*)(const uint8_t* data, size_t len)  // async FfiEvent (protobuf)
  ```
  Протокол — protobuf: request→FfiRequest, ответ→FfiResponse, колбэк→FfiEvent.
- **C#-скелет биндинга написан:** `PISMO/Native/LiveKitFfi.cs` (P/Invoke 4 функций +
  поток request/response + колбэк событий, пока на сырых байтах). Компилируется.

## Следующие шаги
1. **Protobuf-слой:** взять `.proto` LiveKit FFI (livekit-ffi/protocol/*.proto из
   github.com/livekit/rust-sdks), сгенерить C# (`Google.Protobuf` + protoc или
   `Grpc.Tools`), обернуть `LiveKitFfi.Request`/`Event` в типизированные FfiRequest/
   FfiResponse/FfiEvent.
2. **`NativeCallTransport`:** connect(url, token) → InitializeRequest + ConnectRequest,
   обработка RoomEvent (participant/track). Минимальный тест — зайти в комнату с одним
   аудио, проверить ПРИ АКТИВНОМ VR (главная цель).
3. Дальше по фазам Ф1–Ф5 (см. выше).

## Первый шаг (Ф0)
1. Определяемся, откуда берём `livekit_ffi.dll` (прибилд vs cargo build) — нужен Rust toolchain
   у тебя, если собирать.
2. Я делаю каркас: `NativeCallTransport` (пустой, тот же контракт) + P/Invoke-заглушки +
   генерацию C# из `.proto` LiveKit FFI.
3. Минимальный тест: connect + join room + один аудио-трек, проверяем при активном VR.
