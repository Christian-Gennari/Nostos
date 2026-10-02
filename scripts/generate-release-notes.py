#!/usr/bin/env python3
"""Generate reader-facing Markdown from an exact, exclusive/inclusive git range."""

from __future__ import annotations

import argparse
from collections import OrderedDict
from datetime import date
import json
from pathlib import Path
import re
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
SECTIONS = OrderedDict([
    ("reader", "📖 Reader & Notes"),
    ("library", "📚 Library & Discovery"),
    ("assistant", "🤖 Ask Nostos"),
    ("other", "🛠️ Improvements & Fixes"),
])
SCOPES = {
    "reader": {"reader", "reading", "epub", "pdf", "notes", "note", "brain", "concepts"},
    "library": {"library", "discovery", "books", "book", "collections", "upload", "opds"},
    "assistant": {"assistant", "ask-nostos", "ask nostos", "ai"},
}
INTERNAL_SCOPES = {
    "ci", "test", "tests", "testing", "build", "deps", "dependencies", "agent",
    "workflow", "release", "release-notes", "quality-bed", "visual-qa",
}
PUBLIC_TYPES = {"feat", "fix", "perf", "ux", "revert"}
SKIP_LABELS = {"release-note:skip", "release-note:none", "skip-changelog"}
CONVENTIONAL = re.compile(r"^(\w+)(?:\(([^)]+)\))?!?:\s*(.+)$", re.DOTALL)
PR_NUMBER = re.compile(r"^Merge pull request #(\d+)\b|\(#(\d+)\)\s*$")
CALVER = re.compile(r"^v(\d{4})\.(\d{2})\.(\d{2})(?:\.([1-9]\d*))?$")
INTRO = (
    "# Changelog\n\n"
    "Changes to reading, your library, and Ask Nostos. "
    "Unreleased entries describe merged work awaiting a release.\n"
)


