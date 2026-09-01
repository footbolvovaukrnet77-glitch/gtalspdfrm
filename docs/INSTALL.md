# Installation

> English. Русский: [ru/INSTALL.md](ru/INSTALL.md).

Concrete commands. Nothing here says "install the dependencies".

---

## 1. Build machine

### Prerequisites

- **.NET SDK 8.0.x** — https://dotnet.microsoft.com/download/dotnet/8.0
  `global.json` pins the 8.0 band on purpose. A newer SDK compiles this as a
  newer language — the .NET 10 SDK compiles it as C# 14, where `field` is a
  keyword inside property accessors — and the tests target `net8.0`, so they
  need the .NET 8 runtime the 8.0.x SDK carries. With the pin, a machine
  without it fails at the first command with a message naming `global.json`,
  instead of building against the wrong compiler and failing later.

Verify:

```bash
dotnet --version        # expect 8.0.x — global.json pins the band
```

The solution builds on Linux, macOS and Windows — including the `net48` GTA V
client, via the `Microsoft.NETFramework.ReferenceAssemblies` package, which the
build restores automatically.

### Build everything

```bash
git clone <this repository>
cd gtalspdfrm

# Linux/macOS
./tools/build.sh Release

# Windows
tools\build.bat Release
```

Expected output: `Build succeeded. 0 Warning(s) 0 Error(s)`.

### Run the tests

```bash
./tools/test.sh          # or tools\test.bat
```

Expected: `Passed! - Failed: 0, Passed: 493`.

### Check the documentation

```bash
python3 tools/check-docs.py    # or python tools\check-docs.py on Windows
```

Expected: `no broken links, no missing translations`. It fails on a relative link
to a file that does not exist, on a `#anchor` naming a heading that is not there,
and on an English document with no Russian counterpart or the reverse. Needs
nothing but Python 3 — no packages to install.

### The same three commands run in CI

`.github/workflows/ci.yml` runs the build, the test suite and the documentation
check on `ubuntu-latest` for every push and every pull request. The build step
passes `-warnaserror`, so the zero-warning claim above is enforced rather than
asserted. The whole solution compiles on Linux, the `net48` client included,
because `Microsoft.NETFramework.ReferenceAssemblies` supplies the .NET Framework
4.8 reference assemblies; **running** the client still needs Windows and GTA V.

---

## 2. Server

### Files

Nothing to install beyond the build output. The server is self-contained apart
from the .NET 8 runtime.

### First run

```bash
./tools/run-server.sh
```

On first start it writes `server.json` next to the working directory and creates:

```
server.json          configuration, fully populated with defaults
data/world.db        SQLite world database
logs/server-*.log    daily log file
```

### Configure

Edit `server.json`, then restart. The settings that matter first:

```jsonc
{
  "serverName": "My Server",
  "maxPlayers": 32,
  "password": "",              // empty means no password
  "bindAddress": "0.0.0.0",
  "port": 27015,
  "tickRate": 60,              // simulation Hz
  "snapshotRate": 20,          // snapshots per client per second
  "snapshotByteBudget": 1024,  // bytes per client per snapshot
  "saveIntervalSeconds": 60,
  "antiCheat": "Standard",     // Off | Basic | Standard | Strict | Custom
  "startTime": "12:00",
  "startWeather": "EXTRASUNNY"
}
```

### Open the port

The server listens on **UDP 27015** by default. Both the firewall rule and, on a
home connection, the router port-forward must be UDP — TCP will not work.

```powershell
# Windows, elevated PowerShell
New-NetFirewallRule -DisplayName "GTAMP" -Direction Inbound -Protocol UDP -LocalPort 27015 -Action Allow
```

```bash
# Linux, ufw
sudo ufw allow 27015/udp
```

### Run it

```bash
./tools/run-server.sh                    # defaults
./tools/run-server.sh --port 27020       # override the port
./tools/run-server.sh --config /etc/gtamp/server.json
```

Type `help` at the prompt for the admin commands, `stop` to shut down cleanly.

---

## 3. Client

### Prerequisites, in this order

1. **Grand Theft Auto V**, updated.
2. **ScriptHookV** — http://www.dev-c.com/gtav/scripthookv/
   Copy `ScriptHookV.dll` and `dinput8.dll` into the GTA V directory
   (the folder containing `GTA5.exe`).
3. **ScriptHookVDotNet 3** — https://github.com/scripthookvdotnet/scripthookvdotnet/releases
   Copy `ScriptHookVDotNet.asi`, `ScriptHookVDotNet2.dll` and
   `ScriptHookVDotNet3.dll` into the same directory.

Optional, and genuinely optional:

- **RAGE Plugin Hook** — https://ragepluginhook.net/
- **LSPDFR** — https://www.lcpdfr.com/lspdfr/

### Stage the client files

