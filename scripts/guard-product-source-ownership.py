#!/usr/bin/env python3
"""Guard physical source ownership between Nostos.Product and Nostos.Backend."""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[1]

PROJECTS = (
    ("Nostos.Product", REPO_ROOT / "Nostos.Product" / "Nostos.Product.csproj"),
    ("Nostos.Backend", REPO_ROOT / "Nostos.Backend" / "Nostos.Backend.csproj"),
)


def _evaluate_compile_items(project: Path) -> list[dict[str, Any]]:
    command = [
        "dotnet",
        "msbuild",
        str(project),
        "-nologo",
        "-getItem:Compile",
    ]
    completed = subprocess.run(
        command,
        cwd=REPO_ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        detail = completed.stderr.strip() or completed.stdout.strip()
        raise RuntimeError(
            f"{project.relative_to(REPO_ROOT)} Compile evaluation failed"
            + (f": {detail}" if detail else "")
        )

    payload = _parse_msbuild_json(completed.stdout)
    items = payload.get("Items", {}).get("Compile")
    if not isinstance(items, list):
        raise RuntimeError(
            f"{project.relative_to(REPO_ROOT)} did not return an Items.Compile list"
        )
    return items


def _parse_msbuild_json(output: str) -> dict[str, Any]:
    decoder = json.JSONDecoder()
    for index, character in enumerate(output):
        if character != "{":
            continue
        try:
            payload, _ = decoder.raw_decode(output[index:])
        except json.JSONDecodeError:
            continue
        if isinstance(payload, dict) and "Items" in payload:
            return payload
    raise RuntimeError("MSBuild output did not contain the expected JSON payload")


def _full_path(item: dict[str, Any], project: Path) -> Path:
    full_path = item.get("FullPath")
    if not isinstance(full_path, str) or not full_path:
        identity = item.get("Identity", "<unknown>")
        raise RuntimeError(
            f"{project.relative_to(REPO_ROOT)} Compile item {identity!r} "
            "has no FullPath metadata"
        )
    return Path(full_path).resolve()


def _case_key(path: Path) -> str:
    return os.path.normpath(str(path)).casefold()


def _is_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
    except ValueError:
        return False
    return True


def _validate_project(
    label: str,
    project: Path,
    items: list[dict[str, Any]],
) -> tuple[list[Path], list[str]]:
    project_root = project.parent.resolve()
    paths: list[Path] = []
    errors: list[str] = []
    exact_seen: dict[Path, int] = {}
    case_seen: dict[str, Path] = {}

    for item in items:
        try:
            path = _full_path(item, project)
        except RuntimeError as error:
            errors.append(str(error))
            continue

        paths.append(path)
        exact_seen[path] = exact_seen.get(path, 0) + 1

        if not path.is_file():
            errors.append(f"{label}: Compile input does not exist: {path}")
        if not _is_within(path, project_root):
            errors.append(
                f"{label}: Compile input is outside {project_root}: {path}"
            )

        key = _case_key(path)
        previous = case_seen.get(key)
        if previous is None:
            case_seen[key] = path
        elif previous != path:
            errors.append(
                f"{label}: case-fold path collision: {previous} <-> {path}"
            )

    for path, count in exact_seen.items():
        if count > 1:
            errors.append(
                f"{label}: Compile input appears {count} times: {path}"
            )

    return paths, errors


def main() -> int:
    graphs: dict[str, list[Path]] = {}
    errors: list[str] = []

    for label, project in PROJECTS:
        try:
            items = _evaluate_compile_items(project)
        except RuntimeError as error:
            errors.append(str(error))
            continue

        paths, project_errors = _validate_project(label, project, items)
        graphs[label] = paths
        errors.extend(project_errors)

    product_paths = graphs.get("Nostos.Product")
    backend_paths = graphs.get("Nostos.Backend")
    if product_paths is not None and backend_paths is not None:
        product_exact = set(product_paths)
        backend_exact = set(backend_paths)

        for path in sorted(product_exact & backend_exact, key=str):
            errors.append(
                "Product/Backend Compile overlap: "
                f"{path}"
            )

        product_case = {_case_key(path): path for path in product_exact}
        backend_case = {_case_key(path): path for path in backend_exact}
        for key in sorted(product_case.keys() & backend_case.keys()):
            product_path = product_case[key]
            backend_path = backend_case[key]
            if product_path != backend_path:
                errors.append(
                    "Product/Backend case-fold Compile overlap: "
                    f"{product_path} <-> {backend_path}"
                )

    if errors:
        print("Product source ownership guard failed:", file=sys.stderr)
        for error in errors:
            print(f"  - {error}", file=sys.stderr)
        return 1

    counts = ", ".join(
        f"{label}={len(paths)}" for label, paths in graphs.items()
    )
    print(f"Product source ownership guard passed ({counts}).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
