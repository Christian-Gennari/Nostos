#!/usr/bin/env python3
"""Anonymize a Kobo or Apple Books SQLite export for the fixture corpus.

The point of the corpus (issue #1524) is to keep the *shape* of a real export:
every table, column, declared type, row count and row identity stays in place.
Only text that belongs to a reader is replaced:

* book titles and authors,
* highlight and note text,
* Kobo sideload paths, which carry the author and title in the file name,
* account fields that identify the reader (Kobo's ``user`` table, Apple's
  annotation creator id and home-directory path).

The replacements are deterministic: a value that appears twice is replaced by
the same placeholder both times, so grouping and joins keep working. The
SQLite file layout itself may differ after `VACUUM`; compare rows, not bytes.

Usage:
    python3 scripts/anonymize_sqlite_export.py INPUT -o OUTPUT
    python3 scripts/anonymize_sqlite_export.py INPUT --in-place

Run it on a copy, inspect the output, and commit that. The script refuses to
guess: an unrecognized database, or one with no known text column, fails
without writing a fixture.
"""

from __future__ import annotations

import argparse
from collections.abc import Callable
from pathlib import Path
import re
import shutil
import sqlite3
import sys
from urllib.parse import quote as urlquote


class AnonymizeError(RuntimeError):
    """A file the script will not silently copy without anonymizing."""


# Columns the highlights importer reads, plus nearby text that carries the same
# information (chapter/series titles, the sentence around a highlight).
KOBO_TEXT_COLUMNS = {
    "content": {
        "Title": "title",
        "BookTitle": "title",
        "Subtitle": "title",
        "Series": "title",
        "Attribution": "author",
    },
    "Bookmark": {
        "Text": "highlight",
        "Annotation": "note",
        "ContextString": "highlight",
        "ExtraAnnotationData": "highlight",
    },
    # A shelf name is a reader-authored title too.
    "Shelf": {"Name": "title"},
}

# Kobo stores sideloaded books as file:///mnt/onboard/<Author>/<Title>.epub.
# These columns are the row identities, so they are rewritten consistently
# instead of being dropped: every reference resolves to the same fake path.
# Other tables that point at a book (shelves, tabs, keys) carry a ContentID or
# VolumeID column too; they are discovered from the schema and rewritten with
# the same map so joins keep working.
KOBO_PATH_COLUMN_NAMES = {"contentid", "volumeid", "volume_id"}

KOBO_USER_KEEP_COLUMNS = {"SyncTokenAppVersion"}

APPLE_TEXT_COLUMNS = {
    "ZAEANNOTATION": {
        "ZANNOTATIONSELECTEDTEXT": "highlight",
        "ZANNOTATIONNOTE": "note",
        "ZANNOTATIONREPRESENTATIVETEXT": "highlight",
        # The chapter name can name the book ("Alfred Nobel's Other Mistake").
        "ZANNOTATIONCHAPTER": "title",
        "ZFUTUREPROOFING5": "title",
    },
    "ZBKLIBRARYASSET": {
        "ZTITLE": "title",
        "ZSORTTITLE": "title",
        "ZAUTHOR": "author",
        "ZSORTAUTHOR": "author",
        "ZGROUPING": "title",
        "ZSEQUENCEDISPLAYNAME": "title",
    },
}

# Identifiers and free text that are not needed by the importer and can
# identify a reader or the exact edition.
APPLE_REDACT_COLUMNS = (
    ("ZAEANNOTATION", "ZANNOTATIONCREATORIDENTIFIER"),
    ("ZBKLIBRARYASSET", "ZBOOKDESCRIPTION"),
    ("ZBKLIBRARYASSET", "ZCOMMENTS"),
)
KOBO_REDACT_COLUMNS = (
    ("content", "___UserID"),
    ("Bookmark", "UserID"),
    ("content", "Description"),
)

PATH_RE = re.compile(r"^(file://)?/mnt/onboard/([^/]+)/(.+)$")
BOOK_SUFFIX_RE = re.compile(r"(\.(?:kepub\.)?(?:epub|pdf|mobi|cbz|txt))$", re.IGNORECASE)
HOME_RE = re.compile(r"^(/Users/)[^/]+")


