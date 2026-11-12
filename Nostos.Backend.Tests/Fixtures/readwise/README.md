# Readwise export corpus

## Files and provenance

| Version | File | Where it came from |
| --- | --- | --- |
| `2026-10` | `readwise-export.csv` | Synthetic export written for the Readwise import tests (#1225). Not a user's export. |
| `api-2026-11` | `readwise-export.json` | A real `GET https://readwise.io/api/v2/export/` response, recorded on 2026-11-11 with the owner's token and anonymized by `scripts/anonymize_readwise_export.py` (#1525). The account had books and articles but no tweets or podcasts, so one tweet and one podcast entry were added from Readwise's documented response shape; those two rows are synthetic. |

The version is the month the export shape was captured, not a Readwise API
version.

## How it was anonymized

The CSV fixture is synthetic: `B00SYNTH01` is a placeholder ASIN, and every
title, author, highlight and note was invented for the tests. No anonymization
step was needed.

The API fixture is a real recording and **must** be refreshed through the
script, never committed raw:

```bash
python3 scripts/anonymize_readwise_export.py recording.json \
    -o Nostos.Backend.Tests/Fixtures/readwise/api-<yyyy-mm>/readwise-export.json \
    --max-books 5 --max-highlights 15 --add-missing-skip-kinds tweet,podcast
```

The script replaces titles, authors, highlight/note text, document notes,
summaries, URLs, tag names, ASINs and id-like values with deterministic neutral
placeholders, keeps every field name, nesting, type, null-ness and array
element shape, trims to 5 books / 15 highlights, and refuses to write when any
personal string from the recording survives. `--add-missing-skip-kinds` copies
an existing result's structure and changes only its values/category, for
categories the recording account does not have. Keep the raw recording outside
the worktree and delete it afterwards.

The source README must say which rows are real and which are synthetic. Never
commit a recording with a token, an account email, or a file whose provenance
is not written down here.

## Expected counts

### CSV (`2026-10`)

| Found books | Imported annotations | Skipped by reason |
| ---: | ---: | --- |
| 3 | 5 | tweet 1, podcast 1, empty 1 |

The file covers a quoted highlight with an embedded newline and doubled quotes,
a note attached to a highlight, a note with no highlight, a book with no Amazon
ID (flagged as a possible article), a tweet, a podcast and a row with no text.

### API (`api-2026-11`)

| Found books | Imported annotations | Skipped by reason |
| ---: | ---: | --- |
| 1 | 2 | article 3, tweet 1, podcast 1 |

The API folder is discovered by `ReadwiseApiMapperTests`, not by
`ImportFixtureCorpusTests`: the latter parses files with
`HighlightImportSourceReader`, and an API response is JSON rather than a file
the reader accepts. The manifest uses the same layout so the provenance and
expected counts stay in one place.
