#!/usr/bin/env bash
# Lays out the files a player copies into their GTA V directory.
#
#   dist/client/scripts/                     -> GTA V/scripts/
#   dist/client/Gtamp/Adapters/              -> GTA V/Gtamp/Adapters/
#   dist/client/RagePluginHook-plugins/      -> GTA V/Plugins/  (only if RPH is used)
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
CONFIG="${1:-Release}"

dotnet build "$SOLUTION" -c "$CONFIG" -v quiet

OUT="$ROOT/dist/client"
rm -rf "$OUT"
mkdir -p "$OUT/scripts" "$OUT/Gtamp/Adapters" "$OUT/Gtamp/bot" "$OUT/Gtamp/server" "$OUT/RagePluginHook-plugins"

CLIENT_BIN="$ROOT/src/Gtamp.Client.Shv/bin/$CONFIG/net48"
cp "$CLIENT_BIN/Gtamp.Client.Shv.dll" "$OUT/scripts/"
cp "$CLIENT_BIN/Gtamp.Client.Core.dll" "$OUT/scripts/"
cp "$CLIENT_BIN/Gtamp.Shared.dll" "$OUT/scripts/"

for adapter in Rph Lspdfr; do
  SRC="$ROOT/src/Gtamp.Adapters.$adapter/bin/$CONFIG/net48/Gtamp.Adapters.$adapter.dll"
  [ -f "$SRC" ] && cp "$SRC" "$OUT/Gtamp/Adapters/"
done

# The bot ships with the client so the in-game bot menu has something to launch.
# It is a .NET 8 program and the client is .NET Framework 4.8 inside the game's
# process, so it runs as a child process and needs its whole publish output, not
# just one assembly.
if dotnet publish "$ROOT/src/Gtamp.Bot/Gtamp.Bot.csproj" -c "$CONFIG" -o "$OUT/Gtamp/bot" -v quiet; then
  :
else
  echo "  (bot not published; the in-game bot menu will say so)" >&2
fi

# The server ships with the client for the same reason and on the same terms: so
# that hosting a two-player session from the machine that is playing does not mean
# leaving a second console window open beside a full-screen game. A dedicated
# server does not use this copy.
if dotnet publish "$ROOT/src/Gtamp.Server/Gtamp.Server.csproj" -c "$CONFIG" -o "$OUT/Gtamp/server" -v quiet; then
  :
else
  echo "  (server not published; the menu's local-server row will say so)" >&2
fi

# The RPH bridge is loaded by RAGE Plugin Hook, not by ScriptHookVDotNet, so it goes
# in RPH's own plugins folder. It ships alongside the shared assembly it uses, because
# RPH resolves a plugin's dependencies from that folder and not from GTA V/scripts.
BRIDGE="$ROOT/src/Gtamp.RphBridge/bin/$CONFIG/net48/Gtamp.RphBridge.dll"
if [ -f "$BRIDGE" ]; then
  cp "$BRIDGE" "$OUT/RagePluginHook-plugins/"
  cp "$ROOT/src/Gtamp.RphBridge/bin/$CONFIG/net48/Gtamp.Shared.dll" "$OUT/RagePluginHook-plugins/"
fi

cat > "$OUT/README.txt" <<'TXT'
GTAMP client files
==================

Copy the contents of scripts/ into your GTA V "scripts" folder.
Copy the Gtamp folder into your GTA V directory.

Requires ScriptHookV and ScriptHookVDotNet 3 to already be installed.
The adapters in Gtamp/Adapters are optional and only activate when the mod
they adapt is present.

Only if you play through RAGE Plugin Hook: copy the contents of
RagePluginHook-plugins/ into your GTA V "Plugins" folder. Without it the RPH
and LSPDFR adapters still load, but they can only report what is installed —
they cannot see any live RPH or LSPDFR state.

The Gtamp/bot folder is the headless bot the in-game menu (F7) launches, and
Gtamp/server is the server its second page can start. Both need the .NET 8
runtime. Deleting either costs you that row of the menu and nothing else.

See docs/INSTALL.md for the full walkthrough.
TXT

echo "Client staged in $OUT"
find "$OUT" -type f | sed "s|$OUT|  dist/client|"