def quote(name: str) -> str:
    return '"' + name.replace('"', '""') + '"'


def table_names(connection: sqlite3.Connection) -> list[str]:
    return [
        row[0]
        for row in connection.execute(
            "SELECT name FROM sqlite_master WHERE type = 'table' "
            "AND name NOT LIKE 'sqlite_%' ORDER BY name"
        )
    ]


def columns(connection: sqlite3.Connection, table: str) -> list[str]:
    return [row[1] for row in connection.execute(f"PRAGMA table_info({quote(table)})")]


def detect_source(connection: sqlite3.Connection) -> str:
    tables = {name.lower() for name in table_names(connection)}
    if "zaeannotation" in tables or "zbklibraryasset" in tables:
        return "applebooks"
    if "bookmark" in tables or "content" in tables:
        return "kobo"
    raise AnonymizeError(
        "not a Kobo or Apple Books database: expected Bookmark/content or "
        "ZAEANNOTATION/ZBKLIBRARYASSET"
    )


def placeholder(kind: str, number: int) -> str:
    return {
        "title": f"Anonymized Title {number}",
        "author": f"Anonymized Author {number}",
        "highlight": f"Anonymized highlight {number}.",
        "note": f"Anonymized note {number}.",
    }[kind]


def extend(mapping: dict[str, str], value: str, kind: str) -> str:
    """Return the placeholder for value, adding a new one in sorted-seen order."""
    if value not in mapping:
        mapping[value] = placeholder(kind, len(mapping) + 1)
    return mapping[value]


def collect_values(
    connection: sqlite3.Connection,
    rules: dict[str, dict[str, str]],
) -> dict[str, dict[str, str]]:
    maps: dict[str, dict[str, str]] = {kind: {} for kind in ("title", "author", "highlight", "note")}
    for table, table_rules in rules.items():
        if table not in table_names(connection):
            continue
        table_columns = set(columns(connection, table))
        for column, kind in table_rules.items():
            if column not in table_columns:
                continue
            # ORDER BY the value keeps numbering stable across SQLite versions.
            for (value,) in connection.execute(
                f"SELECT DISTINCT {quote(column)} FROM {quote(table)} "
                f"WHERE {quote(column)} IS NOT NULL ORDER BY {quote(column)}"
            ):
                if isinstance(value, str) and value.strip():
                    extend(maps[kind], value, kind)
    return maps


def kobo_path_columns(connection: sqlite3.Connection) -> list[tuple[str, str]]:
    found: list[tuple[str, str]] = []
    for table in table_names(connection):
        for column in columns(connection, table):
            if column.lower() in KOBO_PATH_COLUMN_NAMES:
                found.append((table, column))
    return found


def split_path(value: str) -> tuple[str, str, str, str] | None:
    match = PATH_RE.match(value)
    if not match:
        return None
    prefix, folder, rest = match.groups()
    if "!" in rest:
        filename, tail = rest.split("!", 1)
        fragment = "!" + tail
    else:
        filename, fragment = rest, ""
    return prefix or "", folder, filename, fragment


def extension_of(filename: str) -> str:
    match = BOOK_SUFFIX_RE.search(filename)
    return match.group(1) if match else ""


def filename_without_extension(filename: str) -> str:
    suffix = extension_of(filename)
    return filename[: -len(suffix)] if suffix else filename


def unique_path(
    path_map: dict[tuple[str, str], tuple[str, str]],
    author: str,
    title: str,
) -> tuple[str, str]:
    """Keep the fake path unique when two books share a title and author."""
    used = set(path_map.values())
    candidate = title
    number = 2
    while (author, candidate) in used:
        candidate = f"{title} {number}"
        number += 1
    return author, candidate


