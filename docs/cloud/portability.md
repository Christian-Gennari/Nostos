# Portability and migration

Nostos portable archives are the provider-neutral format for moving user-owned library state between installations.

This document deliberately separates:

1. **Current behaviour (shipped)** — what the existing portability API and `PortableArchiveService` do today.
2. **Migration contract (planned, epic #676 — not yet implemented)** — the contract for one-click SelfHosted ↔ Cloud migration being developed across issues #677–#682.

Do not treat Part 2 as documentation of the currently deployed HTTP API.

---

# Part 1: Current behaviour (shipped)

## Current HTTP API

The shipped portability API exposes:

```text
GET  /api/portability/export
POST /api/portability/import
```

Import uses `multipart/form-data` and uploads the portable archive as one HTTP request.

The backend currently configures Kestrel with a 4 GiB maximum request body size in `Nostos.Backend/Program.cs`:

```csharp
MaxRequestBodySize = 4L * 1024L * 1024L * 1024L;
```

Therefore, although the archive service itself has larger validation limits, the currently shipped import endpoint **does not support portable archives larger than 4 GiB**. A larger archive cannot reach the import service through the existing single-request HTTP endpoint.

This is one of the constraints the planned migration transfer protocol in Part 2 is intended to remove.

## Current export behaviour

`PortableArchiveService` currently exports the portable relational state by issuing its database queries sequentially and assembling the resulting portable archive.

This is the behaviour of the shipped exporter today. It must not be described as an atomic relational snapshot unless and until the planned migration snapshot contract in Part 2 is implemented.

The current archive format is provider-neutral and carries portable user-owned state rather than host-specific database or storage identifiers.

## Current import behaviour

Portable archive restore targets an empty destination library.

For SQLite restores, the import path uses a database transaction and validates the destination through `EnsureDatabaseEmptyAsync` before restoring portable records.

The current API is therefore an **empty-library restore mechanism**. It is not an implicit merge API and it does not implement the replacement/recovery workflow described in Part 2.

## Current archive safety limits

`PortableArchiveService` enforces the following service-level bounds:

| Limit | Value |
|---|---:|
| `MaxSingleEntryBytes` | 16 GiB |
| `MaxExtractedMediaBytes` | 512 GiB |
| `MaxUncompressedArchiveBytes` | 1 TiB |
| `MaxDataBytes` | 128 MiB |

These limits protect archive validation and extraction.

They do **not** override the current HTTP request limit. In particular:

> The shipped `/api/portability/import` endpoint cannot import archives larger than 4 GiB because the complete multipart archive must pass through a single Kestrel request whose `MaxRequestBodySize` is 4 GiB.

## Current archive data versions

The portable archive currently supports:

- Format version 1 / DataVersion 1
- Format version 1 / DataVersion 2
- Format version 1 / DataVersion 3

DataVersion 3 adds portable `NoteImportBookLink` records.

Older DataVersion 1 and DataVersion 2 archives do not contain those records. Import treats the missing collection as empty.

---

# Part 2: Migration contract (planned, epic #676 — not yet implemented)

Epic #676 defines the planned one-click migration system between Nostos SelfHosted and Nostos Cloud.

The implementation is split across issues #677–#682.

This section defines the target contract. These jobs, transfer sessions, chunk APIs, recovery snapshots, and activation semantics are **not yet the behaviour of the shipped `/api/portability/export` and `/api/portability/import` endpoints**.

## Goals

The migration system must:

- remain provider-neutral;
- support large libraries without one giant HTTP request;
- survive interruption and worker restarts;
- make retries safe;
- detect destination changes before destructive activation;
- make populated-library replacement explicit;
- preserve a recoverable copy before replacing existing data;
- keep public product contracts independent of private Cloud infrastructure.

No contract exposed to the public product may require knowledge of database names, tenant/account database identifiers, object-storage bucket names, object keys, provider URLs, or vendor-specific APIs.

Opaque IDs and tokens are used instead.

---

## Migration flow

### Empty destination

If the destination contains no portable user-owned state, a compatible migration may proceed after successful preflight, transfer, validation, and destination revision verification.

Typical lifecycle:

```text
Pending
  -> Preparing
  -> Transferring
  -> Validating
  -> ReadyToActivate
  -> Activating
  -> Completed
```

### Populated destination

A populated destination is never silently merged with the incoming library.

Preflight must return an explicit replacement-required decision.

If the user explicitly chooses replacement:

1. the destination revision is verified;
2. a recovery snapshot of the existing portable library is created;
3. the incoming migration is validated;
4. the destination revision is checked again immediately before activation;
5. activation atomically replaces the destination portable state.

A populated-library replacement therefore always creates a recovery snapshot before destructive activation.

There is **no implicit merge mode** in the migration contract.

---

## Destination revision

Each destination exposes an opaque revision token representing its current portable user-owned state.

The token is implementation-defined and must not expose database internals.

It must change after **any create, update, or delete affecting portable user-owned state**, including modifications to an already-existing row.

Examples include:

- adding or deleting a book;
- editing a book title or metadata;
- changing reading state or other portable book fields;
- editing a note;
- changing note topics;
- editing writing;
- modifying collection membership;
- changing an acquisition record;
- changing portable assistant settings;
- changing a `NoteImportBookLink`.

A migration records the destination revision observed during preflight.

That revision must be checked again at activation.

If the destination has changed, activation fails closed rather than overwriting intervening user changes. Implementation lands with #679/#681.

---

## Job state machine

Planned migration jobs use these states:

```text
Pending
Preparing
Transferring
Validating
ReadyToActivate
Activating
Completed
Failed
Cancelled
Expired
```

### Legal transitions

| Current state | Allowed next states |
|---|---|
| `Pending` | `Preparing`, `Cancelled`, `Expired`, `Failed` |
| `Preparing` | `Transferring`, `Failed`, `Cancelled`, `Expired` |
| `Transferring` | `Validating`, `Failed`, `Cancelled`, `Expired` |
| `Validating` | `ReadyToActivate`, `Failed`, `Cancelled`, `Expired` |
| `ReadyToActivate` | `Activating`, `Failed`, `Cancelled`, `Expired` |
| `Activating` | `Completed`, `Failed` |
| `Completed` | none |
| `Failed` | none |
| `Cancelled` | none |
| `Expired` | none |

`Completed`, `Failed`, `Cancelled`, and `Expired` are terminal states.

The directly retryable terminal outcomes are:

```text
Failed
Cancelled
Expired
```

Retry creates or resets migration work according to the migration recovery contract rather than illegally transitioning a terminal job back into an active state.

Cancellation is supported during preparation, transfer, and validation.

Activation is the atomic point-of-no-return boundary and is not cancellable once entered.

---

## Job ownership and leases

Only one worker may actively own a migration job at a time.

Worker ownership uses a lease with:

```text
Lease duration: 5 minutes
```

The active worker renews the lease through heartbeat updates.

If the worker disappears and the lease expires, another worker may safely recover the job from its durable migration state.

A lease is operational ownership only. It lives strictly within internal store/worker models and is never exposed through the public `MigrationJobStatus` contract.

---

## Idempotency and resumability

Migration transfer must tolerate retries, duplicate requests, network interruption, and worker restart.

### Idempotency key

Session creation and job creation may carry an idempotency key.

Repeating the same logically identical session request with the same key must not create duplicate migration work.

### File fingerprint

A transferred archive is identified using a file fingerprint derived from:

```text
declared file size + SHA-256 checksum
```

The fingerprint prevents an interrupted session from being accidentally resumed using different archive bytes.

### Upload chunks

The standard migration transfer chunk size is:

```text
8 MiB
```

Re-sending an already accepted chunk with the same session, chunk identity/range, and bytes is idempotent.

A conflicting re-send fails closed.

Completed chunks survive interruption and worker restart.

### Download resume

Export/download transfer supports resumable range or offset requests.

A client may continue from the last verified byte boundary instead of restarting the entire archive transfer.

### Session expiry

Transfer sessions expire after:

```text
24 hours
```

Expired sessions may no longer accept transfer activity and follow the explicit retry/recovery path.

---

## Preflight contract

Preflight is a pure product-level decision over declared incoming archive properties and destination state.

It evaluates at least:

### Incoming archive

- entity counts (`MigrationArchiveCounts`);
- declared archive bytes;
- declared media bytes;
- largest single entry;
- archive format version;
- archive data version.

Entity counts include the relevant portable record categories: works, books, notes, writings, topics, collections, collection memberships, acquisitions, assistant settings, and note import book links.

### Destination

- destination entity counts (`MigrationExistingCounts`);
- whether the destination is empty or populated;
- destination revision token;
- available storage capacity.

### Capacity

Preflight reports:

- required storage;
- available storage;
- compatibility;
- destination status;
- typed errors;
- warnings;
- resulting preflight decision.

Typed decisions include:

```text
AllowedEmpty
AllowedReplacementRequired
RejectedIncompatible
RejectedInsufficientStorage
RejectedDestinationConflict
RejectedOperationalBackupNotPortable
```

A rejection reason is part of the contract. Callers must not infer failure categories by parsing human-readable error strings.

---

## Capacity and scratch-space bound

Migration staging must account for both:

1. the archive bytes being transferred; and
2. the extracted media represented by that archive.

The staging capacity bound formula is:

```text
RequiredStorageBytes =
    DeclaredArchiveBytes
    + DeclaredMediaBytes
```

A preflight must reject the migration if the destination cannot provide at least that required capacity.

This is a firm bound for the transfer/staging contract.

---

## Validation and activation

A transferred archive is not active user data merely because every chunk arrived.

The migration must first validate:

- archive format compatibility;
- data-version compatibility;
- declared and computed checksums;
- archive structural integrity;
- entry bounds;
- portable data integrity;
- media completeness;
- destination conditions required for activation.

Only a successfully validated migration may enter `ReadyToActivate`.

At activation:

1. the destination revision is checked again;
2. any required recovery snapshot must already have been created successfully;
3. the incoming portable state is activated atomically.

A mismatch or incomplete prerequisite fails closed.

---

## Recovery snapshots

Replacing a populated destination always requires a recovery snapshot of the outgoing portable user-owned state (`confirmReplacement: true`). Client requests cannot bypass recovery snapshot retention.

Recovery snapshots are retained for:

```text
7 days
```

Retained snapshots count against host storage accounting until expired or purged.

They exist to recover from an explicitly requested destructive replacement.

They are distinct from routine operator backups.

An operational database or infrastructure backup is **not automatically a portable migration archive** and must not be presented as one unless it satisfies the portable archive contract.

---

## Public product and private host ownership

The migration contract is owned by the public Nostos product.

Public product code owns concepts such as:

- archive format and versions;
- portable data records;
- migration job states;
- state transitions;
- preflight inputs and decisions;
- opaque destination revisions;
- transfer/session contracts;
- chunk semantics;
- checksums and fingerprints;
- retry and resumability semantics;
- recovery status;
- activation guarantees.

Private Cloud infrastructure owns provider-specific implementation details such as:

- tenant lookup;
- Cloud account mapping;
- worker scheduling;
- database provisioning;
- private storage locations;
- presigned/provider transfer mechanics;
- provider credentials;
- operational monitoring.

Private host details must not leak into public migration contracts.

The same product contract must remain implementable by another host or by SelfHosted without depending on the Cloud provider stack.

---

## User-owned portability completeness

The portable archive is an explicit inventory of user-owned state.

Every portable entity and field must be classified and covered by the archive model. New user-owned state must not silently become non-portable.

| User-owned state | Portable archive representation | Availability |
|---|---|---|
| Works | `PortableWork` | DataVersion 1+ |
| Books and portable book metadata/state | `PortableBook` | DataVersion 1+ |
| Collections | `PortableCollection` | DataVersion 1+ |
| Book/collection memberships | `PortableBookCollection` | DataVersion 1+ |
| Notes, including supported book anchors | `PortableNote` | DataVersion 1+ |
| Topics | `PortableTopic` | DataVersion 1+ |
| Note/topic relationships | `PortableNoteTopic` | DataVersion 1+ |
| Writings | `PortableWriting` | DataVersion 1+ |
| Writing/note relationships | `PortableWritingNote` | DataVersion 2+ |
| Book acquisitions | `PortableBookAcquisition` | versioned portable data |
| Portable assistant settings | `PortableAssistantSettings` | versioned portable data |
| Remembered note-import book mappings | `PortableNoteImportBookLink` | **DataVersion 3+** |
| Portable media referenced by books | archive media entries | versioned portable data |

`NoteImportBookLink` carries:

```text
Id
Source
SourceKey
BookId
CreatedAtUtc
```

DataVersion 1 and DataVersion 2 archives predate this collection. Importing either version must therefore default `NoteImportBookLinks` to an empty collection.

The executable completeness inventory and archive-model parity tests are the enforcement mechanism for this contract: adding new mapped user-owned state without classifying its portability must fail the test suite.

---

## Version compatibility

Portable format and data versions are explicit compatibility boundaries.

A migration must:

- accept supported historical versions;
- apply defined safe defaults for fields introduced in later data versions;
- reject unknown future versions;
- reject invalid or corrupt archives;
- reject checksum mismatches;
- fail closed rather than partially activating an incompatible archive.

For the current format line:

```text
Format 1 / DataVersion 1
Format 1 / DataVersion 2
Format 1 / DataVersion 3
```

DataVersion 3 is the current version and includes `NoteImportBookLink`.

---

## Summary of fixed migration contract values

| Contract | Value |
|---|---:|
| Transfer chunk size | 8 MiB |
| Worker lease duration | 5 minutes |
| Transfer session expiry | 24 hours |
| Recovery snapshot retention | 7 days |
| Staging capacity bound | `DeclaredArchiveBytes + DeclaredMediaBytes` |

These are contract values, not approximate operational guidance.

The current shipped portability API remains subject to the separate 4 GiB Kestrel single-request import limit described in Part 1.
