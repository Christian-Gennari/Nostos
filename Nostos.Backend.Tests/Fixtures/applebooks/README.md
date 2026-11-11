# Apple Books database corpus

## Files and provenance

| Version | Files | Where they came from |
| --- | --- | --- |
| `macos-unknown-2022-09` | `AEAnnotation_v10312011_1727_local.sqlite` + `BKLibrary-1-091020131601.sqlite` | [tnahs/readstor](https://github.com/tnahs/readstor), [`data/databases/books-annotated`](https://github.com/tnahs/readstor/tree/main/data/databases/books-annotated). Repository licence: **Apache-2.0 OR MIT** (`LICENSE-APACHE` and `LICENSE-MIT`). |

The source repo's `ZPATH` values are the real macOS Books container locations,
but the repo does not record the macOS release. The folder is labelled by the
month the databases were committed (2022-09); `macos-unknown` is deliberate —
do not read it as a macOS version. The filenames carry Apple's schema names
(`AEAnnotation_v10312011_1727_local`, `BKLibrary-1-091020131601`).

The upstream databases had pending writes in `-wal` files. The anonymizer
folded those into the main files and left one file per database, so the fixture
does not need sidecars.

The upstream licence and copyright notice ship next to the file in `macos-unknown-2022-09/LICENSE-MIT`, as the licence requires.

## How it was anonymized

The raw files were processed with:

```bash
python3 scripts/anonymize_sqlite_export.py AEAnnotation.sqlite -o fixture/AEAnnotation.sqlite
python3 scripts/anonymize_sqlite_export.py BKLibrary.sqlite -o fixture/BKLibrary.sqlite
```

The script:

- replaced `ZANNOTATIONSELECTEDTEXT`, `ZANNOTATIONNOTE` and
  `ZANNOTATIONREPRESENTATIVETEXT` with “Anonymized highlight N.” / “Anonymized
  note N.”;
- replaced `ZANNOTATIONCHAPTER` and `ZFUTUREPROOFING5` (chapter names, which
  can name the book) with “Anonymized Title N”;
- replaced `ZTITLE`, `ZSORTTITLE`, `ZAUTHOR`, `ZSORTAUTHOR`, `ZGROUPING` and
  `ZSEQUENCEDISPLAYNAME` with title/author placeholders;
- redacted `ZANNOTATIONCREATORIDENTIFIER`, `ZBOOKDESCRIPTION` and `ZCOMMENTS`;
- replaced the macOS account name in `ZPATH` with `/Users/reader`;
- kept every table, column, declared type, row count and `Z_PK`/`ZASSETID`
  value, ran `VACUUM` so the replaced text is not recoverable from free pages,
  and checked `PRAGMA integrity_check` before writing.

## Expected counts

| Found books | Imported annotations | Skipped by reason | Missing optional columns |
| ---: | ---: | --- | --- |
| 3 | 10 | none | none |

The annotation database has 13 rows; three are bookmarks or reading positions
with neither text nor a note and are left out by the reader. The three books
join to the library database by asset id and keep their positions
(`epubcfi(…)`).

## Adding a new Apple Books version

1. Ask the owner for both files from
   `~/Library/Containers/com.apple.iBooksX/Data/Documents/`: the
   `AEAnnotation/AEAnnotation_*.sqlite` and `BKLibrary/BKLibrary-*.sqlite`
   (include `-wal`/`-shm` if present).
2. Note the macOS release (`sw_vers -productVersion`) and the filenames.
3. `mkdir Fixtures/applebooks/macos-<NN>`, run the anonymizer on each raw
   database into that folder, and inspect them.
4. Add `manifest.json` with `companions` naming the library file, and the
   counts the test should see.
5. Add a row to the table above with source URL, licence and how it was
   anonymized. Never commit the raw files.
