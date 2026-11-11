# From a user's failing import to a fixture

This is the maintainer runbook for issue #1524. It turns a reported import
failure into a versioned, anonymized fixture that the corpus test runs on every
commit.

The shape log from #1523 is the starting point: it tells you whether the file
changed shape before you ask anyone for their library.

## 1. Read the shape log first

Every failed preview, every Kobo/Apple Books database missing an optional
column, and every preview with unrecognised Kindle entries logs one warning:

```
HighlightImportShape {Source} {Outcome} {Shape}
```

It is event id `1523`, named `HighlightImportShape`. `Source` is `kobo`,
`applebooks`, `kindle`, `readwise`, `koreader` or `unknown`; `Outcome` is
`parse-failed`, `missing-optional-columns` or `unrecognised-entries`; `Shape` is
structure only — table and column names, a CSV header, line and separator
counts with masked format lines, or a Lua sidecar's top-level keys. It never
contains a highlight, title, author, file name, path or user id.

- **Cloud:** search the logs for `HighlightImportShape` and group by `Outcome`
  and `Source`.
- **SelfHosted:** it is in the app's normal warning log.
- The code is `Nostos.Product/Services/Notes/Imports/HighlightImportShapeLog.cs`
  and `ImportFileShape.cs`; the user-facing description is in
  [api-reference.md](../api-reference.md) under **Diagnostics** in the notes
  import section.

Useful shapes look like these (the Kobo and Readwise forms are pinned by
`ImportFileShapeTests`; `<n>` stands for a count):

```
kobo          missing-optional-columns  tables: Bookmark, Shelf, content; Bookmark(BookmarkID, VolumeID, Text, Annotation, Hidden, Type, DateCreated); content(ContentID, ContentType, Title, Attribution, ISBN)
applebooks    missing-optional-columns  tables: ZAEANNOTATION, ZBKLIBRARYASSET, …; ZAEANNOTATION(Z_PK, …, ZANNOTATIONSELECTEDTEXT, …); ZBKLIBRARYASSET(Z_PK, …, ZASSETID, ZTITLE, ZAUTHOR)
readwise      parse-failed             header (11 cells): Highlight, Book Title, Book Author, …
kindle        unrecognised-entries     lines <n>; separators <m>; unrecognised patterns: - Xxxxxxxx xxx Xxxx 12 | Xxxx 34
```

If the shape names a missing column, that is the schema drift the corpus exists
to catch. If it is `parse-failed` with no useful shape, ask for the file.

## 2. Ask for the file (with consent)

Ask the reporter to send the export, not a screenshot:

- **Kobo:** `.kobo/KoboReader.sqlite` from the device.
- **Apple Books:** both
  `~/Library/Containers/com.apple.iBooksX/Data/Documents/AEAnnotation/AEAnnotation_*.sqlite`
  and
  `…/BKLibrary/BKLibrary-*.sqlite`; include `-wal`/`-shm` if present.
- **Kindle:** `documents/My Clippings.txt`.
- **Readwise:** the CSV export.
- **KOReader:** the book's `metadata.*.lua` sidecar.

Say that the file will be anonymized and committed as a test fixture, and that
they can redact it themselves if they prefer. Never commit the raw file.

## 3. Record the version

| Source | Version to record |
| --- | --- |
| Kobo | Settings → Device information firmware; `DbVersion.version` from the file |
| Apple Books | macOS release (`sw_vers -productVersion`); the `AEAnnotation_*`/`BKLibrary-*` filenames |
| Kindle | the year and UI language of the clippings (a `My Clippings.txt` has no firmware version) |
| Readwise | `yyyy-mm` of the export |
| KOReader | the sidecar date plus the shape (a sidecar has no plugin version) |

Use that as the folder name: `kobo/fw-4.38.23552`, `applebooks/macos-15`,
`readwise/2026-10`. When a version is genuinely unknown, say so in the folder
name and README rather than guessing (the current Apple corpus uses
`macos-unknown-2022-09`).

## 4. Anonymize the SQLite file

```bash
python3 scripts/anonymize_sqlite_export.py raw/KoboReader.sqlite \
  -o Nostos.Backend.Tests/Fixtures/kobo/fw-4.38.23552/KoboReader.sqlite
```

The script rewrites titles, authors, highlight and note text, Kobo sideload
paths (including the chapter fragment), and Apple Books chapter names; it
redacts account fields (Kobo `user` values except the firmware version,
annotation creator id, book descriptions and comments) and the macOS account name in `ZPATH`. It keeps every table, column,
declared type and row count, preserves row relationships, runs `VACUUM` so the
replaced text is not left in free pages, and checks `PRAGMA integrity_check`
before writing. A file it does not recognize is refused, not copied.

For text sources (Kindle, Readwise, KOReader) there is no script: replace every
title, author, highlight and note, and keep the layout, separators and keys.

Before committing, check both the live rows and the raw bytes:

```bash
sqlite3 fixture.sqlite "SELECT Title, Attribution FROM content LIMIT 5;"
strings -a fixture.sqlite | grep -i 'some original title' || true
```

The `strings` check matters: an `UPDATE` alone leaves the old text in free
pages, which is why the script vacuums.

## 5. Add the fixture and its counts

1. Put the file in `Nostos.Backend.Tests/Fixtures/<source>/<version>/`.
2. Add `manifest.json` next to it:

   ```json
   {
     "source": "kobo",
     "version": "fw-4.38.23552",
     "file": "KoboReader.sqlite",
     "companions": [],
     "expected": {
       "foundBooks": 5,
       "importedAnnotations": 45,
       "skipped": {},
       "missingOptionalColumns": []
     }
   }
   ```

   `companions` names the Apple Books library database; every other source has
   an empty list.
3. Update the source README (`Fixtures/kobo/README.md`, …) with where the file
   came from, its URL and licence, the app/firmware version, how it was
   anonymized, and the expected counts (found / imported / skipped by reason).

## 6. Run the corpus

```bash
export PATH=/home/dev/.nvm/versions/node/v24.15.0/bin:$PATH
lg run -- dotnet build Nostos.sln -m:2
lg run -- dotnet test Nostos.Backend.Tests --no-build --filter FullyQualifiedName~ImportFixtureCorpusTests
```

`ImportFixtureCorpusTests` discovers every version folder with a
`manifest.json`, runs it through `HighlightImportSourceReader` and compares the
book count, annotation count, skipped reasons and missing optional columns.

If the counts differ, decide whether the reader is wrong or the fixture is
teaching you something new. Fix the reader, or correct the manifest after you
have manually confirmed the new count. Never set the manifest to whatever the
test happens to produce.

The in-code SQLite builders stay for edge cases a real file rarely carries
(hidden rows, markup sentinels, missing optional columns, a pending `-wal`);
the corpus complements them, it does not replace them.

## 7. If the reader changes

- Keep the old version's fixture passing.
- Add a new fixture for the new version rather than editing the old one.
- If the change is a tolerated column that moved, add it to
  `KoboDatabaseReader`/`AppleBooksDatabaseReader` as an optional column and
  record it in `MissingOptionalColumns`; the shape log will then name it for
  the next user.
- If the change is a new source shape, extend `ImportFileShape` and the
  diagnostics table in `docs/api-reference.md`.
