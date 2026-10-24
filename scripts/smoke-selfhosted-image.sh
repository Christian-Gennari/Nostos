#!/usr/bin/env bash
# Start a SelfHosted image and check the parts that depend on native code for
# the image's CPU architecture: SQLite, libvips (cover thumbnails), FFmpeg and
# audio metadata.
#
#   scripts/smoke-selfhosted-image.sh <image> [port] [expected-arch]
#
# expected-arch is the .NET name (X64 or Arm64). When given, the app must
# report it. The container is removed afterwards; set KEEP_CONTAINER=1 to keep
# it (it is named nostos-smoke-<port>).
set -euo pipefail

if [ "$#" -lt 1 ] || [ "$#" -gt 3 ]; then
  echo "usage: $0 <image> [port] [expected-arch]" >&2
  exit 2
fi

image="$1"
port="${2:-18090}"
expected_arch="${3:-}"
name="nostos-smoke-$port"
base="http://127.0.0.1:$port"
repo="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    docker logs "$name" 2>&1 | tail -n 80 || true
  fi
  if [ "${KEEP_CONTAINER:-}" != "1" ]; then
    docker rm -f "$name" >/dev/null 2>&1 || true
  fi
  rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

docker rm -f "$name" >/dev/null 2>&1 || true
docker run -d --name "$name" -p "$port:8080" "$image" >/dev/null

# Emulated arm64 starts slowly; allow three minutes.
for i in $(seq 1 90); do
  if curl -fsS "$base/health/ready" >/dev/null 2>&1; then
    echo "ready after ~$((i * 2))s"
    break
  fi
  if [ "$i" -eq 90 ]; then
    echo "not ready after 180s" >&2
    exit 1
  fi
  sleep 2
done

curl -fsS "$base/health/live" >/dev/null
curl -fsS "$base/" >/dev/null
curl -fsS "$base/api/runtime/capabilities" | grep -q '"deploymentMode":"SelfHosted"'
echo "health, frontend and capabilities answer"

arch="$(docker exec "$name" dotnet --info | awk '/^Host:/{h=1} h && /Architecture:/{print $2; exit}')"
echo "runtime architecture: $arch"
if [ -n "$expected_arch" ] && [ "${arch,,}" != "${expected_arch,,}" ]; then
  echo "expected $expected_arch" >&2
  exit 1
fi

create_book() {
  curl -fsS -H 'Content-Type: application/json' -X POST \
    --data "{\"type\":\"$1\",\"title\":\"$2\",\"fileUploadExpected\":true}" \
    "$base/api/books/" | jq -er '.id'
}

# EPUB upload, then a PNG cover. The thumbnail is a WebP made by libvips.
epub_id="$(create_book ebook 'Smoke EPUB')"
curl -fsS -F "file=@$repo/Nostos.Frontend/e2e/assets/tiny.epub;type=application/epub+zip" \
  "$base/api/books/$epub_id/file" | jq -e '.uploaded == true' >/dev/null
curl -fsS "$base/api/books/$epub_id" | jq -e '.hasFile == true' >/dev/null
echo "EPUB uploaded"

# FFmpeg in the image makes the test media, which also shows it runs.
docker exec "$name" ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'color=c=0x293E32:s=400x600' -frames:v 1 /tmp/nostos/cover.png
docker exec "$name" ffmpeg -hide_banner -loglevel error -y \
  -f lavfi -i 'sine=frequency=440:duration=30' -c:a libmp3lame -b:a 64k \
  -metadata title='Smoke audiobook' /tmp/nostos/audio.mp3
docker cp "$name:/tmp/nostos/cover.png" "$work/cover.png" >/dev/null
docker cp "$name:/tmp/nostos/audio.mp3" "$work/audio.mp3" >/dev/null
echo "FFmpeg made a PNG and an MP3"

curl -fsS -F "file=@$work/cover.png;type=image/png" \
  "$base/api/books/$epub_id/cover" | jq -e '.uploaded == true' >/dev/null
curl -fsS "$base/api/books/$epub_id/cover/thumbnail?width=320" -o "$work/thumb.webp"
if [ "$(head -c 4 "$work/thumb.webp")" != "RIFF" ] \
  || [ "$(dd if="$work/thumb.webp" bs=1 skip=8 count=4 2>/dev/null)" != "WEBP" ]; then
  echo "cover thumbnail is not a WebP" >&2
  exit 1
fi
echo "cover thumbnail is a WebP of $(wc -c < "$work/thumb.webp") bytes (libvips)"

# Audiobook upload: ATL reads the duration from the file.
audio_id="$(create_book audiobook 'Smoke audiobook')"
curl -fsS -F "file=@$work/audio.mp3;type=audio/mpeg" \
  "$base/api/books/$audio_id/file" | jq -e '.uploaded == true' >/dev/null
duration="$(curl -fsS "$base/api/books/$audio_id" | jq -er '.duration')"
echo "audiobook recognised, duration $duration"
case "$duration" in
  00:00:29|00:00:30|00:00:31) ;;
  *) echo "expected a duration of about 30 seconds" >&2; exit 1 ;;
esac

docker exec "$name" test -s /data/nostos.db
echo "memory in use: $(docker stats --no-stream --format '{{.MemUsage}}' "$name")"
echo "smoke test passed for $image"
