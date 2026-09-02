#!/usr/bin/env bash
# Builds and starts the server. Extra arguments are passed straight through,
# e.g. tools/run-server.sh --port 27020
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
dotnet build "$ROOT/src/Gtamp.Server/Gtamp.Server.csproj" -c Debug -v quiet
exec dotnet run --project "$ROOT/src/Gtamp.Server/Gtamp.Server.csproj" -c Debug --no-build -- "$@"
