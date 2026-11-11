#!/usr/bin/env python3
"""Build the Calibre library fixture used by the import tests (issue #1527).

The result is a genuine Calibre library folder: a metadata.db created from
Calibre's own schema (`calibre_schema.sql`, copied from Calibre's
`resources/metadata_sqlite.sql`, GPL-3.0-or-later) and the book folders Calibre
would write. The books are public-domain titles; their files are tiny generated
EPUB/PDF/MP3 wrappers around a short public-domain line, and the covers are
solid-colour images. None of this is a copy of anyone's real library.

`calibredb` was not installed on the machine the fixture was built on, so the
database is created by this script with Python's sqlite3, exactly following
Calibre's schema. If `calibredb` is available, `--with-calibredb` asks it to
rebuild the library from the generated files instead; the metadata is then
created by Calibre itself.

The fixture is deterministic: fixed timestamps, fixed UUIDs, fixed ZIP member
dates. Run it with `--force` to rebuild the committed `library/` folder, or with
`--synthetic 300 --out /tmp/calibre-300` to build a larger library for a
measurement run.

Usage:
    python3 build_fixture.py [--out DIR] [--force] [--synthetic N]

Requires only Python 3.11+ and the standard library.
"""

from __future__ import annotations

import argparse
import base64
import datetime as dt
import html
import itertools
import shutil
import sqlite3
import uuid
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCHEMA = HERE / "calibre_schema.sql"

# A 0.6 s silent MP3, generated once with:
#   ffmpeg -f lavfi -i anullsrc=r=8000:cl=mono -t 0.6 -codec:a libmp3lame -b:a 8k
# It is only here to exercise the audio path; it is not a real recording.
SILENT_MP3 = base64.b64decode(
    "SUQzBAAAAAAAI1RTU0UAAAAPAAADTGF2ZjYwLjE2LjEwMAAAAAAAAAAAAAAA/+M4wAAAAAAAAAAAAEluZm8AAAAPAAAACwAAA/AASUlJSUlJSUlJW1tbW1tbW1tbbW1tbW1tbW1tgICAgICAgICAkpKSkpKSkpKSpKSkpKSkpKSktra2tra2tra2ycnJycnJycnJ29vb29vb29vb7e3t7e3t7e3t////////////AAAAAExhdmM2MC4zMQAAAAAAAAAAAAAAACQDwAAAAAAAAAPw41CmJgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/+MYxAAAAANIAAAAAExBTUUzLjEwMFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVV/+MYxDsAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVV/+MYxHYAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVV/+MYxLEAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVV/+MYxMQAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVV/+MYxMQAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVV/+MYxMQAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVMQU1FMy4xMDBVVVVVVVVVVVVV/+MYxMQAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV/+MYxMQAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV/+MYxMQAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV/+MYxMQAAANIAAAAAFVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV"
)

# A 4x6 solid-colour JPEG, generated once with Pillow. Not cover art.
COVER_JPEG = base64.b64decode(
    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAA0JCgsKCA0LCgsODg0PEyAVExISEyccHhcgLikxMC4pLSwzOko+MzZGNywtQFdBRkxOUlNSMj5aYVpQYEpRUk//2wBDAQ4ODhMREyYVFSZPNS01T09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0//wAARCAAGAAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDlaKKKRzn/2Q=="
)

FIXED_TIME = "2020-01-01 00:00:00+00:00"
FIXED_DATE = "2020-01-01"

