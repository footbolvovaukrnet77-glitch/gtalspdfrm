#!/usr/bin/env bash
# Clean build from scratch.
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
CONFIG="${1:-Debug}"
dotnet clean "$SOLUTION" -c "$CONFIG" -v quiet
dotnet build "$SOLUTION" -c "$CONFIG" --no-incremental
