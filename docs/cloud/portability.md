# Portability and migration

Nostos portable archives use the provider-neutral `.nostos` format to move library data between installations. This guide describes the customer flow, the public implementation, and the limits of the evidence. A protocol ceiling is not a promise that every host or account can accept an archive that large.

The public SelfHosted implementation is in this repository. The Cloud hosting adapter and its account-plan rules live outside it. The hosted alpha advertises library migration, but only one SelfHosted → Cloud import is recorded as completed; the other hosted paths listed below remain unverified.

---

## Customer guide

### Find the controls

On a host that advertises library migration, open **Settings → Library & data → Move your library**. The card contains both export and import.

When the host advertises Cloud portable export but not library migration, the UI shows an export-only card. Use **Settings → Library & data → Data export → Export all my Nostos data**. This is a separate export path; it is not evidence that the migration export flow has been exercised.

### Export a library

1. Open **Settings → Library & data → Move your library**.
2. Under **Export library**, choose **Export library**.
3. When the status says **Your export is ready**, choose **Download archive** if the browser did not start the download automatically.

The result is a `.nostos` file. A completed migration export artifact is retained for 24 hours by the public implementation. If the host shows the export-only card instead, the button is **Export all my Nostos data**.

### Import, replace, and activate

1. Open **Settings → Library & data → Move your library**.
2. Under **Import library**, choose **Import library…** and select the `.nostos` archive.
3. Wait while the browser hashes and uploads the file and the host prepares and checks it.
4. If the destination is empty, the UI requests activation automatically after verification. There is no separate **Activate** button. Keep the page open while **Switching libraries…** is shown.
5. If the destination already has library data, review the current and incoming counts in **Replace this library?**. Choose **Replace library** to confirm and start activation, or **Cancel** to leave the current library in place.
6. The UI shows the completed state briefly, then reloads the app; no further action is required.

There is no merge mode. A populated destination is retained as a recovery copy for seven days when replacement succeeds. If the destination changed while the import was preparing, the host can require a fresh review instead of replacing that newer state. The shared UI presents the confirmation; revision checks and recovery-copy creation are server-side. These behaviors are implemented in the public SelfHosted host, but hosted behavior beyond the one recorded import has not been verified.

### Restore the previous library

The shared Angular Settings and transfer templates in this repository do not contain a **Restore previous** button or recovery-copy list, so there is no customer click path to give for this operation. The public SelfHosted API does expose recovery list, status, and restore routes; a restore request needs the recovery ID, the current destination revision, and explicit replacement confirmation. It replaces the current portable library with the retained copy. A recovery copy expires after seven days and cannot be restored twice.

These API routes do not establish that Cloud exposes the operation to customers. Restore after activation has not been verified on the hosted alpha. See the [API reference](../api-reference.md) for the SelfHosted routes.

### Browser resume limits

The browser stores job/session identifiers and file identity in origin-local storage; it does not store the archive bytes. While the page stays open, a paused upload can continue with **Resume upload**. After a reload, return to the same Nostos origin with its browser storage intact and, when prompted, select the same archive again. The client checks the file size and whole-file SHA-256, then asks the host which chunks are missing. A different file cannot continue that upload.

The server transfer session expires after 24 hours. The public client and SelfHosted browser suite cover refresh and same-file reselection; recovery after clearing site data or switching browser profiles is not established for every host. Only one import can hold the active-tab lease across tabs for the same browser origin. The shared client also has an optional active-import discovery request, but that route is not mapped by the public SelfHosted migration endpoint group.

### Size limits and account plans

The public migration contract sets these hard ceilings:

| Limit | Contract ceiling |
|---|---:|
| Compressed archive | 512 GiB |
| Aggregate media in the archive | 512 GiB |
| One archive entry | 16 GiB |
| Relational data payload | 64 MiB |
| Manifest | 4 MiB |
| ZIP entries | 20,000 |
| Total uncompressed archive data | 1 TiB |
| Nominal upload chunk | 4–64 MiB; 16 MiB default |
| Transfer session lifetime | 24 hours |

These are validation and protocol ceilings, not tested practical maximums. A host can reject an archive below them when destination capacity is insufficient; replacing a populated library also reserves space for its recovery copy. The public repository does not define a numeric Cloud plan quota or show how the private Cloud host applies plan limits. Use the host's preflight result for a specific archive; near-plan-limit behavior has not been verified on the hosted alpha.

The older single-request import endpoint is separate from the chunked migration flow and is limited by the SelfHosted Kestrel request-body cap of 4 GiB. Do not use that number as the limit for the migration upload API.

### What an archive contains

A `.nostos` archive contains a manifest with format/version and checksum information, library records, and the primary media files and covers that are stored for those books. Its data includes works and books with metadata and reading progress; collections and memberships; notes, source anchors, topics and note links; Writing Studio documents and folders; writing/note links; remembered e-reader book mappings; acquisition provenance; and the assistant capture-processing preference.

It does not contain local backup history or backup paths, absolute media paths, host/runtime settings, AI provider configuration or credentials, migration jobs or idempotency receipts, or derived caches such as search indexes, EPUB locations, and generated thumbnails. A portable archive is not a same-installation operational backup. The inventory is defined by [PortableArchiveModels](../../Nostos.Product/Services/Portability/PortableArchiveModels.cs) and [PortableArchiveService](../../Nostos.Product/Services/Portability/PortableArchiveService.cs).

The click labels and activation handoff are in [Settings](../../Nostos.Frontend/src/app/settings/settings.component.html), the [transfer host](../../Nostos.Frontend/src/app/library-transfer/components/library-transfer-host.component.html), the [import flow](../../Nostos.Frontend/src/app/library-transfer/components/library-import-flow.component.html), and the [activation controller](../../Nostos.Frontend/src/app/library-transfer/services/library-activation-controller.service.ts). The browser resume record is defined by [TransferResumeStore](../../Nostos.Frontend/src/app/library-transfer/services/transfer-resume-store.service.ts). Numeric ceilings come from [MigrationContractLimits](../../Nostos.Product/Services/Portability/MigrationContracts.cs) and [PortableArchiveLimits](../../Nostos.Product/Services/Portability/PortableArchiveLimits.cs).

### What has and has not been verified

