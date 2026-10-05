# SelfHosted activation journal and startup recovery

This document describes the complete issue #681 SelfHosted activation
protocol: the user-facing import/replace and restore flows, the maintenance
states an operator will see, the durable journal and startup reconciler, the
seven-day recovery copy, scheduled cleanup, and the real-host failure drills
that back the crash-safety claims.

All paths derive from `Persistence:DatabasePath`, `Storage:BooksRoot` and
server-generated nonempty job GUIDs formatted as 32 lowercase hexadecimal
characters. The configured SQLite filename is respected; it need not be
`nostos.db`, and its parent need not be the application content root.

| Object | Host-local location |
| --- | --- |
| Active DB | configured `Persistence:DatabasePath` |
| Active media | configured `Storage:BooksRoot` |
| Candidate DB | `<db-parent>/.nostos-activation/<job-id>/candidate.db` |
| Candidate media | `<media-parent>/.nostos-activation/<job-id>/candidate-books` |
| Previous DB | `<db-parent>/.nostos-recovery/<job-id>/nostos.db` |
| Previous media | `<media-parent>/.nostos-recovery/<job-id>/books` |
| Recovery manifest | `<db-parent>/.nostos-recovery/<job-id>/recovery.json` |
| Active journal | `<db-parent>/.nostos-activation/<job-id>/activation.json` |
| Resolved journal | same directory, `activation.resolved.json` |
| Journal write temporary | same directory, `activation.<write-id>.tmp` |
| Advisory maintenance marker | `<db-parent>/.nostos-activation/maintenance.json` |

Database and media may reside on different volumes. Each candidate and recovery
component must share the mount of its own live component. Linux checks mount IDs
(including bind mounts); Windows checks volume mount paths. Other operating
systems refuse activation path preparation. Preparation checks before creating
control directories and checks again afterwards; every reconciliation validates
this relationship again. Cross-volume layouts produce
`migration_activation_cross_volume`. Native renames never fall back to a copy.

The configured parent directories must be below the filesystem root. They are
operator-owned and may themselves be mount points or links. Descendants,
including the live DB/media names, cannot be symlinks or reparse points. Live
media cannot contain the DB, and live names cannot collide with the reserved
activation/recovery directories. As with transfer storage, this defends against
accidental layouts; a hostile local writer able to change paths between checking
and opening is outside the protection boundary. No archive filename or staged
media reference selects a control path. Paths stay inside the host provider;
public DTOs and journal payloads carry no absolute paths.

## Durability and ownership

The existing version-1 envelope hashes the exact UTF-8 payload JSON with SHA-256.
Unsupported envelope/payload versions, unknown phases, identity omissions,
checksum failures, malformed UTF-8/JSON and a job-ID/directory mismatch fail
closed. Checksums detect corruption, not authenticated changes by local writers.

The singleton store creates a fresh sibling with `CreateNew`, writes the full
envelope, calls `Flush(flushToDisk: true)`, closes it, and publishes it by atomic
native rename. Linux directory `fsync` is mandatory after creating ancestors and
after publication; component moves flush both parent directories. Flush failures
stop recovery. Windows uses `MoveFileEx` with `WRITE_THROUGH`; there is no portable
directory fsync there. Process-crash recovery is supported, but power-loss
survival of directory entries depends on the Windows filesystem. Hardware and
filesystems must honor flushes; the tests inject process-crash boundaries, not
physical power loss. Linux requires `statx` mount-ID support.

A leftover temporary never overrides the final journal. If there is no final
journal, an incomplete initial temporary means no published cutover intent:
no live rename is allowed until `CutoverPrepared` is published. Temporary files
are retained for inspection; Slice 3 does not implement cleanup.

Only `CandidatePrepared` may be written outside exclusive maintenance. Every
other transition requires the current, drained lease from the same host's
coordinator. Foreign/disposed leases and a closed-but-still-draining gate do not
qualify. A synchronous journal/repair operation serializes with lease disposal.
The store serializes writes, enforces the merged transition table, rejects
changes to operation identity/revision/retention, and accepts an identical
same-phase retry without rewriting bytes. All host writers must use the one
singleton store; multiple processes sharing a live installation are unsupported.

## Startup reconciliation

