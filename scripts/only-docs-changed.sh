#!/usr/bin/env bash
# Exit 0 when every file that differs between <base> and <head> is
# documentation, so the build, tests and image cannot change. Exit 1 for
# anything else, including a base that is missing, unknown or all zeros
# (a new branch, a force push): when in doubt, treat it as a code change.
#
#   scripts/only-docs-changed.sh <base> <head>
#
# Documentation is Markdown anywhere except where the frontend serves or
# imports it (Nostos.Frontend/public, Nostos.Frontend/src), plus everything
# under the top-level docs/ directory.
set -uo pipefail

base="${1:-}"
head="${2:-}"

if ! [[ "$base" =~ ^[0-9a-f]{40}$ ]] || [[ "$base" =~ ^0+$ ]]; then
  echo "No usable base revision ('$base'); treating as a code change."
  exit 1
fi
if ! git cat-file -e "$base^{commit}" 2>/dev/null || ! git cat-file -e "$head^{commit}" 2>/dev/null; then
  echo "Base $base or head $head is not in the checked-out history; treating as a code change."
  exit 1
fi

if ! changed="$(git diff --name-only --no-renames "$base" "$head")"; then
  echo "Could not diff $base..$head; treating as a code change."
  exit 1
fi
if [ -z "$changed" ]; then
  echo "No files changed between $base and $head; treating as a code change."
  exit 1
fi

while IFS= read -r path; do
  case "$path" in
    Nostos.Frontend/public/* | Nostos.Frontend/src/*)
      echo "Code change: $path"
      exit 1
      ;;
    docs/* | *.md)
      ;;
    *)
      echo "Code change: $path"
      exit 1
      ;;
  esac
done <<< "$changed"

echo "Only documentation changed between $base and $head:"
printf '%s\n' "$changed" | sed 's/^/  /'
exit 0
