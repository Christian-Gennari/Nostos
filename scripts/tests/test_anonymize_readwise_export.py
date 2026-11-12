"""Exercise the Readwise API fixture anonymizer (scripts/anonymize_readwise_export.py)."""

from __future__ import annotations

import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "anonymize_readwise_export.py"
SPEC = importlib.util.spec_from_file_location("anonymize_readwise_export", SCRIPT)
assert SPEC and SPEC.loader
anonymizer_module = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = anonymizer_module
SPEC.loader.exec_module(anonymizer_module)

PRIVATE_TITLE = "A Private Book Title"
PRIVATE_AUTHOR = "A Private Author"
PRIVATE_TEXT = "A private sentence from the reader's book."
PRIVATE_NOTE = "A private note about the sentence."
PRIVATE_TAG = "private-tag"
PRIVATE_URL = "https://private.example/book/42"
PRIVATE_EXTERNAL = "private-external-id"


def book(
    index: int,
    category: str,
    *,
    title: str = PRIVATE_TITLE,
    author: str = PRIVATE_AUTHOR,
    include_amazon: bool = False,
) -> dict:
    return {
        "user_book_id": 100 + index,
        "is_deleted": False,
        "title": title,
        "author": author,
        "readable_title": title,
        "source": "reader",
        "cover_image_url": PRIVATE_URL,
        "unique_url": PRIVATE_URL,
        "summary": None,
        "book_tags": [{"id": 7 + index, "name": PRIVATE_TAG}],
        "category": category,
        "document_note": None,
        "readwise_url": PRIVATE_URL,
        "source_url": PRIVATE_URL,
        "external_id": PRIVATE_EXTERNAL,
        "asin": f"B00PRIVATE{index}" if include_amazon else None,
        "highlights": [
            {
                "id": 500 + index * 10 + highlight,
                "is_deleted": False,
                "text": f"{PRIVATE_TEXT} {highlight}",
                "location": 10 + highlight,
                "location_type": "page" if category != "podcasts" else "time_offset",
                "note": PRIVATE_NOTE if highlight == 0 else "",
                "color": "yellow",
                "highlighted_at": f"2025-02-0{highlight + 1}T10:00:00.000Z",
                "created_at": f"2025-02-0{highlight + 1}T10:00:00.000Z",
                "updated_at": f"2025-02-0{highlight + 1}T10:00:00.000Z",
                "external_id": None,
                "end_location": None,
                "url": None,
                "book_id": 100 + index,
                "tags": [{"id": 7 + index, "name": PRIVATE_TAG}],
                "is_favorite": False,
                "is_discard": False,
                "readwise_url": PRIVATE_URL,
            }
            for highlight in range(3)
        ],
    }


def export(*books: dict, cursor: object = None) -> dict:
    return {
        "count": sum(len(item["highlights"]) for item in books),
        "nextPageCursor": cursor,
        "results": list(books),
    }


def string_values(document: object) -> list[str]:
    values: list[str] = []

    def walk(node: object) -> None:
        if isinstance(node, dict):
            for child in node.values():
                walk(child)
        elif isinstance(node, list):
            for child in node:
                walk(child)
        elif isinstance(node, str):
            values.append(node)

    walk(document)
    return values