`Program` explicitly runs reconciliation before `DatabaseBootstrapService`,
book-text schema initialization, hosted workers and request serving. Startup
owns an exclusive proof while admission remains closed. A failed repair releases
that proof but keeps admission closed. A successful repair clears the stale
maintenance marker and opens admission. There is no delay-based recovery.

| Durable phase | Direction |
| --- | --- |
| `CandidatePrepared`, `ExclusiveEntered`, `DatabaseCheckpointed` | No cutover; original remains live |
| `CutoverPrepared`, `PreviousMediaRetained`, `PreviousDatabaseRetained` | Roll back original |
| `CandidateMediaActivated`, `CandidateDatabaseActivated`, `PostActivationVerified` | Roll back original |
| `RollingBack` | Continue rolling back original |
| `RolledBack` | Original already restored; resolve journal |
| `Committed` | Keep/finish candidate; retain previous pair |
| Corrupt, unsupported, unexpected layout, multiple unresolved cutovers | Refuse host startup |
| No journal | No filesystem repair |

The phase chooses the generation. Existence checks only validate a reachable
phase position, including the next rename having completed before its phase
write. All journals and all registered recovery steps are validated before live
mutation. Pre-cutover/rolled-back journals must not have retained components;
when no cutover is pending, their live pair must exist.

`ISelfHostedActivationRecoveryStep` has `Order`, read-only `Validate`, and
crash-repeatable `Execute`. Two built-in steps restore media (order 10) and DB
(order 20). Rollback writes `RollingBack` before repair, moves incoming live
components back to their vacant candidate paths, then restores retained original
components. SQLite candidate `-wal`/`-shm` siblings are quarantined before the
original database is restored. Retained nonempty sidecars or conflicting sidecars
are refused. The original database and media remain a matched pair when traffic
is admitted; a temporary mismatch during repair is hidden behind startup's gate.

Committed recovery finishes a missing candidate rename and otherwise leaves the
active pair in place. It never replaces a live original based only on file
existence. Once repair succeeds, rollback records `RolledBack`, and a terminal
journal is renamed to `activation.resolved.json`. This preserves the complete
checksummed outcome for later job finalization without replaying a historical
commit against a future generation. Later Slice 7 must use the store's checksummed `ReadResolved` outcomes
when finalizing jobs interrupted after commit/rollback. No job state is changed
by this slice. Resolved files are historical outcomes rather than recovery intent;
they are not replayed on later startups.

Later slices supply runtime cutover admission/renames and authoritative relational
and media verification, SQLite lifecycle, recovery manifests/capacity, job
projection and retention/cleanup. Additional recovery steps can register through
the interface, must validate before mutation, and must remain safe to repeat.

## Scheduled maintenance: expiry cleanup, orphan sweep, derived rebuild

Issue #681 Slice 10 adds three passes that run outside the exclusive
maintenance window. Each pass takes the shared library operation lease, so a
cutover can never overlap one; when admission is closed (maintenance active or
a cutover that could not be repaired in-process), the pass is skipped and
retried on the next interval. Every worker starts after the startup reconciler
has finished and after `ActivationMaintenance:StartupDelaySeconds`.

| Option | Default | Meaning |
| --- | --- | --- |
| `StartupDelaySeconds` | 30 | Delay before a worker's first pass. |
| `SweepIntervalMinutes` | 15 | Interval between recovery cleanup / orphan sweep passes. |
| `DerivedRebuildIntervalSeconds` | 15 | Interval between derived rebuild passes. |
| `OrphanSafetyAgeHours` | 24 | Minimum age before an unreferenced leftover may be removed. |

**Recovery expiry cleanup.** The worker calls the existing guarded cleanup
(`ISelfHostedRecoveryCleanup.DeleteExpiredAsync`). Expiry eligibility is an
explicit decision per status: `Available` copies expire after their seven-day
retention, and a `Restored` source copy (left as provenance by a successful
restore) becomes eligible on the same expiry; `Expired` resumes an interrupted
terminal deletion. `Restoring`, pending, failed and not-required copies are
kept, and an unrecognized status is kept and logged, so a future status can
never be swept silently. No activation or restore journal may reference the
copy. Removal is one logical operation with a durable deletion marker, so a
crash mid-delete is resumed on the next pass; the copy's claimed recovery
reservation is released only after the retained material is physically gone.
After physical removal the pass updates the job's durable `RecoveryStatus` to
`Expired`, so an activation status cannot keep reporting an available copy that
no longer exists.