```bash
./tools/package-client.sh Release        # or tools\package-client.bat Release
```

That produces:

```
dist/client/scripts/Gtamp.Client.Shv.dll
dist/client/scripts/Gtamp.Client.Core.dll
dist/client/scripts/Gtamp.Shared.dll
dist/client/Gtamp/Adapters/Gtamp.Adapters.Rph.dll
dist/client/Gtamp/Adapters/Gtamp.Adapters.Lspdfr.dll
dist/client/RagePluginHook-plugins/Gtamp.RphBridge.dll
dist/client/RagePluginHook-plugins/Gtamp.Shared.dll
```

### Copy them in

Given a GTA V directory of `D:\Games\Grand Theft Auto V`:

```
dist/client/scripts/*        →  D:\Games\Grand Theft Auto V\scripts\
dist/client/Gtamp/           →  D:\Games\Grand Theft Auto V\Gtamp\
```

**Only if you play through RAGE Plugin Hook**, also copy:

```
dist/client/RagePluginHook-plugins/*  →  D:\Games\Grand Theft Auto V\Plugins\
```

That folder is RPH's own plugin folder, not GTA V's `scripts`. The two halves are
loaded by two different hosts, which is the whole reason there are two of them —
see [RPH_INTEGRATION.md](RPH_INTEGRATION.md). Skip this step if you do not use RPH:
everything else still works, and the RPH and LSPDFR adapters simply report that
they have no live state to read.

The result:

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
    ├── client.ini                      (created on first run)
    ├── logs\                           (created on first run)
    └── Adapters\
        ├── Gtamp.Adapters.Rph.dll
        └── Gtamp.Adapters.Lspdfr.dll
```

With RAGE Plugin Hook, additionally:

```
D:\Games\Grand Theft Auto V\
├── RAGEPluginHook.exe
└── Plugins\
    ├── LSPD First Response.dll         (LSPDFR, installed by you)
    ├── Gtamp.RphBridge.dll
    └── Gtamp.Shared.dll
```

### Configure the client

Start GTA V once. `Gtamp\client.ini` is written with a generated identity token.
Then edit it:

```ini
[client]
PlayerName=YourName
ServerAddress=127.0.0.1
ServerPort=27015
ServerPassword=
IdentityToken=<your public key; safe to share>
IdentitySecret=<your private key; never share it, never lose it>
ConsoleKey=119
InterpolationDelay=0.12
CorrectionThreshold=3
ShowNetworkOverlay=False
VerboseLogging=False
AutoConnectOnStart=False
```

### Connect

1. Start GTA V and load into single player.
2. Press **F8**.
3. Type `connect` (uses `client.ini`) or `connect 203.0.113.9 27015`.

`status` shows the connection, `players` lists who is in the world,
`diagnostics` checks the installation.

---

## 4. Verifying it works

Two GTA V instances are not required — one client and the server is enough to
verify the pipeline:

1. Start the server: `./tools/run-server.sh`
2. Server console: `status` → `players 0/32`
3. In game: F8, `connect`
4. Server console: `players` → your name, ping and position
5. Walk around; run `players` again → the position changes
6. In game: `net` → ping, loss, snapshots applied

For a real two-player test, a second machine (or a second GTA V installation)
connects to the same address. Both clients bind an ephemeral source port, so two
instances on one machine also work.

### If you installed the RPH bridge

Start the game **through `RAGEPluginHook.exe`**, then in the F8 console:

```
diagnostics
```

The RPH line should read `bridge <version>, RPH <version>, N plugin(s)`. If it
reads `waiting for the RPH bridge` for more than ten seconds, the client log says
which of the two causes it is: the game was not started through RPH, or
`Gtamp.RphBridge.dll` is not in `GTA V\Plugins\`. With LSPDFR installed and on
duty, the LSPDFR line shows how many state keys it is reading and how many other
players it has heard from.

---

## 5. Uninstalling and rolling back

### Client

Delete these; nothing else was touched:

```
<GTA V>\scripts\Gtamp.Client.Shv.dll
<GTA V>\scripts\Gtamp.Client.Core.dll
<GTA V>\scripts\Gtamp.Shared.dll
<GTA V>\Gtamp\                          (whole folder, includes config and logs)
<GTA V>\Plugins\Gtamp.RphBridge.dll     (only if you installed the RPH bridge)
<GTA V>\Plugins\Gtamp.Shared.dll
```

ScriptHookV, ScriptHookVDotNet, RPH and LSPDFR are untouched by this framework
and keep working.

### Server

Stop it with `stop`, then delete `server.json`, `data/` and `logs/`.
Removing `data/world.db` resets the world and every stored player.

### Rolling back a code change

```bash
git log --oneline
git revert <commit>
./tools/rebuild.sh Release
./tools/test.sh
```

Nothing in the framework modifies GTA V's own files, so no game repair is ever
needed.
