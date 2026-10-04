# Portable Nostos archive & migration contracts

Issue: #677  
Epic: #676

Nostos supports portable library migration between supported deployments without exposing hosting-provider internals or coupling the archive format to a particular database, object store, authentication provider, or billing system.

This document defines the portability boundary, archive compatibility guarantees, migration lifecycle, preflight behavior, completeness inventory, and operational constraints established for issue #677.

The portable archive format is intended for:

- SelfHosted → Cloud migration.
- Cloud → SelfHosted migration where export is available.
- SelfHosted → SelfHosted migration.
- Moving a complete Nostos library between compatible installations.
- Recovery or explicit replacement of a destination library using a portable `.nostos` archive.

It is distinct from host-local operational backups.

---

## 1. Design principles

Portable migration follows these rules:

1. **Provider-neutral contracts.** Public migration APIs and archive schemas must not expose private hosting implementation details.
2. **Versioned interchange.** Archive format and portable data versions are explicit and independently validated.
3. **Complete user-owned state.** Portable archives preserve all supported user-owned library state unless a field or entity is explicitly classified as excluded.
4. **No implicit merge.** Import into a populated destination is an explicit replacement/recovery operation, not an automatic merge.
5. **Bounded resource usage.** Large archives and media are streamed in chunks rather than buffered in memory.
6. **Restartable transfers.** Completed transfer work survives process interruption and can be resumed.
7. **Concurrency-safe activation.** Destination revision checks prevent an import prepared against stale destination state from silently overwriting newer writes.
8. **Atomic activation.** Validation completes before the destination is replaced. Activation is treated as an atomic point-of-no-return boundary.
9. **Fail closed.** Unknown archive versions, malformed manifests, corrupt content, invalid checksums, and unsupported compatibility combinations are rejected rather than guessed at.
10. **Public/private ownership separation.** Portable data semantics belong to the public product; host-specific infrastructure remains private.

---

## 2. Portable archive identity

Portable exports use the `.nostos` archive format.

A portable archive contains:

- A versioned manifest.
- Portable relational/domain data.
- Referenced media.
- Integrity metadata required to validate the archive before activation.

The archive is designed as an interchange format rather than a raw database backup.

The current archive family uses:

- **Archive format version:** `1`
- **Supported portable data versions:** `1`, `2`, and the current data version when newer than those legacy versions.

Format version and data version have separate meanings:

- **Format version** describes the outer archive/container contract.
- **Data version** describes the portable domain payload and which portable entities or fields are available.

Readers must reject unknown future format or data versions unless support for those versions has been implemented explicitly.

---

## 3. Portable archives are not operational backups

A portable `.nostos` archive must not be confused with Nostos host-local operational backups.

Portable archives:

- Contain provider-neutral portable domain state.
- Use versioned public archive and JSON contracts.
- Can be imported into another compatible Nostos deployment.
- Do not expose host-specific paths, tenant identifiers, storage keys, or provider APIs.

Operational backup files may contain implementation-specific state such as:

- SQLite paths.
- Host-local backup metadata.
- `BackupRecord` rows.
- Deployment-specific storage information.
- Other host recovery details.

Operational backups are for restoring the deployment that created them and are **not** part of the portable migration contract.

---

## 4. Provider-neutral migration contracts

Migration contracts are shared public models used by migration producers, consumers, and compatible hosts.

They must not expose:

- Internal account IDs.
- Tenant database names.
- SQLite or PostgreSQL connection information.
- Object storage bucket names.
- Object storage keys.
- Provider-specific object URIs.
- Authentication-provider identifiers.
- Billing-provider identifiers.
- Internal worker identities.
- Cloud-provider APIs.

Where correlation is required, the contracts use opaque values such as:

- `JobId`
- `TransferId`
- `SessionId`
- Resume tokens.
- Revision tokens.
- Continuation tokens.
- Relative archive paths.

Consumers may retain and return these values but must not depend on their internal representation.

---

## 5. Migration job lifecycle

Migration jobs expose the following shared lifecycle states:

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

### `Pending`

