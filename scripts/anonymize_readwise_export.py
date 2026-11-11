#!/usr/bin/env python3
"""Anonymize a recorded Readwise export-API response for the fixture corpus.

The API response is the *shape* the mapper is tested against (issue #1525).
Before one can be committed to this public repository, every value that
belongs to the reader must be replaced:

* titles and readable titles,
* authors,
* highlight and note text, document notes and summaries,
* URLs (Readwise, source, cover),
* tag names,
* id-like values (user book ids, highlight ids, tag ids, external ids) and
  Amazon ASINs.

Field names, nesting, types, null-ness and array element shapes stay exactly as
recorded. Repeated values are replaced by the same placeholder, so a highlight
that points at its book keeps pointing at it. Dates become neutral 2024
timestamps of the same string type. The response is trimmed to a bounded
number of books and highlights.

Readwise's export normally returns books, articles and supplementals; an
account may not have a tweet or a podcast. ``--add-missing-skip-kinds`` adds
one entry of each requested kind by copying an existing result's structure
(the documented shape) and changing only its values/category, so a public
fixture can still cover the skip rules. The source README must say which rows
are synthetic.

Usage:
    python3 scripts/anonymize_readwise_export.py export.json -o fixture.json
    python3 scripts/anonymize_readwise_export.py export.json -o fixture.json \
        --add-missing-skip-kinds tweet,podcast

Run it on a copy, inspect the output, then commit that. The script refuses to
write a file when a personal string from the input still appears in the output.
"""

from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timedelta, timezone
import copy
import json
from pathlib import Path
import sys

DEFAULT_MAX_BOOKS = 5
DEFAULT_MAX_HIGHLIGHTS = 15

# Categories the mapper imports, in the order the trimmer tries to include
# one of each. The values match Readwise's documented categories.
CATEGORY_PRIORITY = ("books", "articles", "supplementals", "pdfs", "epubs", "tweets", "podcasts")
SKIP_CATEGORIES = {"articles", "tweets", "podcasts", "supplementals"}
SKIP_KIND_CATEGORIES = {"tweet": "tweets", "podcast": "podcasts"}


class AnonymizeError(RuntimeError):
    """The input cannot be anonymized without guessing, or a value leaked."""


# Keys whose values are the reader's words.
TITLE_KEYS = {"title", "readable_title", "title_override"}
AUTHOR_KEYS = {"author", "author_override"}
TEXT_KEYS = {"text"}
NOTE_KEYS = {"note", "document_note"}
SUMMARY_KEYS = {"summary"}
TAG_NAME_KEYS = {"name"}
EXTERNAL_KEYS = {"external_id"}
ASIN_KEYS = {"asin"}

# Keys whose values identify a row and must stay internally consistent.
ID_KEYS = {"id", "user_book_id", "book_id"}

URL_KEYS = {
    "readwise_url",
    "source_url",
    "unique_url",
    "cover_image_url",
    "highlights_url",
    "image_url",
    "url",
    "highlight_url",
}

DATE_KEYS = {"highlighted_at", "created_at", "updated_at", "last_highlight_at"}

# Values that describe the software, not the reader. They are needed by the
# mapper's skip rules and by the fixture's readability.
SAFE_STRING_KEYS = {
    "source",
    "category",
    "color",
    "location_type",
    "language",
    "subscription",
    "status",
}

# Numbers that are reading positions, not identities: the anchor tests need
# their real values.
SAFE_NUMBER_KEYS = {"location", "end_location", "count"}

PERSONAL_STRING_KEYS = (
    TITLE_KEYS
    | AUTHOR_KEYS
    | TEXT_KEYS
    | NOTE_KEYS
    | SUMMARY_KEYS
    | TAG_NAME_KEYS
    | EXTERNAL_KEYS
    | ASIN_KEYS
    | URL_KEYS
)

BASE_DATE = datetime(2024, 1, 1, 12, 0, 0, tzinfo=timezone.utc)


