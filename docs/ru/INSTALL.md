# Установка

> Русская версия. English: [../INSTALL.md](../INSTALL.md).

Конкретные команды. Нигде не написано «установите зависимости».

---

## 1. Сборочная машина

### Предварительные требования

- **.NET SDK 8.0.x** — https://dotnet.microsoft.com/download/dotnet/8.0
  `global.json` намеренно закрепляет ветку 8.0. Более новый SDK компилирует
  этот код как более новый язык — SDK .NET 10 компилирует его как C# 14, где
  `field` является ключевым словом внутри аксессоров свойства, — а тесты
  нацелены на `net8.0` и требуют среды выполнения .NET 8, которую несёт SDK
  8.0.x. С закреплением машина без него падает на первой же команде с
  сообщением, называющим `global.json`, а не собирается не тем компилятором,
  чтобы упасть позже.

Проверка:

```bash
dotnet --version        # ожидается 8.0.x — global.json закрепляет ветку
```

Решение собирается на Linux, macOS и Windows — включая клиент `net48` для GTA V,
через пакет `Microsoft.NETFramework.ReferenceAssemblies`, который сборка
восстанавливает автоматически.

### Собрать всё

```bash
git clone <этот репозиторий>
cd gtalspdfrm

# Linux/macOS
./tools/build.sh Release

# Windows
tools\build.bat Release
```

Ожидаемый вывод: `Build succeeded. 0 Warning(s) 0 Error(s)`.

### Запустить тесты

```bash
./tools/test.sh          # или tools\test.bat
```

Ожидается: `Passed! - Failed: 0, Passed: 573`.

### Проверка документации

```bash
python3 tools/check-docs.py    # или python tools\check-docs.py в Windows
```

Ожидается: `no broken links, no missing translations, docs agree with the code`.
Проверка падает на относительной ссылке на несуществующий файл, на `#якоре`,
называющем отсутствующий заголовок, на английском документе без русского двойника или
наоборот, на версии протокола, не совпадающей с той, которую отправляет
`ProtocolConstants`, на числе тестов, не совпадающем с тем, сколько их в наборе, и на
примере `client.ini`, не совпадающем с файлом, который пишет клиент. Нужен только
Python 3 — устанавливать пакеты не требуется.

Последние три проверки существуют потому, что документация утверждала то, чего никто
не сверял, и все три уже разъехались: документы протокола описывали версию 5 протокола
версии 8, а эта страница печатала `client.ini` без настройки, которая есть в каждом
сгенерированном файле. Утверждение в документе, которому не может возразить ни один
шаг сборки, — это утверждение, которое рано или поздно окажется неверным.

### Те же три команды выполняются в CI

`.github/workflows/ci.yml` запускает сборку, набор тестов и проверку документации
на `ubuntu-latest` при каждом push и каждом pull request. Шаг сборки передаёт
`-warnaserror`, поэтому заявление про ноль предупреждений выше проверяется, а не
декларируется. Всё решение компилируется на Linux, включая клиент `net48`, потому
что `Microsoft.NETFramework.ReferenceAssemblies` поставляет референс-сборки .NET
Framework 4.8; **запуск** клиента по-прежнему требует Windows и GTA V.

---

## 2. Сервер

### Файлы

Кроме результата сборки устанавливать нечего. Сервер самодостаточен, если не
считать среды выполнения .NET 8.

### Первый запуск

```bash
./tools/run-server.sh
```

При первом старте он пишет `server.json` рядом с рабочим каталогом и создаёт:

```
server.json          конфигурация, полностью заполненная умолчаниями
data/world.db        база мира SQLite
logs/server-*.log    ежедневный файл лога
```

### Настройка

Отредактируйте `server.json`, затем перезапустите. Настройки, которые важны в
первую очередь:

```jsonc
{
  "serverName": "My Server",
  "maxPlayers": 32,
  "password": "",              // пусто — значит без пароля
  "bindAddress": "0.0.0.0",
  "port": 27015,
  "tickRate": 60,              // Гц симуляции
  "snapshotRate": 20,          // снапшотов на клиента в секунду
  "snapshotByteBudget": 1024,  // байт на клиента на снапшот
  "saveIntervalSeconds": 60,
  "antiCheat": "Standard",     // Off | Basic | Standard | Strict | Custom
  "startTime": "12:00",
  "startWeather": "EXTRASUNNY"
}
```