**Activation orphan sweep.** Only leftovers that are provably unreferenced are
removed: a candidate database/media area whose job is terminal or absent and
whose directory has no unresolved journal, a recovery plan directory with
neither a manifest nor material and no journal, and interrupted `.tmp` files -
each older than the safety age. The sweep never decides ownership from a
pass-wide job snapshot: immediately before deleting one job's leftovers it
re-reads that job and, for a terminal job, publishes a fenced cleanup claim in
the existing lease fields with a row-version CAS. `RetryAsync` refuses to
reactivate a job while an unexpired cleanup claim is held, so a concurrent
retry either wins before the claim (and the sweep skips the job) or loses
against it (and changes nothing). Active jobs, unresolved journals, valid
manifests, deletion markers, retained material without a manifest and
unrecognized entries are left in place and logged. A recovery manifest is
absent only when the file does not exist; a directory at the manifest path, an
unreadable file, truncated or invalid JSON, a checksum or validation failure,
or a job-id mismatch is corrupt and is left byte-for-byte for an operator.
Every path is re-verified under the configured activation/recovery roots;
symlinked or reparse-point components are never followed, and a tree containing
one is left for an operator.

**Derived caches and the cutover.** Book-text caches under
`<books-root>/<book-id>/derived/` are regenerable and are **never retained**:
the cutover deletes those exact directories from the live media root under the
exclusive lease, immediately before the root is renamed into the recovery area
(a strict walk that refuses links and unknown entries performs the deletion).
The retained recovery copy therefore contains exactly the hash-verified
manifest media, and restoring a copy can never bring back derived state from
another generation.

**Derived rebuild after activation and restore.** A committed cutover and a
resolved rollback/restore both leave a resolved terminal activation journal
(`Committed` or `RolledBack`). The rebuild ensures the book-text schema exists,
then wipes every derived row (chunks, FTS rows, vectors and ingestion state)
exactly once per operation and records that wipe durably in
`derived.reset.json`. It then reschedules every file-backed book through a
strict scheduler that reports failures instead of swallowing them; a book whose
artifacts or ingestion state cannot be written leaves the success marker
absent, and the next pass skips the wipe (reset record) and skips books already
carrying the current extractor version, retrying only what is still missing. A
durable `derived.rebuilt.json` marker in the journal directory is written only
after every book was durably scheduled or intentionally unsupported, so a
restart before that point runs the rebuild again without destroying ingestion
progress. Failure is logged, retried with a bounded exponential backoff, and
can never roll back or fail the already committed activation. The ingestion
worker and the upload-time scheduler also ensure the derived schema before
their first query, so a freshly swapped-in generation (whose candidate
database intentionally carries no derived table) never surfaces a SQLite
`no such table` failure. Thumbnails are not rebuilt here:
`FileStorageService` regenerates missing cover thumbnails lazily.

## For library owners: importing, replacing and restoring

Nostos never changes the live library while an import is uploaded or validated.
The prepared import is materialized and verified in a candidate area beside the
live database and media. The live paths stay untouched until replacement is
explicitly activated.

**Empty library.** Replacing an empty library needs no destructive
confirmation. The cutover uses the same candidate-and-journal engine, so a
crash at any boundary still returns the empty original or completes the
verified import; no seven-day recovery copy is retained.

**Populated library.** Replacement requires an explicit confirmation bound to
the exact library revision shown in the review screen. Any portable change
after that review (a note edit, reading progress, a metadata save, a collection
change) advances the revision, and activation refuses with
`migration_destination_conflict` or `migration_replacement_confirmation_required`
instead of overwriting the change. The library is never merged.

**During the switch.** The final switch runs in a short exclusive maintenance
window. Ordinary library requests answer `503` with the migration error body
(`migration_activation_busy`) while it runs; process liveness, the migration
status routes and the recovery status routes stay available, while readiness
(`/health/ready`) reports `503` because the library is not serving. Requests
already accepted before the window are drained
before any live path is renamed, and a write that lands after the confirmed
revision is refused rather than silently discarded.

**The recovery copy.** A populated replacement retains the previous library —
database and media as one matched pair — for exactly seven days, so the
replacement can be undone. This is activation rollback material, not a backup
and not a portable archive; the recovery routes never expose filesystem paths.

