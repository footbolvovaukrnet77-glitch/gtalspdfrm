#!/usr/bin/env bash
# Tails today's server log.
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
LOG="$ROOT/logs/server-$(date -u +%Y-%m-%d).log"
if [ ! -f "$LOG" ]; then
  echo "No log at $LOG yet. Start the server first (tools/run-server.sh)."
  exit 1
fi
tail -f "$LOG"