def _require_export(document: object) -> dict:
    if not isinstance(document, dict):
        raise AnonymizeError("The input is not a Readwise export object.")
    if not isinstance(document.get("results"), list):
        raise AnonymizeError("The input has no `results` array.")
    if "count" not in document or "nextPageCursor" not in document:
        raise AnonymizeError("The input is not a Readwise export page (count/nextPageCursor missing).")
    return document


class Anonymizer:
    """Deterministic value replacement for one export response."""

    def __init__(self) -> None:
        self._placeholders: dict[tuple[str, object], str] = {}
        self._ids: dict[object, int] = {}
        self._counters: Counter[str] = Counter()
        self._date_index = 0
        self._next_id = 1001

    def string(self, kind: str, value: str) -> str:
        key = (kind, value)
        if key in self._placeholders:
            return self._placeholders[key]
        self._counters[kind] += 1
        placeholder = {
            "title": f"Synthetic Title {self._counters[kind]}",
            "author": f"Synthetic Author {self._counters[kind]}",
            "text": f"Synthetic highlight {self._counters[kind]}.",
            "note": f"Synthetic note {self._counters[kind]}.",
            "document_note": f"Synthetic document note {self._counters[kind]}.",
            "summary": f"Synthetic summary {self._counters[kind]}.",
            "tag": f"synthetic-tag-{self._counters[kind]}",
            "external_id": f"external-{self._counters[kind]}",
            "asin": f"B00SYNTH{self._counters[kind]:02d}",
            "url": f"https://example.invalid/readwise/{self._counters[kind]}",
            "other": f"Synthetic value {self._counters[kind]}",
        }[kind]
        self._placeholders[key] = placeholder
        return placeholder

    def identity(self, value: object) -> int:
        if value not in self._ids:
            self._ids[value] = self._next_id
            self._next_id += 1
        return self._ids[value]

    def date(self) -> str:
        # A fixed neutral instant per distinct value: order and type survive.
        stamp = BASE_DATE + timedelta(minutes=self._date_index)
        self._date_index += 1
        return stamp.strftime("%Y-%m-%dT%H:%M:%S.000Z")

    def value(self, key: str | None, value: object) -> object:
        if isinstance(value, dict):
            return {name: self.value(name, child) for name, child in value.items()}
        if isinstance(value, list):
            return [self.value(key, child) for child in value]
        if value is None:
            return None

        if key in TITLE_KEYS and isinstance(value, str):
            return self.string("title", value) if value else value
        if key in AUTHOR_KEYS and isinstance(value, str):
            return self.string("author", value) if value else value
        if key in TEXT_KEYS and isinstance(value, str):
            return self.string("text", value) if value.strip() else value
        if key in NOTE_KEYS and isinstance(value, str):
            return self.string("note", value) if value.strip() else value
        if key in SUMMARY_KEYS and isinstance(value, str):
            return self.string("summary", value) if value.strip() else value
        if key in TAG_NAME_KEYS and isinstance(value, str):
            return self.string("tag", value) if value else value
        if key in EXTERNAL_KEYS and isinstance(value, str):
            return self.string("external_id", value) if value else value
        if key in ASIN_KEYS and isinstance(value, str):
            return self.string("asin", value) if value else value
        if key in URL_KEYS and isinstance(value, str):
            return self.string("url", value) if value else value
        if key in DATE_KEYS and isinstance(value, str):
            return self.date() if value else value
        if key in ID_KEYS and isinstance(value, int) and not isinstance(value, bool):
            return self.identity(value)
        if key in SAFE_NUMBER_KEYS and isinstance(value, (int, float)) and not isinstance(value, bool):
            return value
        if key in SAFE_STRING_KEYS and isinstance(value, str):
            return value
        if isinstance(value, bool):
            return value
        if isinstance(value, (int, float)):
            # Unknown numeric fields are not known to identify the reader;
            # keep the value so a documented shape test stays meaningful.
            return value
        if isinstance(value, str):
            # Unknown text is still text: replace it rather than risk a leak.
            return self.string("other", value) if value.strip() else value
        raise AnonymizeError(f"Unsupported JSON value for key {key!r}.")