### Открыть порт

Сервер слушает **UDP 27015** по умолчанию. И правило файрвола, и, на домашнем
соединении, проброс порта на роутере должны быть UDP — TCP работать не будет.

```powershell
# Windows, PowerShell с повышенными правами
New-NetFirewallRule -DisplayName "GTAMP" -Direction Inbound -Protocol UDP -LocalPort 27015 -Action Allow
```

```bash
# Linux, ufw
sudo ufw allow 27015/udp
```

### Запуск

```bash
./tools/run-server.sh                    # умолчания
./tools/run-server.sh --port 27020       # переопределить порт
./tools/run-server.sh --config /etc/gtamp/server.json
```

Введите `help` в приглашении для админ-команд, `stop` — для чистого выключения.

---

## 3. Клиент

### Предварительные требования, в этом порядке

1. **Grand Theft Auto V**, обновлённая.
2. **ScriptHookV** — http://www.dev-c.com/gtav/scripthookv/
   Скопируйте `ScriptHookV.dll` и `dinput8.dll` в каталог GTA V (папку, где лежит
   `GTA5.exe`).
3. **ScriptHookVDotNet 3** — https://github.com/scripthookvdotnet/scripthookvdotnet/releases
   Скопируйте `ScriptHookVDotNet.asi`, `ScriptHookVDotNet2.dll` и
   `ScriptHookVDotNet3.dll` в тот же каталог.

Опционально — и действительно опционально:

- **RAGE Plugin Hook** — https://ragepluginhook.net/
- **LSPDFR** — https://www.lcpdfr.com/lspdfr/

### Подготовить файлы клиента

```bash
./tools/package-client.sh Release        # или tools\package-client.bat Release
```

Это порождает:

```
dist/client/scripts/Gtamp.Client.Shv.dll
dist/client/scripts/Gtamp.Client.Core.dll
dist/client/scripts/Gtamp.Shared.dll
dist/client/Gtamp/Adapters/Gtamp.Adapters.Rph.dll
dist/client/Gtamp/Adapters/Gtamp.Adapters.Lspdfr.dll
dist/client/RagePluginHook-plugins/Gtamp.RphBridge.dll
dist/client/RagePluginHook-plugins/Gtamp.Shared.dll
```

### Скопировать на место

Пусть каталог GTA V — `D:\Games\Grand Theft Auto V`:

```
dist/client/scripts/*        →  D:\Games\Grand Theft Auto V\scripts\
dist/client/Gtamp/           →  D:\Games\Grand Theft Auto V\Gtamp\
```

**Только если вы играете через RAGE Plugin Hook**, скопируйте также:

```
dist/client/RagePluginHook-plugins/*  →  D:\Games\Grand Theft Auto V\Plugins\
```

Эта папка — собственная папка плагинов RPH, а не `scripts` от GTA V. Две половины
загружаются двумя разными хостами, и именно поэтому их две — см.
[RPH_INTEGRATION.md](RPH_INTEGRATION.md). Пропустите этот шаг, если не используете
RPH: всё остальное продолжит работать, а адаптеры RPH и LSPDFR просто сообщат, что
им нечего читать.

Результат:

```
D:\Games\Grand Theft Auto V\
├── GTA5.exe
├── dinput8.dll                         (ScriptHookV)
├── ScriptHookV.dll
├── ScriptHookVDotNet.asi
├── ScriptHookVDotNet3.dll
├── scripts\
│   ├── Gtamp.Client.Shv.dll
│   ├── Gtamp.Client.Core.dll
│   └── Gtamp.Shared.dll
└── Gtamp\
    ├── client.ini                      (создаётся при первом запуске)
    ├── logs\                           (создаётся при первом запуске)
    └── Adapters\
        ├── Gtamp.Adapters.Rph.dll
        └── Gtamp.Adapters.Lspdfr.dll
```

С RAGE Plugin Hook дополнительно:

```
D:\Games\Grand Theft Auto V\
├── RAGEPluginHook.exe
└── Plugins\
    ├── LSPD First Response.dll         (LSPDFR, устанавливаете вы)
    ├── Gtamp.RphBridge.dll
    └── Gtamp.Shared.dll
```

