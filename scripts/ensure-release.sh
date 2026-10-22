#!/usr/bin/env bash
# Make one CalVer release complete: git tag, image tag, GitHub Release.
#
#   scripts/ensure-release.sh <version> <revision> <previous-ref> [notes-file]
#
# Every piece is created only when it is missing, and checked when it exists,
# so the same call publishes a new release or repairs a half-finished one.
# Order matters: the git tag is created first because it is how the next run
# finds the release to repair; the image tag comes before the GitHub Release
# so a visible release always has its image.
#
# Release notes are generated from <previous-ref> when the GitHub Release is
# missing and no notes file is given. Set DRY_RUN=1 to print the writes
# instead of making them.
set -euo pipefail

if [ "$#" -lt 3 ] || [ "$#" -gt 4 ]; then
  echo "usage: $0 <version> <revision> <previous-ref> [notes-file]" >&2
  exit 2
fi

version="$1"
revision="$2"
previous="$3"
notes_file="${4:-}"
image="${NOSTOS_IMAGE:-ghcr.io/christian-gennari/nostos}"

if ! [[ "$version" =~ ^v[0-9]{4}\.[0-9]{2}\.[0-9]{2}(\.[0-9]+)?$ ]]; then
  echo "Not a CalVer version: $version" >&2
  exit 2
fi
if ! [[ "$revision" =~ ^[0-9a-f]{40}$ ]]; then
  echo "Expected a full 40-character revision, got: $revision" >&2
  exit 2
fi

write() {
  if [ "${DRY_RUN:-}" = "1" ]; then
    echo "DRY RUN: $*"
  else
    "$@"
  fi
}

# Prints the manifest digest of an image reference, or nothing if it is missing.
digest_of() {
  docker buildx imagetools inspect "$1" --format '{{json .Manifest}}' 2>/dev/null \
    | jq -r '.digest // empty' 2>/dev/null || true
}

source_image="$image:selfhosted-sha-$revision"
source_digest="$(digest_of "$source_image")"
release_digest="$(digest_of "$image:$version")"

# The image for this exact revision must exist before anything is tagged, so a
# release can never point at a revision nobody can run. A release whose image
# tag already exists is runnable even if the revision tag was removed.
if [ -z "$source_digest" ] && [ -z "$release_digest" ]; then
  echo "No published image $source_image yet; Publish SelfHosted container may still be running. Re-run this release once it has finished." >&2
  exit 1
fi
if [ -n "$release_digest" ] && [ -n "$source_digest" ] && [ "$release_digest" != "$source_digest" ]; then
  echo "Image $image:$version is $release_digest, but $source_image is $source_digest." >&2
  exit 1
fi

# 1. Git tag
if git rev-parse -q --verify "refs/tags/$version" >/dev/null; then
  tagged="$(git rev-list -n 1 "refs/tags/$version")"
  if [ "$tagged" != "$revision" ]; then
    echo "Tag $version already points at $tagged, not $revision. Published tags never move." >&2
    exit 1
  fi
  echo "Git tag $version: present at $revision."
else
  echo "Git tag $version: creating at $revision."
  write git -c user.name="github-actions[bot]" \
    -c user.email="github-actions[bot]@users.noreply.github.com" \
    tag -a "$version" "$revision" -m "Nostos $version"
  write git push origin "refs/tags/$version"
fi

# 2. Image tag: same digest, one more name; nothing is rebuilt.
if [ -n "$release_digest" ]; then
  echo "Image tag $version: present at $release_digest."
else
  echo "Image tag $version: adding to $source_image ($source_digest)."
  write docker buildx imagetools create --tag "$image:$version" "$image@$source_digest"
fi

# 3. GitHub Release
if gh release view "$version" --json tagName >/dev/null 2>&1; then
  echo "GitHub Release $version: present."
else
  if [ -z "$notes_file" ]; then
    notes_file="$(mktemp)"
    python3 scripts/generate-release-notes.py \
      --from "$previous" \
      --to "$revision" \
      --version "$version" > "$notes_file"
  fi
  echo "GitHub Release $version: publishing."
  write gh release create "$version" \
    --verify-tag \
    --title "Nostos $version" \
    --notes-file "$notes_file"
fi
