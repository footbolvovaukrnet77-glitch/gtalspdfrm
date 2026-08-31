#!/usr/bin/env bash
# Lays out the files a player copies into their GTA V directory.
#
#   dist/client/scripts/                     -> GTA V/scripts/
#   dist/client/Gtamp/Adapters/              -> GTA V/Gtamp/Adapters/
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
CONFIG="${1:-Release}"

dotnet build "$SOLUTION" -c "$CONFIG" -v quiet

OUT="$ROOT/dist/client"
rm -rf "$OUT"
mkdir -p "$OUT/scripts" "$OUT/Gtamp/Adapters"

CLIENT_BIN="$ROOT/src/Gtamp.Client.Shv/bin/$CONFIG/net48"
cp "$CLIENT_BIN/Gtamp.Client.Shv.dll" "$OUT/scripts/"
cp "$CLIENT_BIN/Gtamp.Client.Core.dll" "$OUT/scripts/"
cp "$CLIENT_BIN/Gtamp.Shared.dll" "$OUT/scripts/"

for adapter in Rph Lspdfr; do
  SRC="$ROOT/src/Gtamp.Adapters.$adapter/bin/$CONFIG/net48/Gtamp.Adapters.$adapter.dll"
  [ -f "$SRC" ] && cp "$SRC" "$OUT/Gtamp/Adapters/"
done

cat > "$OUT/README.txt" <<'TXT'
GTAMP client files
==================

Copy the contents of scripts/ into your GTA V "scripts" folder.
Copy the Gtamp folder into your GTA V directory.

Requires ScriptHookV and ScriptHookVDotNet 3 to already be installed.
The adapters in Gtamp/Adapters are optional and only activate when the mod
they adapt is present. See docs/INSTALL.md for the full walkthrough.
TXT

echo "Client staged in $OUT"
find "$OUT" -type f | sed "s|$OUT|  dist/client|"