def _select_books(results: list[dict], max_books: int) -> list[dict]:
    chosen: list[int] = []
    categories = [str(result.get("category") or "") for result in results]
    for category in CATEGORY_PRIORITY:
        if len(chosen) >= max_books:
            break
        for index, value in enumerate(categories):
            if value == category and index not in chosen:
                chosen.append(index)
                break
    for index in range(len(results)):
        if len(chosen) >= max_books:
            break
        if index not in chosen:
            chosen.append(index)
    return [results[index] for index in sorted(chosen)]


def _trim_highlights(books: list[dict], max_highlights: int) -> None:
    for book in books:
        highlights = book.get("highlights")
        if not isinstance(highlights, list):
            raise AnonymizeError("A result has no `highlights` array.")
        book["highlights"] = highlights[: max(1, max_highlights)]

    total = sum(len(book["highlights"]) for book in books)
    while total > max_highlights:
        removed = False
        for book in sorted(books, key=lambda item: len(item["highlights"]), reverse=True):
            if len(book["highlights"]) > 1 and total > max_highlights:
                book["highlights"].pop()
                total -= 1
                removed = True
        if not removed:
            break


def _template_book(books: list[dict]) -> tuple[dict, dict]:
    for book in books:
        if book.get("highlights"):
            return book, book["highlights"][0]
    raise AnonymizeError("The export has no highlight to copy a synthetic skip entry from.")


def _synthetic(kind: str, template_book: dict, template_highlight: dict) -> dict:
    book = copy.deepcopy(template_book)
    highlight = copy.deepcopy(template_highlight)
    synthetic_id = 900_001 if kind == "tweets" else 900_002
    book["user_book_id"] = synthetic_id
    highlight["book_id"] = synthetic_id
    highlight["id"] = synthetic_id + 10_000
    if kind == "tweets":
        book["title"] = "Tweets From Synthetic Reader"
        book["readable_title"] = "Tweets From Synthetic Reader"
        book["author"] = "@syntheticreader"
        book["category"] = "tweets"
        book["source"] = "reader"
        highlight["location_type"] = "order"
        highlight["location"] = 1
        highlight["text"] = "Synthetic tweet."
        highlight["note"] = ""
    elif kind == "podcasts":
        book["title"] = "Synthetic Podcast"
        book["readable_title"] = "Synthetic Podcast"
        book["author"] = "Synthetic Host"
        book["category"] = "podcasts"
        book["source"] = "reader"
        highlight["location_type"] = "time_offset"
        highlight["location"] = 60
        highlight["text"] = "Synthetic podcast line."
        highlight["note"] = ""
    else:
        raise AnonymizeError(f"Unknown synthetic skip kind {kind!r}.")
    book["highlights"] = [highlight]
    # The rest of the copied book, including its URLs, ids and tags, goes
    # through the same replacements as every recorded row.
    return copy.deepcopy(book)


def _ensure_count(document: dict) -> None:
    document["count"] = sum(len(book.get("highlights") or []) for book in document["results"])


def _personal_strings(document: dict) -> set[str]:
    values: set[str] = set()

    def walk(key: str | None, value: object) -> None:
        if isinstance(value, dict):
            for name, child in value.items():
                walk(name, child)
        elif isinstance(value, list):
            for child in value:
                walk(key, child)
        elif isinstance(value, str) and key in PERSONAL_STRING_KEYS:
            stripped = value.strip()
            # Short values (yes/no labels, a category) are not identifying on
            # their own and would make the leak check noisy.
            if len(stripped) >= 4:
                values.add(stripped)

    walk(None, document)
    return values


