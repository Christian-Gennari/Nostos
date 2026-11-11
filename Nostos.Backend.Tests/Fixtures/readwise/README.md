# Readwise export corpus

## Files and provenance

| Version | File | Where it came from |
| --- | --- | --- |
| `2026-10` | `readwise-export.csv` | Synthetic export written for the Readwise import tests (#1225). Not a user's export. |

The version is the month the export shape was captured, not a Readwise API
version.

## How it was anonymized

The file is synthetic: `B00SYNTH01` is a placeholder ASIN, and every title,
author, highlight and note was invented for the tests. No anonymization step
was needed. A real export contains the reader's library and must have every
cell replaced before it is committed (keep the header row and the column set;
the parser finds columns by name).

## Expected counts

| Found books | Imported annotations | Skipped by reason |
| ---: | ---: | --- |
| 3 | 5 | tweet 1, podcast 1, empty 1 |

The file covers a quoted highlight with an embedded newline and doubled quotes,
a note attached to a highlight, a note with no highlight, a book with no Amazon
ID (flagged as a possible article), a tweet, a podcast and a row with no text.
