#!/usr/bin/env bash
# Runs the automated test suite.
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
dotnet test "$ROOT/tests/Gtamp.Tests/Gtamp.Tests.csproj" "$@"
