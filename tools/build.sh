#!/usr/bin/env bash
# Builds every project in the solution, including the net48 GTA V client.
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
CONFIG="${1:-Debug}"
echo "Building $SOLUTION ($CONFIG)..."
dotnet build "$SOLUTION" -c "$CONFIG" "${@:2}"
