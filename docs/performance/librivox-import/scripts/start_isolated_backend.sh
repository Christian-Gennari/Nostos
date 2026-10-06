#!/usr/bin/env bash
# Start an isolated Nostos SelfHosted backend for the LibriVox performance spike.
#
# Isolation guarantees:
#   * ALL writable paths live under --state-root (default under
#     /home/dev/.hermes/cache/scratch), never the worktree's Storage symlink;
#   * the SQLite database is a private copy, so no production/library row is
#     ever written;
#   * the process is pinned to one CPU and capped at 1 GiB RSS to emulate the
#     Cloud web instance (apps-s-1vcpu-1gb-fixed) -- configurable, and clearly
#     an emulation rather than a provider measurement.
#
# Usage:
#   scripts/start_isolated_backend.sh --state-root /home/dev/.hermes/cache/scratch/librivox-import/app \
#       [--port 5330] [--cpus 1] [--memory-max 1G] [--no-limits]
set -euo pipefail

WORKTREE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
STATE_ROOT="/home/dev/.hermes/cache/scratch/librivox-import/app"
PORT=5330
CPUS=1
MEMORY_MAX="1G"
NO_LIMITS=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --state-root) STATE_ROOT="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --cpus) CPUS="$2"; shift 2 ;;
    --memory-max) MEMORY_MAX="$2"; shift 2 ;;
    --no-limits) NO_LIMITS=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

STATE_ROOT="$(readlink -f "$STATE_ROOT")"
FORBIDDEN_A="$(readlink -f "$WORKTREE/Nostos.Backend/Storage" 2>/dev/null || true)"
FORBIDDEN_B="$(readlink -f "/home/dev/coding/projects/nostos-rebirth/Nostos.Backend/Storage" 2>/dev/null || true)"
for forbidden in "$FORBIDDEN_A" "$FORBIDDEN_B"; do
  if [[ -n "$forbidden" && ( "$STATE_ROOT" == "$forbidden" || "$STATE_ROOT" == "$forbidden"/* ) ]]; then
    echo "REFUSING: state root $STATE_ROOT is inside production storage $forbidden" >&2
    exit 3
  fi
done
if [[ "$STATE_ROOT" != /home/dev/.hermes/cache/scratch/* && "$STATE_ROOT" != "$WORKTREE"/.agent/* ]]; then
  echo "REFUSING: state root $STATE_ROOT is outside the allowed scratch roots" >&2
  exit 3
fi

DB_PATH="$STATE_ROOT/db/nostos.db"
BOOKS_ROOT="$STATE_ROOT/storage/books"
BACKUPS_ROOT="$STATE_ROOT/storage/backups"
WORKING_ROOT="$STATE_ROOT/storage/tmp/acquisitions"
LOG="$STATE_ROOT/app.log"
PID_FILE="$STATE_ROOT/app.pid"

mkdir -p "$(dirname "$DB_PATH")" "$BOOKS_ROOT" "$BACKUPS_ROOT" "$WORKING_ROOT"
if [[ ! -f "$DB_PATH" ]]; then
  echo "[start] copying the worktree database to $DB_PATH"
  cp "$WORKTREE/Nostos.Backend/nostos.db" "$DB_PATH"
fi

BUILD_DLL="$WORKTREE/Nostos.Backend/bin/Debug/net10.0/Nostos.Backend.dll"
if [[ ! -f "$BUILD_DLL" ]]; then
  echo "[start] build output missing; run: dotnet build Nostos.sln" >&2
  exit 1
fi

ENVIRONMENT=(
  ASPNETCORE_ENVIRONMENT=Development
  ASPNETCORE_URLS="http://127.0.0.1:$PORT"
  Persistence__DatabasePath="$DB_PATH"
  Storage__BooksRoot="$BOOKS_ROOT"
  Storage__BackupsRoot="$BACKUPS_ROOT"
  Acquisition__WorkingRoot="$WORKING_ROOT"
  Embedding__Enabled=false
  Assistant__Enabled=false
  Speech__Enabled=false
  BackupSettings__IsEnabled=false
)

LIMIT_ARGS=()
if [[ "$NO_LIMITS" -eq 0 ]]; then
  LIMIT_ARGS=(systemd-run --user --scope --unit="nostos-spike-$$" \
    -p "AllowedCPUs=0-$((CPUS - 1))" \
    -p "MemoryMax=$MEMORY_MAX" -p "MemorySwapMax=0")
fi

echo "[start] backend on http://127.0.0.1:$PORT (cpus=$CPUS memory=$MEMORY_MAX no-limits=$NO_LIMITS)"
echo "[start] state root: $STATE_ROOT"
cd "$WORKTREE/Nostos.Backend"
setsid nohup "${LIMIT_ARGS[@]}" env "${ENVIRONMENT[@]}" \
  dotnet "$BUILD_DLL" >"$LOG" 2>&1 </dev/null &
echo $! >"$PID_FILE"
echo "[start] pid $(cat "$PID_FILE"); log $LOG"