The migration request has been accepted but preparation has not started.

### `Preparing`

The system is preparing the operation.

Examples include:

- Capturing an export snapshot.
- Evaluating source state.
- Reserving destination scratch capacity.
- Preparing transfer metadata.
- Building an archive manifest.

### `Transferring`

Archive or media bytes are actively being transferred.

Transfers are chunked and restartable.

### `Validating`

The server is validating the completed transfer.

Validation may include:

- Archive structure.
- Format and data versions.
- Manifest consistency.
- Declared versus observed sizes.
- Entry paths.
- Media presence.
- Checksums.
- Portable payload invariants.
- Destination capacity.
- Import compatibility.

### `ReadyToActivate`

All required transfer and validation work has completed successfully.

The imported library is staged but has not yet replaced the active destination.

### `Activating`

The staged library is being atomically made active.

Activation is the point-of-no-return boundary for cancellation.

### `Completed`

The migration has finished successfully.

### `Failed`

The migration terminated because of a typed migration, validation, compatibility, storage, or infrastructure failure.

Failure does not imply that a partially prepared library became active.

### `Cancelled`

The job was cancelled before activation crossed its atomic boundary.

### `Expired`

The transfer or migration session exceeded its allowed lifetime and its staging resources are eligible for automatic cleanup.

---

## 6. Job ownership and worker leases

A migration job may be processed by at most one active worker lease at a time.

The lease contract provides:

- Exclusive ownership for a bounded period.
- An expiration time.
- Periodic heartbeat renewal while work is active.
- Safe reclamation when the lease expires.

A worker crash must not permanently lock a migration job.

If the current worker stops renewing its lease, another worker may acquire the expired job and continue from its persisted progress.

The public state machine defines the semantics of job ownership and resumability. The private host owns the actual worker scheduler and lease coordination mechanism.

---

## 7. Idempotency and restart recovery

Migration work must be safe to retry.

Completed chunks and durable migration progress survive:

- Worker restarts.
- Process crashes.
- Temporary network interruption.
- Re-reading the source file.
- Re-opening a transfer session where the session remains valid.

Restarting a worker must not require already completed chunks to be transferred again when their completion has been durably recorded and validated.

Chunk acceptance is idempotent.

A retry of already accepted content must either:

- Be recognized as already complete; or
- Be verified to be identical before being accepted.

Retries must not create duplicate logical library data.

---

## 8. Cancellation boundaries

Cancellation is supported while a job is in:

- `Pending`
- `Preparing`
- `Transferring`
- `Validating`
- `ReadyToActivate`

Cancellation during these phases leaves the currently active destination library unchanged.

`Activating` is an atomic point-of-no-return boundary.

Once activation has started, the system must complete or recover the atomic swap rather than attempt a best-effort partial cancellation.

The migration therefore behaves conceptually as:

```text
prepare
  → transfer
  → validate
  → ready
  → atomic activation
```

rather than modifying the active library incrementally during transfer.

---

## 9. Consistent export snapshot semantics

An export represents one consistent logical library revision.

The exporter must capture an atomic relational snapshot with a consistent:

- Snapshot timestamp.
- Source revision or equivalent source fingerprint.
- Portable relational state.

The export must not combine unrelated database states caused by concurrent writes occurring during serialization.

Media is validated as part of export finalization.

Before the archive manifest is finalized, the exporter verifies that every media object referenced by the portable snapshot exists and is available for export.

An export must not advertise a successfully finalized manifest whose required media is already known to be absent.

The manifest therefore describes a coherent portable snapshot, not merely a best-effort enumeration of whatever files happened to be readable at different times.

---

## 10. Destination revision tokens

Preflight evaluates the destination state before an import is allowed to proceed.

The preflight result returns an opaque destination revision token.

The token represents the destination state against which the operation was evaluated.

Clients:

- May store the token.
- May return the token in subsequent migration requests.
- Must treat the token as opaque.
- Must not attempt to parse or synthesize it.

Before import or activation proceeds, the server re-verifies that the destination revision still matches the preflight expectation.

This catches concurrent writes such as:

- A book being added.
- A note being changed.
- A collection being edited.
- Reading progress changing.
- Another migration completing.
- Any other destination mutation represented by the destination revision.

If the destination revision has changed, the operation fails closed and requires a new preflight or explicit reevaluation.

This prevents a replacement approved against one destination state from silently overwriting a newer one.

---

## 11. Empty destination import

Import into an empty destination is the simple migration path.

When the destination library is empty:

- No existing user library state needs to be replaced.
- `confirmReplacement` is not required.
- No recovery snapshot of existing user data is necessary.
- Validated portable data can be activated automatically after normal compatibility and capacity checks.

This path is intentionally non-destructive.

---

## 12. Populated destination replacement

Import into a populated destination is never interpreted as an implicit merge.

Nostos does not silently combine arbitrary portable archives with existing destination state.

A populated destination requires explicit replacement confirmation:

```json
{
  "confirmReplacement": true
}
```

Without explicit confirmation, preflight or activation must refuse the destructive replacement.

Before replacing a populated library, the destination creates a retained recovery snapshot of the currently active library.

The intended sequence is:

```text
preflight populated destination
  → explicit replacement confirmation
  → create retained recovery snapshot
  → transfer and validate replacement
  → re-check destination revision
  → atomically activate replacement
```

This gives the user a recovery path if the replacement itself was intentional but the imported content later proves unsuitable.

Replacement semantics are deliberately separate from any future merge feature.

---

## 13. Bounded metadata preflight

Preflight operates on bounded metadata and does not trust client declarations as authoritative.

Expected archive metadata may include:

- Archive format identity.
- Archive format version.
- Portable data version.
- Media entry count.
- Declared aggregate media bytes.
- Maximum declared single-entry size.
- Other bounded archive/library metadata needed for capacity evaluation.
- Client-observed destination revision or fingerprint when applicable.

For the current format family, preflight understands:

- Archive format version `1`.
- Legacy data version `1`.
- Legacy data version `2`.
- The current supported data version.

Preflight validates declared metadata against:

- Supported format versions.
- Supported data versions.
- Target plan or destination capacity.
- Single-entry size limits.
- Aggregate archive limits.
- Scratch-space requirements.
- Migration policy.

Preflight is an admission and compatibility check, not proof that the archive is valid.

All untrusted declarations are revalidated from the actual transferred archive during transfer and validation.

A client cannot bypass server limits by lying about:

- Entry size.
- Aggregate bytes.
- Counts.
- Versions.
- Checksums.
- Manifest contents.

---

## 14. Legacy data compatibility

Portable archive readers preserve explicit compatibility with supported legacy data versions.

### DataVersion 1

DataVersion `1` predates newer portable entities including `WritingNotes` and `NoteImportBookLinks`.

When importing a DataVersion 1 archive:

- The archive remains valid if all DataVersion 1 requirements are satisfied.
- Missing fields or entities introduced in later versions default safely.
- Missing `WritingNotes` are treated as empty/no writing-note associations.
- Missing remembered e-reader mappings are treated as empty.
- Existing DataVersion 1 user data must not be discarded merely because newer optional state did not yet exist.

### DataVersion 2

DataVersion `2` includes `WritingNotes` but predates `NoteImportBookLinks`.

When importing a DataVersion 2 archive:

- `WritingNotes` are preserved.
- Missing `NoteImportBookLinks` default safely to empty.
- Other DataVersion 2 portable fields retain their interchange semantics.

### Current data version

Current archives include the full current portable completeness inventory.

A current archive must support:

- Export.
- Import into a compatible empty destination.
- Explicit populated-library replacement.
- Round-trip export/import without silently dropping portable user-owned state.

---

## 15. Incompatible and corrupt archives

Malformed or unsupported archives fail closed.

Examples include:

- DataVersion `0`.
- Unknown future DataVersion values.
- Unknown archive format versions.
- Missing required manifest fields.
- Invalid manifest structure.
- Invalid paths.
- Duplicate or inconsistent entries.
- Missing required portable payloads.
- Missing required media.
- Declared versus observed size inconsistencies.
- Corrupt hashes.
- Checksum mismatches.
- Incompatible portable schema combinations.