def run(*args: str, root: Path = ROOT) -> str:
    result = subprocess.run(args, cwd=root, text=True, encoding="utf-8",
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        raise ValueError(f"{' '.join(args)}: {result.stderr.strip()}")
    return result.stdout.strip()


def validate_version(version: str) -> str:
    if version == "Unreleased":
        return version
    match = CALVER.fullmatch(version)
    if not match:
        raise ValueError("Version must be Unreleased, vYYYY.MM.DD or vYYYY.MM.DD.N (N >= 1)")
    date(int(match[1]), int(match[2]), int(match[3]))
    return version


def resolve_range(base: str, target: str, root: Path = ROOT) -> tuple[str, str]:
    # Resolve before forming the range; --end-of-options keeps refs out of git options.
    start = run("git", "rev-parse", "--verify", "--end-of-options", f"{base}^{{commit}}", root=root)
    end = run("git", "rev-parse", "--verify", "--end-of-options", f"{target}^{{commit}}", root=root)
    run("git", "merge-base", "--is-ancestor", start, end, root=root)
    return start, end


def commits(start: str, end: str, *, first_parent: bool, root: Path = ROOT) -> list[dict]:
    mode = "--first-parent" if first_parent else "--no-merges"
    output = run("git", "log", mode, "--reverse", "--format=%H%x00%s%x00%b%x00%x1e",
                 f"{start}..{end}", root=root)
    records = []
    for record in output.split("\x1e"):
        if not record.strip():
            continue
        sha, title, body, _ = record.strip().split("\x00", 3)
        records.append({"sha": sha, "title": title, "body": body, "labels": []})
    return records


def github_changes(start: str, end: str, repo: str, root: Path = ROOT) -> list[dict]:
    changes = []
    for commit in commits(start, end, first_parent=True, root=root):
        match = PR_NUMBER.search(commit["title"])
        if not match:
            changes.append(commit)
            continue
        number = match[1] or match[2]
        # Fetch each exact PR: no date-window guesses or truncated PR-list pages.
        pr = json.loads(run("gh", "api", f"repos/{repo}/pulls/{number}", root=root))
        if not pr["merged_at"] or pr["merge_commit_sha"] != commit["sha"]:
            raise ValueError(f"PR #{number} does not match merged revision {commit['sha']}")
        changes.append({"sha": commit["sha"], "title": pr["title"],
                        "body": pr.get("body") or "", "labels": pr["labels"],
                        "number": int(number), "url": f"https://github.com/{repo}/pull/{number}"})
    return changes


def paragraph(text: str) -> str:
    """Use one prose paragraph, never verification tables or implementation lists."""
    text = text.strip()
    if not text or re.match(r"(?:[#>|]|[-*] |```|<!--)", text):
        return ""
    return re.split(r"\n\s*\n|\n(?=[#>|]|[-*] |```|<!--)", text, maxsplit=1)[0].strip()


def summary(body: str) -> str:
    explicit = re.search(r"<!--\s*release-note\s*-->(.*?)<!--\s*/release-note\s*-->",
                         body, re.DOTALL | re.IGNORECASE)
    if explicit:
        return paragraph(explicit[1])
    # Existing PRs may use a Summary section. Prefer a dedicated release-note section.
    for heading in ("Release notes", "Summary"):
        match = re.search(rf"^#{{1,6}}\s+{heading}\s*\n(.*?)(?=^#{{1,6}}\s|\Z)",
                          body, re.DOTALL | re.MULTILINE | re.IGNORECASE)
        if match:
            result = paragraph(match[1])
            if result:
                return result
    return ""


def entry(change: dict) -> tuple[str, str] | None:
    labels = {label["name"].strip().lower() for label in change.get("labels", [])}
    if labels & SKIP_LABELS:
        return None
    match = CONVENTIONAL.match(change["title"])
    kind, scope, title = (match[1].lower(), (match[2] or "").lower(), match[3]) if match else (
        "", "", change["title"])
    if scope in INTERNAL_SCOPES or (match and kind not in PUBLIC_TYPES):
        return None
    area = ""
    # Product-area labels take precedence over commit scopes. Section order breaks ties.
    for key, scopes in SCOPES.items():
        if any(re.sub(r"^area:\s*", "", label) in scopes for label in labels if label.startswith("area:")):
            area = key
            break
    if not match and not area and "release-note" not in labels:
        return None
    if not area:
        area = next((key for key, scopes in SCOPES.items() if scope in scopes), "other")
    title = summary(change.get("body", "")) or title
    title = re.sub(r"\s*\(#\d+\)\s*$", "", title)
    title = re.sub(r"\s+", " ", title).strip()
    if not title:
        return None
    title = title[0].upper() + title[1:]
    if title[-1] not in ".!?":
        title += "."
    # GitHub renders the artifact as Markdown; keep descriptions on a single bullet.
    title = title.replace("<", "&lt;").replace(">", "&gt;")
    if change.get("number"):
        title += f" ([#{change['number']}]({change['url']}))"
    return area, title


def render(changes: list[dict], version: str, start: str, end: str, repo: str | None) -> str:
    groups = {key: [] for key in SECTIONS}
    seen = set()
    for change in changes:
        item = entry(change)
        if item is None:
            continue
        area, title = item
        if (area, title.casefold()) not in seen:
            groups[area].append(title)
            seen.add((area, title.casefold()))
    lines = [f"## {version}", "", f"<!-- release-range: {start}..{end} -->", ""]
    for key, heading in SECTIONS.items():
        if groups[key]:
            lines.extend([f"### {heading}", "", *[f"- {title}" for title in groups[key]], ""])
    if not seen:
        lines.extend(["No reader-facing changes in this range.", ""])
    if repo:
        lines.extend([f"[All changes](https://github.com/{repo}/compare/{start}...{end})", ""])
    return "\n".join(lines)


def update_changelog(path: Path, section: str, version: str) -> None:
    original = path.read_text(encoding="utf-8") if path.exists() else INTRO
    headings = list(re.finditer(r"^## (.+)$", original, re.MULTILINE))
    intro = original[:headings[0].start()] if headings else original
    releases = []
    for index, heading in enumerate(headings):
        name = heading[1].strip()
        # A release takes the place of the pending draft. Re-running replaces only that version.
        if name == version or (version != "Unreleased" and name == "Unreleased"):
            continue
        stop = headings[index + 1].start() if index + 1 < len(headings) else len(original)
        releases.append(original[heading.start():stop].strip())
    content = "\n\n".join([intro.strip(), section.strip(), *releases]) + "\n"
    path.write_text(content, encoding="utf-8")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--from", dest="base", required=True, help="Previous tag or pin (excluded)")
    parser.add_argument("--to", default="HEAD", help="Release tag or pin (included; default HEAD)")
    parser.add_argument("--version", required=True, help="Unreleased or vYYYY.MM.DD[.N]")
    parser.add_argument("--source", choices=("github", "git"), default="github")
    parser.add_argument("--repo", help="GitHub owner/repo (inferred with gh when omitted)")
    parser.add_argument("--write", type=Path, metavar="CHANGELOG", help="Prepend/update a changelog; otherwise print")
    args = parser.parse_args(argv)
    try:
        validate_version(args.version)
        start, end = resolve_range(args.base, args.to)
        repo = args.repo
        if repo and not re.fullmatch(r"[\w.-]+/[\w.-]+", repo):
            raise ValueError("--repo must be owner/repo")
        if args.source == "github":
            repo = repo or run("gh", "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner")
            changes = github_changes(start, end, repo)
        else:
            changes = commits(start, end, first_parent=False)
        section = render(changes, args.version, start, end, repo)
        if args.write:
            update_changelog(args.write, section, args.version)
            print(f"Updated {args.write}: {args.version}, {start[:12]}..{end[:12]}", file=sys.stderr)
        else:
            print(section, end="")
        return 0
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.exit(1, f"release notes: {error}\n")


if __name__ == "__main__":
    sys.exit(main())
