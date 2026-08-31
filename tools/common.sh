#!/usr/bin/env bash
# Shared setup for the shell build scripts.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOLUTION="$ROOT/Gtamp.sln"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Pick up a side-by-side install if dotnet is not already on PATH.
if ! command -v dotnet >/dev/null 2>&1; then
  for candidate in /usr/share/dotnet "$HOME/.dotnet"; do
    if [ -x "$candidate/dotnet" ]; then
      export PATH="$PATH:$candidate"
      break
    fi
  done
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: the .NET SDK 8.0 or newer is required but 'dotnet' was not found on PATH." >&2
  echo "       install it from https://dotnet.microsoft.com/download/dotnet/8.0" >&2
  exit 1
fi