Compatibility failures are surfaced as typed portability/migration failures rather than arbitrary low-level parsing exceptions where the public API exposes a portability error contract.

Validation failure must occur before activation.

A corrupt archive must never become the active destination library.

---

## 16. Media size guarantees

Portable migration must support large Nostos libraries without hidden legacy 4 GiB ceilings.

The portability design supports:

- Individual media entries up to **16 GiB**.
- Aggregate archives larger than **4 GiB**.
- Up to **512 GiB** of migration staging where the target host allows it.
- Up to **1 TiB** declared uncompressed archive content under the portability contract.

These are protocol and implementation bounds, not an entitlement to storage beyond the destination account's actual capacity.

The effective accepted archive remains constrained by:

- Destination plan storage.
- Available destination storage.
- Available scratch/staging space.
- Host policy.
- Per-entry limits.
- Aggregate portability limits.

For example, a destination with 15 GiB available storage cannot import 40 GiB of media merely because the archive protocol can represent it.

The important guarantee is that migrations are governed by explicit capacity checks rather than accidental:

- 32-bit length fields.
- HTTP request buffering limits.
- In-memory byte arrays.
- ZIP implementation assumptions around 4 GiB.
- Hidden temporary-file limits.

---

## 17. Streaming and memory bounds

Archive and media processing must use bounded memory.

Large entries are processed using chunked streaming, typically with chunks in the approximate **5–8 MiB** range.

Implementations must not require:

- Loading an entire media file into memory.
- Loading an entire multi-gigabyte archive into memory.
- Seeking over the whole source solely to stage it in RAM.
- Converting large payloads to single in-memory byte arrays.

The portability pipeline supports non-seekable streaming where practical.

The design therefore permits migration of archives much larger than the process memory limit.

Chunk size is an implementation detail and may evolve while preserving the bounded-streaming guarantee.

---

## 18. Scratch-space admission

Transfers may require temporary staging before validation and atomic activation.

Before allocating large staging resources, the host performs scratch-space admission checks.

Checks account for relevant constraints such as:

- Declared transfer bytes.
- Expected uncompressed bytes.
- Currently available scratch capacity.
- Per-job staging quota.
- Host-wide staging quota.
- Destination capacity.
- Existing concurrent migration reservations.

A job that cannot be staged safely must be rejected before consuming unbounded disk space.

Metadata admission is still followed by actual size validation as bytes arrive.

---

## 19. Transfer session lifetime and cleanup

Long-running transfer sessions have a bounded lifetime.

A typical transfer session expiry is approximately **24 hours**.

The exact host policy may vary, but sessions must not reserve staging resources indefinitely.

Expired or cancelled jobs are eligible for automatic cleanup of:

- Partial archive staging.
- Uncommitted media chunks.
- Temporary validation output.
- Other migration-only scratch resources.

Cleanup must not remove:

- The currently active library.
- A retained populated-library recovery snapshot still covered by retention policy.
- Successfully activated user data.

Lease expiry and transfer-session expiry are separate concepts:

- A worker lease may expire and be reacquired while the migration remains resumable.
- A migration session itself may eventually expire and become non-resumable.

---

## 20. Portable completeness guarantee

Nostos maintains an executable completeness inventory for portable user-owned state.

Every relevant entity and field in the application data model must be classified as either:

- **Portable**, or
- **ExplicitlyExcluded**, with a documented architectural reason.

Adding a new user-owned entity or field without classifying it must fail the completeness contract tests.

This prevents new features from silently becoming non-portable.

The inventory is based on semantic ownership, not merely current database tables.

Provider implementation details and derived caches are not made portable simply because they happen to be stored in the same database.

---

## 21. Portable user-owned state

The current portable domain includes the following state.

### Works

Logical work identity and associated portable work-level state.

### Books

Portable book state includes all supported book forms and their user-owned fields.

This includes shared book fields and subtype-specific information.

For audiobooks, portable state includes fields such as:

- Duration.
- Narrator.
- ASIN.

For physical books and ebooks, portable state includes fields such as:

- ISBN.
- Page count.

Other portable book identity/status metadata classified by the completeness inventory is preserved as part of the versioned portable contract.

### BookMetadata

Portable book metadata owned by the user/library.

Host-only or derived implementation state is not implied merely by being attached to a book model.

### ReadingProgress

Reading position and progress state associated with portable books.

### Chapters

Portable chapter information required to preserve the user's library representation.

### Collections

User-created collections.

### BookCollections

Book-to-collection memberships.

Collection membership is portable state and must round-trip with the library.

### Notes

Portable notes include note content and source anchoring information.

Relevant anchor fields include:

- `CfiRange`
- `SourceAnchorKind`
- `SourceAnchorValue`
- `AnchorVerified`

This allows both EPUB-style CFI anchors and other supported source/page anchor forms to survive migration.

### Topics

The current domain name is `Topics`.

For archive compatibility, the interchange key remains:

```text
concepts
```

The legacy interchange key is retained intentionally so domain renaming does not unnecessarily break the portable wire format.

### NoteTopics

Associations between notes and topics are portable.

Their interchange key remains:

```text
noteConcepts
```

As with `concepts`, this preserves archive compatibility across the Concepts → Topics domain rename.

### Writings

Studio writing state is portable.

This includes:

- Studio documents.
- Studio folders.
- Their portable hierarchy and user-owned content.

### WritingNotes

Relationships between writings and notes are portable in data versions that support them.

Legacy DataVersion 1 archives legitimately omit this state.

### BookAcquisitions

Book acquisition/source provenance is portable.

This preserves supported information about where an imported or discovered book originated rather than reducing every migrated book to anonymous local content.

### AssistantSettings

Portable assistant settings include user-owned assistant behavior such as the capture-processing preference.

Provider credentials are explicitly separate and excluded.

### NoteImportBookLink

Remembered e-reader book mappings are portable.

These mappings allow future e-reader annotation imports to retain the user's established correspondence between an external reader book and a Nostos book.

Legacy archive versions that predate this entity default to no remembered mappings.

---

## 22. Explicitly excluded state

The following state is deliberately excluded from portable archives.

### EPUB locations cache

`LocationsJson` is excluded.

It is a derived/cache representation that can be rebuilt and is not authoritative user-owned content.

### E-reader import batch undo history

The following operational import-history entities are excluded:

- `NoteImportBatch`
- `NoteImportBatchNote`

These records represent transient import/undo bookkeeping rather than the resulting user notes themselves.

The resulting portable notes and supported mappings remain portable.

### AI provider configuration and credentials

`AiProviderSettings` is excluded.

Portable archives must not contain:

- API keys.
- Provider credentials.
- Host-managed AI secrets.
- Deployment-specific AI configuration.

A migrated library may therefore require AI provider configuration to be established independently on the destination.

### Local backup logs

`BackupRecord` is excluded.

Backup execution history belongs to the host that performed the backup and has no portable user-library meaning.

### Command receipts

The following command/idempotency bookkeeping is excluded:

- `LibraryCommandReceipt`
- `NoteCommandReceipt`

These receipts exist to implement command-processing guarantees within a deployment and are not part of the user's portable library state.

### Event synchronization state

`LibraryState` is excluded.

Synchronization/event-processing state is deployment operational state and must be recreated by the destination rather than imported as authoritative state from another host.

---

## 23. Portable relationships and owned EF state

The portability inventory is defined in terms of logical portable data, not direct serialization of EF Core's internal representation.

Consequently:

- EF shadow foreign keys may be represented by their logical portable relationship rather than copied as implementation artifacts.
- Owned navigations may serialize through their portable value fields.
- TPH discriminator implementation details do not automatically become public archive fields.
- Navigation properties themselves are not duplicated when the relationship is already represented by portable scalar keys or join records.

The completeness tests therefore distinguish between:

- User-owned portable data.
- Relationship/navigation structure.
- EF implementation metadata.
- Explicitly excluded derived or operational state.