- **Local SelfHosted browser coverage:** the disposable-instance suite in [library-transfer-browser-qa.md](../library-transfer-browser-qa.md) records export/download, empty import with automatic activation, populated replacement, same-file resume after reload, cancellation, and backend restart recovery using generated test libraries. It is not a Cloud production run.
- **Local large-archive measurement:** this document's existing measurement uses a synthetic archive over 4 GiB and local test storage. It measures archive read/write paths, not a full hosted browser transfer or a Cloud plan limit.
- **Hosted alpha:** issue [#676](https://github.com/Christian-Gennari/Nostos/issues/676) records the feature as deployed and advertised for early access, but not fully proved. Issue [#682](https://github.com/Christian-Gennari/Nostos/issues/682) records one completed SelfHosted → Cloud import. PR [#790](https://github.com/Christian-Gennari/Nostos/pull/790) records that import and activation completed before a backup-status error toast; PR [#791](https://github.com/Christian-Gennari/Nostos/pull/791) forwards the guard for that unsupported refresh request to public main.
- **Not verified on the hosted alpha:** restore after activation, any export, Cloud → SelfHosted, full round trips in both directions, the protocol maximum, and near-plan-limit admission. The #682 rescope explicitly leaves the broader proof for later.

The links above identify the code and evidence used for this guide. In particular, the shared UI is not evidence of a completed Cloud export or restore journey.

---

# Part 1: Current behaviour (shipped)

## Current HTTP API

The shipped portability API exposes:

```text
GET  /api/portability/export
POST /api/portability/import
```

- `GET /api/portability/export` streams a `.nostos` archive using `application/vnd.nostos.portable+zip`.
- `POST /api/portability/import` accepts an archive and returns a structured import result or typed validation error.
- A non-empty destination returns HTTP 409 (`destination_not_empty`).

Import sends the raw `.nostos` archive bytes as the HTTP request body. The frontend sets `Content-Type: application/vnd.nostos.portable+zip`; the endpoint passes `request.Body` directly to `ImportAsync` and does not parse a multipart envelope.

The backend currently configures Kestrel with a 4 GiB maximum request body size in `Nostos.Backend/Program.cs`:

```csharp
MaxRequestBodySize = 4L * 1024L * 1024L * 1024L;
```

Therefore, although the archive service itself has larger validation limits, the currently shipped import endpoint **does not support portable archives larger than 4 GiB**. A larger archive cannot reach the import service through the existing single-request HTTP endpoint.

This is one of the primary constraints the durable transfer protocol below removes.

## Durable library transfer jobs

The SelfHosted reference implementation of the epic #676 transfer protocol is
implemented in the product and reachable over HTTP. It is advertised to the
frontend from the same `IMigrationPhaseAvailability` the migration routes
consult: `GET /api/runtime/capabilities` reports
`supportsLibraryMigration: true` when the SelfHosted phase handlers are
registered and migration is enabled; otherwise Settings hides the flow.
Activation, replacement, restore, and the mandatory recovery copy shipped with
#681 are described below. Cloud uses a separate private host adapter; the
hosted alpha advertises migration, but its plan rules and provider implementation
are not defined in this repository. See the verification status above.

### Routes

All routes live under `/api/portability/migration` and use the migration error
body (`{"error":"<stable_code>","message":"…"}`). The legacy routes above are
untouched.

| Method | Route | Success | Notable failures |
|---|---|---|---|
| `POST` | `/preflight` | `200` with `evaluation`, `reservationId`, `reservationExpiresAtUtc`, `chunkSizeBytes` | `409 migration_import_preparation_unavailable` while import is unavailable |
| `POST` | `/jobs` | `201` created / `200` idempotent replay | `409 migration_idempotency_conflict`, `409 migration_reservation_required`, `409 migration_too_many_jobs` |
| `GET` | `/jobs/{id}` | `200` status | `404 migration_not_found` |
| `POST` | `/jobs/{id}/cancel` | `200` | `409 migration_cannot_cancel` |
| `POST` | `/jobs/{id}/retry` | `200` | `409 migration_not_retryable` |
| `POST` | `/jobs/{id}/upload-session` | `201` / `200` replay | `409 migration_idempotency_conflict`, `409 migration_file_identity_mismatch` |
| `GET` | `/jobs/{id}/upload-session` | `200` with `receivedChunks` and compact `receivedRanges` | `410 migration_session_expired` |
| `PUT` | `/jobs/{id}/upload-session/chunks/{index}` | `200` with `alreadyPresent` | `400 migration_invalid_request`, `409 migration_chunk_conflict`, `410 migration_session_expired`, `416 migration_chunk_range_invalid`, `422 migration_chunk_hash_mismatch`, `507 migration_storage_exhausted`, `413` for an oversize chunk |
| `POST` | `/jobs/{id}/upload-session/complete` | `200` session status | `409 migration_invalid_state`, `409 migration_file_identity_mismatch` |
| `GET`/`HEAD` | `/jobs/{id}/export-download` | `200`/`206` range-enabled file | `404 migration_export_not_available`, `410 migration_export_expired` |
| `POST` | `/jobs/{id}/activate` | `202` accepted / replay | `409 migration_replacement_confirmation_required`, `409 migration_destination_conflict`, `409 migration_invalid_state`, `503 migration_activation_busy` |
| `GET` | `/jobs/{id}/activation` | `200` activation status | `404 migration_not_found` |
| `GET` | `/recovery` | `200` retained recovery copies | – |
| `GET` | `/recovery/{id}` | `200` recovery status | `404 migration_recovery_not_found`, `410 migration_recovery_expired`, `422 migration_recovery_corrupt` |
| `POST` | `/recovery/{id}/restore` | `202` accepted | `409 migration_recovery_restore_conflict`, `409 migration_replacement_confirmation_required`, `503 migration_activation_busy` |

Chunk requests carry `Content-Range: bytes <start>-<end>/<total>` and
`X-Nostos-Chunk-SHA256: <64 hex>`. The body is the raw chunk; there is no
multipart envelope, no `byte[]` buffering, and the request is streamed straight
to the engine. The chunk endpoint's own request-body cap is the configured
maximum chunk size, not the global 4 GiB cap, and it never raises the global
cap.

### Chunking, resume, and identity

- Default nominal chunk size is 16 MiB; the server accepts sessions between
  4 MiB and 64 MiB, configurable within those contract bounds.
- Chunks may arrive in any order. A retransmitted chunk with matching length and
  SHA-256 is idempotent (`alreadyPresent: true`); different bytes at the same
  index are a typed conflict and the original receipt and bytes are unchanged.
- A session binds an exact file identity (size + whole-file SHA-256 + optional
  client fingerprint). Resuming with a different file is rejected; the client
  reselects the file and uploads only the missing chunks reported by
  `GET …/upload-session`.
- `complete` re-reads every receipt, streams the whole archive once for length
  and SHA-256, and only then seals `archive.nostos`. A mismatch fails the job
  and no final archive is published.

### State machine

Import jobs run `Pending → Preparing → Transferring → Validating →
ReadyToActivate` and wait there for an explicit activation request.
`ReadyToActivate` carries a committed, durable prepared descriptor (staging id,
data/media hashes, counts) that survives a restart. Export jobs run `Pending →
Preparing → Transferring → Validating → Completed` and publish a sealed
artifact.

Activation, replacement and recovery restore shipped with #681:

- `POST …/jobs/{id}/activate` admits an owned job to
  `ReadyToActivate → Activating → Completed` through the activation contract
  below; the worker never activates on its own;
- a populated destination requires explicit confirmation bound to the
  destination revision and retains a mandatory seven-day recovery copy;
- `supportsLibraryMigration` is advertised by the SelfHosted host (the
  capability remains an explicit host decision, not phase inference);
- `POST /api/portability/import` is the only route that mutates a library
  outside the durable migration flow, and it still targets an empty
  destination only.

### Expiry, capacity, and limits

Server defaults (`Storage` configuration section, `TransferStorageOptions`):

| Setting | Default | Meaning |
|---|---:|---|
| `TransferPath` | `transfers` beside the resolved books root | Transfer root; absolute or relative to the content root. With the default books root this is `<content-root>/Storage/transfers`. |
| `ChunkBytes` / `MinChunkBytes` / `MaxChunkBytes` | 16 / 4 / 64 MiB | Accepted nominal chunk size and bounds. |
| `MaxConcurrentJobs` | 1 | Worker processing concurrency. |
| `MaxOutstandingJobs` | 10 | Hard ceiling on non-terminal jobs (and therefore sessions) per installation, enforced under the library admission lock inside the creation transaction; further creates return `409 migration_too_many_jobs`. |
| `DiskSafetyMarginBytes` / `DiskSafetyMarginPercent` | 1 GiB / 5% | Effective margin is the larger of the byte floor and the percentage of the volume. |
| `PreflightReservationMinutes` | 15 | Lifetime of an unclaimed preflight hold; claiming a reservation stops the clock. |
| `ExportRetentionHours` | 24 | Download retention after an artifact becomes available. |
| `PreparedImportRetentionHours` | 24 | Retention of committed prepared-import staging. |
| `CleanupIntervalMinutes` | 15 | Cleanup worker interval. |

Transfer sessions and jobs expire 24 hours after creation; an explicit retry
reactivates a failed/cancelled/expired job with a fresh attempt and a new
window. A repeated preflight atomically supersedes the previous *unclaimed*
hold, so repeated preflights cannot pile up reservations; claimed holds are
untouched and survive the preflight window.

### Operator notes

- **Transfer root.** The root is created on startup and validated: it must not
  be a filesystem root, the application content root, or an ancestor of it.
  Generated layout:

  ```text
  Storage/transfers/
  ├── uploads/<session-guid>/archive.part|archive.nostos
  ├── staging/<staging-guid>/{state.json,manifest.json,data/,media/}
  ├── exports/<job-guid>/library.nostos(.tmp)
  ├── detached/<guid>/
  └── locks/<guid>.lock
  ```

  No client-supplied name, header, ZIP path, or media reference is ever used as
  a filesystem path. Put the transfer root on the same volume as the library
  and size it for the archive plus one chunk plus the safety margin; capacity
  admission uses the actual free space of that volume.

- **Capacity.** Preflight reserves the declared archive + media + recovery
  estimate + one chunk + a fixed per-job overhead, and admission subtracts the
  global safety margin plus outstanding unmaterialized reservations from the
  volume's real free space. Already-materialized bytes are not double-counted.
  ENOSPC after admission still fails truthfully (`507
  migration_storage_exhausted`) and never leaves a partial receipt or artifact.

- **Cleanup.** The cleanup worker runs immediately at startup and every
  `CleanupIntervalMinutes`. It expires jobs/sessions/artifacts, releases
  expired unclaimed reservations, removes abandoned upload scopes, committed
  staging for terminal jobs, unreferenced export files, durably tombstoned
  staging areas, and generated legacy `nostos-portable-import-*` scratch
  directories. Age gates use the newest write anywhere inside a tree, and a
  live legacy import holds an exclusive `import.lock` for its lifetime, so a
  long upload is never mistaken for abandoned scratch. Unknown operator
  siblings and linked components are never touched; every delete is idempotent
  and retried on the next sweep.

- **Inspecting and cancelling a job.** Poll `GET
  /api/portability/migration/jobs/{id}` for state, progress, session
  (including received chunks), download availability, and the prepared-import
  descriptor. `POST …/cancel` is accepted in every pre-activation state,
  releases the reservation, and deletes uncommitted staging; verified chunks
  are retained until the session TTL for a possible retry. `POST …/retry`
  reactivates a terminal failed/cancelled/expired job and reuses retained
  verified chunks only when the file identity still matches.

- **Slow transfers.** No custom Kestrel data-rate policy is set for migration
  routes. Kestrel's defaults apply: a minimum request-body data rate of 240
  bytes/second with a 5-second grace period (`MinRequestBodyDataRate`), a
  30-second request-headers timeout, and the global 4 GiB body cap (the chunk
  route lowers its own cap to `MaxChunkBytes`). Deployments behind a reverse
  proxy should disable proxy request buffering for this route so a slow client
  streams directly to the app.

### Destination revision

The opaque destination revision bound into preflight and persisted on the job
is composed from the singleton `LibraryState.StateVersion` plus the portable row
counts. `LibraryRevision` is the **only** writer of `StateVersion`: every
committed create, update, or delete of portable user-owned state advances it
with one atomic SQL increment in the same transaction as the mutation —
including content-only edits, owned-value edits, bulk `ExecuteUpdate`/
`ExecuteDelete` paths, and commands whose caller needs the new value (the
helper returns it; callers never compute it). Inside an ambient transaction the
advance and the portable save share one savepoint, so a failed save rolls the
advance back with its own rows and a later successful save in the same
transaction advances again; a provider without savepoint support fails closed.
Host-only operational writes (jobs, sessions, chunk receipts, backups, host
provider settings, migration records) never advance it. A missing singleton row
is created at `"0"` before the first advance, and a corrupted (empty,
non-numeric, negative, or maxed-out) revision fails the mutation closed with
`library_revision_invalid` rather than being normalised. Every portable writer
takes the revision row before its portable rows, so concurrent writers
serialize deterministically. Activation compares the stored revision with the
current one and refuses to replace a library that changed after the import was
prepared.

**Legacy import transaction scope.** The compatibility `POST
/api/portability/import` already ran its relational apply, media publication
and verification inside one serializable transaction before this work (that is
how a failed import leaves the destination empty). The revision advance joins
that transaction at the relational save, so during a legacy import into an
empty library other portable writes for that library wait until the import
commits — the same window the import already held, with one additional
singleton row lock on PostgreSQL. Shortening it would require separating media
publication from the relational commit and is tracked as a follow-up.


## Archive layout

Portable archive is a ZIP container:

```text
library.nostos
├── manifest.json
├── data/
│   └── library.json
└── media/
    └── books/
        └── <book-guid-without-dashes>/
            ├── book.<supported-extension>
            └── cover.<supported-extension>
```

Archive paths are never used directly as filesystem destinations or storage keys. Import derives destinations from stable book IDs and sends staged streams through `IBookAssetStorage`.

### `manifest.json`

The manifest contains the format and data versions, export timestamp, product assembly version, entity counts, the data file length and SHA-256, and a canonical descriptor for each media entry. Each descriptor includes its book ID, logical kind, archive path, filename, content type, uncompressed length, and SHA-256.

Media is streamed into the archive. The manifest is written after the stream finishes, so large book and audio files do not need to be buffered in memory.

### `data/library.json`

This is an explicit provider-independent representation, not an EF entity graph. It preserves stable IDs and relationships for:

- works, books, and book metadata;
- reading progress, ratings, favorites, reviews, and timestamps;
- collections and nested collection membership;
- notes, source anchors, capture provenance, topics, and note links (stored under the archive's original `concepts` / `noteConcepts` JSON keys, which the Concepts → Topics rename deliberately left unchanged so older archives still import);
- Writing Studio documents and folder hierarchy;
- writing/note links (`WritingNotes`, introduced in DataVersion 2);
- remembered e-reader book mappings (`NoteImportBookLinks`, introduced in DataVersion 3);
- generic acquisition provenance and assistant capture-processing preference.

## Data classification

Portable exports include user-owned product data, source media files, and covers. They exclude reconstructed caches such as normalized search fields, EPUB locations, generated thumbnails, and library version bookkeeping.

Operational state is excluded: local backup records and paths, absolute media paths, idempotency receipts, job schedules, provider configuration, and runtime settings. Credentials and secrets are never serialized. The DTO has no fields for these values.

## Export guarantees

`PortableArchiveService` materializes the portable relational state inside one serializable read transaction through the active `NostosDbContext`. Every relational export query therefore observes a single revision; no media is read, hashed or copied while that transaction is open. The manifest `ExportedAtUtc` is captured inside that transaction, immediately after it begins and before the first relational query.

After the relational snapshot closes, referenced media is pinned by length, last-modified, entity tag and SHA-256. Each pin is re-verified immediately before the bytes are copied into the archive, the copied bytes are checked against the pinned length and hash, and storage metadata is observed again after the copy. Media absent on a metadata lookup (initial pin or copy start) or unopenable during the initial pin or copy-pass open fails closed with typed error `source_media_missing`. Once an initial pin observation is underway, a media revision mismatch against the pin — content, length or metadata changed after pinning, a disappearance detected by the pin's post-hash metadata check, or a disappearance detected by the post-copy metadata check — fails closed with typed error `source_media_changed`. No inconsistent archive is emitted.

Media is read through `IBookAssetStorage`, the manifest is written only after every referenced media stream succeeds, and the exporter never changes or removes source library data.

## Import guarantees and restore mechanism

Portable archive restore targets an empty destination library.

For SQLite restores, the import path uses a database transaction and validates the destination through `EnsureDestinationIsEmptyAsync` before restoring portable records.

If the destination contains library content or an assistant preference, import returns `destination_not_empty`; the shipped API never merges or replaces existing data implicitly.

Import validates the archive structure, versions, checksums, sizes, IDs, relationships, media references, and hierarchy before changing the destination. It stages and verifies media, writes relational state in a transaction, streams assets through `IBookAssetStorage`, re-reads the imported state, verifies stored media (including `CreatedAtUtc` on links), and then commits. If import fails before commit, it rolls back relational state and removes only the newly imported media.

## Service safety limits

`PortableArchiveService` enforces the following service-level constants in `Nostos.Product/Services/Portability/PortableArchiveService.cs`:

| Constant | Value | Description |
|---|---:|---|
| `MaxArchiveEntries` | 20,000 | Maximum total entries in ZIP container |
| `MaxManifestBytes` | 4 MiB | Maximum uncompressed bytes for `manifest.json` |
| `MaxDataBytes` | 64 MiB | Maximum uncompressed bytes for `data/library.json` |
| `MaxSingleEntryBytes` | 16 GiB | Maximum uncompressed bytes for any single entry |
| `MaxArchiveBytes` | 512 GiB | Maximum compressed archive staging capacity |
| `MaxUncompressedBytes` | 1 TiB | Maximum declared uncompressed total archive bytes |
| `MaxCompressionRatio` | 1,000 | Maximum compression ratio protecting against ZIP bombs |

These limits protect archive validation and extraction. They do **not** override the current HTTP request limit (4 GiB Kestrel body limit).

## Measured resource bounds

The export and prepared-import paths were measured on a generated library whose media total exceeds 4 GiB. No large archive is committed to the repository; the media is deterministic patterned data produced on demand by a synthetic `IBookAssetStorage`, and the SHA-256 of each entry is computed while the bytes are generated.

Measured on one machine and one run:

```text
hardware:  AMD Ryzen 5 4600H, 12 logical cores, 14 GiB RAM, NVMe storage
OS:        Ubuntu 24.04.4 LTS (X64)
runtime:   .NET 10.0.12
library:   97 media entries = 1 x 4,563,402,752-byte entry + 96 x 1 MiB entries
           (4,664,066,048 media bytes total)
```

| Metric | Export | Import preparation |
|---|---:|---:|
| archive bytes | 4,664,097,715 | 4,664,097,715 |
| explicit operation buffer high-water | 17,825,792 B (17 MiB) | 17,842,176 B (17 MiB) |
| buffer cap | 67,108,864 B (64 MiB) | 67,108,864 B (64 MiB) |
| physical synchronous sink writes | 0 | 0 (all source access is `ReadAtAsync`) |
| engine scratch | 0 bytes | 0 bytes |
| managed heap growth | 1,484,328 B | 387,216 B |
| process working-set growth (sampled) | 52,957,184 B | 72,216,576 B |
| duration | 10.75 s | 12.26 s |
| throughput | ~414 MiB/s | ~363 MiB/s |

The measured export budget high-water is the capture sink's 16 MiB synchronous-write buffer plus the 1 MiB copy buffer. A writer-only stress with the same entry inventory through `BoundedSynchronousCaptureSink` recorded a capture pending high-water of 7,750 bytes and a maximum single synchronous framework write of 7,734 bytes — the 16 MiB cap exists for a future runtime change and is not approached by current `System.IO.Compression` finalization. The import source observed 4,551 range reads, a maximum single request of 1,048,576 bytes, and 4,765,916,863 total bytes read (about 1.02x the archive size); the archive is never fetched whole and media is never buffered whole. The archive carried a ZIP64 end-of-central-directory record, and import preparation read it through the ZIP64 tail path.

The table is one representative run of four on the same machine; durations varied between about 11 s and 38 s with machine load, while the recorded high-water, call-count and scratch values were identical on every run.

How it was measured: export streamed to a counting, non-seekable, sync-forbidding sink that hashes and discards every byte. Prepared import ran over `FilePortableArchiveSource` (which reads with `RandomAccess.ReadAsync`) into a test staging provider that verifies each media item's declared length and SHA-256 and discards the bytes. The import measurement served the same archive from one test-owned temporary file (about 4.35 GiB, deleted afterwards); the engine itself wrote no scratch. The opt-in test is `PortableArchiveLargeMeasurementTests.Export_and_prepare_of_greater_than_4gib_archive_keep_bounded_memory_and_zero_scratch`, enabled with `NOSTOS_RUN_LARGE_PORTABILITY_TESTS=1`. A scaled 96 MiB variant runs in ordinary CI continuously.

What this does **not** claim or measure:

- It is not a process RSS ceiling. The 64 MiB contract covers Nostos-owned archive-I/O buffers; the CLR, EF Core and `ZipArchive`/`DeflateStream` runtime buffers are outside it.
- The legacy `POST /api/portability/import` endpoint is still limited by the 4 GiB Kestrel request-body cap and still stages a full local copy of the archive and its media in scratch. Only the range-backed prepared-import path measured here avoids that copy; large hosted transfers belong to the chunked migration protocol (epic #676).
- Remote/object-storage `IPortableArchiveSource` implementations, hosted providers, compression-heavy libraries, concurrent archive operations, and the EF object graph at the 20,000-entry limit were not measured.
- The real-Kestrel regression proves the shipped endpoint streams the archive without synchronous response IO; it does not measure network throughput.

## Archive hardening

Before writing imported library data, the reader rejects malformed manifests, unsupported format or data versions, duplicate or unexpected ZIP entries, unsafe paths, excessive entry counts or sizes, suspicious compression ratios, empty or duplicate IDs, invalid relationships, hierarchy cycles, missing referenced media, and data or media length/SHA-256 mismatches.

## Relationship to local operational backup

SelfHosted local backup remains useful for same-installation recovery because it can snapshot and restore SQLite directly. It is not an interchange format. Use `IPortableArchiveService` to migrate between Nostos installations; do not copy database files, storage paths, or provider backup artifacts as a portable archive.

## Archive versions

- Format version 1 / DataVersion 1: Base portable archive.
- Format version 1 / DataVersion 2: Adds `WritingNotes` membership relationships.
- Format version 1 / DataVersion 3: Adds `NoteImportBookLink` remembered book mappings.

Payload and manifest versions must strictly agree (`data_version_mismatch`). A payload declaring version < 3 carrying `NoteImportBookLinks` or declaring version < 2 carrying `WritingNotes` is rejected with `unexpected_version_data`. Older versions default omitted collections to empty.

---

# Part 2: Migration contract and implementation status

Epic #676 defines the guided library transfer flow between Nostos SelfHosted and Nostos Cloud.

The public repository contains the provider-neutral contract, durable job/session/chunk/artifact/reservation records, SelfHosted transfer API, import preparation, export artifact generation, activation, and replacement with a mandatory recovery copy. The local real-browser suite uses disposable SelfHosted instances; it is not hosted Cloud acceptance. The Cloud adapter is maintained outside this repository and the hosted alpha advertises migration. One hosted SelfHosted → Cloud import is recorded as completed; export, restore after activation, and Cloud → SelfHosted remain unverified. See the customer guide above and the Nostos #682 rescope for the evidence boundary. Destructive replacement in the public implementation requires explicit, server-checked confirmation.

## Goals and authenticated ownership boundary

The migration system must:

- remain strictly provider-neutral;
- define a resumable chunked transfer protocol with a 512 GiB archive ceiling; that ceiling is not evidence of a tested or plan-supported archive size;
- survive interruption and worker restarts;
- make retries safe and idempotent;
- detect destination changes before destructive activation;
- make populated-library replacement explicit;
- preserve a recoverable copy before replacing existing data;
- keep public product contracts independent of private Cloud infrastructure.

### Authenticated ownership invariant

Job, session, and recovery IDs are selectors, not authorization tokens. Every operation is scoped by the host adapter to the authenticated user/owner established by the hosting environment. Access by a non-owner must yield the identical not-found response as a missing identifier. Public DTOs and contracts expose no account IDs, user IDs, tenant database names, or provider bucket identifiers.

---

## Migration flows

### 1. Empty destination flow

If the destination contains no portable user-owned state, migration proceeds directly after preflight, transfer, validation, and destination revision verification.

Typical lifecycle:

```text
Pending -> Preparing -> Transferring -> Validating -> ReadyToActivate -> Activating -> Completed
```

### 2. Populated replacement & recovery flow

A populated destination is never silently merged with the incoming library.

Preflight returns `AllowedReplacementRequired`.

If the user explicitly confirms replacement (`confirmReplacement: true`):

1. The incoming migration is validated and durable staging prepared via `IMigrationTransferService.PrepareActivationAsync`.
2. `MigrationActivateRequest` binds explicit confirmation to the job's exact preflight destination revision. A preliminary revision mismatch rejects admission.
3. The activation worker drains library readers and writers under exclusive maintenance and rechecks that same revision. It never silently updates the job to a newer revision.
4. A mandatory recovery generation of the existing populated portable library is retained via the recovery subsystem. Client requests cannot bypass recovery creation.
5. A verified candidate database and media root replace the destination through the durable cutover protocol below.

There is **no implicit merge mode** in the migration contract.

---

## Destination revision semantics

Each destination exposes an opaque revision token representing its current portable user-owned state.

The token is implementation-defined and must not expose database internals.

It must change after **any create, update, or delete affecting portable user-owned state**, including modifications to an already-existing row (e.g. editing a book title, modifying reading progress, updating note content, changing topics, changing writing, or editing a note import link).

A migration records the destination revision observed during preflight. That revision is checked again at activation. If the destination changed, activation fails closed with `RejectedDestinationConflict` rather than overwriting intervening user changes.

The SelfHosted implementation of this invariant is shipped. `LibraryState.StateVersion` is advanced by the `NostosDbContext.SaveChanges` pipeline (once per transaction, atomically) whenever a save contains an Added/Modified/Deleted entry for any entity in the shared portable entity set, and every portable bulk `ExecuteUpdate`/`ExecuteDelete` call site advances it explicitly in the same transaction. `ILibraryDestinationRevisionProvider` composes the version with the portable row counts, and the activation candidate builder advances the live revision once more when it finalizes a candidate. The portable entity set is asserted in parity with the archive completeness inventory by the test suite, so a new portable entity cannot silently escape the revision.

---

## Export snapshot contract (#678)

The shipped export path (see Part 1) guarantees:

1. **Relational consistency boundary:** Export reads a single consistent relational snapshot under a read snapshot / transaction. It never mixes rows from different revisions.
2. **Media change detection and pinning:** Source media files referenced by the relational snapshot are pinned by size, mtime, ETag and SHA-256 during initial indexing, and every pin is re-verified before and after the archive copy. Media absent on a metadata lookup (initial pin or copy start) or unopenable during the initial pin or copy-pass open fails closed with `source_media_missing`. Once an initial pin observation is underway, a media revision mismatch against the pin — content, length or metadata changed after pinning, a disappearance detected by the pin's post-hash metadata check, or a disappearance detected by the post-copy metadata check — fails closed with typed error `source_media_changed`. No inconsistent archive is emitted.
3. **Manifest snapshot records:** The generated manifest records the timestamp captured inside that transaction and the pinned source revisions (the relational payload SHA-256 plus each media SHA-256).

---

## Job state machine

Planned migration jobs use `MigrationJobState`:

```text
Pending, Preparing, Transferring, Validating, ReadyToActivate, Activating, Completed, Failed, Cancelled, Expired
```

### Legal transitions

Transition validation (`MigrationJobTransitions`) is direction-aware. Both directions share the same lifecycle before validation and the same terminal states; they diverge at `Validating` because an import cannot complete before activation while an export has no activation step.

Import:

| Current state | Allowed next states |
|---|---|
| `Pending` | `Preparing`, `Cancelled`, `Expired`, `Failed` |
| `Preparing` | `Transferring`, `Cancelled`, `Expired`, `Failed` |
| `Transferring` | `Validating`, `Cancelled`, `Expired`, `Failed` |
| `Validating` | `ReadyToActivate`, `Cancelled`, `Expired`, `Failed` |
| `ReadyToActivate` | `Activating`, `Cancelled`, `Expired`, `Failed` |
| `Activating` | `Completed`, `Failed` |
| `Completed`, `Failed`, `Cancelled`, `Expired` | none (Terminal states) |

Export:

| Current state | Allowed next states |
|---|---|
| `Pending` | `Preparing`, `Cancelled`, `Expired`, `Failed` |
| `Preparing` | `Transferring`, `Cancelled`, `Expired`, `Failed` |
| `Transferring` | `Validating`, `Cancelled`, `Expired`, `Failed` |
| `Validating` | `Completed`, `Cancelled`, `Expired`, `Failed` |
| `ReadyToActivate`, `Activating` | none (exports never enter the activation path) |
| `Completed`, `Failed`, `Cancelled`, `Expired` | none (Terminal states) |

- **Import completion boundary:** Import `Validating -> ReadyToActivate` is mandatory and import `Validating -> Completed` is illegal. Only #681 activation may take an import through `ReadyToActivate -> Activating -> Completed`; #679 import work stops at `ReadyToActivate`.
- **Export completion:** Export `Validating -> Completed` is legal and is the only successful exit. Export never enters `ReadyToActivate` or `Activating`.
- **Terminal states:** `Completed`, `Failed`, `Cancelled`, `Expired`. No transitions permitted out of terminal states.
- **Retryable states:** `Failed`, `Cancelled`, `Expired`. Retrying a job creates a new attempt or resets work from the last verified phase via `IMigrationJobStore.RetryAsync`.
- **Cancellation boundary:** Cancellation via `IMigrationJobStore.CancelAsync` is allowed from `Pending`, `Preparing`, `Transferring`, `Validating`, and `ReadyToActivate`. It discards uncommitted staging data and sets the job to `Cancelled`.
- **Activation boundary:** Entering `Activating` is the atomic point-of-no-return; once entered, it cannot be cancelled and must complete or fail. Exports never enter `Activating`.

---

## Worker leases, concurrency, and restart recovery

Only one worker may actively process a migration job at a time. Lease time authority belongs to the store: callers pass durations, never timestamps, and the store obtains its authoritative current time itself, atomically with the compare-and-set. Lease tokens are opaque capabilities for the active lease and must not be parsed.

- **Lease acquisition:** `IMigrationJobStore.TryAcquireLeaseAsync(jobId, leaseDuration, ct)` is non-throwing for contention. It sets the lease expiry to the store's authoritative current time plus `leaseDuration` and returns the new opaque lease token when the job is non-terminal and either has no lease or its current lease already expired. It returns `null` when another unexpired lease owns the job. A stale lease may be taken over only after expiry.
- **Lease duration:** 5 minutes (`WorkerLeaseDurationMinutes = 5`), requested as a `TimeSpan`; `leaseDuration` must be positive.
- **Heartbeat renewal:** `RenewLeaseAsync(jobId, leaseToken, leaseDuration, ct)` returns `true` only when the supplied token is still the current lease token and the lease has not already expired according to the store's authoritative clock; it then extends the expiry to authoritative-now plus `leaseDuration`. It returns `false` for a superseded or already-expired lease and never revives it.
- **Concurrency control:** All state transitions and progress updates require a `leaseToken` matching the active, unexpired worker lease. Mismatched or expired tokens fail with concurrency conflict; a mutation never revives an expired lease. Releasing with a superseded or expired token is a no-op rather than an error.
- **Restart recovery:** `IMigrationJobStore.GetJobsNeedingRecoveryAsync(cutoffUtc)` discovers active, non-terminal jobs whose worker leases expired before the cutoff, allowing orphaned jobs to be safely acquired and resumed by another worker.

### Idempotent job and session creation

Job creation and transfer session start require a non-empty idempotency key scoped to the authenticated owner and operation. For session start, the key is scoped to its parent job.

- A retry with the same key and the same payload returns the original job or session through `MigrationIdempotencyResult<T>.Replayed`; it does not create another resource.
- A retry with the same key and a different payload returns `MigrationIdempotencyResult<T>.Conflicted` with `MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload`.
- The job payload is its migration direction. The session payload is its parent job plus purpose, byte size, chunk size/count, and file identity; the idempotency key itself is not part of the payload.

---

## Transfer chunking, resumability, and file identity

To reliably transfer large libraries without giant HTTP requests, the contract enforces:

- **Fixed-size chunking:** The session's nominal chunk size (`MigrationSessionRequest.ChunkSize`) is 16 MiB by default (`DefaultChunkBytes = 16 * 1024 * 1024`) and must be within a 4 MiB minimum (`MinChunkBytes = 4 * 1024 * 1024`) and a 64 MiB maximum (`MaxChunkBytes = 64 * 1024 * 1024`); `MigrationContractLimits.IsValidChunkSize` validates it. Every non-final chunk payload has exactly the session chunk size. The final chunk has 1..session-chunk-size bytes and may be smaller than `MinChunkBytes`; a whole file smaller than `MinChunkBytes` is transferred as one short final chunk. `MigrationContractLimits.IsValidChunkBytes(bytes, chunkSize, isFinalChunk)` validates actual payload lengths, and a zero-length chunk is never legal.
- **Required file identity:** `MigrationSessionRequest` requires a non-nullable `MigrationFileIdentity(TotalSizeBytes, Sha256Checksum, ClientFingerprint?)`. This prevents an interrupted session from being resumed with a different or modified file. File identity mismatch fails closed.
- **Received chunks tracking:** `MigrationSessionStatus` returns `IReadOnlyList<int> ReceivedChunks` and `ReceivedChunkCount`. Clients query the session to resume exactly from missing chunks.
- **Per-chunk integrity and idempotency:** Each chunk upload includes its `chunkIndex`. Uploading an already accepted chunk with matching bytes is idempotent and returns `AlreadyPresent: true`. Conflicting chunks fail closed.
- **Session expiry:** Transfer sessions expire after 24 hours (`SessionExpiryHours = 24`).

---

## Preflight contract & capacity accounting

Preflight (`MigrationPreflightEvaluator.Evaluate`) evaluates declared archive metadata against destination state.

### Incoming counts (`MigrationArchiveCounts`)
Must report: `Works`, `Books`, `Notes`, `Topics`, `NoteTopics`, `Writings`, `WritingNotes`, `Collections`, `CollectionMemberships`, `Acquisitions`, `AssistantSettings`, `NoteImportBookLinks`, `MediaEntries`, and `TotalRows`.

### Destination counts (`MigrationExistingCounts`)
Derived from the completeness inventory: `Works`, `Books`, `Notes`, `Topics`, `NoteTopics`, `Writings`, `WritingNotes`, `Collections`, `BookCollections`, `Acquisitions`, `NoteImportBookLinks`, `AssistantSettings`, and `TotalRows`.

### Staging capacity formula

Preflight calculates required storage and estimates the recovery copy:

1. **Estimated recovery size (for populated replacement):**
   ```text
   EstimatedRecoveryBytes = (ExistingCounts.Books * 50_000_000L) + (ExistingCounts.TotalRows * 1024L)
   ```
2. **Required storage calculation:**
   - **Empty destination:**
     ```text
     RequiredStorageBytes = DeclaredArchiveBytes + DeclaredMediaBytes
     ```
   - **Populated replacement:**
     ```text
     RequiredStorageBytes = DeclaredArchiveBytes + DeclaredMediaBytes + EstimatedRecoveryBytes
     ```

If `AvailableStorageBytes < RequiredStorageBytes`, preflight returns `RejectedInsufficientStorage`.

---

## Recovery snapshots

When replacing a populated destination, activation retains a recovery copy of
the previous generation under `.nostos-recovery/<job-id>/` before the cutover.
The SelfHosted host exposes the retained copies through `GET …/recovery` and
`GET …/recovery/{id}`, and restores one through `POST …/recovery/{id}/restore`
with confirmation bound to the destination revision.

- **Retention duration:** 7 days (`RecoveryRetentionDays = 7`).
- **Storage accounting:** Retained recovery snapshots count against host storage accounting until expired and purged by the SelfHosted recovery cleanup worker.
- **Operational backups distinction:** Host operational backups are local SQLite/infrastructure dumps. Preflight explicitly rejects operational backups (`RejectedOperationalBackupNotPortable`).

### SelfHosted activation, cutover and recovery (#681)

The provider-neutral activation/recovery DTOs are `MigrationActivateRequest`,
`MigrationRecoveryRestoreRequest`, and `MigrationRecoveryStatusResponse`. Public
DTOs expose no local paths or provider/account identifiers. `IMigrationActivationService`
consumes an owned job already admitted durably to `Activating`; the SelfHosted
implementation and HTTP activation routes are shipped.

`MigrationActivationAdmission` implements pure confirmation and revision rules.
An empty destination may activate without confirmation; a populated destination
requires confirmation. Requested, stored, and current revisions must all match
ordinally. Recovery restore always requires confirmation bound to the current
revision. These checks do not replace ownership, staging integrity, capacity, or
worker lease checks in the later orchestrator.

The existing direction-aware frozen job transition table remains authoritative:
imports take `ReadyToActivate -> Activating -> Completed`, while exports never
activate. User cancellation ends at `Activating`. This cancellation boundary is
distinct from the subsequent durable filesystem commit. The executable journal model
allows a completed job outcome only with `Committed`; a failed activation outcome
requires untouched live paths or a completed rollback. A cutover failure must
restore the original generation before releasing exclusive maintenance.

The filesystem journal and recovery manifest are version 1 documents outside the
active SQLite database. Their checksum envelope contains `Version`, `PayloadJson`,
and a lowercase SHA-256 over the exact UTF-8 payload JSON. Unknown payload fields
round-trip; unsupported versions, unknown phases, missing required journal fields,
and checksum mismatches fail closed. Checksums detect corruption and do not
authenticate documents. Journals contain generated job/operation identifiers,
phase, opaque destination revision, retention intent and timestamp. Recovery
manifests contain the matching generation IDs, counts, DB/media lengths and hashes,
status and seven-day expiry. They contain no user content or absolute paths.

| Durable journal phase | Startup decision |
| --- | --- |
| `CandidatePrepared`, `ExclusiveEntered`, `DatabaseCheckpointed` | Nothing; original paths untouched |
| `CutoverPrepared`, `PreviousMediaRetained`, `PreviousDatabaseRetained`, `CandidateMediaActivated`, `CandidateDatabaseActivated`, `PostActivationVerified`, `RollingBack` | Roll back original generation |
| `Committed` | Roll forward verified candidate; finalize job if necessary |
| `RolledBack` | Nothing; original generation already restored |
| Unknown/unsupported/corrupt | Fail closed |

Each phase is durable intent for the next rename, so rollback must also handle a
rename that finished before the following phase write. File existence validates
the chosen recovery action; it cannot determine which generation wins. The
activation journal store persists each phase with temp-write/flush/rename, and
the startup service reconciles an interrupted cutover before the host serves
traffic.

SelfHosted now uses one singleton `ILibraryMaintenanceCoordinator`. HTTP operations
take shared leases across their complete response/stream and request-scope
disposal. REST, OPDS, MCP and database readiness traffic all participate. During
drain/exclusivity new operations receive HTTP 503, stable code
`migration_activation_busy`, and `Retry-After: 5`. Process liveness, static UI and
the GET backup-progress endpoint remain available without opening the library.
The activation and recovery read routes are memory-safe and avoid the live
database; `GET /jobs/{id}` has no exemption.

Background acquisition, reconciliation, topic cleanup, receipt retention,
book-text extraction/embedding/backfill and scheduled backup operations take
shared leases before opening their work scopes. They quiesce at operation
boundaries and resume when admission reopens. New background operations wait
outside the gate; existing long operations or long-lived streams can make
activation time out. `LibraryMaintenance:DrainTimeout` defaults to `00:00:30`,
must be positive and at most ten minutes, and bounds drain acquisition. Timeout
or cancellation releases admission without granting exclusivity. Contending
exclusive attempts fail immediately; an acquired exclusive lease stays held
until its owner disposes it, even if its cancellation token fires.

`BackupSettingsProvider.IsInMaintenanceMode` projects this same coordinator.
Its compatibility enter/exit methods own one reference-counted coordinator
lease. Backup creation participates as shared work; local backup restore uses
the exclusive drain barrier instead of a fixed delay. The restore HTTP endpoint
delegates admission to the service so it never waits on its own request lease.
The existing archive restore protocol remains operational backup functionality;
it is not the migration cutover engine.

The advisory maintenance marker is `.nostos-activation/maintenance.json` beside
the configured database. Startup clears stale markers before bootstrap/workers
and never reconstitutes process-local leases. An unresolved actionable or corrupt
activation journal fails startup closed until the startup reconciler resolves it.
No schema additions are needed: existing job fields hold state, recovery
projection, destination revision and prepared staging facts. WAL checkpointing
and SQLite pool lifecycle are handled by the activation database lifecycle rather
than the maintenance coordinator.

---

## Summary of fixed migration contract constants

The following normative constants are defined in `MigrationContractLimits`:

| Constant | Value | Description |
|---|---:|---|
| `MaxArchiveBytes` | 512 GiB | Maximum archive staging capacity |
| `MaxMediaBytes` | 512 GiB | Maximum aggregate media bytes |
| `MaxSingleEntryBytes` | 16 GiB | Maximum size of an individual file in archive |
| `MaxDataBytes` | 64 MiB | Maximum relational JSON payload size |
| `MaxManifestBytes` | 4 MiB | Maximum manifest size |
| `MaxArchiveEntries` | 20,000 | Maximum total entries in ZIP container |
| `MinChunkBytes` | 4 MiB | Minimum accepted nominal session chunk size |
| `DefaultChunkBytes` | 16 MiB | Default nominal session chunk size |
| `MaxChunkBytes` | 64 MiB | Maximum accepted nominal session chunk size |
| `WorkerLeaseDurationMinutes` | 5 | Default worker lease duration, requested as a `TimeSpan` |
| `SessionExpiryHours` | 24 | Transfer session lifetime |
| `RecoveryRetentionDays` | 7 | Mandatory recovery snapshot retention |
