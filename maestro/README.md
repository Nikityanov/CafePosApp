# Maestro — UI-автоматизация на эмуляторе

Инструмент даёт агенту глаза на эмуляторе: иерархия элементов и скриншоты.
Это способ выполнить требование `.opencode-rules.md` §1.2 — «layout change ships with a
measurement or a screenshot, never with a calculation».

## Установлено

| Что | Где |
|---|---|
| Maestro CLI 2.11.0 | `C:\maestro\maestro\bin` (в PATH) |
| MCP-сервер | `maestro` в `~/.config/opencode/opencode.json` |
| `JAVA_HOME` | `C:\Program Files\Java\jdk-26.0.2` (в PATH вручную — см. ниже) |
| `ANDROID_HOME` | `C:\Program Files (x86)\Android\android-sdk` |
| `adb` | `…\android-sdk\platform-tools` (в PATH) |

**Важно про `JAVA_HOME`:** в системе есть шим
`C:\Program Files\Common Files\Oracle\Java\javapath\java.exe` — это **не** JDK,
и `maestro` с ним падает с `JAVA_HOME is set to an invalid directory`. Настоящий JDK
лежит в `C:\Program Files\Java\jdk-26.0.2`. Если `maestro` перестанет запускаться —
проверь `JAVA_HOME`, а не версию Java.

`adb`, `ANDROID_HOME`, `MAESTRO_CLI_NO_ANALYTICS` уже в пользовательском PATH.
`JAVA_HOME` пришлось задать явно, потому что `java` в PATH указывает на шим.

## Эмулятор

AVD: `pixel_7_-_api_36_0` → `emulator-5554`, Android 16 (API 36), 1080×2400.
**Maestro работает с API 36** — проверено сквозным flow, несмотря на то что документация
перечисляет 29–34 и обещает 35/36 «в Q2 2026».

## ЖЁСТКОЕ ОГРАНИЧЕНИЕ: только эмулятор

К машине подключён **реальный POS-терминал Samsung SM-S916U1** (`R3CWA03SDKN`).
На нём — рабочая локальная база. `launchApp` с `clearState: true` уничтожит её.

- **Всегда передавай `device_id: "emulator-5554"`.**
- Никогда не используй `device_id: "R3CWA03SDKN"`.
- Если эмулятор не поднят и нужно ждать — **спроси**, не переключайся на телефон.

`list_devices` возвращает оба устройства; выбирать нужно вручную.

## Запуск эмулятора

```powershell
& "C:\Program Files (x86)\Android\android-sdk\emulator\emulator.exe" `
  -avd pixel_7_-_api_36_0 -no-snapshot-load
adb devices   # дождаться строки "emulator-5554  device"
adb -e shell getprop sys.boot_completed   # должно стать 1
adb -e shell uptime                       # load average должен упасть
```

**Эмулятор стартует 3–5 минут.** `boot_completed=1` и `init.svc.bootanim=stopped`
означают, что загрузка закончилась, но первые минуты `load average` держится около 40 —
система ещё «всплёскивается». Flow, запущенный в этот момент, может выполнить часть
шагов и упасть на неожиданном месте. **Перед первым flow дай ему 60 секунд и проверь
`uptime`** — нагрузка должна быть ниже 20.

## Проверенный пример

`smoke.yaml` — прогоняется на чистом состоянии, ноль предположений о содержимом экрана:

```yaml
appId: com.android.settings
---
- launchApp:
    clearState: true
- assertVisible: "Search Settings"
- scrollUntilVisible:
    element:
      text: "About emulated device"
    direction: DOWN
    timeout: 15000
- tapOn:
    text: "About emulated device"
- assertVisible: "sdk_gphone64_x86_64"
- back
- assertVisible: "Search Settings"
- takeScreenshot: maestro-ok
```

Запуск (последний прогон: 9 команд, `success: true`):

```powershell
maestro --device emulator-5554 test maestro\smoke.yaml
```

Через MCP:

```
list_devices → {"device_id": "emulator-5554"}
run          → { yaml: <содержимое flow>, device_id: "emulator-5554" }
```

## Что показала первая неудачная попытка

Flow без `scrollUntilVisible` упал: `Element not found: Text matching regex: About
emulated device`. Элемент существовал, но был ниже видимой области — Maestro ищет
**по иерархии**, а не по экрану, и не скроллит сам. Это общий принцип: сначала
`inspect_screen`, потом тапать; либо `scrollUntilVisible` для списков.

`clearState: true` сбрасывает приложение в состояние «только что установлено» —
после этого позиция скролла всегда исходная, поэтому первый тап по списку требует
явного `scrollUntilVisible`.