# Public-domain titles. The text is one short public-domain sentence, not the
# whole work; the fixture is about metadata and files, not reading.
BOOKS = [
    {
        "id": 1,
        "title": "Pride and Prejudice",
        "authors": ["Jane Austen"],
        "isbn": "9780141439518",
        "series": None,
        "series_index": 1.0,
        "publisher": "T. Egerton",
        "pubdate": "1813-01-28",
        "rating": 10,
        "tags": ["classics", "romance"],
        "comments": "<p>Elizabeth Bennet and Mr Darcy, in <b>the</b> drawing room.</p><p>Second paragraph.</p>",
        "formats": ["EPUB", "PDF"],
        "cover": True,
        "read": True,
        "date_read": "2020-05-01",
        "text": "It is a truth universally acknowledged, that a single man in possession of a good fortune must be in want of a wife.",
    },
    {
        "id": 2,
        "title": "Alice's Adventures in Wonderland",
        "authors": ["Lewis Carroll"],
        "isbn": "9780141439761",
        "series": "Children's Classics",
        "series_index": 1.0,
        "publisher": "Macmillan",
        "pubdate": "1865-11-26",
        "rating": 8,
        "tags": ["classics", "fantasy"],
        "comments": "<p>Down the rabbit hole.</p>",
        # MOBI is deliberately unsupported: the review should list it, not hide it.
        "formats": ["EPUB", "MOBI"],
        "cover": True,
        "read": True,
        # A read flag without a date: Nostos cannot infer the finish date.
        "date_read": None,
        "text": "Alice was beginning to get very tired of sitting by her sister on the bank.",
    },
    {
        "id": 3,
        "title": "The Time Machine",
        "authors": ["H. G. Wells"],
        "isbn": "9780451528551",
        "series": None,
        "series_index": 1.0,
        "publisher": "William Heinemann",
        "pubdate": "1895-05-07",
        "rating": 0,
        "tags": ["science fiction"],
        "comments": None,
        "formats": ["EPUB"],
        "cover": False,
        "read": False,
        "date_read": None,
        "text": "The Time Traveller (for so it will be convenient to speak of him) was expounding a recondite matter.",
    },
    {
        "id": 4,
        "title": "The Gilded Age",
        "authors": ["Mark Twain", "Charles Dudley Warner"],
        "isbn": "9780199537181",
        "series": "The Gilded Age",
        "series_index": 2.5,
        "publisher": "American Publishing Company",
        "pubdate": "1873-12-01",
        "rating": 6,
        "tags": ["satire"],
        "comments": "<p>A novel of <i>the</i> post-war boom.</p>",
        "formats": ["EPUB", "MP3"],
        "cover": True,
        "read": True,
        "date_read": "2021-01-02",
        "text": "It was a time of great expectations and greater speculation.",
    },
    {
        "id": 5,
        "title": "The Art of War",
        "authors": ["Sun Tzu"],
        "isbn": None,
        "series": None,
        "series_index": 1.0,
        "publisher": None,
        "pubdate": "1910-01-01",
        "rating": 0,
        "tags": ["strategy"],
        "comments": None,
        # Only formats Nostos cannot read.
        "formats": ["MOBI", "AZW3"],
        "cover": False,
        "read": False,
        "date_read": None,
        "text": "The art of war is of vital importance to the State.",
    },
    {
        "id": 6,
        "title": "The Yellow Wallpaper",
        "authors": ["Charlotte Perkins Gilman"],
        "isbn": None,
        "series": None,
        "series_index": 1.0,
        "publisher": "The New England Magazine",
        "pubdate": "1892-01-01",
        "rating": 0,
        "tags": ["classics"],
        "comments": None,
        "formats": ["EPUB"],
        # Deliberately not written: Calibre says it is there, the folder does not.
        "missing": True,
        "cover": False,
        "read": False,
        "date_read": None,
        "text": "It is very seldom that mere ordinary people like John and myself secure ancestral halls.",
    },
    {
        "id": 7,
        "title": "The Adventure of the Blue Carbuncle",
        "authors": ["Arthur Conan Doyle"],
        "isbn": None,
        "series": "Sherlock Holmes",
        "series_index": 7.0,
        "publisher": "The Strand Magazine",
        "pubdate": "1892-01-01",
        "rating": 0,
        "tags": [],
        "comments": None,
        "formats": ["EPUB"],
        "cover": False,
        "read": False,
        "date_read": None,
        "text": "I had called upon my friend Sherlock Holmes upon the second morning after Christmas.",
    },
    {
        "id": 8,
        "title": "The King in Yellow",
        "authors": ["Robert W. Chambers"],
        "isbn": "9781497404304",
        "series": None,
        "series_index": 1.0,
        "publisher": "F. Tennyson Neely",
        "pubdate": "1895-01-01",
        "rating": 10,
        "tags": ["weird fiction", "horror"],
        "comments": "<p>Stories of a forbidden play.</p>",
        "formats": ["EPUB", "PDF"],
        "cover": True,
        "read": False,
        "date_read": None,
        "text": "Along the shore the cloud waves break, the twin suns sink behind the lake.",
    },
]

CUSTOM_COLUMNS = [
    (1, "read", "Read", "bool"),
    (2, "date_read", "Date read", "datetime"),
    (3, "genre", "Genre", "text"),
    (4, "pages", "Pages", "int"),
]

CUSTOM_VALUES = {
    3: {8: "Weird fiction"},
    4: {3: 224},
}


def title_sort(value: str) -> str:
    for prefix in ("The ", "A ", "An "):
        if value.startswith(prefix):
            return f"{value[len(prefix):]}, {prefix.strip()}"
    return value


