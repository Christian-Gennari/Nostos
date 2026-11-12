# Nostos — API Reference

All endpoints return JSON. Base path: `/api` (except OPDS at `/opds` and MCP
at `/mcp`).

> **Coverage note (2026-08-12):** this reference predates several shipped
> surfaces — Backup and the issue #34 canonical
> library service. The Books / Notes / Collections / Topics / Writings /
> OPDS sections below remain accurate (inline corrections noted where the
> library service changed behavior); the sections at the end cover Backup
> and Library. The authoritative route tables live in
> [`Nostos.Backend/_docs/endpoints.md`](../Nostos.Backend/_docs/endpoints.md).

---

## Books — `/api/books`

### `GET /api/books`

List books with filtering, sorting, search, and pagination.

| Query Param | Type   | Default  | Description                                    |
| ----------- | ------ | -------- | ---------------------------------------------- |
| `search`    | string | —        | Search by title or author (LIKE)               |
| `filter`    | string | —        | `Favorites`, `Finished`, `Reading`, `Unsorted` |
| `sort`      | string | `Recent` | `Recent`, `Title`, `Rating`, `LastRead`, `RecentlyFinished` |
| `page`      | int    | 1        | Page number                                    |
| `pageSize`  | int    | 20       | Items per page; values above 100 are clamped to 100 |
| `collectionId` | guid | —      | Restrict the listing to one collection (subtree-inclusive: descendants match too) |

**Response:** `PaginatedResponse<BookDto>` — `{ items, totalCount, page, pageSize }`. `pageSize` is the
effective size after clamping, and the `X-Max-Page-Size` response header carries the ceiling (100).

### `GET /api/books/{id}`

Get a single book by ID.

**Response:** `BookDto`

### `POST /api/books`

Create a new book.

**Body:** `CreateBookDto`

```json
{
  "type": "physical|ebook|audiobook",
  "title": "string (required)",
  "author": "string?",
  "subtitle": "string?",
  "isbn": "string?",
  "publisher": "string?",
  "collectionIds": ["guid", "..."]
  // ... all metadata fields
}
```

**Response:** `201 Created` with `BookDto` when a book was created
(`outcome: created`), or `200 OK` with the existing `BookDto` when an exact
normalized-identity match was found (`outcome: matched`). Legacy permissive
mode: ambiguity creates rather than asking — the strict confirmation flow is
used only by Ask Nostos.

### `PUT /api/books/{id}`

Update book metadata. All fields are optional — only provided fields are updated.

**Body:** `UpdateBookDto`

**Response:** `BookDto`

### `PUT /api/books/{id}/collections`

Set the book's collection membership. **Full replacement set**: send every
collection the book should end up in.

**Body:** `{ "collectionIds": ["guid", "..."] }`

- adding a collection = include it in the set (a book may be in several);
- removing one = omit it;
- removing all = `[]`.

Set semantics rather than add/remove verbs, so one call expresses every case and
is naturally idempotent. Returns `200 OK` with the updated `BookDto`, or
`404 collection_not_found` if any id does not exist. An unknown id rejects the
whole call rather than applying part of it.

### `PUT /api/books/{id}/progress`

Update reading progress.

**Body:**

```json
{
  "location": "string (epub CFI or page number)",
  "percentage": 0-100
}
```

`percentage` is validated 0–100 (invalid values → 400). Auto-sets
`lastReadAt` to now and aligns `finishedAt` with the finished state.

### `POST /api/books/{id}/progress/reset`

Reset reading progress to the canonical "not started" state (`lastLocation`,
`progressPercent`, `finishedAt` and `lastReadAt` all cleared). Idempotent: an
already-reset book returns `200` as a no-op.

**Query:** `allEditions=true` resets every edition of the book (the work), not
only this one. Resetting editions that are all reset is a no-op.

### `POST /api/books/{id}/work/link`

**Manual multi-edition override.** Merge this book's work with another book's
work, so both — and everything already grouped with either of them — become
editions of one work. Metadata is deliberately NOT consulted: differing
title/author is the case this exists for.

**Body:** `{ "targetBookId": "guid" }`

The target's work survives as the merged group's identity. **Group semantics,
not book semantics:** every member of the source work moves, because pulling
only the named book out of an already-valid group would be a side effect the
user did not ask for. A work left empty by the merge is deleted.

Only `WorkId` changes on the affected books. Files, reading progress, notes,
metadata and collection memberships are untouched. Rating, favourite and review
belong to the book (the work), so the two works' values are combined without
losing any: values that agree, or that only one side has, become the book's;
where they differ, the side read most recently wins and the other side's
editions keep their value as an edition rating / review.

| Response | Meaning |
| --- | --- |
| `200` + `{ bookId, workId, editionCount, removedWorkId }` | merged (`removedWorkId` set when a work was emptied and deleted) |
| `200`, already in one work | no-op; no state-version bump |
| `400 invalid_work_link` | `targetBookId` is the book itself |
| `404 book_not_found` / `404 target_book_not_found` | either id missing |

### `POST /api/books/{id}/work/unlink`

**Manual multi-edition override.** Detach this book from its work into a NEW
work built from the book's own current title/author identity. The book always
ends up with a valid work — a null or empty `WorkId` is not representable. A
book already alone in its work returns `200` as a no-op.

**Response:** `{ bookId, workId, editionCount: 1, removedWorkId: null }`, or
`404 book_not_found`.

### `GET /api/books/{id}/locations`

Get cached epub locations JSON (used for fast progress percentage calculation).

**Response:** `{ locations: "string" }` or `404`

### `POST /api/books/{id}/locations`

Save epub locations JSON.

**Body:** `{ "locations": "string" }`

### `DELETE /api/books/{id}`

Delete a book, its files, and cover. Files are removed only after the
database row is gone.

**Response:** `204 No Content`, or `409 book_in_use` when the book is
referenced by a note.

### `POST /api/books/{id}/file`

Upload a book file (epub, pdf, mobi, azw3, m4b, m4a, mp3, txt).

**Body:** `multipart/form-data` with file. Max size: 4 GB.

| Query Param | Type | Default | Description |
| ----------- | ---- | ------- | ----------- |
| `replace`   | bool | false   | Must be `true` to replace a file the book already has |

A book that already has a file answers `409` `{ "code": "book_has_file", "message": … }` unless
`replace=true` is passed. This matters because `POST /api/books` is create-or-match by title and
author and can return an existing book. A book that is importing answers `409`
`book_import_in_progress`.

The resumable contract (`POST /api/books/{id}/file/uploads`) follows the same rule: creating a
session for a book that already has a file answers `409` `book_has_file` unless the request carries
`?replace=true`. A session that already matches the file resumes either way.

For audio files, metadata (chapters, duration) is extracted automatically via ATL.NET.

Uploading a file to a multi-track audiobook replaces its tracks: a book holds
one file or a track list, never both.

### `GET /api/books/{id}/file`