This keeps the archive provider-neutral and resilient to persistence-layer refactoring.

---

## 24. Round-trip guarantees

For the current supported data version, a valid portable library must survive:

```text
source
  → portable export
  → validation
  → import
  → portable export
```

without silent loss of classified portable user-owned state.

Round-trip equivalence does not require byte-for-byte archive identity.

Differences are permitted for values that are intentionally regenerated or deployment-local, including:

- Archive creation timestamps.
- Opaque migration IDs.
- Destination revision tokens.
- ZIP entry ordering where semantically irrelevant.
- Recomputed integrity metadata.
- Explicitly excluded state.

Semantic portable content must remain equivalent.

---

## 25. Public product ownership

The public Nostos product owns the portable migration semantics.

Public responsibilities include:

- `.nostos` ZIP/archive format.
- Archive manifest format.
- Versioned JSON schemas.
- Portable domain serializers and deserializers.
- Archive format-version compatibility.
- Data-version compatibility.
- Portable completeness inventory.
- Portable invariant validation.
- Checksum/integrity validation rules.
- Bounded metadata preflight logic.
- Migration request/result models.
- Shared migration lifecycle states.
- Provider-neutral state-machine semantics.
- Destination revision contract semantics.
- Replacement confirmation semantics.
- Restart/idempotency contract semantics.
- Cancellation boundaries.

These contracts must remain usable without knowledge of the hosted Nostos Cloud implementation.

---

## 26. Private host ownership

The private hosted implementation owns infrastructure concerns that are not part of the portable product contract.

Private host responsibilities include:

- Tenant isolation.
- Authentication and authorization.
- Account ownership checks.
- Billing-plan enforcement.
- Hosted storage quotas.
- Physical block/object storage implementation.
- Provider-specific multipart upload APIs.
- Temporary object/staging placement.
- Background worker scheduling.
- Physical worker lease coordination.
- Host-specific cleanup scheduling.
- Hosted observability and alerts.
- Hosted recovery-snapshot retention policy.

The private host may implement these responsibilities using any suitable provider as long as the public migration behavior remains conformant.

A migration client should not be able to determine from the public portability contract whether the destination uses, for example:

- SQLite or PostgreSQL.
- Local disk or object storage.
- One object-storage provider or another.
- One worker scheduler or another.

---

## 27. Preflight versus validation

Preflight and validation intentionally serve different purposes.

### Preflight

Preflight answers questions such as:

- Is this archive version potentially supported?
- Does the declared archive fit the target plan?
- Is the destination empty or populated?
- Is explicit replacement confirmation required?
- Is sufficient staging capacity available?
- What destination revision is this decision based on?

Preflight uses bounded metadata and can reject obviously impossible migrations before expensive transfer work begins.

### Validation

Validation answers:

- Is the actual archive structurally correct?
- Do the observed bytes match the declarations?
- Are checksums correct?
- Are all required entries present?
- Are all portable invariants satisfied?
- Is the actual data version supported?
- Is referenced media present?
- Does the staged import fit the destination?
- Is the archive safe to activate?

A successful preflight does not guarantee a successful validation.

---

## 28. Activation safety

No imported library becomes active merely because transfer completed.

Activation requires:

1. Transfer completion.
2. Archive validation.
3. Portable invariant validation.
4. Capacity validation.
5. Any required explicit replacement confirmation.
6. Recovery snapshot creation for populated replacement.
7. Destination revision re-verification.
8. Entry into the atomic activation phase.

Only after these conditions are satisfied may the staged library replace the active destination.

This keeps potentially corrupt or stale imports isolated from the active library.

---

## 29. Concurrency behavior

Migration does not require users to stop using Nostos merely because a long transfer exists.

Instead:

- Preflight records the destination revision.
- Transfer and validation may continue against staging.
- Normal destination activity may change the destination revision.
- Activation rechecks that revision.
- A mismatch prevents stale replacement.

The system therefore prefers detecting concurrent mutation over silently discarding it.

A failed revision check does not imply that the uploaded archive is corrupt; it means the destructive action must be reconsidered against the destination's newer state.