**Restore previous library.** The recovery routes offer a retained copy and a
confirmed restore. Restoring replaces the current portable library with the
retained one, while preserving current host operational state (job history,
backup records, settings, credentials). The restore itself is crash-safe and
reversible: the library it replaces is retained as a new seven-day copy, and
the restored source copy is marked `Restored` so it cannot be restored twice.
An expired copy answers `410 migration_recovery_expired`; an expired source is
removed by the scheduled cleanup once no journal references it.

**What changes and what survives.** Only portable library content is replaced:
works, books and their metadata and reading state, notes, topics, writings,
collections and memberships, acquisitions, note-import links and the portable
assistant preference. Host operational state survives both activation and
restore. Derived caches (book-text chunks, search rows, EPUB locations,
thumbnails) are rebuilt after a committed switch; a rebuild failure never turns
a committed switch into a failed import.

## Operators: maintenance states, HTTP answers and failed switches

A SelfHosted host has four observable states around a switch:

1. **Normal.** Admission is open; ordinary library requests are served.
2. **Candidate work.** A prepared import is being rebuilt and verified, and the
   candidate database/media are being built. This runs outside maintenance; the
   live library stays available and unchanged. Only the migration routes and
   library traffic exist as usual.
3. **Exclusive maintenance.** The cutover window: admission is closed and all
   existing readers/writers have drained (bounded by
   `LibraryMaintenanceOptions:DrainTimeout`, default 30 seconds). Library
   routes — including `/health/ready` — answer `503 migration_activation_busy`;
   `/health/live`, the migration job routes and the recovery status routes
   remain answerable. If the drain cannot complete, activation fails **before**
   any rename with `migration_activation_busy` (`503`, `Retry-After: 5`).
4. **Fail closed.** The cutover could not be completed *or* rolled back
   in-process. The durable advisory marker
   (`<db-parent>/.nostos-activation/maintenance.json`) keeps admission closed
   until a restart reconciles the journal; the activation status reports
   `migration_activation_recovery_failed` and `maintenanceRequired: true`.

| Route | Normal | Exclusive window | Fail closed |
| --- | --- | --- | --- |
| `GET /health/live` | 200 | 200 | 200 |
| `GET /health/ready` | 200 | `503 migration_activation_busy` | `503 migration_activation_busy` |
| `GET /api/portability/migration/jobs/{id}/activation` | 200 | 200 from the in-memory run snapshot | 200 with `RecoveryFailed` |
| `POST /api/portability/migration/jobs/{id}/activate` | 202, or 409 conflict codes, or 507 storage | 202 replay / 503 busy | 409 recovery failed |
| `GET /api/portability/migration/recovery`, `/recovery/{id}` | 200; 404 unknown; 422 corrupt | 200 | 200 |
| `POST /api/portability/migration/recovery/{id}/restore` | 202; 409 conflict; 410 expired; 422 corrupt | 503 busy | 409 recovery failed |
| Ordinary library routes | 200 | `503 migration_activation_busy` | `503 migration_activation_busy` |

Activation failures before the durable `Committed` marker roll the previous
generation back in-process and report `409 migration_activation_failed`; the
job stays retryable. Failures after `Committed` keep the imported generation
and complete the job on the next restart or activation call.

### Emergency switch: withdrawing library migration

The feature is advertised from the same phase availability the migration routes
consult, and SelfHosted additionally exposes one operator key:

```text
LibraryMigration:Enabled = false      # or LibraryMigration__Enabled=false
```

Default: `true` for SelfHosted. With the key off:

- `GET /api/runtime/capabilities` reports `supportsLibraryMigration: false`, so
  the Settings/onboarding entry points disappear on the next load;
- `POST /api/portability/migration/preflight` and
  `POST /api/portability/migration/jobs` refuse new work with the existing
  typed codes (`migration_import_preparation_unavailable` /
  `migration_export_artifact_unavailable`) and create no durable rows;
- a host whose phase handlers are not registered reports `false` regardless of
  the key, and a host that maps product endpoints with
  `MapMigrationTransferEndpoints = false` never advertises the capability;
- existing in-flight jobs keep their status, cancellation and activation routes
  and may finish or be cancelled normally.

Restart is not required if the configuration source is reloadable; the
capability is read per request. This is the recommended first action if a
destructive replacement ever misbehaves on a host.

