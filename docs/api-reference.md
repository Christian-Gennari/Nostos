# Nostos — API Reference

All endpoints return JSON. Base path: `/api` (except OPDS at `/opds` and MCP
at `/mcp`).

> **Coverage note (2026-08-12):** this reference predates several shipped
> surfaces — Backup, MCP, and the issue #34 canonical
> library service. The Books / Notes / Collections / Topics / Writings /
> OPDS sections below remain accurate (inline corrections noted where the
> library service changed behavior); the sections at the end cover Backup,
> MCP, and Library. The authoritative route tables live in
> [`Nostos.Backend/_docs/endpoints.md`](../Nostos.Backend/_docs/endpoints.md).

---

## Books — `/api/books`

### `GET /api/books`

List books with filtering, sorting, search, and pagination.

| Query Param | Type   | Default  | Description                                    |
| ----------- | ------ | -------- | ---------------------------------------------- |
| `search`    | string | —        | Search by title or author (LIKE)               |
| `filter`    | string | —        | `Favorites`, `Finished`, `Reading`, `Unsorted` |
| `sort`      | string | `Recent` | `Recent`, `Title`, `Rating`, `LastRead`        |
| `page`      | int    | 1        | Page number                                    |
| `pageSize`  | int    | 20       | Items per page                                 |
| `collectionId` | guid | —      | Restrict the listing to one collection (subtree-inclusive: descendants match too) |

**Response:** `PaginatedResponse<BookDto>` — `{ items, totalCount, page, pageSize }`

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
available through the MCP tool `library_create_or_match_book`.

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
ratings/reviews, metadata and collection memberships all belong to the book and
are untouched.

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

For audio files, metadata (chapters, duration) is extracted automatically via ATL.NET.

Uploading a file to a multi-track audiobook replaces its tracks: a book holds
one file or a track list, never both.

### `GET /api/books/{id}/file`

Download/stream the book file. Supports HTTP range requests for streaming.
`404` for a multi-track audiobook, which has no single file; play it through
the track route below.

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
  "selectedText": "string? (highlighted text)"
}
```

**Response:** `201 Created` with `NoteDto`

### `PUT /api/notes/{id}`

Update a note. Re-processes `[[topic]]` links.

**Body:**

```json
{
  "content": "Updated content with [[NewTopic]]",
  "selectedText": "string?"
}
```

### `DELETE /api/notes/{id}`

Delete a note and its topic links.

**Response:** `204 No Content`

### E-reader highlight import — `/api/notes/imports`

Settings → Library & data → E-reader highlights opens the review dialog. Select
one Kobo `KoboReader.sqlite` or several KOReader `metadata.*.lua` sidecars. The
server detects the format from the contents. Kobo files are read-only, with a
256 MB upload limit; KOReader sidecars have a 2 MB limit.

| Method | Path | Behavior |
| --- | --- | --- |
| `POST` | `/api/notes/imports/preview` | Read multipart field `file`; return `{ source, books }` without writing. |
| `POST` | `/api/notes/imports/commit` | Read the same `file` and a JSON `decisions` form field; import only decided books. |
| `GET` | `/api/notes/imports/batches` | Return the five most recent imports that still have notes, with note/book counts. |
| `DELETE` | `/api/notes/imports/batches/{id}` | Undo a batch; return `{ removed }`, or `404` if it no longer exists. |

Preview books include `sourceKey`, title/author, annotation/new counts, and
candidates. `match` is `exact` for one ISBN or title-and-author identity,
`remembered` for a previously confirmed destination, `suggested` for a match
that needs confirmation (including multiple editions), or `none`.

The commit's `decisions` is an array. Use `{ "sourceKey": "…", "bookId": "guid" }`
to select a library book, or `{ "sourceKey": "…", "create": true }` to add a
physical book from the device metadata. Omitted books are left out. Books and
notes are added only on commit. Confirmed destinations are remembered.

Commit returns `{ batchId, source, books }`. Each result book reports its
`status` (`imported` or `skipped`), destination, imported/duplicate counts,
whether it was created, and any message. An optional `batchId` form field joins
later selected files to the first file's batch so one Undo covers the selection.
An import that adds nothing creates no new batch.

Re-import does not duplicate notes. Undo removes the batch's notes and their
import receipts, allowing them to be imported again; newly added books and
remembered destinations stay in the library. Kobo dog-ears, stylus markup and
deleted rows are ignored. The original `POST /api/notes/import/koreader` endpoint
remains available for direct single-sidecar imports.

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

The shared browser client also has an optional GET /active-import discovery
request when it has no local resume record. That route is not registered by
the public SelfHosted endpoint group above; discovery after local browser data
is lost therefore depends on the host and is not guaranteed by this API
reference.

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
to the public internet under this model. `Opds:Enabled=false` removes the route
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

During a restore the application enters maintenance mode: `/api` (and the MCP
route, when enabled) return `503`
`{ "error": "Application is in maintenance mode during restore." }`.

---

## MCP — Model Context Protocol

Opt-in (`Mcp:Enabled`, default disabled) bearer-authenticated **Streamable
HTTP** endpoint at `/mcp` (configurable via `Mcp:Path`). The bearer token is
resolved exclusively from the `Mcp:ApiKeyEnvironmentVariable` environment
variable (default `NOSTOS_MCP_TOKEN`) at startup; enabling MCP without the
token fails startup closed. Tools are discovered from the assembly and
registered as `mcp__nostos__*` (double underscore).

The shipped surface is the **11 Library tools** below. Library
tools: `library_list_books`, `library_get_book`, `library_resolve_book`,
`library_create_or_match_book`, `library_update_book`,
`library_list_collections`, `library_get_collection`,
`library_create_collection`, `library_rename_collection`,
`library_move_collection`, `library_delete_collection`. Responses use the
`{ reply, data, stateVersion, duplicate }` envelope; `duplicate=true` only on
receipt replay. Every library mutation requires a caller-supplied
`idempotencyKey` and is exact-once on `(clientId, idempotencyKey)` with the
fixed client `nostos-mcp`. Full contracts:
[`docs/library-mcp-contracts.md`](library-mcp-contracts.md) and
`Nostos.Backend/_docs/endpoints.md`.

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
