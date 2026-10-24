#!/usr/bin/env python3
"""Point the app-store packages at a Nostos release.

    python3 deploy/app-stores/update-image.py v2026.10.25

It asks ghcr.io (anonymously, like a store's installer) for the release tag's
image index, refuses a release that is not published for both linux/amd64
and linux/arm64, and then rewrites:

- casaos/Nostos/docker-compose.yml: image tag, version, update_at;
- umbrel/nostos/umbrel-app.yml and docker-compose.yml: version, and the image
  as tag@sha256:<index digest>;
- runtipi/apps/nostos: image tag, version, tipi_version + 1, updated_at.

The Unraid template follows `latest` and needs no change. Run check.py
afterwards. Pass --dry-run to only print the digest and platforms.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import re
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPOSITORY = "christian-gennari/nostos"
IMAGE = f"ghcr.io/{REPOSITORY}"
INDEX_TYPES = ", ".join(
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
    ]
)


def fetch(url: str, headers: dict[str, str]) -> tuple[bytes, dict[str, str]]:
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read(), dict(response.headers)


def inspect(tag: str) -> tuple[str, set[str]]:
    token_body, _ = fetch(f"https://ghcr.io/token?scope=repository:{REPOSITORY}:pull", {})
    token = json.loads(token_body)["token"]
    body, headers = fetch(
        f"https://ghcr.io/v2/{REPOSITORY}/manifests/{tag}",
        {"Authorization": f"Bearer {token}", "Accept": INDEX_TYPES},
    )
    digest = {key.lower(): value for key, value in headers.items()}.get("docker-content-digest", "")
    index = json.loads(body)
    platforms = {
        f"{m['platform']['os']}/{m['platform']['architecture']}"
        for m in index.get("manifests", [])
        if m.get("platform", {}).get("os") != "unknown"
    }
    return digest, platforms


def replace(path: Path, pattern: str, replacement: str, count: int = 0) -> None:
    text = path.read_text(encoding="utf-8")
    new, matches = re.subn(pattern, replacement, text, count=count, flags=re.MULTILINE)
    if matches == 0:
        sys.exit(f"{path}: no match for {pattern!r}")
    path.write_text(new, encoding="utf-8")
    print(f"updated {path.relative_to(HERE)}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("tag", help="release tag, for example v2026.10.25")
    parser.add_argument("--date", help="release date YYYY-MM-DD (default: today)")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    if not re.fullmatch(r"v\d{4}\.\d{2}\.\d{2}(\.\d+)?", args.tag):
        sys.exit(f"{args.tag} is not a Nostos release tag (vYYYY.MM.DD[.N])")
    digest, platforms = inspect(args.tag)
    print(f"{IMAGE}:{args.tag} index {digest}, platforms {sorted(platforms)}")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        sys.exit("ghcr.io returned no index digest")
    missing = {"linux/amd64", "linux/arm64"} - platforms
    if missing:
        sys.exit(f"{args.tag} is missing {sorted(missing)}; the stores need both amd64 and arm64")
    if args.dry_run:
        return 0

    version = args.tag.removeprefix("v")
    date = args.date or dt.date.today().isoformat()
    image_tag = rf"{re.escape(IMAGE)}:v[0-9.]+"

    casaos = HERE / "casaos" / "Nostos" / "docker-compose.yml"
    replace(casaos, image_tag + r"$", f"{IMAGE}:{args.tag}")
    replace(casaos, r'^  version: "[^"]*"', f'  version: "{version}"')
    replace(casaos, r'^  update_at: "[^"]*"', f'  update_at: "{date}"')

    umbrel = HERE / "umbrel" / "nostos"
    replace(umbrel / "docker-compose.yml", image_tag + r"(@sha256:[0-9a-f]{64})?", f"{IMAGE}:{args.tag}@{digest}")
    replace(umbrel / "umbrel-app.yml", r'^version: "[^"]*"', f'version: "{version}"')

    runtipi = HERE / "runtipi" / "apps" / "nostos"
    replace(runtipi / "docker-compose.yml", image_tag + r"$", f"{IMAGE}:{args.tag}")
    config_path = runtipi / "config.json"
    config = json.loads(config_path.read_text(encoding="utf-8"))
    if config["version"] != version:
        config["tipi_version"] += 1
    config["version"] = version
    config["updated_at"] = int(dt.datetime.now(dt.timezone.utc).timestamp() * 1000)
    config_path.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
    print(f"updated {config_path.relative_to(HERE)}")

    print("\nNow run: python3 deploy/app-stores/check.py")
    return 0


if __name__ == "__main__":
    sys.exit(main())
