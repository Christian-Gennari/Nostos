"""Exercise the SQLite fixture anonymizer (scripts/anonymize_sqlite_export.py)."""

import sqlite3
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "anonymize_sqlite_export.py"


def run_script(*arguments):
    return subprocess.run(
        [sys.executable, str(SCRIPT), *map(str, arguments)],
        capture_output=True,
        text=True,
        check=False,
    )


def tables(connection):
    return [
        row[0]
        for row in connection.execute(
            "SELECT name FROM sqlite_master WHERE type = 'table' "
            "AND name NOT LIKE 'sqlite_%' ORDER BY name"
        )
    ]


def schema(connection):
    return (
        connection.execute(
            "SELECT type, name, tbl_name, sql FROM sqlite_master "
            "WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name"
        ).fetchall(),
        {
            name: connection.execute(f'SELECT COUNT(*) FROM "{name}"').fetchone()[0]
            for name in tables(connection)
        },
        {
            name: [
                tuple(row)
                for row in connection.execute(f'PRAGMA table_info("{name}")')
            ]
            for name in tables(connection)
        },
    )


def text_values(connection):
    for table in tables(connection):
        for row in connection.execute(f'PRAGMA table_info("{table}")'):
            column = row[1]
            for (value,) in connection.execute(
                f'SELECT "{column}" FROM "{table}" WHERE "{column}" IS NOT NULL'
            ):
                if isinstance(value, str):
                    yield table, column, value


def create_kobo_database(path, keep_open=False):
    connection = sqlite3.connect(path)
    connection.executescript(
        """
        CREATE TABLE content (
            ContentID TEXT PRIMARY KEY,
            ContentType TEXT,
            Title TEXT,
            BookTitle TEXT,
            Attribution TEXT,
            Subtitle TEXT,
            Series TEXT,
            Description TEXT,
            ISBN TEXT
        );
        CREATE TABLE Bookmark (
            BookmarkID TEXT PRIMARY KEY,
            VolumeID TEXT,
            ContentID TEXT,
            Text TEXT,
            Annotation TEXT,
            ContextString TEXT,
            ExtraAnnotationData TEXT,
            Hidden BOOLEAN,
            Type TEXT,
            DateCreated TEXT
        );
        CREATE TABLE content_keys (ContentID TEXT, Key TEXT);
        CREATE TABLE Shelf (Name TEXT);
        CREATE TABLE user (
            UserID TEXT,
            UserDisplayName TEXT,
            UserEmail TEXT,
            RefreshToken TEXT,
            AuthToken TEXT,
            Storefront TEXT,
            SyncTokenAppVersion TEXT
        );
        """
    )
    connection.executemany(
        "INSERT INTO content VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [
            (
                "file:///mnt/onboard/Secret Author/The Secret Garden.kepub.epub",
                "6",
                "The Secret Garden",
                "The Secret Garden",
                "Secret Author",
                "A subtitle",
                "A series",
                "A description that names The Secret Garden and Secret Author.",
                "9780000000001",
            ),
            (
                "file:///mnt/onboard/Secret Author/The Secret Garden Copy.kepub.epub",
                "6",
                "The Secret Garden",
                "The Secret Garden",
                "Secret Author",
                None,
                None,
                None,
                "9780000000001",
            ),
            (
                "file:///mnt/onboard/Other Writer/A Second Book.epub",
                "6",
                "A Second Book",
                "A Second Book",
                "Other Writer",
                None,
                None,
                None,
                None,
            ),
        ],
    )
    connection.executemany(
        "INSERT INTO Bookmark VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [
            (
                "bookmark-1",
                "file:///mnt/onboard/Secret Author/The Secret Garden.kepub.epub",
                "/mnt/onboard/Secret Author/The Secret Garden.kepub.epub!OPS!c0_The_Secret_Garden.xhtml",
                "It was a dark and stormy night.",
                "A private note.",
                "the sentence around it",
                None,
                0,
                "highlight",
                "2026-01-01T10:00:00Z",
            ),
            (
                "bookmark-2",
                "file:///mnt/onboard/Secret Author/The Secret Garden.kepub.epub",
                "/mnt/onboard/Secret Author/The Secret Garden.kepub.epub!OPS!c0_The_Secret_Garden.xhtml",
                "A second passage.",
                None,
                None,
                None,
                0,
                "highlight",
                "2026-01-01T10:01:00Z",
            ),
            (
                "bookmark-3",
                "file:///mnt/onboard/Other Writer/A Second Book.epub",
                "/mnt/onboard/Other Writer/A Second Book.epub!OPS!c1_Second_Book.xhtml",
                None,
                "A note with no passage.",
                None,
                None,
                0,
                "highlight",
                "2026-01-01T10:02:00Z",
            ),
        ],
    )
    connection.execute(
        "INSERT INTO content_keys VALUES (?, ?)",
        (
            "file:///mnt/onboard/Secret Author/The Secret Garden.kepub.epub!OPS!c0_The_Secret_Garden.xhtml",
            "secret-key",
        ),
    )
    connection.execute("INSERT INTO Shelf VALUES ('A private shelf')")
    connection.execute(
        "INSERT INTO user VALUES (?, ?, ?, ?, ?, ?, ?)",
        (
            "user-1",
            "A Reader",
            "reader@example.com",
            "refresh-token-123",
            "auth-token-456",
            "DE",
            "4.38.23552",
        ),
    )
    connection.commit()
    if keep_open:
        return connection
    connection.close()
    return None


