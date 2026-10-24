#!/usr/bin/env python3
"""Check the app-store packages against each other and against the image.

    python3 deploy/app-stores/check.py

It parses every XML, YAML and JSON file, runs `docker compose config -q` on
each Compose file (skipped when Docker is not installed), and checks that the
packages agree with the Dockerfile and with each other: the container port,
the /data volume, the image name, the pinned release and the store rules each
package must meet. It is fast and needs only PyYAML.

The stores' own validators are separate; deploy/app-stores/README.md says how
to run each one.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import yaml

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
IMAGE = "ghcr.io/christian-gennari/nostos"

UNRAID = HERE / "unraid"
CASAOS = HERE / "casaos" / "Nostos"
UMBREL = HERE / "umbrel" / "nostos"
RUNTIPI = HERE / "runtipi" / "apps" / "nostos"
SELFHST = HERE / "selfhst"

errors: list[str] = []


def fail(message: str) -> None:
    errors.append(message)


def expect(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def rel(path: Path) -> str:
    return str(path.relative_to(ROOT))


def load_yaml(path: Path) -> dict:
    try:
        return yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as error:
        fail(f"{rel(path)}: invalid YAML: {error}")
        return {}


def compose_config(path: Path, env: dict[str, str]) -> None:
    if not shutil.which("docker"):
        print(f"skip  docker compose config {rel(path)} (no docker)")
        return
    result = subprocess.run(
        ["docker", "compose", "-f", str(path), "config", "-q"],
        env={**os.environ, **env},
        capture_output=True,
        text=True,
        check=False,
    )
    expect(result.returncode == 0, f"{rel(path)}: docker compose config failed: {result.stderr.strip()}")
    if result.returncode == 0:
        print(f"ok    docker compose config {rel(path)}")


def dockerfile_facts() -> tuple[str, str]:
    text = (ROOT / "Dockerfile").read_text(encoding="utf-8")
    port = re.search(r"^EXPOSE\s+(\d+)", text, re.MULTILINE)
    volume = re.search(r'^VOLUME\s+\["([^"]+)"\]', text, re.MULTILINE)
    if not port or not volume:
        fail("Dockerfile: could not find EXPOSE or VOLUME")
        return "", ""
    return port.group(1), volume.group(1)


def image_parts(reference: str) -> tuple[str, str, str]:
    """('ghcr.io/x/y', 'v1', 'sha256:...') from 'ghcr.io/x/y:v1@sha256:...'."""
    digest = ""
    if "@" in reference:
        reference, digest = reference.split("@", 1)
    name, _, tag = reference.rpartition(":")
    return name, tag, digest


def check_unraid(port: str, volume: str) -> None:
    profile = ET.parse(UNRAID / "ca_profile.xml").getroot()
    expect(profile.tag == "CommunityApplications", "ca_profile.xml: root must be <CommunityApplications>")
    expect(bool((profile.findtext("Profile") or "").strip()), "ca_profile.xml: <Profile> must not be empty")

    template = ET.parse(UNRAID / "templates" / "nostos.xml").getroot()
    expect(template.tag == "Container" and template.get("version") == "2", "nostos.xml: root must be <Container version=\"2\">")
    for tag in ("Name", "Repository", "Overview", "Category", "WebUI", "Icon", "Support", "Project", "TemplateURL"):
        expect(bool((template.findtext(tag) or "").strip()), f"nostos.xml: <{tag}> is required")
    expect(template.findtext("Repository") == f"{IMAGE}:latest", "nostos.xml: <Repository> must be the image's latest tag")
    expect(f"[PORT:{port}]" in (template.findtext("WebUI") or ""), f"nostos.xml: <WebUI> must use [PORT:{port}]")

    configs = {config.get("Target"): config for config in template.findall("Config")}
    expect(port in configs and configs[port].get("Type") == "Port", f"nostos.xml: needs a Port config for {port}")
    expect(volume in configs and configs[volume].get("Type") == "Path", f"nostos.xml: needs a Path config for {volume}")
    expect("/external/media" in configs and configs["/external/media"].get("Mode") == "ro", "nostos.xml: /external/media must be read-only")
    # Unraid creates missing host paths owned by 99:100 (dockerMan Helpers.php),
    # so the container runs as that user and keeps its temp files in /tmp.
    expect("--user 99:100" in (template.findtext("ExtraParams") or ""), "nostos.xml: ExtraParams must run as --user 99:100")
    expect(configs.get("TMPDIR") is not None and configs["TMPDIR"].text == "/tmp", "nostos.xml: TMPDIR must be /tmp")
    for config in template.findall("Config"):
        default = config.get("Default") or ""
        value = config.text or ""
        expect(default == value, f"nostos.xml: Config {config.get('Name')!r} Default and value differ")
    expect((UNRAID / "nostos-icon.png").is_file(), "unraid/nostos-icon.png is missing")
    print("ok    unraid")


def check_casaos(port: str, volume: str, release: str) -> None:
    path = CASAOS / "docker-compose.yml"
    compose = load_yaml(path)
    meta = compose.get("x-casaos", {})
    expect(re.fullmatch(r"[a-z0-9_-]+", compose.get("name", "")) is not None, "casaos: top-level name must match ^[a-z0-9_-]+$")
    expect(re.fullmatch(r"[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)+", meta.get("id", "")) is not None, "casaos: x-casaos.id must be reverse-domain")
    for field in ("main", "index", "port_map", "icon", "title", "category", "version", "architectures"):
        expect(field in meta, f"casaos: x-casaos.{field} is required")
    expect(isinstance(meta.get("port_map"), str), "casaos: port_map must be a quoted string")
    expect(meta.get("category") in {"Media", "Productivity", "Home", "Networking", "AI", "Finance", "Social", "Developer", "Others"}, "casaos: unknown category")
    expect(sorted(meta.get("architectures", [])) == ["amd64", "arm64"], "casaos: architectures must be amd64 and arm64")
    expect(meta.get("version") == release.lstrip("v"), f"casaos: version must be {release.lstrip('v')}")
    for locale_field in ("title", "tagline", "description", "release_notes"):
        expect("en_US" in meta.get(locale_field, {}), f"casaos: x-casaos.{locale_field} needs en_US")

    services = compose.get("services", {})
    main = services.get(meta.get("main"), {})
    expect(bool(main), "casaos: x-casaos.main must name a service")
    published = [str(p.get("published")) for p in main.get("ports", []) if str(p.get("target")) == port]
    expect(published == [meta.get("port_map")], f"casaos: port_map must be the published port for container port {port}")
    targets = [v.get("target") if isinstance(v, dict) else v.split(":")[1] for v in main.get("volumes", [])]
    expect(volume in targets, f"casaos: main service must mount {volume}")
    for name, service in services.items():
        expect(service.get("image") == f"{IMAGE}:{release}", f"casaos: service {name} must use {IMAGE}:{release}")

    for asset in ["icon.svg", "icon.png", "thumbnail.png"]:
        expect((CASAOS / asset).is_file(), f"casaos: {asset} is missing")
    for link in [meta.get("icon"), meta.get("thumbnail"), *meta.get("screenshot_link", [])]:
        name = (link or "").rsplit("/", 1)[-1]
        expect((CASAOS / name).is_file(), f"casaos: {link} has no file {name} beside the compose file")
    compose_config(path, {"AppID": "nostos"})
    print("ok    casaos")


UMBREL_CATEGORIES = {"ai", "automation", "bitcoin", "crypto", "developer", "files", "finance", "media", "networking", "social"}


def check_umbrel(port: str, volume: str, release: str) -> None:
    manifest = load_yaml(UMBREL / "umbrel-app.yml")
    compose = load_yaml(UMBREL / "docker-compose.yml")
    expect(manifest.get("id") == UMBREL.name, "umbrel: id must match the folder name")
    expect(manifest.get("category") in UMBREL_CATEGORIES, "umbrel: unknown category")
    expect(manifest.get("storage") == {"dataRoot": "data"}, "umbrel: storage.dataRoot must be data")
    expect(manifest.get("version") == release.lstrip("v"), f"umbrel: version must be {release.lstrip('v')}")
    expect(manifest.get("gallery") == [], "umbrel: gallery must be empty for a new package")
    expect("icon" not in manifest, "umbrel: official packages have no icon field")

    proxy = compose.get("services", {}).get("app_proxy", {}).get("environment", {})
    web = compose.get("services", {}).get("web", {})
    expect(proxy.get("APP_HOST") == f"{manifest.get('id')}_web_1", "umbrel: APP_HOST must be <app-id>_web_1")
    expect(str(proxy.get("APP_PORT")) == port, f"umbrel: APP_PORT must be {port}")
    expect("PROXY_AUTH_ADD" not in proxy, "umbrel: keep Umbrel auth on (no PROXY_AUTH_ADD)")
    expect("ports" not in web, "umbrel: app_proxy publishes the web UI; no raw ports")
    name, tag, digest = image_parts(web.get("image", ""))
    expect(name == IMAGE and tag == release, f"umbrel: image must be {IMAGE}:{release}@sha256:...")
    expect(re.fullmatch(r"sha256:[0-9a-f]{64}", digest) is not None, "umbrel: image must be pinned by digest")
    mounts = [v.split(":") for v in web.get("volumes", [])]
    expect(any(m[1] == volume and m[0].startswith("${APP_DATA_DIR}/data/") for m in mounts), f"umbrel: {volume} must be a bind mount under ${{APP_DATA_DIR}}/data/")
    for source, *_ in mounts:
        if source.startswith("${APP_DATA_DIR}/data/"):
            folder = UMBREL / source.removeprefix("${APP_DATA_DIR}/")
            expect(folder.is_dir(), f"umbrel: commit {rel(folder)} (with .gitkeep) for its bind mount")
    expect(web.get("user") == "1000:1000", "umbrel: run as 1000:1000, the owner of app data")
    expect((web.get("environment") or {}).get("TMPDIR") == "/tmp", "umbrel: TMPDIR must be /tmp when running as 1000:1000")
    # No `docker compose config` here: app_proxy has no image until umbrelOS
    # patches the file, so Compose rejects it on its own. Umbrel's linter
    # (README) covers the compose file instead.
    print("ok    umbrel")


RUNTIPI_CATEGORIES = {"network", "media", "development", "automation", "social", "utilities", "photography", "security", "featured", "books", "data", "music", "finance", "gaming", "ai"}


def check_runtipi(port: str, volume: str, release: str) -> None:
    config = json.loads((RUNTIPI / "config.json").read_text(encoding="utf-8"))
    compose_path = RUNTIPI / "docker-compose.yml"
    compose = load_yaml(compose_path)
    app_id = config.get("id")
    for field in ("id", "available", "name", "tipi_version", "short_desc", "author", "source", "description", "version", "port", "categories", "supported_architectures", "created_at", "updated_at"):
        expect(field in config, f"runtipi: config.json needs {field}")
    expect(app_id == RUNTIPI.name, "runtipi: id must match the folder name")
    expect(set(config.get("categories", [])) <= RUNTIPI_CATEGORIES, "runtipi: unknown category")
    expect(sorted(config.get("supported_architectures", [])) == ["amd64", "arm64"], "runtipi: architectures must be amd64 and arm64")
    expect(config.get("version") == release.lstrip("v"), f"runtipi: version must be {release.lstrip('v')}")
    expect(compose.get("x-runtipi", {}).get("schema_version") == 2, "runtipi: x-runtipi.schema_version must be 2")
    main = compose.get("services", {}).get(app_id, {})
    expect(bool(main), "runtipi: the main service must be named after the app id")
    expect(config.get("version", "?") in main.get("image", ""), "runtipi: the main image tag must contain config.json's version")
    expect(main.get("x-runtipi", {}).get("is_main") is True, "runtipi: main service needs x-runtipi.is_main")
    expect(str(main.get("x-runtipi", {}).get("internal_port")) == port, f"runtipi: internal_port must be {port}")
    expect(any(v.split(":")[1] == volume for v in main.get("volumes", [])), f"runtipi: main service must mount {volume}")
    for name, service in compose.get("services", {}).items():
        expect(service.get("image") == f"{IMAGE}:{release}", f"runtipi: service {name} must use {IMAGE}:{release}")
    expect((RUNTIPI / "metadata" / "description.md").is_file(), "runtipi: metadata/description.md is missing")
    expect((RUNTIPI / "metadata" / "logo.jpg").is_file(), "runtipi: metadata/logo.jpg is missing")
    compose_config(compose_path, {"APP_DATA_DIR": "/tmp/runtipi-app-data", "ROOT_FOLDER_HOST": "/tmp/runtipi"})
    print("ok    runtipi")


def check_selfhst() -> None:
    for variant in ("nostos", "nostos-light", "nostos-dark"):
        for folder, suffix in (("svg", "svg"), ("png", "png"), ("webp", "webp")):
            path = SELFHST / folder / f"{variant}.{suffix}"
            expect(path.is_file(), f"selfhst: {rel(path)} is missing")
        svg = SELFHST / "svg" / f"{variant}.svg"
        if svg.is_file():
            root = ET.parse(svg).getroot()
            expect(root.get("viewBox") == "0 0 1024 1024", f"selfhst: {svg.name} must have a square viewBox")
    print("ok    selfhst")


def main() -> int:
    port, volume = dockerfile_facts()
    umbrel_image = load_yaml(UMBREL / "docker-compose.yml").get("services", {}).get("web", {}).get("image", "")
    release = image_parts(umbrel_image)[1]
    print(f"image {IMAGE}, release {release}, container port {port}, volume {volume}")
    check_unraid(port, volume)
    check_casaos(port, volume, release)
    check_umbrel(port, volume, release)
    check_runtipi(port, volume, release)
    check_selfhst()
    if errors:
        print("\nFAIL", file=sys.stderr)
        for message in errors:
            print(f"  - {message}", file=sys.stderr)
        return 1
    print("\nAll app-store packages are consistent.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
