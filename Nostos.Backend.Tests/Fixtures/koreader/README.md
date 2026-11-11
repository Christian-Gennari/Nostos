# KOReader sidecar corpus

## Files and provenance

| Version | File | Where it came from |
| --- | --- | --- |
| `2026-09-epub` | `metadata.lua` | Synthetic `metadata.epub.lua` sidecar written for the KOReader import tests (#631). Not a capture from a device. |
| `2026-10-epub-multiline` | `metadata.multiline.lua` | Synthetic sidecar for the multi-line highlight and bookmark tests. |
| `2026-09-pdf` | `metadata.pdf.lua` | Synthetic PDF sidecar for the nested-position-table test. |
| `2026-10-pdf-untitled` | `metadata.pdf-untitled.lua` | Synthetic PDF sidecar with empty `doc_props` and a file-name title. |

A KOReader sidecar does not record the plugin version, so the folder names use
the sidecar's date (`2026-09-28` inside the files) plus the shape it covers.
A real sidecar becomes a new dated folder with its own manifest.

## How it was anonymized

Every file begins with `-- synthetic sidecar` and contains invented books,
authors and text. No anonymization step was needed. A real sidecar must have
`doc_props.title`, `doc_props.authors`, every annotation's `text` and `note`,
and the `doc_path`/`stats` title replaced before it is committed; keep every Lua
key and annotation entry.

## Expected counts

| Version | Found books | Imported annotations | Skipped by reason |
| --- | ---: | ---: | --- |
| `2026-09-epub` | 1 | 2 | none |
| `2026-10-epub-multiline` | 1 | 3 | none |
| `2026-09-pdf` | 1 | 2 | none |
| `2026-10-pdf-untitled` | 1 | 2 | none |

The sidecars cover an EPUB highlight with a note, a page bookmark, a highlight
that crosses a paragraph break, a PDF with nested position tables and a PDF
with no embedded metadata.
