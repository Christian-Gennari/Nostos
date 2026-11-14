#!/usr/bin/env bash
# Start a SelfHosted image on a named volume, write a book, replace the
# container, and read the book back: the volume must carry the database and the
# book across the replacement. Both the pull-request image
# (selfhosted-container.yml) and the published image
# (publish-selfhosted-container.yml) run this same script, so the two checks
# cannot drift.
#
#   scripts/verify-container-persistence.sh <image> [port] [options]
#
# Options:
#   --writable-data      also require /data to be writable inside the container
#   --non-root           also require the container to run as a non-root user
#   --name-prefix NAME   container names and the volume start with NAME
#                        (default nostos-persistence)
#   --title TEXT         title of the probe book, followed by the run id
#                        (default "Container persistence probe")
#   -h, --help           print this usage
#
# Exit status: 0 when the book survives the replacement, non-zero when any
# check fails (the status of the command that failed), 2 on bad usage. Set
# KEEP_CONTAINERS=1 to keep the containers and the volume for inspection; their
# logs are printed on failure either way.
set -euo pipefail

usage() {
  cat >&2 <<'USAGE'
usage: verify-container-persistence.sh <image> [port] [options]

  --writable-data      also require /data to be writable inside the container
  --non-root           also require the container to run as a non-root user
  --name-prefix NAME   container names and the volume start with NAME
  --title TEXT         title of the probe book, followed by the run id
USAGE
}

case "${1:-}" in
  -h | --help)
    usage
    exit 0
    ;;
esac

if [ "$#" -lt 1 ]; then
  usage
  exit 2
fi

image="$1"
shift
port=18081
name_prefix="nostos-persistence"
title="Container persistence probe"
check_writable=false
check_non_root=false

if [ "$#" -gt 0 ] && [[ "$1" =~ ^[0-9]+$ ]]; then
  port="$1"
  shift
fi

while [ "$#" -gt 0 ]; do
  case "$1" in
    --writable-data)
      check_writable=true
      shift
      ;;
    --non-root)
      check_non_root=true
      shift
      ;;
    --name-prefix)
      if [ "$#" -lt 2 ] || [ -z "$2" ]; then
        echo "--name-prefix needs a value" >&2
        usage
        exit 2
      fi
      name_prefix="$2"
      shift 2
      ;;
    --title)
      if [ "$#" -lt 2 ] || [ -z "$2" ]; then
        echo "--title needs a value" >&2
        usage
        exit 2
      fi
      title="$2"
      shift 2
      ;;
    *)
      echo "unknown argument: $1" >&2
      usage
      exit 2
      ;;
  esac
done

run_id="${GITHUB_RUN_ID:-local}"
run_attempt="${GITHUB_RUN_ATTEMPT:-0}"
volume="${name_prefix}-${run_id}-${run_attempt}"
first="${name_prefix}-first"
second="${name_prefix}-second"
base="http://127.0.0.1:$port"

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    for name in "$first" "$second"; do
      if docker inspect "$name" >/dev/null 2>&1; then
        echo "--- logs: $name" >&2
        docker logs "$name" 2>&1 | tail -n 80 || true
      fi
    done
  fi
  if [ "${KEEP_CONTAINERS:-}" != "1" ]; then
    docker rm -f "$first" "$second" >/dev/null 2>&1 || true
    docker volume rm "$volume" >/dev/null 2>&1 || true
  fi
  exit "$status"
}
trap cleanup EXIT

docker volume create "$volume" >/dev/null

# 45 attempts, two seconds apart: the same 90 seconds the inline checks used,
# enough for a cold start on an emulated or loaded runner.
start_container() {
  local name="$1"
  docker run -d \
    --name "$name" \
    -p "$port:8080" \
    -v "$volume:/data" \
    "$image" >/dev/null

  for i in $(seq 1 45); do
    if curl -fsS "$base/health/ready" >/dev/null 2>&1; then
      return 0
    fi
    if [ "$i" -eq 45 ]; then
      docker logs "$name"
      return 1
    fi
    sleep 2
  done
}

start_container "$first"

curl -fsS "$base/" >/dev/null
# jq rather than `curl ... | grep -q`: with `pipefail`, grep exiting early on
# a match can turn curl's SIGPIPE into a failure of the pipeline.
capabilities="$(curl -fsS "$base/api/runtime/capabilities")"
printf '%s' "$capabilities" | jq -e '.deploymentMode == "SelfHosted"' >/dev/null

probe_title="$title $run_id-$run_attempt"
created="$(curl -fsS \
  -H 'Content-Type: application/json' \
  -X POST \
  --data "{\"type\":\"physical\",\"title\":\"$probe_title\"}" \
  "$base/api/books/")"
book_id="$(printf '%s' "$created" | jq -er '.id')"

docker exec "$first" test -s /data/nostos.db
if [ "$check_writable" = true ]; then
  docker exec "$first" test -w /data
fi
if [ "$check_non_root" = true ]; then
  runtime_user="$(docker inspect -f '{{.Config.User}}' "$first")"
  test -n "$runtime_user"
  test "$runtime_user" != "0"
fi

docker rm -f "$first" >/dev/null

start_container "$second"

persisted="$(curl -fsS "$base/api/books/$book_id")"
printf '%s' "$persisted" | jq -e \
  --arg id "$book_id" \
  --arg title "$probe_title" \
  '.id == $id and .title == $title' >/dev/null

echo "book $book_id survived the container replacement on volume $volume"