def build_path_map(
    connection: sqlite3.Connection,
    maps: dict[str, dict[str, str]],
) -> dict[tuple[str, str], tuple[str, str]]:
    """Map an original (folder, filename) to a fake (author, title)."""
    path_map: dict[tuple[str, str], tuple[str, str]] = {}

    if "content" in table_names(connection):
        table_columns = set(columns(connection, "content"))
        title_column = "Title" if "Title" in table_columns else None
        book_title_column = "BookTitle" if "BookTitle" in table_columns else None
        author_column = "Attribution" if "Attribution" in table_columns else None
        fields = ["ContentID"]
        for name in (title_column, book_title_column, author_column):
            if name:
                fields.append(name)
        rows = connection.execute(
            f"SELECT {', '.join(quote(field) for field in fields)} FROM content"
        ).fetchall()
        indices = {field: index for index, field in enumerate(fields)}
        for row in rows:
            parsed = split_path(row[indices["ContentID"]] or "")
            if parsed is None:
                continue
            _prefix, folder, filename, _fragment = parsed
            title = None
            for name in (title_column, book_title_column):
                if name and row[indices[name]]:
                    title = row[indices[name]]
                    break
            title = title or filename_without_extension(filename)
            author = (row[indices[author_column]] if author_column else None) or folder
            path_map[(folder, filename)] = unique_path(
                path_map,
                extend(maps["author"], author, "author"),
                extend(maps["title"], title, "title"),
            )

    # A bookmark may point at a path with no content row (an orphan); give it
    # the same kind of fake path so it can never leak a title.
    for table, column in kobo_path_columns(connection):
        for (value,) in connection.execute(
            f"SELECT DISTINCT {quote(column)} FROM {quote(table)} "
            f"WHERE {quote(column)} IS NOT NULL"
        ):
            parsed = split_path(value or "")
            if parsed is None:
                continue
            _prefix, folder, filename, _fragment = parsed
            if (folder, filename) in path_map:
                continue
            path_map[(folder, filename)] = unique_path(
                path_map,
                extend(maps["author"], folder, "author"),
                extend(maps["title"], filename_without_extension(filename), "title"),
            )

    return path_map


def fragment_replacements(maps: dict[str, dict[str, str]]) -> list[tuple[str, str]]:
    """Original titles and authors as they can appear inside a chapter path.

    Kobo's ContentID ends in an EPUB fragment such as
    ``!OPS!c0_Max_und_Moritz.xhtml``; the book title is in there too.
    """
    article = re.compile(r"^(?:A|An|The)\s+", re.IGNORECASE)
    replacements: list[tuple[str, str]] = []
    for kind in ("title", "author"):
        for original, replacement in maps[kind].items():
            variants = {
                original,
                original.replace(" ", "_"),
                urlquote(original, safe=""),
            }
            without_article = article.sub("", original)
            if without_article and without_article != original:
                variants.add(without_article)
                variants.add(without_article.replace(" ", "_"))
            for variant in variants:
                replacements.append((variant, replacement))
                if "_" in variant:
                    replacements.append((variant, replacement.replace(" ", "_")))
    # Longest first so "The Art Spirit" is replaced before "Art".
    return sorted(set(replacements), key=lambda pair: len(pair[0]), reverse=True)


def anonymize_path(
    value: str,
    path_map: dict[tuple[str, str], tuple[str, str]],
    scrub: list[tuple[str, str]],
) -> str:
    parsed = split_path(value)
    if parsed is None:
        return value
    prefix, folder, filename, fragment = parsed
    author, title = path_map[(folder, filename)]
    for original, replacement in scrub:
        if original:
            fragment = fragment.replace(original, replacement)
    return f"{prefix}/mnt/onboard/{author}/{title}{extension_of(filename)}{fragment}"


def row_key(connection: sqlite3.Connection, table: str) -> str:
    for candidate in ("rowid", "_rowid_", "oid"):
        try:
            connection.execute(f"SELECT {candidate} FROM {quote(table)} LIMIT 1").fetchone()
            return candidate
        except sqlite3.Error:
            continue
    raise AnonymizeError(f"{table} has no rowid; refusing to rewrite it row by row")


