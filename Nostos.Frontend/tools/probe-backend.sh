#!/usr/bin/env bash
#
# Library probe backend — development tooling, not part of the app runtime.
#
# The isolated Playwright fixture has an empty library, which is fine for
# chrome/geometry assertions and useless for judging a list view or measuring
# scroll cost. This launches the real backend over a SECOND loopback port so
# the probes can render real books and real cover art.
#
# How the real data is reached, and why it is safe:
#   * It runs from a git worktree, never the production working tree.
#   * `nostos.db` is COPIED into the worktree. The copy stays writable, because
#     startup runs EF migrations and SQLite takes a write lock to do so; a
#     read-only copy crashes the boot (verified). Safety comes from the copy
#     living outside the production tree, never from file permissions.
#   * `Storage/books` is a SYMLINK to the production book files. The backend
#     resolves it to <contentRoot>/Storage/books, so a symlink serves the real
#     covers and files without duplicating 13 GB. Nothing in the probe writes
#     book files.
#   * Both the copied DB and Storage/ are gitignored, so the snapshot can never
#     be committed.
#   * It binds 127.0.0.1:5099 from appsettings.Probe.json and never touches
#     PM2 or the production port (5214).
#
# Usage (from the worktree's Nostos.Frontend directory):
#   bash tools/probe-backend.sh                 # production checkout as source
#   bash tools/probe-backend.sh /path/to/checkout /tmp/probe-backend.log
#
# Then point a probe at it:
#   node tools/probe-library-views.mjs dist/Nostos.Frontend/browser out \
#     http://127.0.0.1:5099
set -euo pipefail

PROD_CHECKOUT="${1:-/home/dev/coding/projects/Nostos}"
LOG="${2:-/tmp/nostos-probe-backend.log}"

BACKEND_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../Nostos.Backend" && pwd)"
REPO_ROOT="$(cd "$BACKEND_DIR/.." && pwd)"
PROD_DB="$PROD_CHECKOUT/Nostos.Backend/nostos.db"
PROD_STORAGE="$PROD_CHECKOUT/Nostos.Backend/Storage/books"

if [ ! -f "$PROD_DB" ]; then
  echo "probe backend: no production database at $PROD_DB" >&2
  exit 2
fi
if [ ! -d "$PROD_STORAGE" ]; then
  echo "probe backend: no production Storage/books at $PROD_STORAGE" >&2
  exit 2
fi

# Re-copy only when the production DB is newer, so a restart is not delayed by
# copying the whole database every time. Stale WAL/SHM sidecars from a previous
# run must go with it or SQLite would apply them over the fresh copy.
if [ ! -f "$BACKEND_DIR/nostos.db" ] || [ "$PROD_DB" -nt "$BACKEND_DIR/nostos.db" ]; then
  echo "probe backend: snapshotting the production database into the worktree"
  rm -f "$BACKEND_DIR/nostos.db-wal" "$BACKEND_DIR/nostos.db-shm"
  cp "$PROD_DB" "$BACKEND_DIR/nostos.db"
  chmod u+w "$BACKEND_DIR/nostos.db"
fi

mkdir -p "$BACKEND_DIR/Storage"
if [ ! -e "$BACKEND_DIR/Storage/books" ]; then
  ln -s "$PROD_STORAGE" "$BACKEND_DIR/Storage/books"
fi

if [ "$REPO_ROOT" = "$PROD_CHECKOUT" ]; then
  echo "probe backend: refusing to run — this is the production checkout." >&2
  echo "Run the probe from a separate worktree so the production tree is untouched." >&2
  exit 3
fi

cd "$BACKEND_DIR"
echo "probe backend: http://127.0.0.1:5099  (log: $LOG)"
exec env ASPNETCORE_ENVIRONMENT=Probe MCP__ENABLED=false \
  dotnet run -c Debug --no-launch-profile --no-build >>"$LOG" 2>&1
