# Kindle clippings corpus

## Files and provenance

| Version | File | Where it came from |
| --- | --- | --- |
| `2024-en` | `My Clippings.txt` | Synthetic fixture written for the Kindle/Readwise import tests (#1225). Not a capture from a device. |
| `2024-multi` | `My Clippings.languages.txt` | Synthetic multi-language fixture for the same tests. Not a capture from a device. |

The labels are the year and language coverage of the examples; they are not
Kindle firmware versions (a `My Clippings.txt` does not carry one). A real
device file, when the owner supplies one, becomes a new `<year>-<lang>` folder
with its own manifest.

## How it was anonymized

The files are synthetic: the books, titles, authors and highlights were
invented for the tests, so no anonymization step was needed. A real file must
be replaced the same way a real SQLite file is: replace every title, author,
highlight and note, keep the entry layout and the `==========` separators.

## Expected counts

| Version | Found books | Imported annotations | Skipped by reason |
| --- | ---: | ---: | --- |
| `2024-en` | 4 | 5 | bookmark 1, clipping-limit 1, empty 1 |
| `2024-multi` | 6 | 6 | bookmark 1, unrecognised-format 2 |

The English file covers a highlight extended on the device, a note on a
highlight, a standalone note, a bookmark, a publisher clipping limit, an empty
entry and an older firmware's abbreviated location. The multi-language file
covers German, French, Spanish, Japanese and Chinese UI wording plus a Turkish
entry the parser does not recognise, and a final entry with no separator.
