# Kobo database corpus

## Files and provenance

| Version | File | Where it came from |
| --- | --- | --- |
| `fw-4.38.23552` | `KoboReader.sqlite` | [JuliBD/Kobo2Anki](https://github.com/JuliBD/Kobo2Anki), [`example/example-base-folder/.kobo/KoboReader.sqlite`](https://github.com/JuliBD/Kobo2Anki/blob/main/example/example-base-folder/.kobo/KoboReader.sqlite). Repository licence: MIT. |

Firmware **4.38.23552** is the `user.SyncTokenAppVersion` value in the file;
`DbVersion.version` is **174**. The upstream example had already been trimmed
to its five sample books by
[`sanitize-example-kobo-db.py`](https://github.com/JuliBD/Kobo2Anki/blob/main/sanitize-example-kobo-db.py),
but the tables, columns and types are those of a real firmware 4.38.23552
database. One value is not: the sanitizer rewrote `content.ContentType` to
`"book"` instead of Kobo's `6`, so the reader takes the title and author from
the (anonymized) sideload path rather than from `content`. That is still a real
schema; the fallback is covered by the in-code Kobo tests as well.

The upstream licence and copyright notice ship next to the file in `fw-4.38.23552/LICENSE`, as the licence requires.

## How it was anonymized

The upstream example still contained an account email, device id and live
tokens in its `user` table. The raw file was never committed. It was processed
with:

```bash
python3 scripts/anonymize_sqlite_export.py KoboReader.sqlite -o fixture/KoboReader.sqlite
```

The script:

- replaced `content.Title`, `content.BookTitle`/`Subtitle`/`Series` and
  `content.Attribution` with “Anonymized Title N” / “Anonymized Author N”;
- replaced `Bookmark.Text` and `Bookmark.Annotation` with “Anonymized
  highlight N.” / “Anonymized note N.”;
- rewrote `content.ContentID`, `Bookmark.VolumeID` and `Bookmark.ContentID`
  consistently, including the `!OPS!…` chapter fragment that contained the
  book title, so joins and the row count are unchanged;
- redacted `content.Description`, `content.___UserID`, `Bookmark.UserID` and
  every `user` column except `SyncTokenAppVersion` (kept as version evidence);
- left every table, column, declared type and row count in place, ran
  `VACUUM` so the replaced text is not recoverable from free pages, and
  checked `PRAGMA integrity_check` before writing.

`BookmarkID` and the relationships between content and bookmark rows are
preserved; all other non-text values are untouched. The only “ids” that change
are the path-shaped Kobo ids (the content id, the volume id and the bookmark's
chapter content id), which are rewritten to the same fake path everywhere they
appear because the original path is the title.

## Expected counts

| Found books | Imported annotations | Skipped by reason | Missing optional columns |
| ---: | ---: | --- | --- |
| 5 | 45 | none | none |

The file has 45 bookmarks in five books, all plain highlights. The upstream
sanitizer left no notes, hidden rows or dog-ears, so the in-code Kobo builders
still cover those edge cases.

## Adding a new Kobo version

1. Ask the owner for `.kobo/KoboReader.sqlite` from the device that failed.
2. Note the firmware from **Settings → Device information** and the
   `DbVersion.version` value.
3. `mkdir Fixtures/kobo/fw-<firmware>`, copy the raw file to a private temp
   folder, run `scripts/anonymize_sqlite_export.py raw.sqlite -o Fixtures/kobo/fw-<firmware>/KoboReader.sqlite`,
   and inspect it.
4. Add `manifest.json` with the counts the test should see.
5. Add a row to the table above with the source URL, licence and how it was
   anonymized. Never commit the raw file.
