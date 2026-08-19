# Сборка livekit_ffi.dll с аппаратным NVENC (демонстрация экрана)

## Зачем

Демонстрация экрана кодируется нативным LiveKit FFI (`PISMO/livekit_ffi.dll`),
без WebView2/Chromium. Текущая DLL взята из **официального** Python-пакета
`livekit` (см. `Native/LiveKitFfi.cs`) и собрана **без NVENC** — это видно по
строке внутри самой DLL:

```
LIVEKIT_PREFERRED_HW_ENCODER=nvenc requested, but NVENC support is not compiled in; falling back to other encoders.
```

Фабрика энкодеров в ней — только программные: `LibvpxVp8 / OpenH264 / LibaomAv1 /
LibvpxVp9`. Поэтому H264 кодируется на CPU (в диспетчере задач Video Encode = 0%,
а RTX грузится только захватом/цветоконвертацией). Никакие правки в C#, реестре
или настройках это не меняют — нужен **пересобранный `livekit_ffi.dll` с NVENC**.

**Важно:** C#-сторона уже полностью готова. `NativeCallTransport.ResolveScreenEncoder`
шлёт в FFI хинт `ENCODER_BACKEND_NVENC` (3), а `GpuPreference` пинит процесс на
дискретную NVIDIA. Как только DLL будет уметь NVENC — он задействуется сам, без
изменений в коде приложения. Достаточно заменить файл `PISMO/livekit_ffi.dll`.

---

## Что понадобится (машина сборки — только Windows x64)

- **Windows 10/11 x64** с NVIDIA GPU (для проверки).
- **Visual Studio 2022** с компонентами «Desktop development with C++»
  (MSVC, Windows 10/11 SDK, ATL).
- **depot_tools** (gn/ninja) — тулчейн сборки WebRTC.
- **Python 3.11**, **Rust (stable, MSVC toolchain)** — `rustup default stable-msvc`.
- **CUDA Toolkit** + **NVIDIA Video Codec SDK** (заголовки/либы NVENC) — именно они
  добавляют аппаратный энкодер в libwebrtc на этапе компиляции.
- ~40–80 ГБ на диске и несколько часов на первую сборку libwebrtc.

---

## Шаг 0. Зафиксировать версию (чтобы не сломать ABI)

Текущая C#-обвязка (`Native/LiveKitFfi.cs` + `Native/protocol/*.proto`) рассчитана
на конкретную версию FFI. **Собирать нужно ту же версию `rust-sdks`, из которой
взята текущая DLL**, иначе P/Invoke и протоколы разъедутся.

1. Узнай версию livekit, из которой брали DLL (как в комментарии `LiveKitFfi.cs`):
   ```powershell
   pip download livekit==<ВЕРСИЯ> --only-binary=:all: --python-version 3.11 -d wheels
   ```
   Если версия неизвестна — возьми ту, чьи `protocol/*.proto` совпадают с
   `PISMO/Native/protocol/` (сравни `room.proto` — там уже есть
   `VideoEncoderBackend` / `ENCODER_BACKEND_NVENC`).
2. В `rust-sdks` перейди на соответствующий git-тег (`git checkout <tag>`).

> Если решишь брать более свежую версию ради NVENC — будь готов перегенерировать
> `Native/protocol/*.proto` из новой и пересобрать C#-обвязку под возможные
> изменения ABI.

---

## Шаг 1. Custom libwebrtc с NVENC

NVENC в libwebrtc живёт в **форке LiveKit** (официальная prebuilt-сборка его не
включает). Порядок — как в доке `webrtc-sys/libwebrtc/README.md` из rust-sdks:

```powershell
# depot_tools в PATH
git clone https://github.com/livekit/rust-sdks
cd rust-sdks\webrtc-sys\libwebrtc

# Сборка WebRTC из форка LiveKit (скрипт тянет исходники и патчи).
# Для Windows используется аналог build-linux.sh (см. каталог libwebrtc);
# профиль release, arch x64.
.\build-windows.cmd --arch x64 --profile release   # имя скрипта сверь в каталоге
```

**Ключевой момент — включить NVENC.** libwebrtc должен собираться с CUDA/NVENC
SDK в путях и соответствующим build-флагом форка (gn-аргумент вида
`rtc_use_nvenc`/`use_nvenc` — **точное имя сверь в форке LiveKit webrtc**, оно
задаётся в скрипте сборки или `args.gn`). Без CUDA SDK и этого флага получишь ту
же софтовую DLL, что и сейчас.

Полезные ориентиры (там уже интегрированы desktop-capturer и HW-энкодеры):
- LiveKit rust-sdks: <https://github.com/livekit/rust-sdks>
- Issue про HW-энкодинг: <https://github.com/livekit/rust-sdks/issues/503>
- Форк с desktop capturer + энкодерами: <https://github.com/iparaskev/rust-sdks/tree/add_desktop_capturer>
- Сравнение энкодеров (сборка с HW): <https://github.com/gethopp/livekit_encoders_compared>
- Native WebRTC dev: <https://webrtc.googlesource.com/src/+/main/docs/native-code/development/>

Результат шага — каталог со свежесобранной libwebrtc, например
`webrtc-sys/libwebrtc/windows-x64-release`.

---

## Шаг 2. Собрать livekit_ffi.dll против своей libwebrtc

Указать rust-sdks на кастомную libwebrtc через `LK_CUSTOM_WEBRTC`
(файл `rust-sdks/.cargo/config.toml`):

```toml
[env]
LK_CUSTOM_WEBRTC = { value = "webrtc-sys/libwebrtc/windows-x64-release", relative = true }
```

Затем собрать FFI:

```powershell
cd rust-sdks
cargo build --release -p livekit-ffi
```

Готовый файл: `rust-sdks/target/release/livekit_ffi.dll`.

---

## Шаг 3. Подменить DLL в проекте

1. Скопируй `livekit_ffi.dll` из `target/release` в `PISMO/livekit_ffi.dll`
   (замени существующий).
2. Пересобери PISMO (обычный `dotnet build` / CI).
3. `PISMO.csproj` уже копирует `livekit_ffi.dll` в выход сборки — проверь, что
   в `bin` лежит новая DLL (по размеру/дате).

---

## Шаг 4. Проверка

1. Запусти приложение, в настройках звонка выбери **«Дискретная (RTX/GTX, NVENC)»**,
   **перезапусти** приложение (пин GPU читается при старте процесса).
2. Начни демонстрацию экрана.
3. Диспетчер задач → Производительность → GPU (RTX) → **Video Encode** должен
   быть > 0%. В логах приложения не должно быть строки
   «software encoder (NVENC/QuickSync не задействован)».
4. Опционально можно выставить переменную окружения
   `LIVEKIT_PREFERRED_HW_ENCODER=nvenc` для явного предпочтения NVENC.

---

## Честные оговорки

- Первая сборка libwebrtc — это **часы** компиляции и десятки ГБ; делается один
  раз, дальше только `cargo build`.
- Аппаратный NVENC на **Windows** в LiveKit не является «первоклассной»
  официальной фичей — точный build-флаг и наличие патчей **надо подтвердить в
  актуальном форке** (ссылки выше). На Linux путь протоптан лучше.
- Версия FFI должна совпасть с C#-обвязкой (Шаг 0), иначе приложение упадёт на
  вызовах FFI.
- Собрать эту DLL внутри данной сессии/окружения нельзя (нет исходников libwebrtc,
  нет CUDA-тулчейна, не Windows). Этот документ — точный план для сборочной машины.