def write_epub(path: Path, title: str, author: str, text: str) -> None:
    container = """<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
"""
    opf = f"""<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid">urn:uuid:{uuid.uuid5(uuid.NAMESPACE_URL, title)}</dc:identifier>
    <dc:title>{html.escape(title)}</dc:title>
    <dc:creator>{html.escape(author)}</dc:creator>
    <dc:language>en</dc:language>
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/>
  </manifest>
  <spine>
    <itemref idref="chapter"/>
  </spine>
</package>
"""
    nav = f"""<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
  <head><title>{html.escape(title)}</title></head>
  <body><nav epub:type="toc"><ol><li><a href="chapter.xhtml">Chapter</a></li></ol></nav></body>
</html>
"""
    chapter = f"""<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
  <head><title>{html.escape(title)}</title></head>
  <body><h1>{html.escape(title)}</h1><p>{html.escape(text)}</p></body>
</html>
"""

    def entry(name: str, data: str, compress: int) -> tuple[zipfile.ZipInfo, bytes]:
        info = zipfile.ZipInfo(name, date_time=(2020, 1, 1, 0, 0, 0))
        info.compress_type = compress
        info.external_attr = 0o644 << 16
        return info, data.encode("utf-8")

    with zipfile.ZipFile(path, "w") as archive:
        info = zipfile.ZipInfo("mimetype", date_time=(2020, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_STORED
        archive.writestr(info, b"application/epub+zip")
        for name, data in (
            ("META-INF/container.xml", container),
            ("OEBPS/content.opf", opf),
            ("OEBPS/nav.xhtml", nav),
            ("OEBPS/chapter.xhtml", chapter),
        ):
            info, payload = entry(name, data, zipfile.ZIP_DEFLATED)
            archive.writestr(info, payload)


def write_pdf(path: Path, title: str, text: str) -> None:
    objects = [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R "
        "/Resources << /Font << /F1 5 0 R >> >> >>",
        None,  # content stream, built below
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        f"<< /Title ({title.replace('(', '').replace(')', '')}) /Producer (Nostos fixture builder) >>",
    ]
    content = f"BT /F1 11 Tf 20 150 Td ({text.replace('(', '').replace(')', '')}) Tj ET".encode("latin-1", "replace")
    objects[3] = f"<< /Length {len(content)} >>\nstream\n" + content.decode("latin-1") + "\nendstream"

    out = bytearray(b"%PDF-1.4\n")
    offsets = [0]
    for number, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += f"{number} 0 obj\n{body}\nendobj\n".encode("latin-1")
    xref = len(out)
    out += f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode("latin-1")
    for offset in offsets[1:]:
        out += f"{offset:010d} 00000 n \n".encode("latin-1")
    out += (
        f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R /Info {len(objects)} 0 R >>\n"
        f"startxref\n{xref}\n%%EOF\n"
    ).encode("latin-1")
    path.write_bytes(bytes(out))


def file_name(title: str, author: str, extension: str) -> str:
    return f"{title} - {author}.{extension.lower()}"


def write_book_files(library: Path, book: dict) -> list[tuple[str, str, int]]:
    """Writes the book's files and returns (format, file name, bytes)."""
    author = " & ".join(book["authors"])
    folder = library / book_path(book)
    written: list[tuple[str, str, int]] = []
    if book.get("missing"):
        return [(book["formats"][0], file_name(book["title"], author, book["formats"][0]), 0)]

    folder.mkdir(parents=True, exist_ok=True)
    for fmt in book["formats"]:
        name = file_name(book["title"], author, fmt)
        target = folder / name
        if fmt == "EPUB":
            write_epub(target, book["title"], author, book["text"])
        elif fmt == "PDF":
            write_pdf(target, book["title"], book["text"])
        elif fmt == "MP3":
            target.write_bytes(SILENT_MP3)
        else:
            # MOBI/AZW3: bytes do not matter; Nostos never opens them. Keep them
            # tiny and recognisably synthetic.
            target.write_bytes(b"SYNTHETIC CALIBRE FIXTURE " + fmt.encode() + b"\n")
        written.append((fmt, name, target.stat().st_size))

    if book.get("cover"):
        (folder / "cover.jpg").write_bytes(COVER_JPEG)
    return written


def book_path(book: dict) -> str:
    author = book["authors"][0].replace("/", "_")
    title = book["title"].replace("/", "_")
    return f"{author}/{title} ({book['id']})"


def create_database(library: Path, books: list[dict]) -> None:
    database = library / "metadata.db"
    if database.exists():
        database.unlink()

    connection = sqlite3.connect(database)
    counter = itertools.count(1)

    def deterministic_uuid() -> str:
        return str(uuid.UUID(int=next(counter)))

    connection.create_function("title_sort", 1, title_sort)
    connection.create_function("sortconcat", 2, lambda link_id, value: value)
    connection.create_function("concat", 1, lambda value: value)
    connection.create_function("books_list_filter", 1, lambda book_id: 1)
    connection.create_function("uuid4", 0, deterministic_uuid)

    connection.executescript(SCHEMA.read_text())
    connection.execute(
        "INSERT INTO library_id (id, uuid) VALUES (1, ?)",
        ("2f0c1d8e-3a4b-4c5d-8e9f-0a1b2c3d4e5f",),
    )
    connection.execute(
        "INSERT INTO preferences (id, key, val) VALUES (1, 'library_name', 'Nostos fixture library')"
    )

    authors: dict[str, int] = {}
    series: dict[str, int] = {}
    tags: dict[str, int] = {}
    publishers: dict[str, int] = {}
    ratings: dict[int, int] = {}

    for book in books:
        connection.execute(
            """
            INSERT INTO books
                (id, title, sort, timestamp, pubdate, series_index, author_sort, path, uuid, has_cover, last_modified)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                book["id"],
                book["title"],
                title_sort(book["title"]),
                FIXED_TIME,
                f"{book['pubdate']} 00:00:00+00:00",
                book["series_index"],
                title_sort(book["authors"][0]),
                book_path(book),
                str(uuid.UUID(int=book["id"])),
                1 if book.get("cover") else 0,
                FIXED_TIME,
            ),
        )

        for order, author in enumerate(book["authors"]):
            if author not in authors:
                authors[author] = len(authors) + 1
                connection.execute(
                    "INSERT INTO authors (id, name, sort, link) VALUES (?, ?, ?, '')",
                    (authors[author], author, title_sort(author)),
                )
            connection.execute(
                "INSERT INTO books_authors_link (book, author) VALUES (?, ?)",
                (book["id"], authors[author]),
            )

        if book["series"]:
            if book["series"] not in series:
                series[book["series"]] = len(series) + 1
                connection.execute(
                    "INSERT INTO series (id, name, sort, link) VALUES (?, ?, ?, '')",
                    (series[book["series"]], book["series"], title_sort(book["series"])),
                )
            connection.execute(
                "INSERT INTO books_series_link (book, series) VALUES (?, ?)",
                (book["id"], series[book["series"]]),
            )

        if book["publisher"]:
            if book["publisher"] not in publishers:
                publishers[book["publisher"]] = len(publishers) + 1
                connection.execute(
                    "INSERT INTO publishers (id, name, sort, link) VALUES (?, ?, ?, '')",
                    (publishers[book["publisher"]], book["publisher"], title_sort(book["publisher"])),
                )
            connection.execute(
                "INSERT INTO books_publishers_link (book, publisher) VALUES (?, ?)",
                (book["id"], publishers[book["publisher"]]),
            )

        for tag in book["tags"]:
            if tag not in tags:
                tags[tag] = len(tags) + 1
                connection.execute(
                    "INSERT INTO tags (id, name, link) VALUES (?, ?, '')",
                    (tags[tag], tag),
                )
            connection.execute(
                "INSERT INTO books_tags_link (book, tag) VALUES (?, ?)",
                (book["id"], tags[tag]),
            )

        if book["rating"]:
            if book["rating"] not in ratings:
                ratings[book["rating"]] = len(ratings) + 1
                connection.execute(
                    "INSERT INTO ratings (id, rating, link) VALUES (?, ?, '')",
                    (ratings[book["rating"]], book["rating"]),
                )
            connection.execute(
                "INSERT INTO books_ratings_link (book, rating) VALUES (?, ?)",
                (book["id"], ratings[book["rating"]]),
            )

        if book["isbn"]:
            connection.execute(
                "INSERT INTO identifiers (book, type, val) VALUES (?, 'isbn', ?)",
                (book["id"], book["isbn"]),
            )

        if book["comments"]:
            connection.execute(
                "INSERT INTO comments (book, text) VALUES (?, ?)",
                (book["id"], book["comments"]),
            )

    for column_id, label, name, datatype in CUSTOM_COLUMNS:
        connection.execute(
            """
            INSERT INTO custom_columns (id, label, name, datatype, mark_for_delete, editable, display, is_multiple, normalized)
            VALUES (?, ?, ?, ?, 0, 1, '{}', 0, 1)
            """,
            (column_id, label, name, datatype),
        )
        # Calibre creates one table per custom column when the column is added;
        # its master schema cannot contain them because each is user-defined.
        value_type = {
            "bool": "BOOL",
            "datetime": "TIMESTAMP",
            "int": "INTEGER",
            "float": "REAL",
        }.get(datatype, "TEXT")
        connection.execute(
            f"CREATE TABLE custom_column_{column_id} ("
            f"id INTEGER PRIMARY KEY, book INTEGER NOT NULL, value {value_type} NOT NULL, UNIQUE(book))"
        )
        connection.execute(
            f"CREATE INDEX custom_column_{column_id}_idx ON custom_column_{column_id} (book)"
        )

    for book in books:
        for fmt, name, size in book["_files"]:
            connection.execute(
                "INSERT INTO data (book, format, uncompressed_size, name) VALUES (?, ?, ?, ?)",
                (book["id"], fmt, size, name),
            )

    for table_column, values in CUSTOM_VALUES.items():
        for book_id, value in values.items():
            connection.execute(
                f"INSERT INTO custom_column_{table_column} (book, value) VALUES (?, ?)",
                (book_id, value),
            )

    # The read / date-read columns are common choices; fill them as a Calibre
    # user would have.
    for book in books:
        if book.get("read"):
            connection.execute(
                "INSERT INTO custom_column_1 (book, value) VALUES (?, 1)",
                (book["id"],),
            )
        if book.get("date_read"):
            connection.execute(
                "INSERT INTO custom_column_2 (book, value) VALUES (?, ?)",
                (book["id"], f"{book['date_read']} 00:00:00+00:00"),
            )

    # The books trigger creates a row whose timestamp would otherwise be now.
    connection.execute("UPDATE books_pages_link SET timestamp = ?", (FIXED_TIME,))

    connection.commit()
    # Leave the file as Calibre leaves it: a single self-contained database
    # with no WAL sidecars the reader would have to handle.
    connection.execute("PRAGMA journal_mode = DELETE")
    connection.execute("VACUUM")
    connection.close()


def synthetic_books(count: int) -> list[dict]:
    books = []
    for index in range(1, count + 1):
        title = f"Public Domain Book {index:03d}"
        books.append(
            {
                "id": index,
                "title": title,
                "authors": [f"Fixture Author {index:03d}"],
                "isbn": synthetic_isbn(index),
                "series": f"Fixture Series {index % 10}" if index % 10 == 0 else None,
                "series_index": float(index),
                "publisher": "Fixture Press",
                "pubdate": "1900-01-01",
                "rating": (index % 5) * 2,
                "tags": ["fixture", f"group-{index % 7}"],
                "comments": f"<p>A synthetic public-domain book ({index}) for measurement.</p>",
                "formats": ["EPUB"] if index % 11 else ["EPUB", "PDF"],
                "cover": index % 4 == 0,
                "read": index % 3 == 0,
                "date_read": "2022-02-02" if index % 3 == 0 else None,
                "text": "A synthetic sentence for a measurement library.",
            }
        )
    return books


def synthetic_isbn(index: int) -> str:
    prefix = f"9798{index:08d}"[:12]
    total = sum((1 if position % 2 == 0 else 3) * int(digit) for position, digit in enumerate(prefix))
    return prefix + str((10 - total % 10) % 10)


def build(out: Path, books: list[dict]) -> None:
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    for book in books:
        book["_files"] = write_book_files(out, book)

    create_database(out, books)


class _Parser(argparse.ArgumentParser):
    def error(self, message: str) -> None:  # pragma: no cover - argparse plumbing
        raise SystemExit(f"{self.prog}: {message}")


def main() -> None:
    parser = _Parser(description=__doc__)
    parser.add_argument("--out", type=Path, default=HERE / "library", help="output library folder")
    parser.add_argument("--force", action="store_true", help="replace an existing output folder")
    parser.add_argument("--synthetic", type=int, default=0, help="build N synthetic books instead")
    args = parser.parse_args()

    if args.out.exists() and not args.force:
        raise SystemExit(f"{args.out} already exists; pass --force to replace it")

    books = synthetic_books(args.synthetic) if args.synthetic else [dict(book) for book in BOOKS]
    build(args.out, books)
    print(f"Built {len(books)} books in {args.out}")


if __name__ == "__main__":
    main()
