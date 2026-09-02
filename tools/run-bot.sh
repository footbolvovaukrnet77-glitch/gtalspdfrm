#!/usr/bin/env bash
# Everything after the script name is passed straight to the bot.
#   tools/run-bot.sh --task follow
#   tools/run-bot.sh --count 10 --task patrol
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
exec dotnet run --project src/Gtamp.Bot -c Release -- "$@"