Download/stream the book file. Supports HTTP range requests for streaming.
`404` for a multi-track audiobook, which has no single file; play it through
the track route below.

### `GET /api/books/{id}/file/version`

Which bytes the book's file holds right now, for a client that keeps a copy.
`version` is opaque and moves whenever the stored file does.

**Response:** `{ version: "string", bytes: 123 }`, never cached, or `404` when
there is no single file.

### `GET /api/books/{id}/file/download`

Download the book as an attachment. For a multi-track audiobook this is one
uncompressed `.zip` of its tracks, cover and a Readium audiobook
`manifest.json`, generated on the fly. It has an exact `Content-Length`, an
`ETag`, and supports range requests, so an interrupted download can resume.

### `GET /api/books/{id}/tracks/{number}`

Stream one track of a multi-track audiobook (1-based). Supports HTTP range
requests. A book's tracks are listed in `BookDto.tracks` (`number`, `title`,
`duration` in seconds, `bytes`, `contentType`); `tracks` is absent for a
single-file book. Positions elsewhere (`lastLocation`, `chapters`) are seconds
across the whole book.

### `GET /api/books/{id}/tracks/{number}/download`

Download one track as an attachment, named after its place and title.

### `POST /api/books/{id}/cover`

Upload a cover image (PNG or JPEG).

**Body:** `multipart/form-data` with image file.

### `GET /api/books/{id}/cover`

Download the cover image.

### `DELETE /api/books/{id}/cover`

Delete the cover image.

**Response:** `204 No Content`

### `GET /api/books/lookup/{isbn}`

Lookup book metadata by ISBN. Queries both **Google Books API** and **Open Library API** in parallel and merges results (Open Library preferred, Google fills gaps).

**Response:** `400` for an invalid ISBN; `CreateBookDto` (pre-filled) or
`404` when no metadata is found. The external lookup has a 15-second timeout.

---

## Notes — `/api`

Notes are always scoped to a book.

### `GET /api/books/{bookId}/notes`

List all notes for a book.

**Response:** `NoteDto[]`

```json
{
  "id": "guid",
  "bookId": "guid",
  "content": "string",
  "cfiRange": "string? (epub location)",
  "selectedText": "string? (highlighted text)",
  "createdAt": "datetime",
  "bookTitle": "string?"
}
```

### `GET /api/notes/{id}`

Get one canonical note for exact note/evidence deep-links.

