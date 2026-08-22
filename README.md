# Phone Screen / Экран телефона

A simple Windows program that shows an Android phone's screen on the computer over
USB. Designed so that even someone who barely uses a PC can run it: one window,
one step at a time, everything prompted as you go. The interface is bilingual —
English or Russian, chosen automatically by the system language and switchable in
settings.

Under the hood it wraps [scrcpy](https://github.com/Genymobile/scrcpy). The program
just puts a friendly interface around it: installs scrcpy itself, finds the phone,
picks a hardware encoder, and remembers your settings.

## Projects used

This program bundles and relies on two open-source tools by Genymobile / Romain
Vimont, distributed unmodified under the Apache License 2.0:

- **[scrcpy](https://github.com/Genymobile/scrcpy)** — screen mirroring over USB/TCP.
- **[gnirehtet](https://github.com/Genymobile/gnirehtet)** — reverse tethering
  (sharing the PC's internet with the phone).

This wrapper is **not** an official Genymobile product and is not affiliated with it.

## Related projects

- [scrcpy](https://github.com/Genymobile/scrcpy) — the engine this is built on.
- [gnirehtet](https://github.com/Genymobile/gnirehtet) — reverse tethering, also bundled.
- [QtScrcpy](https://github.com/barry-ran/QtScrcpy) — another cross-platform scrcpy GUI.
- [guiscrcpy](https://github.com/srevinsaju/guiscrcpy) — a Python/Qt scrcpy GUI.

## Features

- Mirrors any Android phone over USB (H.264 by default).
- Step-by-step wizard: connection, developer mode, USB debugging, on-phone
  permission — all detected automatically and explained.
- Automatic hardware video-encoder selection for the phone's chipset.
- Five video codecs (H.264, H.265, AV1, VP8, VP9) and four audio codecs
  (Opus, AAC, FLAC, raw), with automatic fallback to H.264 if a codec isn't
  supported in hardware.
- Settings: size, frame rate, bit rate, video and audio codec, audio routing,
  window, control from the PC.
- Works through a USB hub.
- Optional advanced modes: network connection and sharing the PC's internet with
  the phone (both behind warnings).
- Auto-close when the phone is unplugged.
- Bilingual UI (English / Russian), auto-selected and switchable in settings.
- No administrator rights required; installs into the user profile.

## Requirements

- Windows 10 or 11.
- .NET Framework 4.x (preinstalled on all Windows 10/11).
- An Android phone with a hardware H.264 encoder (almost any).

iPhone and iPad are not supported — that's an iOS limitation, not the program's.

## Run it (for regular users)

Download `PhoneScreen.exe` from [Releases](../../releases) and double-click it.
The program does everything else itself.

On first launch Windows SmartScreen may say "Windows protected your PC" — normal
for an unsigned program. Click "More info", then "Run anyway".

## Build it yourself

You need Windows with .NET Framework (the `csc.exe` compiler is already in the
system) and four files next to the source:

1. `PhoneScreen.cs` — the source code (in this repo).
2. `app.ico` — the icon (in this repo).
3. `scrcpy.zip` — the official
   [scrcpy-win64-v4.1.zip](https://github.com/Genymobile/scrcpy/releases/tag/v4.1),
   **renamed** to `scrcpy.zip`.
4. `gnirehtet.zip` — the official
   [gnirehtet-rust-win64-v2.5.1.zip](https://github.com/Genymobile/gnirehtet/releases/tag/v2.5.1),
   **renamed** to `gnirehtet.zip`.

The archives are deliberately not in the repo — they're third-party builds; take
them from the official pages above so you build from genuine, unpatched binaries.

Then run `build.bat`. The output is `PhoneScreen.exe` with everything embedded.

## Licenses

- This program's code — see `LICENSE` (MIT).
- Bundled **scrcpy** — Apache License 2.0, see `LICENSE-scrcpy.txt`,
  © Genymobile / Romain Vimont.
- Bundled **gnirehtet** — Apache License 2.0, see `LICENSE-gnirehtet.txt`,
  © Genymobile / Romain Vimont.

---

# Экран телефона (по-русски)

Простая программа для Windows, которая показывает экран Android-телефона на
компьютере через USB. Задумана так, чтобы справился даже человек, который почти
не пользуется ПК: одно окно, один шаг за раз, всё подсказывается по ходу. Язык
интерфейса — русский или английский, выбирается по системе и переключается в
настройках.

Под капотом — [scrcpy](https://github.com/Genymobile/scrcpy). Программа лишь
оборачивает его в понятный интерфейс: сама ставит scrcpy, находит телефон,
подбирает кодировщик и запоминает настройки.

## Используемые проекты

Внутри программы встроены и используются два инструмента с открытым кодом от
Genymobile / Romain Vimont, распространяемые без изменений под Apache License 2.0:

- **[scrcpy](https://github.com/Genymobile/scrcpy)** — вывод экрана по USB/TCP.
- **[gnirehtet](https://github.com/Genymobile/gnirehtet)** — раздача интернета
  телефону с компьютера (reverse tethering).

Эта обёртка **не** является официальным продуктом Genymobile и не связана с ним.

## Связанные проекты

- [scrcpy](https://github.com/Genymobile/scrcpy) — движок, на котором всё построено.
- [gnirehtet](https://github.com/Genymobile/gnirehtet) — раздача интернета, тоже встроена.
- [QtScrcpy](https://github.com/barry-ran/QtScrcpy) — другой кроссплатформенный GUI для scrcpy.
- [guiscrcpy](https://github.com/srevinsaju/guiscrcpy) — GUI для scrcpy на Python/Qt.

## Что умеет

- Показ экрана любого Android-телефона по USB (H.264 по умолчанию).
- Пошаговый мастер: подключение, режим разработчика, отладка по USB, разрешение
  на телефоне — всё определяется автоматически и подсказывается.
- Автоподбор аппаратного видеокодировщика под чипсет телефона.
- Пять видеокодеков (H.264, H.265, AV1, VP8, VP9) и четыре аудиокодека (Opus,
  AAC, FLAC, raw) с автооткатом на H.264, если кодек не поддержан аппаратно.
- Настройки: размер, кадры, битрейт, видео- и аудиокодек, звук, окно, управление
  с компьютера.
- Работа через USB-хаб.
- Необязательные режимы для опытных: подключение по сети и раздача интернета
  телефону с компьютера (оба с предупреждениями).
- Автозакрытие при отключении телефона.
- Двуязычный интерфейс (русский / английский), выбор по системе и вручную.
- Не требует прав администратора, ставится в профиль пользователя.

## Требования

- Windows 10 или 11.
- .NET Framework 4.x (предустановлен на всех Windows 10/11).
- Android-телефон с аппаратным кодировщиком H.264 (почти любой).

iPhone и iPad не поддерживаются — это ограничение iOS, а не программы.

## Как запустить

Скачать `PhoneScreen.exe` из раздела [Releases](../../releases) и запустить
двойным кликом. Всё остальное программа сделает сама.

При первом запуске Windows SmartScreen может показать «Windows защитила ваш
компьютер» — это нормально для неподписанной программы. Нажать «Подробнее», затем
«Выполнить в любом случае».

## Как собрать самому

Нужен Windows с .NET Framework (компилятор `csc.exe` уже в системе) и четыре
файла рядом с исходником: `PhoneScreen.cs`, `app.ico`, `scrcpy.zip` (официальный
[scrcpy-win64-v4.1.zip](https://github.com/Genymobile/scrcpy/releases/tag/v4.1),
переименованный) и `gnirehtet.zip` (официальный
[gnirehtet-rust-win64-v2.5.1.zip](https://github.com/Genymobile/gnirehtet/releases/tag/v2.5.1),
переименованный). Затем запустить `build.bat`.

Архивы не лежат в репозитории намеренно — это чужие сборки, их надо взять с
официальных страниц выше, чтобы собирать из подлинных бинарников.

## Лицензии

- Код этой программы — см. `LICENSE` (MIT).
- Встроенный **scrcpy** — Apache License 2.0, см. `LICENSE-scrcpy.txt`,
  © Genymobile / Romain Vimont.
- Встроенный **gnirehtet** — Apache License 2.0, см. `LICENSE-gnirehtet.txt`,
  © Genymobile / Romain Vimont.