## Disk space and capacity

The switch needs room for the candidate and the retained previous generation at
the same time:

- candidate database: the live database plus the imported relational payload;
- candidate media: the imported primary media;
- retained recovery database: the current live database size;
- retained recovery media: the current live media size.

Activation integrates with the transfer reservation and the configured safety
margin (`Storage:DiskSafetyMarginBytes`, default 1 GiB, and
`Storage:DiskSafetyMarginPercent`, default 5%). Admission fails before any live
rename with `migration_storage_exhausted` (`507`) when the required headroom is
not available. When the database and books root are on different volumes, each
component is checked against its own volume; no code assumes `/tmp`, the
transfer root, the database volume and the books volume share a filesystem.

## Backups, portable archives and recovery copies

These are three different things and are not interchangeable:

- **Operational backup** (`BackupService`): a full-installation artifact used to
  restore an installation; restoring one can also roll back host operational
  state.
- **Portable archive** (`.nostos`): provider-neutral library content meant to
  move a library between installations; it never carries host operational
  state.
- **Recovery copy**: activation rollback material kept for seven days so a
  just-replaced library can be restored; it is managed by the activation
  lifecycle and expires automatically.

## Manual recovery when startup refuses

The fail-closed mode is refusal to start the host, as in the merged Slices 1–2.
There is no partially available API or background worker. The typed error is
`migration_recovery_corrupt` for an invalid journal or
`migration_activation_recovery_failed` for an inconsistent protocol layout.

1. Stop Nostos and every process using its configured DB/media locations.
2. Preserve copies of the entire active pair, both control trees, journal and
   temporaries, recovery pair, and all SQLite `-wal`/`-shm` siblings. Work on
   copies while investigating; do not delete the journal to force startup.
3. Compare the checksummed final journal and any independently preserved valid
   outcome. A corrupt journal cannot establish the commit boundary. File
   presence, filenames and timestamps do not establish which generation won.
4. If a valid pre-commit journal is available, restore its complete original pair;
   if a valid committed journal is available, finish its complete candidate pair.
   Resolve unexpected sidecars with SQLite expertise before touching base files.
   If no valid evidence establishes the outcome, explicitly select and verify a
   complete generation from an independent operational backup. Never combine a
   database from one generation with media from another.
5. Verify SQLite integrity and the matching media inventory on disposable copies.
   The supplied Slice 3 repair engine does not perform full import verification.
   Only after the complete pair is verified should an operator install both
   components, retain the forensic copies, and move the unusable control journal
   out of the active `.nostos-activation` tree. Clear the advisory marker while
   the host is stopped, then restart and check readiness and the library.

### Exact fail-closed procedure

1. **Restart the host once.** Startup always runs the journal reconciler before
   database bootstrap, workers and ordinary traffic. A pre-commit journal rolls
   back to the previous generation and a `Committed` journal rolls the imported
   generation forward; the host then leaves maintenance and serves normally.
2. **If the restart still refuses**, the reconciler could not validate or
   execute the layout (for example a missing or conflicting component, an
   unreadable journal, a held read-only directory, or mixed SQLite sidecars).
   The host refuses to start and prints `migration_recovery_corrupt` or
   `migration_activation_recovery_failed`; nothing is served. Follow the
   step-by-step preservation and selection procedure above. Do not delete the
   journal to force startup, and never combine a database from one generation
   with media from another.
3. **If the procedure is unresolvable from the local evidence**, restore a
   complete generation from an independent operational backup, keep the
   forensic copies, and report the case with the exact journal phase — the
   reconciler's refusal is deliberate.

Nothing under `.nostos-activation` or `.nostos-recovery` is safe to delete by
hand while activation or recovery work may be present. The orphan sweep removes
only provably unreferenced leftovers after the safety age; resolved
(`activation.resolved.json`) outcomes are historical evidence and are retained.
A recovery copy that reaches its seven-day expiry is removed by the scheduled
cleanup together with its reservation.

## Design differences from the plan

The merged state model is authoritative: the first three phases recover with
`Nothing`; `CutoverPrepared` already assumes its next rename may have completed;
`Committed` rolls forward; `RolledBack` requires no component repair. The plan's
blanket “all pre-commit failures roll back” includes phases with no live mutation,
which the executable model correctly treats as no cutover.

