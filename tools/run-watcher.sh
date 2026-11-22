#!/usr/bin/env bash
# Everything after the script name goes straight to the watcher.
#   tools/run-watcher.sh                 just record, nothing is sent
#   tools/run-watcher.sh --screenshot    also grab the screen (windowed mode)
#   tools/run-watcher.sh --rules         what counts as a problem
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
exec dotnet run --project src/Gtamp.Watcher -c Release -- "$@"