---

## 30. Security and trust model

All migration input is untrusted.

That includes:

- Client preflight metadata.
- Archive manifests.
- ZIP entry metadata.
- Relative paths.
- Counts.
- Sizes.
- Hashes supplied by the producer.
- Resume/chunk metadata.
- Revision tokens returned by clients.
- Replacement confirmation flags.

Servers independently enforce:

- Authorization.
- Ownership.
- Capacity.
- Version compatibility.
- Path safety.
- Size limits.
- Hash validation.
- Destination revision validity.
- State-machine transitions.

Opaque tokens must not be treated as authority merely because the client possesses them.

---

## 31. Compatibility evolution

Future portability changes should prefer additive evolution within a data version only when old readers can safely ignore the addition and all required semantics remain unambiguous.

A new data version is required when compatibility semantics materially change, for example when:

- New required portable state is introduced.
- Existing fields change meaning.
- A formerly optional structure becomes mandatory.
- A relationship requires new interpretation.
- An old reader could otherwise silently lose user-owned state.

A new archive format version is reserved for changes to the outer archive/container contract that cannot be represented compatibly within format version `1`.

Unknown future versions must fail closed.

---

## 32. Required compatibility fixtures

The portability test suite maintains redistributable synthetic fixtures covering supported compatibility generations.

At minimum, fixtures cover:

- Format 1 / DataVersion 1:
  - Legacy archive.
  - No `WritingNotes`.
  - No `NoteImportBookLinks`.

- Format 1 / DataVersion 2:
  - Legacy archive.
  - Includes `WritingNotes`.
  - No `NoteImportBookLinks`.

- Current format/current data:
  - Full current portable inventory.

Current-format fixtures should exercise representative state including:

- Books and supported media.
- EPUB/CFI note anchors.
- Page/source anchors.
- Topics and note-topic relationships.
- Writings.
- Writing-note relationships.
- Collection memberships.
- Acquisition provenance.
- Assistant settings.
- Remembered e-reader mappings.

The fixtures are synthetic and redistributable so compatibility tests do not depend on private user data.

---

## 33. Required contract test guarantees

The portability contract tests are expected to verify at least the following behavior:

- DataVersion 1 imports successfully.
- Fields introduced after DataVersion 1 default safely.
- DataVersion 2 imports successfully.
- `WritingNotes` survive DataVersion 2 import.
- Current archives import successfully.
- Current archives round-trip successfully.
- Invalid archive versions are rejected.
- Unknown future versions are rejected.
- Corrupt checksums are rejected.
- Empty-library preflight is distinguished from populated-library preflight.
- Populated replacement requires explicit confirmation.
- Destination revisions are tracked and rechecked.
- Compatibility decisions fail closed.
- The completeness inventory fails when new EF/domain state is added without classification.

These tests are part of the portability contract rather than incidental implementation coverage.

---

## 34. Summary

The Nostos portable migration contract provides a stable boundary between user-owned library data and deployment-specific infrastructure.

A conforming migration:

- Uses versioned `.nostos` archives.
- Preserves classified portable user state.
- Supports legacy DataVersion 1 and 2 imports.
- Uses provider-neutral public contracts.
- Streams large media with bounded resources.
- Supports archives beyond 4 GiB.
- Checks target and scratch capacity explicitly.
- Captures exports from a consistent source snapshot.
- Validates media before finalizing exports.
- Uses opaque destination revision tokens to detect concurrent writes.
- Imports automatically into an empty destination.
- Never implicitly merges into a populated destination.
- Requires explicit confirmation and a recovery snapshot before populated replacement.
- Is resumable and idempotent across worker interruption.
- Uses worker leases with expiry and heartbeat renewal.
- Allows cancellation until the atomic activation boundary.
- Cleans up expired or cancelled staging.
- Rejects unsupported or corrupt archives before activation.
- Keeps host-specific credentials, backup records, caches, receipts, and synchronization state outside the portable archive.

The result is a migration format that belongs to Nostos itself rather than to whichever database, storage provider, or hosted deployment happens to be running it.
