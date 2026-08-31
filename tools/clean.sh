#!/usr/bin/env bash
# Removes build output. Does not touch server.json, logs or the database.
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
dotnet clean "$SOLUTION" -v quiet || true
find "$ROOT/src" "$ROOT/tests" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
echo "Build output removed."
