# Library MCP Contracts (issue #34 — frozen design)

Status: FROZEN 2026-08-12. Changes require reopening the design discussion.

## 1. Goal and boundaries

Give Hermes (and any authenticated MCP client) safe natural-language access to the
Nostos library: add books, attach files, manage hierarchical collections. Follows the
reading-training doctrine: Nostos owns the domain; one canonical service; REST, Angular,
MCP call the same service; MCP stays generic, authenticated, optional; connectors stay thin.

**v1 scope — 11 new tools** (5 book + 6 collection). Out of scope, explicitly:
notes/writings/concepts/backup/OPDS MCP tools, `library_delete_book`, automatic merge,
collection ordering, binary-over-MCP, REST authentication (loopback-only invariant),
book-sources synchronization (Nostos never reads `~/.hermes`).

## 2. Tool manifest

Read-only (never mutate):
1. `library_list_books` — args `filter`, `sort`, `search`, `page = 1`, `pageSize = 20`, `collectionId = null`. Validation: `page >= 1`; `pageSize` clamped 1..100. Returns canonical `BookDto` page.
2. `library_get_book` — arg `bookId`. Returns canonical `BookDto`.
3. `library_resolve_book` — args `isbn = null`, `asin = null`, `title = null`, `author = null`, `includeExternalMetadata = true`. Read-only; returns `resolution: exact_match | candidates | not_found | identity_conflict`, `matchedBook`, `candidates[]`, `prefill`, per-candidate match reason. Never mutates.
4. `library_list_collections` — no args. Flat canonical list `{id, name, parentId}`.
5. `library_get_collection` — arg `collectionId`.

Mutations (all require caller-supplied `idempotencyKey` as first argument, no default):
6. `library_create_or_match_book` — `idempotencyKey`, `type`, `title`, optional standard metadata from `CreateBookDto`, `collectionId = null`, `confirmedBookId = null`, `forceCreate = false`. Returns canonical `bookId` + `outcome: created | matched | confirmation_required`.
7. `library_update_book` — `idempotencyKey`, `bookId`, optional mutable metadata. Explicit nullable-field semantics so `collectionId` etc. can be cleared.
8. `library_create_collection` — `idempotencyKey`, `name`, `parentId = null`.
9. `library_rename_collection` — `idempotencyKey`, `collectionId`, `name`.
10. `library_move_collection` — `idempotencyKey`, `collectionId`, `newParentId = null`. Server-side cycle detection.
11. `library_delete_collection` — `idempotencyKey`, `collectionId`, `confirm` (required true). Books survive and are unlinked; response reports affected book/child counts.

Tool-count manifest: after phase 2 = 28 (23 reading + 5 book); after phase 3 = 34. Asserted in tests.

## 3. Identity normalization

- **ISBN**: strip spaces/hyphens, uppercase terminal `X`, accept only valid ISBN-10/ISBN-13 checksums → `NormalizedIsbn`, filtered unique index where non-null.
- **ASIN**: trim + uppercase, `[A-Z0-9]{10}` → `NormalizedAsin`, filtered unique index where non-null.
- **Title**: Unicode normalize, trim, collapse internal whitespace, case-fold, normalize punctuation spacing. Do NOT strip subtitles automatically.
- **Author**: Unicode normalize, trim, collapse whitespace, case-fold. Do NOT strip accents or reorder names.
- **Never** unique-constrain normalized title/author (legitimate duplicate editions/copies exist).

## 4. Resolution precedence (create-or-match)

1. Conflicting identifiers (ISBN → book A, ASIN → book B) → `identity_conflict`, never mutate.
2. Exact ISBN or ASIN match → auto-match, `outcome = matched`.
3. Exactly one exact normalized title+author match, no identifier conflict → auto-match, `outcome = matched`.
4. Multiple exact title/author matches → `confirmation_required` + candidates. Never choose by rating/date/type/collection.
5. Title-only, fuzzy title, author/edition/translator mismatch → candidates, never auto-match.
6. No plausible candidates → auto-create if: valid ISBN/ASIN, or title+author, or `forceCreate = true`. Bare title with ambiguous external metadata → confirmation, never guess.

Confirmation flow: first call may return `confirmation_required`; a follow-up call uses a **new** idempotency key with `confirmedBookId` (use existing) or `forceCreate = true` (create anyway). Corrected/confirmed commands must never reuse the first key (failed receipts may replay).

Post-creation duplicates: unique ISBN/ASIN indexes prevent strong-identity dups; title/author dups remain possible and sometimes legitimate. No automatic merge — ever.

## 5. Idempotency and envelopes

- New `LibraryCommandReceipt` (separate table; do NOT share `ReadingCommandReceipt`), unique `(ClientId, IdempotencyKey)`, fixed MCP client id `nostos-mcp`.
- New `LibraryState` version (separate from reading state version; never globally atomic).
- Envelope identical to reading: `{ reply, data, stateVersion, duplicate }`.
  - `duplicate = true` ONLY on receipt replay.
  - Matching an existing book = `outcome: matched`, `duplicate: false`.