def _string_values(document: object) -> list[str]:
    values: list[str] = []

    def walk(value: object) -> None:
        if isinstance(value, dict):
            for child in value.values():
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)
        elif isinstance(value, str):
            values.append(value)

    walk(document)
    return values


def _assert_no_leak(raw: dict, anonymized: dict) -> None:
    strings = _string_values(anonymized)
    remaining = [
        value
        for value in _personal_strings(raw)
        if any(value in candidate for candidate in strings)
    ]
    if remaining:
        # Never print the values themselves: the point is that they are private.
        raise AnonymizeError(
            f"{len(remaining)} personal value(s) from the recording survived anonymization."
        )


def anonymize_export(
    document: object,
    *,
    max_books: int = DEFAULT_MAX_BOOKS,
    max_highlights: int = DEFAULT_MAX_HIGHLIGHTS,
    add_missing_skip_kinds: tuple[str, ...] = (),
) -> dict:
    """Return an anonymized, trimmed, commit-safe copy of one export page."""
    if max_books < 1 or max_highlights < 0:
        raise AnonymizeError("max_books must be at least 1 and max_highlights at least 0.")
    source = _require_export(copy.deepcopy(document))
    anonymizer = Anonymizer()

    source_categories = {str(result.get("category") or "") for result in source["results"]}
    requested_kinds = tuple(
        SKIP_KIND_CATEGORIES.get(kind, kind) for kind in add_missing_skip_kinds
    )
    missing_kinds = tuple(
        kind for kind in requested_kinds if kind not in source_categories
    )
    real_books = _select_books(source["results"], max(1, max_books - len(missing_kinds)))
    _trim_highlights(real_books, max(0, max_highlights - len(missing_kinds)))

    template_book, template_highlight = _template_book(real_books or source["results"])
    present = {str(book.get("category") or "") for book in real_books}
    books = list(real_books)
    for kind in missing_kinds:
        if kind in present:
            continue
        books.append(_synthetic(kind, template_book, template_highlight))
        present.add(kind)

    anonymized_books = [anonymizer.value(None, book) for book in books]
    document = {
        "count": source.get("count"),
        "nextPageCursor": _anonymize_cursor(source.get("nextPageCursor"), anonymizer),
        "results": anonymized_books,
    }
    _ensure_count(document)
    _assert_no_leak(source, document)
    return document


def _anonymize_cursor(cursor: object, anonymizer: Anonymizer) -> object:
    if cursor is None:
        return None
    if isinstance(cursor, str):
        return "synthetic-cursor"
    if isinstance(cursor, int) and not isinstance(cursor, bool):
        return 9_000_000_001
    raise AnonymizeError("The export's nextPageCursor is neither null, a string nor a number.")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="recorded export JSON")
    parser.add_argument("-o", "--output", type=Path, required=True, help="anonymized fixture path")
    parser.add_argument("--max-books", type=int, default=DEFAULT_MAX_BOOKS)
    parser.add_argument("--max-highlights", type=int, default=DEFAULT_MAX_HIGHLIGHTS)
    parser.add_argument(
        "--add-missing-skip-kinds",
        default="",
        help="comma-separated tweet,podcast entries to add when the account has none",
    )
    args = parser.parse_args(argv)

    try:
        document = json.loads(args.input.read_text(encoding="utf-8"))
        anonymized = anonymize_export(
            document,
            max_books=args.max_books,
            max_highlights=args.max_highlights,
            add_missing_skip_kinds=tuple(
                kind.strip() for kind in args.add_missing_skip_kinds.split(",") if kind.strip()
            ),
        )
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(
            json.dumps(anonymized, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
    except (AnonymizeError, OSError, json.JSONDecodeError) as error:
        print(f"anonymize_readwise_export: {error}", file=sys.stderr)
        return 1

    books = anonymized["results"]
    print(
        "anonymize_readwise_export: wrote "
        f"{len(books)} book(s), {anonymized['count']} highlight(s); "
        f"categories: {', '.join(sorted({str(book.get('category')) for book in books}))}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