The suggested `CommitAsync` is deferred to Slices 5–7 under this assignment;
Slice 3 implements the recovery engine and step interface. The coordinator's
existing lease type is opaque, so ownership proof and pre-bootstrap recovery
entry/completion are minimal additive host-local methods. Fail-closed preserves
merged behavior (host startup refusal), rather than starting a diagnostics-only
host. The plan specifies file flushing but no platform directory-flush policy;
the Linux/Windows policy above makes that limit explicit. Unique temporary names
replace the illustrative fixed `.tmp`, avoiding stale-temp overwrite. Resolved
terminal envelopes are an additional lifecycle rule to prevent historical replay.

Legacy backup restore still hardcodes `<ContentRoot>/nostos.db` and does not
explicitly manage `-wal`/`-shm` siblings. Activation path resolution never copies
that limitation; its host tests deliberately separate DB/media from content root.

## Real-host failure drills (issue #681, Slice 11)

The in-process crash matrix asserts every durable journal boundary and every
rename against real SQLite and media with fast, deterministic seams. The
real-host drills additionally start, SIGKILL and restart actual
`Nostos.Backend` processes on a disposable data root, with real Kestrel over
HTTP, so process death, connection pools, file handles and startup ordering are
exercised end to end. Convergence is asserted by content — portable table dumps
and media SHA-256 — plus the HTTP job/recovery status, not by status alone.

| Drill | Boundary / fault | Converged state |
| --- | --- | --- |
| Populated kill at `CutoverPrepared` | before any live rename | original; retry completes the import |
| Populated kill at `CandidateDatabaseActivated` | after both uncommitted renames | original; retry completes the import |
| Populated kill at `Committed` | after the durable commit, before finalization | verified import; the resumed call finalizes the job and retains the recovery copy |
| Empty kill at `PreviousDatabaseRetained` | pre-commit empty cutover | empty original; retry completes the import, no recovery copy retained |
| Restore kill at `CandidateDatabaseActivated` | uncommitted restore | imported library stays; the resumed `Restoring` claim completes the restore |
| Restore kill at `Committed` | committed restore | restored previous library; the replaced library is retained as a new available copy |
| Read-only database root during the swap | rollback cannot rename in-process | fail closed (`migration_activation_recovery_failed`), nothing served; restart after the operator fixes the root reconciles to the original and the retry succeeds |
| Corrupt journal at startup | unreadable durable phase | startup refuses; the database and media are byte-for-byte unchanged. The drill then deletes the journal tree to prove a clean startup is possible; that removal is a synthetic drill action, **not** an operator procedure — never delete a journal to force startup |
| Corrupt recovery manifest | unreadable retained copy | host runs; the library is served; recovery status/restore answer `422 migration_recovery_corrupt`; the manifest is preserved |
| Second host during the window | two processes on one data root | the second host reconciles the shared pre-commit journal to the original generation; the first is stopped; exactly one complete generation remains |
| Clock jump across recovery expiry | expired copy + startup cleanup | the expired copy and its reservation are removed; the imported library is untouched |

How to run (local or release verification only; the suite spawns real processes
and is deliberately not part of the default fast backend run):

```bash
NOSTOS_RUN_ACTIVATION_DRILLS=1 dotnet test Nostos.Backend.Tests/Nostos.Backend.Tests.csproj \
  --filter "FullyQualifiedName~ActivationRealHostDrillTests"
```

The eleven drills run in roughly four minutes on a developer machine; three
consecutive verification runs took 3m37s, 3m36s and 3m35s. The in-process
regression suite remains the fast default; the drills are the release evidence
that the same guarantees hold across real process death.

Capacity exhaustion is simulated through the storage capacity seam in the
in-process suite, where the exact byte requirement and the refusal point can be
asserted: `InsufficientRecoveryCapacity_FailsBeforeCutover_AndMutatesNothing`
drives the real activation coordinator with a nearly full volume and proves the
typed `migration_storage_exhausted` refusal, the untouched generation and a
retryable job, and `RecoveryCapacityTests` covers the per-volume boundaries and
the retention-claim accounting. The refusal always happens before the exclusive
window; the swap itself performs renames and allocates no new bytes. The
real-host build is not forced onto a full filesystem because filling the shared
machine volume is unsafe.