def rewrite_text_column(
    connection: sqlite3.Connection,
    table: str,
    column: str,
    replacement_for: Callable[[str], str],
) -> int:
    key = row_key(connection, table)
    changed = 0
    rows = connection.execute(
        f"SELECT {key}, {quote(column)} FROM {quote(table)} ORDER BY {key}"
    ).fetchall()
    for identity, value in rows:
        if not isinstance(value, str) or not value.strip():
            continue
        replacement = replacement_for(value)
        if replacement == value:
            continue
        connection.execute(
            f"UPDATE {quote(table)} SET {quote(column)} = ? WHERE {key} = ?",
            (replacement, identity),
        )
        changed += 1
    return changed


def anonymize(
    connection: sqlite3.Connection,
    source: str,
    maps: dict[str, dict[str, str]],
) -> list[str]:
    notes: list[str] = []
    rules = KOBO_TEXT_COLUMNS if source == "kobo" else APPLE_TEXT_COLUMNS
    changed_by_column: dict[tuple[str, str], int] = {}
    handled = 0

    # Build the path map from the original title/author columns, before those
    # columns are rewritten: the map and the column values must agree.
    path_map = build_path_map(connection, maps) if source == "kobo" else {}

    for table, table_rules in rules.items():
        if table not in table_names(connection):
            continue
        table_columns = set(columns(connection, table))
        for column, kind in table_rules.items():
            if column not in table_columns:
                continue
            handled += 1
            mapping = maps[kind]
            changed_by_column[(table, column)] = rewrite_text_column(
                connection, table, column, lambda value, mapping=mapping: mapping[value]
            )

    if source == "kobo":
        scrub = fragment_replacements(maps)
        for table, column in kobo_path_columns(connection):
            changed_by_column[(table, column)] = rewrite_text_column(
                connection,
                table,
                column,
                lambda value, path_map=path_map, scrub=scrub: anonymize_path(value, path_map, scrub),
            )

        for table, column in KOBO_REDACT_COLUMNS:
            if table in table_names(connection) and column in columns(connection, table):
                handled += 1
                changed_by_column[(table, column)] = rewrite_text_column(
                    connection, table, column, lambda _value: "redacted"
                )

        # The device account table is not read by the importer, but it holds
        # an email address and live tokens. Keep its row and columns; redact
        # every text value except the firmware version used in the README.
        if "user" in table_names(connection):
            for column in columns(connection, "user"):
                if column in KOBO_USER_KEEP_COLUMNS:
                    continue
                handled += 1
                changed_by_column[("user", column)] = rewrite_text_column(
                    connection, "user", column, lambda _value: "redacted"
                )

    else:
        for table, column in APPLE_REDACT_COLUMNS:
            if table in table_names(connection) and column in columns(connection, table):
                handled += 1
                changed_by_column[(table, column)] = rewrite_text_column(
                    connection, table, column, lambda _value: "redacted"
                )

        # A real library's path includes the macOS account name.
        if "ZBKLIBRARYASSET" in table_names(connection) and "ZPATH" in columns(
            connection, "ZBKLIBRARYASSET"
        ):
            changed_by_column[("ZBKLIBRARYASSET", "ZPATH")] = rewrite_text_column(
                connection,
                "ZBKLIBRARYASSET",
                "ZPATH",
                lambda value: HOME_RE.sub(r"\1reader", value),
            )

    for (table, column), changed in sorted(changed_by_column.items()):
        notes.append(f"{table}.{column}: {changed} row(s)")

    if handled == 0:
        raise AnonymizeError(
            "no known title, author, highlight or note column was found; "
            "refusing to write a file that may still contain private text"
        )
    return notes


def snapshot(connection: sqlite3.Connection) -> tuple[list[tuple], dict[str, int], dict[str, tuple]]:
    schema = connection.execute(
        "SELECT type, name, tbl_name, sql FROM sqlite_master "
        "WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name"
    ).fetchall()
    counts = {name: connection.execute(f"SELECT COUNT(*) FROM {quote(name)}").fetchone()[0]
              for name in table_names(connection)}
    column_info = {
        name: tuple(
            (row[1], row[2], row[3], row[5])
            for row in connection.execute(f"PRAGMA table_info({quote(name)})")
        )
        for name in table_names(connection)
    }
    return schema, counts, column_info