### Настроить клиент

Запустите GTA V один раз. `Gtamp\client.ini` будет создан со сгенерированным
токеном личности. Затем отредактируйте его:

```ini
[client]
PlayerName=YourName
ServerAddress=127.0.0.1
ServerPort=27015
ServerPassword=
IdentityToken=<публичный ключ; безопасно показывать>
IdentitySecret=<приватный ключ; никому не показывайте и не теряйте>
ConsoleKey=119
InterpolationDelay=0.12
CorrectionThreshold=3
HealthCorrectionThreshold=20
ShowNetworkOverlay=False
ShowPlayerBlips=True
ShowPlayerNames=True
VerboseLogging=False
AutoConnectOnStart=False
```

**Настройка, которой этот билд не знает, сообщается, а не игнорируется.** И
`client.ini`, и JSON-конфиг сервера сверяются с теми настройками, которые в билде
действительно есть, и всё лишнее пишется предупреждением при старте и попадает в
диагностический пакет. Написав `ShowPlayerBlibs=False`, вы раньше получали значение
по умолчанию без единого слова.

Проверка предупреждает, а не отказывается стартовать, и это намеренно: отказ сделал
бы так, что старый билд не смог бы прочитать файл, написанный новым, — а это отказ
хуже того, который предотвращается.

### Подключиться

1. Запустите GTA V и загрузитесь в одиночную игру.
2. Нажмите **F8**.
3. Введите `connect` (берёт настройки из `client.ini`) или
   `connect 203.0.113.9 27015`.

`status` показывает соединение, `players` перечисляет тех, кто в мире,
`diagnostics` проверяет установку.

---

## 4. Проверка, что всё работает

Два экземпляра GTA V не нужны — одного клиента и сервера достаточно, чтобы
проверить конвейер:

1. Запустите сервер: `./tools/run-server.sh`
2. Консоль сервера: `status` → `players 0/32`
3. В игре: F8, `connect`
4. Консоль сервера: `players` → ваше имя, пинг и позиция
5. Походите; выполните `players` снова → позиция изменилась
6. В игре: `net` → пинг, потери, применённые снапшоты

Для настоящего теста на двух игроков вторая машина (или вторая установка GTA V)
подключается по тому же адресу. Оба клиента привязываются к эфемерному исходному
порту, поэтому два экземпляра на одной машине тоже работают.

### Если вы установили мост RPH

Запустите игру **через `RAGEPluginHook.exe`**, затем в консоли F8:

```
diagnostics
```

Строка RPH должна читаться как `bridge <версия>, RPH <версия>, N plugin(s)`. Если
она больше десяти секунд показывает `waiting for the RPH bridge`, лог клиента
скажет, какая из двух причин: игра запущена не через RPH либо
`Gtamp.RphBridge.dll` отсутствует в `GTA V\Plugins\`. С установленным LSPDFR и
выходом на смену строка LSPDFR покажет, сколько ключей состояния она читает и от
скольких других игроков она что-то слышала.

---

## 5. Удаление и откат

### Клиент

Удалите вот это; больше ничего не трогалось:

```
<GTA V>\scripts\Gtamp.Client.Shv.dll
<GTA V>\scripts\Gtamp.Client.Core.dll
<GTA V>\scripts\Gtamp.Shared.dll
<GTA V>\Gtamp\                          (вся папка, включая конфиг и логи)
<GTA V>\Plugins\Gtamp.RphBridge.dll     (только если вы ставили мост RPH)
<GTA V>\Plugins\Gtamp.Shared.dll
```

ScriptHookV, ScriptHookVDotNet, RPH и LSPDFR этот фреймворк не трогает, и они
продолжают работать.

### Сервер

Остановите его через `stop`, затем удалите `server.json`, `data/` и `logs/`.
Удаление `data/world.db` сбрасывает мир и всех сохранённых игроков.

### Откат изменения кода

```bash
git log --oneline
git revert <commit>
./tools/rebuild.sh Release
./tools/test.sh
```

Ничто во фреймворке не изменяет собственные файлы GTA V, поэтому восстановление
игры никогда не требуется.