**Response:** [`NoteSearchHitDto`](../Nostos.Shared/Dtos/NoteDto.cs) — produced by
[`NotesEndpoints`](https://github.com/Christian-Gennari/Nostos/blob/cf09b3114f54d9f2727fa590c2002ef9f31c5216/Nostos.Backend/Endpoints/NotesEndpoints.cs).

```json
{
  "id": "guid",
  "bookId": "guid",
  "bookTitle": "string?",
  "content": "string",
  "selectedText": "string?",
  "snippet": "string?",
  "topicNames": ["string"],
  "createdAt": "datetime",
  "cfiRange": "string?",
  "sourceAnchorKind": "string",
  "sourceAnchorValue": "string?",
  "anchorVerified": false,
  "captureSource": "string",
  "processingMode": "string",
  "hasRawContent": true
}
```

`hasRawContent` only reports whether an original raw capture exists; the raw
text itself is not included in this canonical response.

### `GET /api/notes/{id}/raw`

Get the explicit raw transcript for a note, alongside the current stored text
and processing mode.

**Response:** [`NoteRawTranscriptDto`](../Nostos.Shared/Dtos/NoteDto.cs) —
produced by
[`NoteProcessingEndpoints`](https://github.com/Christian-Gennari/Nostos/blob/cf09b3114f54d9f2727fa590c2002ef9f31c5216/Nostos.Backend/Endpoints/NoteProcessingEndpoints.cs).

`{ id, rawContent, content, processingMode }`

Unlike the canonical note response's `hasRawContent` marker, this route carries
the nullable `rawContent` value itself.

### `POST /api/books/{bookId}/notes`

Create a note. Topics wrapped in `[[double brackets]]` are auto-extracted and linked.

**Body:**

```json
{
  "content": "This is about [[Philosophy]] and [[Ethics]]",
  "cfiRange": "string? (epub CFI range)",
  "selectedText": "string? (highlighted text)",
  "highlightColour": "string? (amber | sage | blue | rose)",
  "topics": ["string"]
}
```

`topics` are topics added as pills: each one `content` does not already
declare is written into it as its own `[[Name]]` paragraph. A note may be only
topics. At most 50; a name that cannot stand inside `[[ ]]` is a `400`.

**Response:** `201 Created` with `NoteDto`. An unknown `highlightColour` is a
`400`.

### `PUT /api/notes/{id}`

Update a note. Re-processes `[[topic]]` links.

**Body:**

```json
{
  "content": "Updated content with [[NewTopic]]",
  "selectedText": "string?",
  "highlightColour": "string? (amber | sage | blue | rose)",
  "topics": ["string"]
}
```

With `topics`, `content` is the note's own words and `topics` all of its pill
topics: each is written as a `[[Name]]` paragraph, and a pill topic left out
(or an older link the text never declared) is removed. Without `topics`,
`content` is the whole text, as before.

`highlightColour` recolours a highlight. Omitted, it leaves the stored pen
unchanged; a name that is not one of the four pens is a `400`. A note with no
stored pen (`null`) is drawn in amber.

### `PUT /api/notes/{id}/location`

Record where the reader found an imported highlight in its EPUB, so it is drawn
and opened like a highlight made in Nostos. Only notes with
`captureSource: "import"` accept it. The first location is kept: a later call
returns the note unchanged.

**Body:**

```json
{
  "cfiRange": "epubcfi(/6/14!/4/2/12,/1:0,/1:42)"
}
```

**Response:** `200 OK` with `NoteDto`; `400` for a non-import note or a value
that is not an EPUB CFI; `404` if the note does not exist.

### `DELETE /api/notes/{id}`

Delete a note and its topic links.

**Response:** `204 No Content`

### Highlight import — `/api/notes/imports`

Settings → Highlight import → Import… opens the review dialog (also
"Import highlights" in the command palette). Select one Kobo
`KoboReader.sqlite`, several KOReader `metadata.*.lua` sidecars, a Kindle's
`My Clippings.txt`, or a Readwise CSV export. The server detects the format
from the contents, not the file name. Kobo files are read-only, with a 256 MB
upload limit; KOReader sidecars have a 2 MB limit; Kindle and Readwise files a
64 MB limit.

| Method | Path | Behavior |
| --- | --- | --- |
| `POST` | `/api/notes/imports/preview` | Read multipart field `file`; return `{ source, books, skipped }` without writing. |
| `POST` | `/api/notes/imports/commit` | Read the same `file` and a JSON `decisions` form field; import only decided books. |
| `GET` | `/api/notes/imports/batches` | Return the five most recent imports that still have notes, with note/book counts. |
| `DELETE` | `/api/notes/imports/batches/{id}` | Undo a batch; return `{ removed }`, or `404` if it no longer exists. |

Preview books include `sourceKey`, title/author, annotation/new counts, and
candidates. `match` is `exact` for one ISBN or title-and-author identity,
`remembered` for a previously confirmed destination, `suggested` for a match
that needs confirmation (including multiple editions), or `none`. `source` is
`kobo`, `koreader`, `kindle` or `readwise`. A book may carry a `notice`, such as
a Readwise row that looks like a web article. `skipped` lists what the file
holds that is never imported, as `{ title, author, count, reason }`: Kindle
bookmarks, highlights the publisher's clipping limit emptied, entries in a
format Nostos does not recognise, and Readwise tweets, podcasts and other
sources that are not books.

The commit's `decisions` is an array. Use `{ "sourceKey": "…", "bookId": "guid" }`
to select a library book, or `{ "sourceKey": "…", "create": true }` to add a
physical book from the device metadata. Omitted books are left out. Books and
notes are added only on commit. Confirmed destinations are remembered.

Commit returns `{ batchId, source, books }`. Each result book reports its
`status` (`imported` or `skipped`), destination, imported/duplicate counts,
whether it was created, and any message. An optional `batchId` form field joins
later selected files to the first file's batch so one Undo covers the selection.
An import that adds nothing creates no new batch.

Re-import does not duplicate notes. A KOReader note that an earlier version of
the import stored cut short at a line break, or a page bookmark it anchored to a
screen count, is corrected in place and reported in the book's `message`. Undo removes the batch's notes and their
import receipts, allowing them to be imported again; newly added books and
remembered destinations stay in the library. Kobo dog-ears, stylus markup and
deleted rows are ignored. The original `POST /api/notes/import/koreader` endpoint
remains available for direct single-sidecar imports.

**Kindle.** Entries are read by keyword, so `My Clippings.txt` from a Kindle
set to English, German, French, Spanish, Italian, Portuguese, Dutch, Russian,
Japanese, Chinese or Korean is understood; any other entry is listed in
`skipped`. A UTF-8 byte-order mark and CRLF or LF line endings are accepted.
The title line's trailing `(Author)` becomes the author, with `Last, First`
turned around and several authors separated by `;`. A highlight edited on the
device (a later entry over the same or an overlapping range, with one text
containing the other) is imported once, as the latest or longest version. A
note at a highlight's last location is attached to that highlight; a note with
no highlight is imported on its own. Books group by title and author.

**Readwise.** Columns are found by name (`Highlight`, `Book Title`,
`Book Author`, `Amazon Book ID`, `Note`, `Location Type`, `Location`, and an
optional `Category`); quoted fields may span lines. Books group by Amazon Book
ID, or by title and author. With a `Category` column, only books, supplementals
and PDFs are imported. Without one, tweets (`Tweets From …` or an `@` author)
and podcasts (positioned by time) are skipped, and a book positioned by order
with no Amazon ID is flagged with a `notice`.

### Readwise export API — `/api/notes/imports/readwise`

Settings → Import → Readwise → **Connect with a token** stores a Readwise
access token for this library. The token is checked with Readwise's
`GET /api/v2/auth/` (204 means valid), saved as ASP.NET Data-Protection
ciphertext, and never returned to a client, written to a log, put in a portable
export or kept in a SelfHosted backup. The CSV import above stays available.

| Method | Path | Behavior |
| --- | --- | --- |
| `GET` | `/api/notes/imports/readwise` | Return `{ connected, lastImportedAtUtc, importedThroughUtc }`; never the token. |
| `POST` | `/api/notes/imports/readwise/connect` | Body `{ "token": "…" }`; validate with Readwise and save it. `400` when Readwise rejects it. |
| `DELETE` | `/api/notes/imports/readwise` | Remove the token and its watermark. |
| `POST` | `/api/notes/imports/readwise/preview` | Fetch the export from the saved `updatedAfter` watermark and return the same `{ source, books, skipped }` shape as a file preview. Writes nothing. |
| `POST` | `/api/notes/imports/readwise/commit` | Body `{ "decisions": […], "batchId": "…" }`; fetch the same range, import through the same preview → match → dedupe → undo path, then advance the watermark to the run's start. |

The API maps into the CSV reader's `ImportSourceBook` shape, including the same
book identity (Amazon id, otherwise title and author), skip reasons for
articles, tweets and podcasts, and `readwise_*` anchors. Readwise's response is
checked for the fields Nostos reads before mapping; a missing field or a changed
type is a `502` whose message carries JSON paths and types only. Paging follows
`nextPageCursor`, `updatedAfter` is sent only on the first page, and a `429`
waits for its `Retry-After`. A weekly GitHub Actions canary
(`.github/workflows/readwise-canary.yml`) runs the same shape check against the
first page of the live export using `READWISE_CANARY_TOKEN`.

**Positions.** Kindle notes are anchored as `kindle_location` (`180-181`) or,
for a PDF with no locations, `kindle_page`; Readwise notes as
`readwise_location`, `readwise_page` or `readwise_order`. These anchors are
never verified, so no citation claims a page from them. The EPUB reader looks
the highlighted text up in the book and saves a verified location once it is
found, as it does for Kobo and KOReader highlights. Colours and tags are not
imported; notes arrive in the default pen.

**Diagnostics.** A file that fails to parse, a Kobo or Apple Books database
that lost a column the readers treat as optional, and a preview that skips
Kindle entries as an unrecognised format each log one warning event (the
KOReader plugin's `POST /api/notes/import/koreader` logs it too when its
sidecar does not parse; a commit does not repeat its preview's event), named
`HighlightImportShape` (event id 1523), with three structured properties:
`Source` (`kobo`, `applebooks`, `kindle`, `readwise`, `koreader` or `unknown`),
`Outcome` (`parse-failed`, `missing-optional-columns` or
`unrecognised-entries`) and `Shape`. The shape is structure only: table and
column names, a CSV header row, line and `==========` counts with the masked
format line of each unrecognised clipping
(`- Xxxx xxx xx Xxxx 12 | Xxxxx 34`), or a Lua sidecar's top-level keys, capped
at 2 KB. It never contains highlight text, titles, authors, file names, paths
or user identifiers. In Cloud logs, search for `HighlightImportShape` and
group by `Outcome`; SelfHosted writes it to the app's normal warning log.
The maintainer workflow from a reported failure to a versioned, anonymized
fixture is in
[runbooks/import-failure-to-fixture.md](runbooks/import-failure-to-fixture.md).

### Calibre library import — `/api/calibre`

Settings → Import → Calibre library opens the review dialog. A SelfHosted host
reads the folder named by `Calibre:LibraryRoot` in place; any host that maps
the upload routes accepts a zipped library instead (#1572). No route accepts a
filesystem path. The database is opened read-only and the book files are only
opened for reading.

| Method | Path | Behavior |
| --- | --- | --- |
| `GET` | `/api/calibre/` | `{ configured, available, source, folderSupported, uploadSupported, maxUploadBytes, upload, import }`. Always answers, so Settings can explain how to set a library up. |
| `GET` | `/api/calibre/preview` | Read the library and return `{ source, summary, books, skipped, customColumns, warnings }` without writing. |
| `POST` | `/api/calibre/import` | Body `{ decisions }`; start one background import and return `202` with its progress. |
| `GET` | `/api/calibre/import` | The running or last import: `{ status, total, completed, current, batchId, results, error }`. Answers after an uploaded library has been released. |
| `GET` | `/api/calibre/batches` | The five most recent Calibre imports, with book and file counts. |
| `DELETE` | `/api/calibre/batches/{id}` | Undo an import; return `{ removed, kept }` or `404`. |

#### Uploading a zipped library

A host that maps these routes advertises `supportsCalibreUpload` in
`/api/runtime/capabilities`. The upload is the same resumable contract as the
library transfer: server-owned `partBytes`, a SHA-256 receipt per part, and a
whole-file check before extraction. `X-Nostos-Chunk-SHA256` and
`Content-Range: bytes start-end/total` are required on every part.

| Method | Path | Behavior |
| --- | --- | --- |
| `GET` | `/api/calibre/upload` | The owner's current upload, or `204`. |
| `POST` | `/api/calibre/upload` | Body `{ totalBytes, fileName, sha256 }`; create or resume the one session. `413` with `calibre_upload_too_large` over the cap. |
| `GET` | `/api/calibre/upload/{id}` | One session's state, received parts and limits. |
| `PUT` | `/api/calibre/upload/{id}/parts/{index}` | One part. Re-sending the same part is idempotent; different bytes answer `409 calibre_upload_part_conflict`. |
| `POST` | `/api/calibre/upload/{id}/complete` | Verify, extract and validate; answers the staged session with `books` and `stagedBytes`. `409 calibre_upload_incomplete` names the missing parts. |
| `DELETE` | `/api/calibre/upload/{id}` | Remove the session and its scratch bytes. |

Invalid archives answer `422` with `calibre_upload_invalid_archive`,
`calibre_upload_unsafe_entry` (zip slip, absolute path, symlink),
`calibre_upload_too_many_files`, `calibre_upload_extracted_too_large`,
`calibre_upload_metadata_missing` or `calibre_upload_not_calibre`.

Preview books carry the Calibre `sourceKey`, title/author, series and volume,
ISBN, publisher, publication date, `rating` (0–5), plain-text `description`,
tags, `readAt`, `match` (`exact`, `suggested` or `none`), candidates, and
`formats`. Each format has a stable `key`, its `format` name, `fileName`,
`bytes`, `kind` (`ebook` or `audiobook`), `alreadyHere`, `existingBookId` and
`selectedByDefault`. `unsupportedFormats` lists the files Nostos cannot read
with the reason. `skipped` lists books with no readable format. `customColumns`
lists every Calibre custom column with `status: "used"` (a common read or
read-date column) or `"skipped"` and why.

A decision is `{ sourceKey, formats, bookId?, createCollections? }`.
`formats` names the format keys to bring; an empty list leaves the book out.
`bookId` picks a library book for a `suggested` match; without it the book is
created. `createCollections` is the only way tags become collections. Each
commit is one `BookImportBatch`, so Undo takes it all back; a created book that
has since been read or annotated is kept.

---

## Portable archives and library migration — /api/portability

The customer steps, archive contents, size ceilings, and verification status
are in [Library portability and migration](cloud/portability.md). Migration
routes are mapped only by hosts that enable the transfer capability. Their
authorization and rate limits are host-configured; the Cloud adapter and its
account-plan rules are outside this public repository.

### Portable archive endpoints

| Method | Route | Behavior |
| --- | --- | --- |
| GET | /api/portability/export | Streams a .nostos archive. |
| GET | /api/portability/markdown | Notes, highlights, passages, topics and (unless `writings=false`) writings as a .zip of Markdown files (issue #1221). Never imported back. |
| POST | /api/portability/import | Reads raw .nostos bytes from the request body and imports only into an empty destination; a populated destination returns 409. |

The single-request import is subject to the SelfHosted Kestrel request-body
limit of 4 GiB. It is separate from the chunked migration API below.

### Durable migration endpoints

All routes use the /api/portability/migration base path and the migration error
response with a stable error code and message.

| Method | Route suffix | Purpose and success |
| --- | --- | --- |
| POST | /preflight | Check archive compatibility, destination state, and required storage; returns the admission decision and any reservation. |
| POST | /jobs | Create an Import or Export job; 201 created or 200 idempotent replay. |
| GET | /jobs/{id} | Read job, progress, and available upload-session status. |
| POST | /jobs/{id}/cancel | Cancel a cancellable job; 200 on success. |
| POST | /jobs/{id}/retry | Retry a failed, cancelled, or expired job; 200 on success. |
| POST | /jobs/{id}/upload-session | Create or replay a transfer session; 201 created or 200 replay. |
| GET | /jobs/{id}/upload-session | Read session state and received chunk indexes for resumption. |
| PUT | /jobs/{id}/upload-session/chunks/{index} | Stream one raw chunk; the request includes Content-Range and X-Nostos-Chunk-SHA256. |
| POST | /jobs/{id}/upload-session/complete | Seal and verify the uploaded archive, then continue server-side preparation. |
| GET, HEAD | /jobs/{id}/export-download | Download a completed export. GET supports byte ranges; the artifact is retained for 24 hours. |
| POST | /jobs/{id}/activate | Request activation; 202 means accepted for background work, not that the switch has finished. |
| GET | /jobs/{id}/activation | Read activation outcome while the switch is running or after it finishes. |
| GET | /recovery | List retained recovery copies. |
| GET | /recovery/{id} | Read a recovery copy's status and expiry. |
| POST | /recovery/{id}/restore | Request restoration; requires destination revision and explicit replacement confirmation; 202 means accepted. |

Chunk size is 4–64 MiB (16 MiB default). The uploaded file identity binds the
exact byte length and whole-file SHA-256. A session expires after 24 hours.
The browser stores resume metadata, not the file. After a reload, select the
same file again; while the page remains open, a paused upload can be resumed
from its current screen. See the portability guide for browser-storage and
host-specific discovery caveats.

Activation requests include the destination revision returned by the host.
For a populated destination, set confirmReplacement to true only after
reviewing the current and incoming counts. An empty destination activates
automatically in the shared UI. A successful replacement retains the previous
library for seven days. Restoring a recovery copy also replaces the current
portable library and requires confirmation bound to the current revision.

The public Angular Settings UI does not expose a recovery list or restore
button. The recovery endpoints are present in the public SelfHosted
implementation; their availability and customer UI on Cloud have not been
verified. The public contract ceiling is not a Cloud plan quota. See
[MigrationEndpoints](../Nostos.Product/Endpoints/MigrationEndpoints.cs),
[MigrationRecoveryEndpoints](../Nostos.Product/Endpoints/MigrationRecoveryEndpoints.cs),
and [MigrationContractLimits](../Nostos.Product/Services/Portability/MigrationContracts.cs)
for the route map and fixed limits.

`GET /api/portability/migration/active-import` returns the status of the import
running on the host (same body as `GET /jobs/{id}`), or `204 No Content` when
none runs. A browser that lost its resume record uses it to show the import the
server is working on.

## Collections — `/api/collections`

Hierarchical folders for organizing books.

### `GET /api/collections`

List all collections (flat list with `parentId` for hierarchy).

**Response:** `CollectionDto[]` — `{ id, name, parentId? }`

### `GET /api/collections/{id}`

Get a single collection.

### `POST /api/collections`

Create a collection. Always returns `201 Created`; a sibling with the same
normalized name under the same parent returns the existing collection instead
of creating a duplicate.

**Body:** `{ "name": "string", "parentId": "guid?" }`

### `PUT /api/collections/{id}`

Update name and/or parent through the canonical library service. Includes
**cycle detection** — `409 collection_cycle` if the move would create a
circular reference; a sibling name collision at the destination returns
`409 collection_name_conflict`.

**Body:** `{ "name": "string", "parentId": "guid?" }`

### `DELETE /api/collections/{id}`

Delete a collection. Its membership rows are removed; the **books themselves
are never deleted** and keep every other collection they belong to.

**Response:** `204 No Content`, or `409 collection_has_children` while the
collection still has child collections.

---

## Topics — `/api/topics`

Read-only. Topics are created automatically when notes with `[[brackets]]` are saved.

> Topics were formerly called Concepts. `/api/concepts` remains a transition
> prefix alias to the identical handlers in
> [`TopicsEndpoints`](https://github.com/Christian-Gennari/Nostos/blob/cf09b3114f54d9f2727fa590c2002ef9f31c5216/Nostos.Backend/Endpoints/TopicsEndpoints.cs). Both prefixes
> use the current topic request/response field names; the alias does not restore
> old `concept` payload field names.

### `GET /api/topics`

List all topics with usage count, sorted by most-used first.

**Response:** `TopicDto[]` — `{ id, name, usageCount }`

### `GET /api/topics/{id}`

Get topic detail with all related notes across books.

**Response:**

```json
{
  "id": "guid",
  "name": "string",
  "notes": [
    {
      "noteId": "guid",
      "content": "string",
      "selectedText": "string?",
      "cfiRange": "string?",
      "bookId": "guid",
      "bookTitle": "string"
    }
  ]
}
```

---

## Writings — `/api/writings`

Hierarchical file system for the writing studio.

### `GET /api/writings`

List all writings (flat list with `parentId` for tree structure).

**Response:** `WritingDto[]` — `{ id, name, type: "Folder"|"Document", parentId?, updatedAt }`

### `GET /api/writings/{id}`

Get document content.

**Response:** `{ id, name, content, updatedAt }`

### `POST /api/writings`

Create a folder or document.

**Body:** `{ "name": "string", "type": "Folder|Document", "parentId": "guid?" }`

### `PUT /api/writings/{id}`

Update name and/or content (used by auto-save).

**Body:** `{ "name": "string", "content": "string?" }`

### `PUT /api/writings/{id}/move`

Move a writing to a new parent folder. Includes **cycle detection**.

**Body:** `{ "newParentId": "guid?" }` (`null` = move to root)

### `DELETE /api/writings/{id}`

Delete a writing. Cascading delete removes all children.

**Response:** `204 No Content`

---

## OPDS Catalog — `/opds`

### `GET /opds/?page=N`

OPDS 1.2 **acquisition** catalog of the books that have a stored file. Compatible
with OPDS reader apps (Moon Reader, KOReader, Calibre, …).

**Response:** `application/atom+xml;profile=opds-catalog;kind=acquisition`

Each entry includes:

- Title, author, description
- Language as `dc:language` (Dublin Core), and `dc:identifier` as
  `urn:isbn:…` / `urn:asin:…` when one is known
- Cover image link (`http://opds-spec.org/image`) and thumbnail
  (`http://opds-spec.org/image/thumbnail`), each advertising the media type the
  stored cover actually has (`image/png` or `image/jpeg`)
- Acquisition link (`http://opds-spec.org/acquisition`) advertising the real
  media type: `application/epub+zip`, `application/pdf`, `text/plain`,
  `application/x-mobipocket-ebook`, `audio/mpeg` (mp3) or `audio/mp4` (m4a/m4b)

**Pagination.** The feed is paged (`Opds:PageSize`, default 50, max 500). Page 1
is `/opds/`; later pages are `/opds/?page=N`. Feed-level links carry
`rel="self"`, `"start"`, `"first"`, `"last"`, and `"next"`/`"previous"` where
they apply, so a reader can follow the collection without knowing the page
count. A page number past the end clamps to the last page rather than returning
a dead `next`.

**Absolute URLs.** Cover and acquisition URLs are absolute. Their scheme and
host come from the request, honouring `X-Forwarded-Proto` / `X-Forwarded-Host`
from a trusted (loopback) reverse proxy; set `Opds:PublicBaseUrl` to override
the origin when the deployment cannot reveal it.

### `GET /api/opds/info`

What the Settings surface needs to describe e-reader access. Mapped whether or
not the catalog is, so "turned off" is distinguishable from "broken".

**Response:**

```json
{
  "enabled": true,
  "catalogUrl": "https://your-instance:5215/opds/",
  "urlSource": "request",
  "localOnly": false
}
```

- `catalogUrl` — the address to give a reader. `null` when `enabled` is false.
- `urlSource` — `configured` when `Opds:PublicBaseUrl` decided the origin,
  `request` when it came from how the client reached this endpoint.
- `localOnly` — true when that origin is a loopback address, so no other device
  can use it.

### Access model — `/opds/` is unauthenticated

The catalog and the acquisition URLs it advertises are served **without
authentication**, like the rest of the Nostos API. The supported deployment is
therefore a private network (LAN or Tailscale), and Nostos must not be published
to the public internet under this model. The one exception is optional: with
`Mcp:PublicBaseUrl` set, a tunnel may publish `/mcp`, `/oauth/*` and
`/.well-known/oauth-*` for claude.ai and ChatGPT (#1470), and Nostos itself
answers `404` to every other path on that address, `/opds/` included. `Opds:Enabled=false` removes the route
entirely (requests then get an ordinary 404 — never the SPA shell). Nostos logs
the effective access model once at startup.

| Key                  | Default              | Meaning                                              |
| -------------------- | -------------------- | ---------------------------------------------------- |
| `Opds:Enabled`       | `true`               | Map `/opds/` at all                                   |
| `Opds:PageSize`      | `50`                 | Entries per page (clamped to 500)                     |
| `Opds:PublicBaseUrl` | unset                | Externally visible origin, e.g. `https://host:5215`   |
 
 ---
 
 ## Backup — `/api/backup`

| Method     | Route            | Description |
| ---------- | ---------------- | ----------- |
| `GET`      | `/status`        | Last backup time and scheduled status |
| `GET`      | `/settings`      | Current retention and interval settings |
| `PUT`      | `/settings`      | Update backup configuration |
| `POST`     | `/trigger`       | Manually start a backup immediately |
| `POST`     | `/restore/{id}`  | Restore library from a specific archive |
| `GET`      | `/history`       | List all backup records |
| `DELETE`   | `/history/{id}`  | Delete a backup record and its archive file |
| `GET`      | `/download/{id}` | Stream `.nostos` archive to browser |
| `POST`     | `/import`        | Scan `/backups` folder for untracked files |
| `GET`      | `/progress`      | Real-time step-by-step progress tracking |

During a restore the application enters maintenance mode: `/api` and `/mcp` return `503`
`{ "error": "Application is in maintenance mode during restore." }`.

---

## MCP — `/mcp` (issue #1467)

A Model Context Protocol server (Streamable HTTP, stateless) for the user's own
AI app: Claude, ChatGPT, Gemini or a local agent. It lets that app search,
quote and cite the library and keep notes in it, on the app's own plan.

**Off until a host turns it on.** The product registers the server everywhere
but maps `/mcp` only when the host registers the `NostosMcp` authorization
policy (`NostosMcp.AuthorizationPolicy`). With no policy, `/mcp` is a plain 404
and never the app shell. SelfHosted registers it with access tokens (#1469,
below) and its own OAuth server for hosted apps (#1470, below); Cloud with
Clerk OAuth (Nostos-Cloud #557).
A host may also pass `NostosProductEndpointPolicies.McpRateLimitPolicy`.

**Scopes.** What a caller may do comes from its `scope` claim
(`NostosMcp.ScopeClaimType`): space-separated OAuth scopes, matched ordinally,
read across every `scope` claim (and the type ASP.NET's JWT handler maps it to).
`nostos:read` (`NostosMcp.ReadScope`) is needed for every tool;
`nostos:write` (`NostosMcp.WriteScope`) also for `save_note`, `save_passage`, `create_draft`,
`add_to_reading_list`, `add_book` and `update_book`. A caller with no scope
claim at all counts as read-only. A tool the scopes do not cover answers with a tool error
(`isError: true`, e.g. "This connection to Nostos is read-only, so nothing was
saved…") and never runs; a tool without a `readOnlyHint` counts as a write.
The check is the call-tool filter `McpScopes.Enforce`
(`Nostos.Product/Integrations/Mcp/McpScopes.cs`), installed by
`AddNostosMcp`, so a host only fills the claim: SelfHosted from the token's
scope, Cloud straight from the Clerk OAuth access token's `scope`
(e.g. `"openid profile email offline_access nostos:read nostos:write"`).

### Connected apps — `/api/mcp/apps` (issue #1469)

Settings → Connected apps on any host that registers `IMcpConnectedApps`
(`Nostos.Product/Integrations/Mcp/McpConnectedApps.cs`). The product maps these
routes, and `GET /api/runtime/capabilities` reports
`supportsMcpConnectedApps: true`, only then. SelfHosted registers its access
tokens; Cloud registers its record of OAuth clients (#557).

| Method | Path | Does |
| --- | --- | --- |
| `GET` | `/api/mcp/apps` | `{ canCreate, apps: [{ id, name, kind: "token" \| "oauth_app", scope: "read" \| "read_write", createdAt, lastUsedAt }] }` |
| `DELETE` | `/api/mcp/apps/{id}` | Revoke: `204`, or `404` when there is no such app. The app's next `/mcp` request is refused |
| `POST` | `/api/mcp/apps/{id}/restore` | Allow a revoked app again (#1483): `204`, or `404`. Only a host that keeps revoked apps (Cloud: a revoke stays in force, because a refreshed OAuth token looks like a new sign-in) lists them, with `revoked: true`; SelfHosted deletes a revoked token, so it has nothing to restore |

`canCreate` is true where the owner makes tokens (SelfHosted); where it is
false, Settings shows the `/mcp` address to connect from instead. `id` is the
host's own string.

**Request gates.** Every authenticated `/mcp` request runs each registered
`IMcpRequestGate` (`AllowAsync(user, http, ct)`) after authorization and
before the MCP server. One `false` answers `401` with
`WWW-Authenticate: Bearer realm="Nostos", error="invalid_token"`. A host whose
credential outlives a revoke (a signed OAuth access token) refuses revoked
clients there and may note "last used"; SelfHosted needs none, because its
token scheme looks the token up on every request.

### SelfHosted access tokens (issue #1469)

SelfHosted turns `/mcp` on with access tokens made in **Settings → Connected
apps**. Send one as `Authorization: Bearer nst_…`. The rest of the API keeps its
access model (no login); the token scheme is used by `/mcp` only.

- A token is `nst_` plus 256 random bits in base64url (47 characters). Only its
  SHA-256 is stored (`McpAccessTokens`, host-local, carried across a library
  replacement, never in a portable archive). It is returned once, by the request
  that makes it, and is never logged.
- Scope `read` gives `nostos:read`; `read_write` gives `nostos:read nostos:write`.
- Every request looks the token up again, so a revoked token gets `401` on its
  next call. "Last used" is written at most once a minute.
- No token, a malformed one or an unknown one: `401` with
  `WWW-Authenticate: Bearer realm="Nostos"` (`error="invalid_token"` for a wrong
  one). After 10 failed attempts from one address in 10 minutes, that address
  gets `429` with `Retry-After` until the window ends, the same rule as the
  Cloud OPDS credentials; checks are also capped at 600 a minute in total.

| Method | Path | Does |
| --- | --- | --- |
| `POST` | `/api/mcp/tokens` | Make one from `{ name, scope: "read" \| "read_write" }` (name 1–80 characters, at most 100 tokens). `201` with `{ details, token }` and `Cache-Control: no-store`; `details` is a connected app (below) |

Listing and revoking go through the connected-apps routes below; each token
is one app of kind `token`.

Client setup (Settings shows these with the server's own address):

```bash
# Claude Code
claude mcp add --transport http nostos https://nostos.example/mcp --header "Authorization: Bearer nst_…"
```

```jsonc
// Cursor: ~/.cursor/mcp.json
{ "mcpServers": { "nostos": { "url": "https://nostos.example/mcp", "headers": { "Authorization": "Bearer nst_…" } } } }
// VS Code: .vscode/mcp.json
{ "servers": { "nostos": { "type": "http", "url": "https://nostos.example/mcp", "headers": { "Authorization": "Bearer nst_…" } } } }
// Claude Desktop: claude_desktop_config.json, through mcp-remote (needs Node.js;
// add "--allow-http" to args for a plain-http address that is not localhost)
{ "mcpServers": { "nostos": { "command": "npx",
  "args": ["mcp-remote", "https://nostos.example/mcp", "--header", "Authorization:${AUTH_HEADER}"],
  "env": { "AUTH_HEADER": "Bearer nst_…" } } } }
```

Any other client that speaks Streamable HTTP and can send a header (Hermes,
for example) connects the same way. Hosted apps such as claude.ai and ChatGPT
connect from their own servers and need a public address plus OAuth (below).

### SelfHosted OAuth for hosted apps (issue #1470)

A small OAuth 2.1 authorization server for `/mcp` only, so claude.ai and
ChatGPT can connect through a tunnel. Setup: [docs/mcp-tunnel.md](mcp-tunnel.md).

**Off unless `Mcp:PublicBaseUrl` is set** (`Mcp__PublicBaseUrl`) to an absolute
`https://` URL with no path. Anything else logs a warning and leaves it off.
That URL is the issuer and the base of every URL below; the protected resource
is exactly `{PublicBaseUrl}/mcp`. With it on, a `401` from `/mcp` carries
`WWW-Authenticate: Bearer realm="Nostos", resource_metadata="{PublicBaseUrl}/.well-known/oauth-protected-resource/mcp", scope="nostos:read nostos:write"`.

| Method | Path | Does |
| --- | --- | --- |
| `GET` | `/.well-known/oauth-protected-resource`, `…/mcp` | RFC 9728: `resource`, `authorization_servers: [issuer]`, `scopes_supported`, `bearer_methods_supported: ["header"]` |
| `GET` | `/.well-known/oauth-authorization-server` | RFC 8414: endpoints, `response_types_supported: ["code"]`, `grant_types_supported: ["authorization_code", "refresh_token"]`, `code_challenge_methods_supported: ["S256"]`, `token_endpoint_auth_methods_supported: ["none"]`, `client_id_metadata_document_supported: true` |
| `GET` | `/oauth/authorize` | `response_type=code`, `client_id`, `redirect_uri`, `code_challenge` + `code_challenge_method=S256` (required), `state`, `scope`, `resource` (if given, must be the resource). Shows the approval page (below) |
| `GET` | `/oauth/authorize/status?request=…` | What the approval page polls: `{ status: "pending" }`, `{ status: "done", redirect }` or `404 { status: "unknown" }` |
| `POST` | `/oauth/token` | Form. `authorization_code` (`code`, `client_id`, `redirect_uri`, `code_verifier`) or `refresh_token` (`refresh_token`, `client_id`). `{ access_token, token_type: "Bearer", expires_in: 3600, refresh_token, scope }`, `Cache-Control: no-store`. Errors are RFC 6749 JSON (`{ error, error_description }`, `400`) |
| `POST` | `/oauth/revoke` | RFC 7009, form `token`. A refresh token revokes its whole grant, an access token only itself. Always `200` |

- **Clients are client metadata documents (CIMD) only**: `client_id` is an
  https URL whose JSON names the same `client_id`, a `client_name` and its
  `redirect_uris` (https, or `http://127.0.0.1` / `localhost` / `[::1]` for an
  app on the owner's computer, any port). `token_endpoint_auth_method`, if
  given, must be `none`. The fetch is https only (also after up to three
  redirects), to public addresses only (checked before the request and again
  when the socket connects, so loopback, private, link-local, CGNAT, multicast
  and ULA addresses are refused after DNS too), no proxy, 5 seconds, 16 KiB. A
  good document is cached 10 minutes. No dynamic client registration.
- `redirect_uri` must match one listed exactly (loopback: any port). Until the
  client and its redirect URI check out, errors are shown on a page and never
  redirected; after that they go back as `error=…&state=…&iss=…`.
- **Owner approval instead of a login.** Authorize creates a pending request
  (10 minutes, at most 20 waiting) with a four-letter code and shows a
  self-contained page: "Claude wants to connect to your Nostos… approve the
  request with code KMRT" in Settings → Connected apps. The page polls the
  status; approved, it redirects with `code`, `state` and `iss`; denied or
  expired, with `error=access_denied`. Pending requests and codes live in
  memory.
- **Codes and tokens.** A code lasts 60 seconds and works once (PKCE S256,
  `client_id` and `redirect_uri` must match); a second use revokes what the
  first made. Access tokens are `nsa_…` (1 hour), refresh tokens `nsr_…`
  (60 days unused); both are 256 random bits and only their SHA-256 is stored
  (`McpOAuthGrants`, `McpOAuthTokens`, host-local like access tokens). Every
  refresh replaces the refresh token; presenting a used one revokes the grant
  (`invalid_grant`).
- **Scopes.** `nostos:write` asked for (or no scope) is "read and write", only
  `nostos:read` is "read only"; the owner picks either when approving. The
  token answers with the granted `scope`, and the access token scheme puts it
  in the same `scope` claim as access tokens, so `McpScopes.Enforce` applies.
- **One grant is one connected app** (`kind: "oauth_app"`, the client's name).
  Revoking it in Settings deletes its tokens: the next `/mcp` call and the
  next refresh fail.
- Authorize, status, token and revoke are rate-limited per address (20, 120,
  60 and 60 a minute).

**The owner's side** (`/api/mcp/oauth`, SelfHosted, always mapped, `404` on the
public host):

| Method | Path | Does |
| --- | --- | --- |
| `GET` | `/api/mcp/oauth` | `{ enabled, connectorUrl, requests: [{ id, code, clientName, clientId, clientHost, redirectHost, requestedScope, createdAt, expiresAt }] }` |
| `POST` | `/api/mcp/oauth/requests/{id}/approve` | `{ scope: "read" \| "read_write" }`: `204`, or `404` when the request is gone or decided |
| `POST` | `/api/mcp/oauth/requests/{id}/deny` | `204` or `404` |

**The public host.** When a request's host and port (or a forwarded host from
a trusted local proxy) are the `PublicBaseUrl` ones, or Tailscale marks it as
a Funnel request (`Tailscale-Funnel-Request`, even with OAuth off), only `/mcp`,
`/oauth/authorize`, `/oauth/authorize/status`, `/oauth/token`, `/oauth/revoke`
and the three `/.well-known/oauth-*` documents answer; everything else,
including the app shell, `/api`, `/opds` and the health checks, is `404`. The
tunnel should restrict paths as well; this check is the backstop.

**Tools.** Each wraps Ask Nostos capabilities through `AssistantCapabilityRegistry`,
so validation and limits are the same; only the result shape differs.

| Tool | Reads or writes | Wraps |
| --- | --- | --- |
| `search` | read | `knowledge_search` (notes, topics, book text in one list) |
| `fetch` | read | `knowledge_read_evidence` |
| `find_quote` | read | `book_text_find_quote` |
| `overview` | read | `library_overview` + `knowledge_overview` |
| `list_books` | read | `library_list_books`, or `library_resolve_book` for an ISBN |
| `get_book` | read | `library_get_book` |
| `list_notes` | read | `notes_list_for_book`, in page order |
| `list_topics` | read | `topics_list` / `topics_search` |
| `get_topic` | read | `topics_get`, with a citation on every linked note |
| `list_drafts` | read | `writings_list` |
| `read_draft` | read | `writings_read` |
| `reading_list` | read | `reading_list_list` |
| `save_note` | creates a note | `notes_capture` |
| `save_passage` | creates a passage (a titled stretch of a book) | `notes_save_passage` (MCP only) |
| `create_draft` | creates a Studio document | `writings_create` |
| `add_to_reading_list` | creates a To read entry | `reading_list_add` |
| `add_book` | creates a book (paper, or an ebook/audiobook without its file) | `library_create_or_match_book` |
| `update_book` | edits a book's details | `library_edit_book_details` (MCP only) |

Tools call the registry with `AssistantSurface.Mcp`, so a capability that is
not marked for MCP cannot be reached from `/mcp` even by name. App links in
results (`/studio?writingId=…`, `/library/to-read`) come back absolute.

`search` and `fetch` keep the shape ChatGPT deep research expects (`results`
with `id`, `title`, `text`, `url`; `fetch` by `id`). Result ids are
`note:<id>`, `topic:<id>` and `text:<book>:<ordinal>:<sha256>:<extractor>`.

**Citations.** Every note and book passage carries `citation`: `book`,
`author`, `year`, `location` (when Nostos knows it: a verified PDF page, a
paper book's page, a verified audio time, an EPUB chapter heading), `url` (an
absolute link into this Nostos, from the request's origin), `link` (the
Studio's own `nostos:cite/<book>?p=…&loc=…&as=quote|note|reference&src=<note>`)
and `markdown` (`[words](link)`). A draft that uses `markdown` has live Studio
citations that export as footnotes.

**Writes.** `save_note` saves through the capture path with client id
`nostos-mcp`, `CaptureSource = "mcp"`, and an idempotency key derived from
the request (book, words, quote, topics), so a resent call keeps one note.
`save_passage` keeps a passage (`Kind = "passage"`, as the reader's Passage
mark does) from a `find_quote` `quoteRef` and a title: the words are the
book's own, and both ends come from the book text index, as epub.js point
CFIs for an EPUB (written on the server from the spine resource and the
block's text offset, checked against the quote's words) or as the first and
last PDF page. It needs the book's own file and text; a quote found in another
edition (a paper book's) is refused, with `save_note` as the way to keep it.
Same idempotency as `save_note`, with the title in the key.
`create_draft` never changes an existing document; a resend with the same
name, folder and words within ten minutes returns the document it already
made (`duplicate: true`). `add_to_reading_list` returns an existing entry
for the same ISBN, or the same title and author. A quote saved this way is
found and drawn in the reader like one Ask Nostos saved.
`add_book` runs the strict create-or-match path with an idempotency key derived
from the request and never looks metadata up elsewhere: a book with the same
ISBN, or the same title and author, comes back as `outcome: "matched"` with
`duplicate: true`; an ambiguous match adds nothing and returns `candidates`
(`outcome: "choose"`), and the app calls again with `confirmedBookId` or
`forceCreate: true`. When the book is in the library, a To read entry that
matches it moves there the way **Got it** does (`POST
/api/reading-list/{id}/acquired`: its reason becomes a note on the book).
`update_book` sets title, subtitle, author, description, published date, ISBN,
publisher, language, series and volume through `ILibraryService.UpdateBookAsync`
(the `PUT /api/books/{id}` path). It only fills details that are empty unless
the call passes `replace: true`, and returns `changed` (each field with `from`
and `to`), `kept` (details it left because they had a value) and `undo` (the
`update_book` arguments that put the old values back). It is annotated
destructive because `replace` overwrites. There is no cover-by-URL: Nostos has
no server-side cover download for an arbitrary URL with SSRF protection.
Writes advance the library revision, so open tabs update through
`/api/library/changes`.

**Services** come from the HTTP request's scope, so a host that binds the
library per account in that scope binds MCP calls the same way.

---

## Library — `/api/books`, `/api/collections` (issue #34)

All book and collection routes forward to the canonical `ILibraryService`;
the endpoint layer holds no domain rules. Errors map through
`LibraryHttpMapper` to Problem Details (error code in `title`, reply in
`detail`): `invalid_*` → 400, `*_not_found` → 404, the conflict family
(`identity_conflict`, `duplicate_identifier`, `confirmation_required`,
`collection_name_conflict`, `collection_cycle`, `collection_has_children`,
`book_in_use`) → 409, everything else → 422.

### Books

| Method   | Route             | Status codes |
| -------- | ----------------- | ------------ |
| `GET`    | `/`               | 200 `PaginatedResponse<BookDto>` (filter/sort/search/page/pageSize/collectionId) |
| `GET`    | `/{id}`           | 200 `BookDto` / 404 |
| `POST`   | `/`               | 201 created / 200 matched (`CreateBookDto`, legacy permissive) |
| `PUT`    | `/{id}`           | 200 `BookDto` / 400 / 404 / 409 |
| `PUT`    | `/{id}/progress`  | 200; percentage validated 0–100 (400), `FinishedAt` aligned |
| `DELETE` | `/{id}`           | 204 / 404 / 409 `book_in_use` (queued or noted) |
| `GET`    | `/lookup/{isbn}`  | 200 `CreateBookDto` prefill / 400 invalid ISBN / 404 |

Create-or-match precedence: exact normalized ISBN/ASIN → single exact
title+author → create. Type rules require audiobooks to carry an ASIN and
physical/ebook books an ISBN (`invalid_book_identity` otherwise). Identifier
changes that collide with another book return `duplicate_identifier`.

### Collections

| Method   | Route   | Status codes |
| -------- | ------- | ------------ |
| `GET`    | `/`     | 200 flat `CollectionDto[]` |
| `GET`    | `/{id}` | 200 `CollectionDto` / 404 |
| `POST`   | `/`     | 201 (duplicate sibling returns the existing collection) |
| `PUT`    | `/{id}` | 200; 409 `collection_cycle` / `collection_name_conflict` |
| `DELETE` | `/{id}` | 204; 409 `collection_has_children`; books are unlinked, never deleted |

---

## Error Handling

All errors follow the Problem Details standard:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
  "title": "An unexpected error occurred.",
  "status": 500
}
```

Validation errors return `400 Bad Request` with `{ "error": "message" }`.

Library errors use the Problem Details shape with the domain error code in
`title` and the human-readable reply in `detail`; the Library section above
lists the status mapping.