def copy_database(source: Path, destination: Path) -> None:
    shutil.copyfile(source, destination)
    for suffix in ("-wal", "-shm"):
        sidecar = Path(str(source) + suffix)
        if sidecar.exists():
            shutil.copyfile(sidecar, Path(str(destination) + suffix))


def checkpoint(connection: sqlite3.Connection) -> None:
    try:
        connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        # A fixture is one file. Leave rollback-journal mode so opening it does
        # not recreate a -wal beside the committed fixture.
        connection.execute("PRAGMA journal_mode=DELETE")
    except sqlite3.Error:
        # Not a WAL database, or the file is read-only: nothing to fold in.
        pass


def remove_sidecars(path: Path) -> None:
    for suffix in ("-wal", "-shm"):
        sidecar = Path(str(path) + suffix)
        if sidecar.exists():
            sidecar.unlink()


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Anonymize a Kobo or Apple Books SQLite export for the fixture corpus."
    )
    parser.add_argument("input", type=Path, help="SQLite file to read")
    destination = parser.add_mutually_exclusive_group(required=True)
    destination.add_argument("-o", "--output", type=Path, help="new file to write")
    destination.add_argument(
        "--in-place", action="store_true", help="rewrite the input file itself (make a backup first)"
    )
    parser.add_argument(
        "--source",
        choices=("auto", "kobo", "applebooks"),
        default="auto",
        help="which schema to expect (default: detect from the tables)",
    )
    parser.add_argument(
        "--force", action="store_true", help="overwrite the output file if it exists"
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    source_path: Path = args.input
    if not source_path.is_file():
        print(f"error: {source_path} is not a file", file=sys.stderr)
        return 2

    if args.in_place:
        output_path = source_path
    else:
        output_path = args.output
        if output_path.resolve() == source_path.resolve():
            print("error: input and output are the same file; use --in-place", file=sys.stderr)
            return 2
        if output_path.exists() and not args.force:
            print(f"error: {output_path} already exists; use --force to replace it", file=sys.stderr)
            return 2

    if not args.in_place:
        try:
            copy_database(source_path, output_path)
        except OSError as error:
            print(f"error: could not copy {source_path}: {error}", file=sys.stderr)
            return 2

    connection: sqlite3.Connection | None = None
    try:
        connection = sqlite3.connect(str(output_path))
        with connection:
            checkpoint(connection)
            detected = detect_source(connection)
            source = detected if args.source == "auto" else args.source
            if args.source != "auto" and detected != args.source:
                raise AnonymizeError(
                    f"detected {detected}, not {args.source}; wrong --source?"
                )

            before = snapshot(connection)
            maps = collect_values(
                connection, KOBO_TEXT_COLUMNS if source == "kobo" else APPLE_TEXT_COLUMNS
            )
            notes = anonymize(connection, source, maps)
            after = snapshot(connection)

            if before != after:
                raise AnonymizeError(
                    "the schema, column types or row counts changed while anonymizing"
                )
            integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
            if integrity != "ok":
                raise AnonymizeError(f"SQLite integrity check failed: {integrity}")

        checkpoint(connection)
        # UPDATE leaves the replaced text in free pages. VACUUM rebuilds the
        # file from live rows only, so `strings` and a byte scan cannot
        # recover the original library.
        connection.execute("VACUUM")
        if snapshot(connection) != before:
            raise AnonymizeError(
                "the schema, column types or row counts changed while vacuuming"
            )
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        if integrity != "ok":
            raise AnonymizeError(f"SQLite integrity check failed after vacuum: {integrity}")
        checkpoint(connection)
        connection.close()
        connection = None
        remove_sidecars(output_path)

        print(f"source: {source}")
        distinct = sum(len(mapping) for mapping in maps.values())
        print(f"distinct text values replaced: {distinct}")
        for note in notes:
            if not note.endswith(": 0 row(s)"):
                print(f"  {note}")
        print(f"wrote: {output_path}")
        return 0
    except (AnonymizeError, sqlite3.Error) as error:
        print(f"error: {error}", file=sys.stderr)
        if connection is not None:
            connection.close()
        if not args.in_place:
            output_path.unlink(missing_ok=True)
            remove_sidecars(output_path)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
