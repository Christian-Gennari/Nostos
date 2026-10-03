# Portable Nostos archive v1

Issue #399 defines portability as a product boundary: a Nostos library can move
between installations without exposing or depending on a database file, local
filesystem layout, object key, or provider backup format.

The archive format, serializer, importer, and API are public product code. The
SelfHosted host stores assets locally; each host supplies storage and access
policy through provider-neutral contracts.

Portable archives are separate from local operational backups. Local backups
retain same-installation restore semantics. Portable exports serialize Nostos
domain data and stream book assets through `IBookAssetStorage`.

## Archive layout

Portable archive v1 is a ZIP container:

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

Archive paths are never used directly as filesystem destinations or storage
keys. Import derives destinations from stable book IDs and sends staged streams
through `IBookAssetStorage`.

### `manifest.json`

The manifest contains the format and data versions, export timestamp, product
assembly version, entity counts, the data file length and SHA-256, and a
canonical descriptor for each media entry. Each descriptor includes its book
ID, logical kind, archive path, filename, content type, uncompressed length, and
SHA-256.

Media is streamed into the archive. The manifest is written after the stream
finishes, so large book and audio files do not need to be buffered in memory.

### `data/library.json`

This is an explicit provider-independent representation, not an EF entity graph.
It preserves stable IDs and relationships for:

- works, books, and book metadata;
- reading progress, ratings, favorites, reviews, and timestamps;
- collections and nested collection membership;
- notes, source anchors, capture provenance, topics, and note links (stored under the
  archive's original `concepts` / `noteConcepts` JSON keys, which the Concepts → Topics
  rename deliberately left unchanged so older archives still import);
- Writing Studio documents and folder hierarchy;
- generic acquisition provenance and assistant capture-processing preference.

## Data classification

Portable exports include user-owned product data, source media files, and covers.
They exclude reconstructed caches such as normalized search fields, EPUB
locations, generated thumbnails, and library version bookkeeping.

Operational state is excluded: local backup records and paths, absolute media
paths, idempotency receipts, job schedules, provider configuration, and runtime
settings. Credentials and secrets are never serialized. The v1 DTO has no fields
for these values.

## Export guarantees

Export reads relational state through the active `NostosDbContext`, reads media
through `IBookAssetStorage`, and writes a manifest only after every referenced
media stream succeeds. It never changes or removes source library data.

The API accepts no account ID, database name, or storage namespace. Host
authorization and storage selection stay outside the archive format.

## Import v1 contract

Version 1 supports import into an empty library only. If the destination already
contains library content or an assistant preference, import returns
`destination_not_empty`; v1 never merges or replaces existing data implicitly.

Import validates the archive structure, versions, checksums, sizes, IDs,
relationships, media references, and hierarchy before changing the destination.
It stages and verifies media, writes relational state in a transaction, streams
assets through `IBookAssetStorage`, re-reads the imported state, verifies stored
media, and then commits. If import fails before commit, it rolls back relational
state and removes only the newly imported media.

The result reports the format version, entity and media counts, media bytes, and
whether integrity verification completed.

## Archive hardening

The reader rejects malformed manifests, unsupported versions, duplicate or
unexpected entries, unsafe paths, excessive entry counts and sizes, suspicious
compression ratios, invalid IDs or relationships, hierarchy cycles, missing
media, and length or SHA-256 mismatches before destination mutation.

Current service-level safety limits are 20,000 ZIP entries, 512 GiB compressed
staging, 1 TiB declared uncompressed size, 16 GiB per entry, 64 MiB relational
JSON, and a 4 MiB manifest. HTTP hosts may impose a lower request limit.

## API boundary

- `GET /api/portability/export` streams a `.nostos` archive using
  `application/vnd.nostos.portable+zip`.
- `POST /api/portability/import` accepts an archive and returns a structured
  import result or typed validation error.
- A non-empty destination returns HTTP 409.

The format does not expose database, identity, storage-provider, or account
resource details. The host supplies authorization and storage adapters around
the product API.

## Compatibility policy

Format and data versions are explicit and independent. Readers fail closed on
unknown versions. A future version should parse and validate its manifest, read
through a version-specific portable model, run current integrity checks, and
only then enter the staged import path. Do not deserialize arbitrary historical
EF entity graphs.

## Relationship to local operational backup

SelfHosted local backup remains useful for same-installation recovery because it
can snapshot and restore SQLite directly. It is not an interchange format.
Use `IPortableArchiveService` for migration between Nostos installations; do not
copy database files, storage paths, or provider backup artifacts as a portable
archive.
