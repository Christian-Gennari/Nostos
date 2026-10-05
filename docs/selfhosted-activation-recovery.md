# SelfHosted activation journal and startup recovery

Issue #681 Slice 3 supplies filesystem paths, durable journals and startup
repair. It does not yet activate imports. Candidate construction, SQLite
checkpoint/pool management, authoritative verification, recovery retention and
job finalization are supplied by Slices 4–7.

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
| Recovery manifest (future Slice 6) | `<db-parent>/.nostos-recovery/<job-id>/recovery.json` |
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

**Derived rebuild after activation.** A committed cutover leaves a resolved
`Committed` activation journal. The rebuild ensures the book-text schema
exists, then wipes every derived row (chunks, FTS rows, vectors and ingestion
state) exactly once per committed operation and records that wipe durably in
`derived.reset.json`. It then reschedules every file-backed book through a
strict scheduler that reports failures instead of swallowing them; a book whose
artifacts or ingestion state cannot be written leaves the success marker
absent, and the next pass skips the wipe (reset record) and skips books already
carrying the current extractor version, retrying only what is still missing. A
durable `derived.rebuilt.json` marker in the journal directory is written only
after every book was durably scheduled or intentionally unsupported, so a
restart before that point runs the rebuild again without destroying ingestion
progress. Failure is logged, retried with a bounded exponential backoff, and
can never roll back or fail the already committed activation. Thumbnails are
not rebuilt here: `FileStorageService` regenerates missing cover thumbnails
lazily.

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
