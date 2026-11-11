# Highlight-import fixture corpus

This folder holds the versioned inputs for
`Nostos.Backend.Tests/Services/Notes/ImportFixtureCorpusTests.cs` (issue
#1524). The other fixture folders here (Gutenberg, LibriVox, PDF, …) are for
different features and are not part of this corpus.

```
Fixtures/
  kobo/fw-4.38.23552/       KoboReader.sqlite
  applebooks/macos-unknown-2022-09/
                            AEAnnotation_…sqlite + BKLibrary_…sqlite
  kindle/2024-en/           My Clippings.txt
  kindle/2024-multi/        My Clippings.languages.txt
  readwise/2026-10/         readwise-export.csv
  readwise/api-2026-11/     readwise-export.json (export API, mapper tests)
  koreader/2026-09-epub/    metadata.lua
  …
```

Each version folder has one `manifest.json` next to the file(s):

```json
{
  "source": "kindle",
  "version": "2024-en",
  "file": "My Clippings.txt",
  "companions": [],
  "expected": {
    "foundBooks": 4,
    "importedAnnotations": 5,
    "skipped": { "bookmark": 1, "clipping-limit": 1, "empty": 1 },
    "missingOptionalColumns": []
  }
}
```

- `foundBooks` — books the reader returns; a book with no importable
  annotation is not counted.
- `importedAnnotations` — highlights/notes that would be written. A skipped
  entry is not counted here.
- `skipped` — entries the parser drops, by short reason key. The keys are the
  `Reason(...)` switch in the corpus test, not the user-facing sentence.
- `companions` — the Apple Books library database, empty for every other
  source.
- `missingOptionalColumns` — “table.column” for optional columns the file
  lacks; `[]` means the file has everything the reader knows. This is the same
  data the `HighlightImportShape` log reports (issue #1523).

`ImportFixtureCorpusTests` discovers every folder with a `manifest.json` except
the `readwise/api-*` recordings, which the JSON mapper tests read; there is no
list of fixtures in C#. To add a version: drop the file in a new folder,
add its manifest, and run:

```bash
export PATH=/home/dev/.nvm/versions/node/v24.15.0/bin:$PATH
lg run -- dotnet test Nostos.Backend.Tests --no-build --filter ImportFixtureCorpusTests
```

If the counts are not what the manifest says, fix the reader or the manifest —
never change the expected count to whatever the code happens to produce.
The end-to-end maintainer workflow, starting from the `HighlightImportShape`
log in a user report, is
[docs/runbooks/import-failure-to-fixture.md](../../docs/runbooks/import-failure-to-fixture.md).
In-code SQLite builders remain for edge cases (hidden rows, markup sentinels,
missing optional columns, a pending `-wal`) that a real file rarely has.

## Real vs synthetic, and anonymization

The corpus is meant to be built from real exports. A source README says which
files are real captures and which are synthetic shapes written for the parser
tests. Do not describe a synthetic file as real.

Real SQLite files must be anonymized before they are committed:

```bash
python3 scripts/anonymize_sqlite_export.py source.sqlite -o fixture.sqlite
```

The script rewrites titles, authors, highlight/note text and Kobo sideload
paths, redacts account fields, keeps every table, column, declared type, row
count and row identity, runs `VACUUM` so replaced text is not left in free
pages, then checks the schema and `PRAGMA integrity_check` before writing.
Inspect the output, including a byte scan, before committing it:

```bash
sqlite3 fixture.sqlite '.tables'
sqlite3 fixture.sqlite "SELECT Title, Attribution FROM content LIMIT 5;"
strings -a fixture.sqlite | grep -i 'an original title' || true
```

Never commit the raw file, a database with a reader's email/token, or a file
whose provenance is not written down in the source README. A file without a
clear redistribution licence does not belong in this folder.