def create_apple_database(path, keep_open=False):
    connection = sqlite3.connect(path)
    connection.executescript(
        """
        CREATE TABLE ZAEANNOTATION (
            Z_PK INTEGER PRIMARY KEY,
            ZANNOTATIONASSETID TEXT,
            ZANNOTATIONSELECTEDTEXT TEXT,
            ZANNOTATIONNOTE TEXT,
            ZANNOTATIONREPRESENTATIVETEXT TEXT,
            ZANNOTATIONCHAPTER TEXT,
            ZFUTUREPROOFING5 TEXT,
            ZANNOTATIONCREATORIDENTIFIER TEXT,
            ZANNOTATIONDELETED INTEGER
        );
        CREATE TABLE ZBKLIBRARYASSET (
            Z_PK INTEGER PRIMARY KEY,
            ZASSETID TEXT,
            ZTITLE TEXT,
            ZAUTHOR TEXT,
            ZSORTTITLE TEXT,
            ZSORTAUTHOR TEXT,
            ZGROUPING TEXT,
            ZBOOKDESCRIPTION TEXT,
            ZPATH TEXT,
            ZCOMMENTS TEXT
        );
        CREATE TABLE Z_METADATA (Z_UUID TEXT, Z_PLIST BLOB);
        """
    )
    connection.executemany(
        "INSERT INTO ZAEANNOTATION VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [
            (
                1,
                "ASSET-1",
                "A private passage.",
                "A private note.",
                "A representative passage.",
                "A chapter that names the book",
                "A chapter that names the book",
                "creator-123",
                0,
            ),
            (2, "ASSET-1", None, "A note with no passage.", None, None, None, "creator-123", 0),
        ],
    )
    connection.executemany(
        "INSERT INTO ZBKLIBRARYASSET VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
        [
            (
                1,
                "ASSET-1",
                "A Secret Apple Book",
                "A Secret Apple Author",
                "Secret Apple Book, A",
                "Secret Apple Author",
                "A Secret Series",
                "This description names A Secret Apple Book and A Secret Apple Author.",
                "/Users/secretuser/Library/Containers/com.apple.BKAgentService/Data/Documents/iBooks/Books/ASSET-1.epub",
                "A private comment.",
            )
        ],
    )
    connection.execute("INSERT INTO Z_METADATA VALUES ('store-uuid', X'00')")
    connection.commit()
    if keep_open:
        return connection
    connection.close()
    return None


class AnonymizerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def test_kobo_titles_authors_highlights_paths_and_account_are_replaced(self):
        source = self.root / "KoboReader.sqlite"
        output = self.root / "anonymized.sqlite"
        create_kobo_database(source)

        result = run_script(source, "-o", output)
        self.assertEqual(result.returncode, 0, result.stderr)

        with sqlite3.connect(source) as before, sqlite3.connect(output) as after:
            self.assertEqual(schema(before), schema(after), "schema and row counts must not change")
            self.assertEqual(
                [row[0] for row in before.execute("SELECT BookmarkID FROM Bookmark ORDER BY BookmarkID")],
                [row[0] for row in after.execute("SELECT BookmarkID FROM Bookmark ORDER BY BookmarkID")],
            )

            titles = [row[0] for row in after.execute("SELECT Title FROM content ORDER BY rowid")]
            self.assertEqual(len(titles), 3)
            self.assertEqual(titles[0], titles[1], "a repeated title keeps one placeholder")
            self.assertNotEqual(titles[0], titles[2])
            self.assertTrue(all(title.startswith("Anonymized Title ") for title in titles))

            content = dict(after.execute("SELECT ContentID, Title FROM content"))
            bookmarks = after.execute("SELECT VolumeID, ContentID FROM Bookmark").fetchall()
            for volume, content_id in bookmarks:
                self.assertIn(volume, content)
                self.assertTrue(content_id.startswith(volume.removeprefix("file://") + "!"))

            key = after.execute("SELECT ContentID FROM content_keys").fetchone()[0]
            bookmark_ids = {
                row[0].removeprefix("file://")
                for row in after.execute("SELECT ContentID FROM Bookmark")
            }
            self.assertIn(key.removeprefix("file://"), bookmark_ids)

            user = after.execute(
                "SELECT UserDisplayName, UserEmail, RefreshToken, AuthToken, Storefront, "
                "SyncTokenAppVersion FROM user"
            ).fetchone()
            self.assertEqual(user, ("redacted", "redacted", "redacted", "redacted", "redacted", "4.38.23552"))

            self.assertEqual(after.execute("SELECT Description FROM content").fetchone()[0], "redacted")
            text = after.execute("SELECT Text FROM Bookmark WHERE BookmarkID='bookmark-1'").fetchone()[0]
            self.assertTrue(text.startswith("Anonymized highlight "), text)
            note = after.execute("SELECT Annotation FROM Bookmark WHERE BookmarkID='bookmark-1'").fetchone()[0]
            self.assertTrue(note.startswith("Anonymized note "), note)

        secrets = {
            "The Secret Garden",
            "Secret Author",
            "A Second Book",
            "Other Writer",
            "It was a dark and stormy night",
            "A private note",
            "reader@example.com",
            "refresh-token-123",
            "auth-token-456",
            "The_Secret_Garden",
            "A private shelf",
        }
        with sqlite3.connect(output) as connection:
            for table, column, value in text_values(connection):
                for secret in secrets:
                    self.assertNotIn(secret, value, f"{table}.{column} still contains {secret!r}")

        self.assertFalse(Path(str(output) + "-wal").exists())
        self.assertFalse(Path(str(output) + "-shm").exists())
        raw = output.read_bytes()
        for secret in ("The Secret Garden", "Secret Author", "reader@example.com"):
            self.assertNotIn(secret.encode(), raw, f"the raw file still contains {secret!r}")

    def test_apple_books_titles_authors_annotations_and_creator_are_replaced(self):
        source = self.root / "AEAnnotation.sqlite"
        output = self.root / "anonymized.sqlite"
        create_apple_database(source)

        result = run_script(source, "-o", output)
        self.assertEqual(result.returncode, 0, result.stderr)

        with sqlite3.connect(source) as before, sqlite3.connect(output) as after:
            self.assertEqual(schema(before), schema(after))

            annotation = after.execute(
                "SELECT Z_PK, ZANNOTATIONASSETID, ZANNOTATIONSELECTEDTEXT, ZANNOTATIONNOTE, "
                "ZANNOTATIONREPRESENTATIVETEXT, ZANNOTATIONCHAPTER, ZFUTUREPROOFING5, "
                "ZANNOTATIONCREATORIDENTIFIER FROM ZAEANNOTATION WHERE Z_PK = 1"
            ).fetchone()
            self.assertEqual(annotation[0], 1)
            self.assertEqual(annotation[1], "ASSET-1")
            self.assertTrue(annotation[2].startswith("Anonymized highlight "), annotation[2])
            self.assertTrue(annotation[3].startswith("Anonymized note "), annotation[3])
            self.assertTrue(annotation[4].startswith("Anonymized highlight "), annotation[4])
            self.assertTrue(annotation[5].startswith("Anonymized Title "), annotation[5])
            self.assertEqual(annotation[5], annotation[6])
            self.assertEqual(annotation[7], "redacted")

            book = after.execute(
                "SELECT Z_PK, ZASSETID, ZTITLE, ZAUTHOR, ZSORTTITLE, ZSORTAUTHOR, ZGROUPING, "
                "ZBOOKDESCRIPTION, ZPATH, ZCOMMENTS FROM ZBKLIBRARYASSET"
            ).fetchone()
            self.assertEqual(book[0], 1)
            self.assertEqual(book[1], "ASSET-1")
            self.assertTrue(book[2].startswith("Anonymized Title "))
            self.assertTrue(book[3].startswith("Anonymized Author "))
            self.assertTrue(book[4].startswith("Anonymized Title "))
            self.assertTrue(book[5].startswith("Anonymized Author "))
            self.assertTrue(book[6].startswith("Anonymized Title "))
            self.assertEqual(book[7], "redacted")
            self.assertTrue(book[8].startswith("/Users/reader/Library/Containers/"))
            self.assertEqual(book[9], "redacted")

            self.assertEqual(after.execute("SELECT Z_UUID FROM Z_METADATA").fetchone()[0], "store-uuid")

        secrets = {
            "A private passage",
            "A private note",
            "A representative passage",
            "A chapter that names the book",
            "creator-123",
            "A Secret Apple Book",
            "A Secret Apple Author",
            "A Secret Series",
            "A private comment",
            "/Users/secretuser",
        }
        with sqlite3.connect(output) as connection:
            for table, column, value in text_values(connection):
                for secret in secrets:
                    self.assertNotIn(secret, value, f"{table}.{column} still contains {secret!r}")

        raw = output.read_bytes()
        for secret in ("A Secret Apple Book", "A Secret Apple Author", "creator-123"):
            self.assertNotIn(secret.encode(), raw, f"the raw file still contains {secret!r}")

    def test_unknown_database_is_refused_and_no_output_is_left(self):
        source = self.root / "notes.sqlite"
        output = self.root / "anonymized.sqlite"
        with sqlite3.connect(source) as connection:
            connection.execute("CREATE TABLE notes (id INTEGER, body TEXT)")
            connection.execute("INSERT INTO notes VALUES (1, 'a private note')")

        result = run_script(source, "-o", output)

        self.assertEqual(result.returncode, 1)
        self.assertIn("not a Kobo or Apple Books database", result.stderr)
        self.assertFalse(output.exists())

    def test_a_bookmark_table_without_known_text_is_refused(self):
        source = self.root / "KoboReader.sqlite"
        output = self.root / "anonymized.sqlite"
        with sqlite3.connect(source) as connection:
            connection.execute("CREATE TABLE Bookmark (BookmarkID TEXT, VolumeID TEXT)")
            connection.execute("INSERT INTO Bookmark VALUES ('b', 'v')")

        result = run_script(source, "-o", output)

        self.assertEqual(result.returncode, 1)
        self.assertIn("no known title, author, highlight or note column", result.stderr)
        self.assertFalse(output.exists())

    def test_existing_output_is_not_replaced_without_force(self):
        source = self.root / "KoboReader.sqlite"
        output = self.root / "anonymized.sqlite"
        create_kobo_database(source)
        output.write_text("keep me")

        result = run_script(source, "-o", output)

        self.assertEqual(result.returncode, 2)
        self.assertIn("already exists", result.stderr)
        self.assertEqual(output.read_text(), "keep me")

    def test_in_place_rewrites_the_database(self):
        source = self.root / "KoboReader.sqlite"
        create_kobo_database(source)

        result = run_script(source, "--in-place")

        self.assertEqual(result.returncode, 0, result.stderr)
        with sqlite3.connect(source) as connection:
            self.assertTrue(
                connection.execute("SELECT Title FROM content LIMIT 1").fetchone()[0].startswith(
                    "Anonymized Title "
                )
            )

    def test_a_pending_wal_file_is_folded_into_the_output(self):
        source = self.root / "KoboReader.sqlite"
        output = self.root / "anonymized.sqlite"
        connection = create_kobo_database(source, keep_open=True)
        connection.execute("PRAGMA journal_mode=WAL")
        connection.execute("PRAGMA wal_autocheckpoint=0")
        connection.execute("UPDATE Shelf SET Name = 'A private shelf'")
        connection.commit()
        self.assertTrue(Path(str(source) + "-wal").exists())

        try:
            result = run_script(source, "-o", output)
        finally:
            connection.close()

        self.assertEqual(result.returncode, 0, result.stderr)
        with sqlite3.connect(output) as after:
            self.assertEqual(after.execute("SELECT COUNT(*) FROM Bookmark").fetchone()[0], 3)
            self.assertFalse(Path(str(output) + "-wal").exists())
            self.assertFalse(Path(str(output) + "-shm").exists())


if __name__ == "__main__":
    unittest.main()