class AnonymizeReadwiseExportTests(unittest.TestCase):
    def test_every_personal_value_is_replaced_and_the_shape_is_kept(self):
        raw = export(
            book(1, "books", include_amazon=True),
            book(2, "articles", title="A Private Article", author="Another Private Author"),
        )

        result = anonymizer_module.anonymize_export(raw, max_books=5, max_highlights=15)

        text = "\n".join(string_values(result))
        for private in (
            PRIVATE_TITLE,
            PRIVATE_AUTHOR,
            "A Private Article",
            "Another Private Author",
            PRIVATE_TEXT,
            PRIVATE_NOTE,
            PRIVATE_TAG,
            PRIVATE_URL,
            PRIVATE_EXTERNAL,
            "B00PRIVATE1",
        ):
            self.assertNotIn(private, text)

        self.assertEqual(len(result["results"]), 2)
        self.assertNotEqual(result["results"][0]["title"], raw["results"][0]["title"])
        self.assertEqual(
            set(result["results"][0]),
            set(raw["results"][0]),
            "every book field name and nesting level must survive",
        )
        self.assertEqual(
            set(result["results"][0]["highlights"][0]),
            set(raw["results"][0]["highlights"][0]),
        )

        # Null-ness and the empty-string distinction survive.
        self.assertIsNone(result["results"][0]["summary"])
        self.assertIsNone(result["results"][0]["document_note"])
        self.assertEqual(result["results"][0]["highlights"][1]["note"], "")
        self.assertEqual(result["results"][0]["highlights"][0]["color"], "yellow")
        self.assertEqual(result["results"][0]["category"], "books")
        self.assertIsInstance(result["results"][0]["highlights"][0]["location"], int)

        # The Amazon id and the highlight's book id still point at the row.
        self.assertEqual(result["results"][0]["asin"], "B00SYNTH01")
        self.assertEqual(
            result["results"][0]["highlights"][0]["book_id"],
            result["results"][0]["user_book_id"],
        )

    def test_the_recording_is_trimmed_to_the_requested_bounds(self):
        raw = export(*[book(index, "books") for index in range(1, 9)])

        result = anonymizer_module.anonymize_export(raw, max_books=5, max_highlights=6)

        self.assertLessEqual(len(result["results"]), 5)
        self.assertLessEqual(sum(len(item["highlights"]) for item in result["results"]), 6)
        self.assertEqual(
            result["count"],
            sum(len(item["highlights"]) for item in result["results"]),
        )

    def test_a_category_the_account_lacks_can_be_added_from_the_documented_shape(self):
        raw = export(book(1, "books"))

        result = anonymizer_module.anonymize_export(
            raw,
            max_books=5,
            max_highlights=15,
            add_missing_skip_kinds=("tweet", "podcast"),
        )

        categories = [item["category"] for item in result["results"]]
        self.assertIn("tweets", categories)
        self.assertIn("podcasts", categories)
        self.assertEqual(len(result["results"]), 3)
        for item in result["results"]:
            self.assertEqual(set(item), set(raw["results"][0]))
            self.assertEqual(set(item["highlights"][0]), set(raw["results"][0]["highlights"][0]))
        tweet = next(item for item in result["results"] if item["category"] == "tweets")
        podcast = next(item for item in result["results"] if item["category"] == "podcasts")
        self.assertEqual(tweet["highlights"][0]["location_type"], "order")
        self.assertEqual(podcast["highlights"][0]["location_type"], "time_offset")
        self.assertNotEqual(tweet["user_book_id"], podcast["user_book_id"])

    def test_an_existing_category_is_not_duplicated(self):
        raw = export(book(1, "books"), book(2, "tweets"))

        result = anonymizer_module.anonymize_export(
            raw,
            max_books=5,
            max_highlights=15,
            add_missing_skip_kinds=("tweet",),
        )

        self.assertEqual(
            [item["category"] for item in result["results"]].count("tweets"),
            1,
        )

    def test_the_cursor_keeps_its_type(self):
        for cursor, expected_type in ((None, type(None)), (12345, int), ("abc", str)):
            result = anonymizer_module.anonymize_export(export(book(1, "books"), cursor=cursor))
            self.assertIsInstance(result["nextPageCursor"], expected_type)

    def test_an_object_that_is_not_an_export_is_refused(self):
        with self.assertRaises(anonymizer_module.AnonymizeError):
            anonymizer_module.anonymize_export({"results": []})

    def test_a_surviving_personal_value_is_refused(self):
        raw = export(book(1, "books"))

        # The raw object still contains the private values, so the leak guard
        # must refuse it. This is the same guard a real run uses before write.
        with self.assertRaises(anonymizer_module.AnonymizeError):
            anonymizer_module._assert_no_leak(raw, copy.deepcopy(raw))

    def test_the_written_file_is_valid_json(self):
        raw = export(book(1, "books"), book(2, "articles", title="An Article"))

        result = anonymizer_module.anonymize_export(raw)

        self.assertEqual(json.loads(json.dumps(result)), result)


if __name__ == "__main__":
    unittest.main()