- Commit state change + receipt in one transaction; `DbUpdateException` on receipt insert converges on stored winner; same-key concurrent requests produce one row + one receipt.
- Bound client-id/key/response sizes (see reading receipt limits; keep response payloads small).
- MCP tools delegate exactly once to `ILibraryService`, return the envelope unchanged; no EF, no filesystem, no lookup logic, no validation in the tool class.

### 5.1 Receipt retention boundary (issue #51)

Library command receipts are retained for a bounded window only; retention is part of the contract, not invisible cleanup.

- Same-key replay is exact and frozen (`duplicate = true`, stored response replayed) **only while its receipt is retained**.
- Receipt retention defaults to **90 days** and is additionally **count-bounded at 10,000 receipts** (oldest first); both bounds are configurable under `LibraryReceiptRetention` and clamped to safe ranges.
- After expiry (or eviction past the count cap) the key is treated as **new**: the command executes again against current state, converging through the library's normalized identities and not-found/no-op rules.
- **Clients must not treat idempotency keys as permanent command identifiers.** A command whose replay window has closed may legitimately re-execute; designs that need durable provenance must record the canonical `bookId`/`collectionId` returned in the response.
- The retention policy is library-only. Reading Training receipts and import receipts are deliberately not pruned — some reading commands are not effect-idempotent after receipt loss.

## 6. Stable error codes

`invalid_idempotency`, `book_not_found`, `collection_not_found`, `invalid_book_identity`,
`identity_conflict`, `confirmation_required`, `duplicate_identifier`, `invalid_collection_parent`,
`collection_cycle`, `collection_name_conflict`, `book_in_use`, `lookup_timeout`.

Added by the work-membership override (issue #143, REST-only for now):
`invalid_work_link` (a book linked to itself), `target_book_not_found`.

HTTP mapping for REST mirrors the reading ToHttp mapping (409 family / 400 invalid_* / 404 *_not_found / default 422).

## 6.1 Work membership override (issue #143, REST-only)

Automatic grouping by normalized title+author stays the default. `POST
/api/books/{id}/work/link` and `POST /api/books/{id}/work/unlink` are the
explicit correction, and they are **not** MCP tools: the frozen v1 manifest above
is unchanged and the MCP tool-count assertions still hold.

- **Link merges groups, not books.** The target's work survives; every member of
  the source work moves onto it. `WorkId` is never written by a client — the
  service owns the merge, the orphan cleanup and the state-version bump, exactly
  as it does for collection membership.
- **Unlink always leaves a valid work.** The book gets a new work built from its
  own current title/author; there is no "no work" state.
- **Book-level state is untouchable** by either operation: files, progress,
  notes, ratings/reviews, metadata and collection memberships are per book.
- **Idempotent:** an already-grouped pair, or a book already alone, succeeds as a
  no-op without a state-version bump. Repeating a link with the arguments
  swapped is the same no-op, so the pair does not need a canonical order.
- **A metadata edit is not the workaround.** These operations consult no
  metadata at all, so a wrong grouping is repairable without editing titles and
  without deleting and re-importing a book.

## 7. Collections

- Identity: normalized `(name, parentId)`. Duplicate sibling on create → return existing match (documented behavior); rename/move collisions → `collection_name_conflict`.
- Move: null parent = root; reject self-parenting and descendant cycles (`collection_cycle`).
- Delete: requires `confirm = true`; books survive and are unlinked; child-collection behavior explicit and tested; response reports affected counts.
- Ordering unsupported; list order is not persistent.

## 8. File attachment

- Bulk data plane stays on REST multipart: `POST /api/books/{id}/file` after MCP creation, then `library_get_book` to verify.
- Upload hardening required: single extension/content-type validation source; cancellation support; clear BOTH `LocationsJson` and `ChaptersJson` on replacement; return stable file metadata (size/type/hash if practical); re-upload of same content safe.
- No base64-in-MCP, no arbitrary server-path MCP argument, no Nostos dependency on `~/.hermes/book-sources/`.

## 9. Sanctioned Hermes sequence (replaces the old prohibition)

1. `library_resolve_book` (or `library_list_books` search) — establish membership.
2. `library_create_or_match_book` — create or match (confirmation only when ambiguous).
3. Optional: locate a confident matching file under `~/.hermes/book-sources/` and upload via REST; ambiguous files require confirmation.
4. Optional: collection placement (during creation or via `library_update_book`).
5. `reading_add_book(bookId, mode, makeDefault, idempotencyKey)` — queue assignment, unchanged.

Rules: `reading_add_book` never creates books; no combined create-and-queue tool; library creation and queue assignment are two domain transactions; retry only the failed step.

## 10. Legacy endpoint fixes required before MCP exposes the domain

- Book delete ordering (DB restrict on reading FKs means delete is blocked for used books; ensure file deletion never precedes row deletion when delete is possible).
- Progress validation: bounded percentages; clearing `FinishedAt` when progress drops below 100.
- Pagination validation (`page >= 1`, `pageSize` clamp).
- CancellationToken end-to-end (tools → service → repository → external lookup → file storage).
- `BookLookupService`: named HttpClient with explicit shorter timeout + typed `lookup_timeout`; ISBN validated before external calls.
- REST book/collection mutations route through `ILibraryService` (no endpoint-side logic duplication).
