/**
 * Browser-side mirror of the provider-neutral migration HTTP contract
 * (`Nostos.Product/Services/Portability/MigrationContracts.cs`) for issue #680
 * slices B1-B3.
 *
 * Naming and field order follow the backend records so the later real
 * `SelfHostedMigrationTransport` (slice B7) is a mechanical mapping. Enums are
 * modelled as the C# member names because #679's HTTP layer is expected to
 * serialise them as strings; the numeric values are the wire values today.
 *
 * Byte counts are plain numbers: the contract's 512 GiB archive limit is far
 * below `Number.MAX_SAFE_INTEGER` (2^53 - 1), so integer precision is safe.
 * Future limits beyond petabyte scale must revisit this assumption.
 */

export type MigrationDirection = 'Import' | 'Export';

export type MigrationJobState =
  | 'Pending'
  | 'Preparing'
  | 'Transferring'
  | 'Validating'
  | 'ReadyToActivate'
  | 'Activating'
  | 'Completed'
  | 'Failed'
  | 'Cancelled'
  | 'Expired';

export type MigrationPreflightDecision =
  | 'AllowedEmpty'
  | 'AllowedReplacementRequired'
  | 'RejectedIncompatible'
  | 'RejectedInsufficientStorage'
  | 'RejectedDestinationConflict'
  | 'RejectedOperationalBackupNotPortable';

export type MigrationDestinationStatus = 'Empty' | 'Populated';

export type MigrationSessionPurpose = 'Import' | 'Export' | 'RecoveryRetention';

export type MigrationSessionState = 'Created' | 'Receiving' | 'Complete' | 'Expired' | 'Cancelled';

export type MigrationProgressPhase =
  | 'Pending'
  | 'Preparing'
  | 'Transferring'
  | 'Validating'
  | 'PreparingActivation'
  | 'Activating'
  | 'Completed';

export type MigrationRecoveryStatus =
  | 'NotRequired'
  | 'Pending'
  | 'Creating'
  | 'Available'
  | 'Restoring'
  | 'Restored'
  | 'Failed'
  | 'Expired';

/** Browser-facing activation outcome (mirrors `MigrationActivationOutcome`). */
export type MigrationActivationOutcome =
  | 'Idle'
  | 'Accepted'
  | 'Running'
  | 'Completed'
  | 'Failed'
  | 'RecoveryFailed';

/** Coarse phase of an accepted activation run (mirrors `MigrationActivationPhase`). */
export type MigrationActivationPhase = 'Queued' | 'Preparing' | 'Activating' | 'Finalizing';

/**
 * Stable machine-readable error codes the server can emit, mirrored from
 * `MigrationHttpErrors` in
 * `Nostos.Product/Endpoints/MigrationHttpContracts.cs`, the activation codes in
 * `MigrationActivationErrorCodes` the activation routes map, plus the
 * `migration_activation_busy` code the maintenance middleware writes for
 * migration routes. `unexpected_error` is the server's fail-closed 500 code.
 */
export const SERVER_MIGRATION_ERROR_CODES = [
  'migration_not_found',
  'migration_idempotency_conflict',
  'migration_invalid_state',
  'migration_lease_conflict',
  'migration_reservation_required',
  'migration_file_identity_mismatch',
  'migration_chunk_conflict',
  'migration_chunk_hash_mismatch',
  'migration_chunk_range_invalid',
  'migration_session_expired',
  'migration_storage_exhausted',
  'migration_cannot_cancel',
  'migration_not_retryable',
  'migration_invalid_request',
  'migration_storage_contended',
  'migration_import_preparation_unavailable',
  'migration_export_artifact_unavailable',
  'migration_export_not_available',
  'migration_export_expired',
  'migration_too_many_jobs',
  'migration_replacement_confirmation_required',
  'migration_destination_conflict',
  'migration_activation_failed',
  'migration_activation_recovery_failed',
  'migration_activation_busy',
  'unexpected_error',
] as const;

export type ServerMigrationErrorCode = (typeof SERVER_MIGRATION_ERROR_CODES)[number];

/**
 * Codes produced inside the browser: bounded archive inspection, local mapping
 * of durable job states, and transport-level failures. The server never emits
 * these, so they are kept separate from the server table.
 */
export const CLIENT_MIGRATION_ERROR_CODES = [
  'archive_not_portable',
  'archive_operational_backup',
  'archive_unsupported_version',
  'migration_not_supported',
  'migration_maintenance_timeout',
  'portable_import_failed',
  'portable_export_failed',
  'network_error',
  'request_aborted',
] as const;

export type ClientMigrationErrorCode = (typeof CLIENT_MIGRATION_ERROR_CODES)[number];

export type MigrationErrorCode = ServerMigrationErrorCode | ClientMigrationErrorCode;

/** Mirrors `MigrationContractLimits`; safe as numbers below 2^53 (see file header). */
export const MIGRATION_LIMITS = {
  maxArchiveBytes: 512 * 1024 * 1024 * 1024,
  maxMediaBytes: 512 * 1024 * 1024 * 1024,
  maxSingleEntryBytes: 16 * 1024 * 1024 * 1024,
  maxDataBytes: 64 * 1024 * 1024,
  maxManifestBytes: 4 * 1024 * 1024,
  maxArchiveEntries: 20_000,
  minChunkBytes: 4 * 1024 * 1024,
  defaultChunkBytes: 16 * 1024 * 1024,
  maxChunkBytes: 64 * 1024 * 1024,
} as const;

export interface MigrationArchiveCountsDto {
  works: number;
  books: number;
  notes: number;
  topics: number;
  noteTopics: number;
  writings: number;
  writingNotes: number;
  collections: number;
  collectionMemberships: number;
  acquisitions: number;
  assistantSettings: number;
  noteImportBookLinks: number;
  mediaEntries: number;
  totalRows: number;
}

export interface MigrationExistingCountsDto {
  works: number;
  books: number;
  notes: number;
  topics: number;
  noteTopics: number;
  writings: number;
  writingNotes: number;
  collections: number;
  bookCollections: number;
  acquisitions: number;
  noteImportBookLinks: number;
  assistantSettings: number;
  totalRows: number;
}

export interface MigrationPreflightRequestDto {
  incomingCounts: MigrationArchiveCountsDto;
  declaredArchiveBytes: number;
  declaredMediaBytes: number;
  maxSingleEntryBytes: number;
  declaredFormatVersion: number;
  declaredDataVersion: number;
  declaredFormatName?: string | null;
  clientDestinationRevision?: string | null;
  isOperationalBackup: boolean;
}

export interface MigrationPreflightResultDto {
  decision: MigrationPreflightDecision;
  isCompatible: boolean;
  isAllowed: boolean;
  incomingCounts: MigrationArchiveCountsDto;
  existingCounts: MigrationExistingCountsDto;
  declaredArchiveBytes: number;
  declaredMediaBytes: number;
  estimatedRecoveryBytes: number;
  requiredStorageBytes: number;
  availableStorageBytes: number;
  errors: string[];
  warnings: string[];
  destinationRevision: string;
}

export interface MigrationPreflightResponseDto {
  evaluation: MigrationPreflightResultDto;
  reservationId: string | null;
  reservationExpiresAtUtc: string | null;
  chunkSizeBytes: number;
}

export interface MigrationCreateJobRequestDto {
  direction: MigrationDirection;
  idempotencyKey: string;
  reservationId?: string | null;
}

export interface MigrationFileIdentityDto {
  totalSizeBytes: number;
  sha256Checksum: string;
  clientFingerprint?: string | null;
}

export interface MigrationSessionRequestDto {
  purpose: MigrationSessionPurpose;
  totalBytes: number;
  chunkSize: number;
  totalChunks: number;
  fileIdentity: MigrationFileIdentityDto;
  idempotencyKey: string;
}

export interface MigrationChunkRangeDto {
  /** Inclusive first chunk index. */
  startIndex: number;
  /** Inclusive last chunk index. */
  endIndex: number;
}

export interface MigrationSessionStatusDto {
  sessionId: string;
  purpose: MigrationSessionPurpose;
  state: MigrationSessionState;
  totalBytes: number;
  chunkSize: number;
  totalChunks: number;
  fileIdentity: MigrationFileIdentityDto;
  receivedChunks: number[];
  receivedChunkCount: number;
  createdAtUtc: string;
  expiresAtUtc: string;
  idempotencyKey?: string | null;
}

export interface MigrationUploadSessionResponseDto {
  session: MigrationSessionStatusDto;
  /** Server-authoritative receipt state, compressed for HTTP efficiency. */
  receivedRanges: MigrationChunkRangeDto[];
}

export interface MigrationChunkUploadResultDto {
  sessionId: string;
  chunkIndex: number;
  alreadyPresent: boolean;
}

export interface MigrationProgressDto {
  phase: MigrationProgressPhase;
  bytesProcessed: number;
  totalBytes: number | null;
  completedChunks?: number | null;
  totalChunks?: number | null;
  message?: string | null;
}

export interface MigrationJobDto {
  id: string;
  direction: MigrationDirection;
  state: MigrationJobState;
  recoveryStatus: MigrationRecoveryStatus;
  createdAtUtc: string;
  updatedAtUtc: string;
  leaseToken?: string | null;
  leaseExpiresAtUtc?: string | null;
  failureCode?: string | null;
  failureMessage?: string | null;
}

export interface MigrationJobStatusResponseDto {
  job: MigrationJobDto;
  progress: MigrationProgressDto;
  session?: MigrationSessionStatusDto | null;
  downloadAvailable: boolean;
  artifactExpiresAtUtc?: string | null;
  preparedImport?: unknown;
}

export interface MigrationErrorResponseDto {
  error: MigrationErrorCode | string;
  message: string;
}

/**
 * Replacement-activation request body (#681 slice 8). The destination
 * revision is the opaque revision the user reviewed; it is always a value the
 * server returned (preflight, activation status or a 409 conflict body).
 */
export interface MigrationActivateRequestDto {
  destinationRevision: string;
  confirmReplacement: boolean;
}

/**
 * Activation status returned by both activation routes (POST 202 and GET
 * 200). Mirrors `MigrationActivationStatusResponse`; the same shape stays
 * answerable while exclusive maintenance has the live database closed.
 */
export interface MigrationActivationStatusDto {
  jobId: string;
  state: MigrationJobState;
  outcome: MigrationActivationOutcome;
  errorCode?: string | null;
  message?: string | null;
  maintenanceRequired: boolean;
  destinationRevision?: string | null;
  existingCounts?: MigrationExistingCountsDto | null;
  destinationStatus?: MigrationDestinationStatus | null;
  recoveryAvailable: boolean;
  recoveryExpiresAtUtc?: string | null;
  recoverySizeBytes?: number | null;
  phase?: MigrationActivationPhase | null;
  accepted: boolean;
  canActivate: boolean;
}

/**
 * Conflict body the activation route returns for
 * `migration_replacement_confirmation_required` and
 * `migration_destination_conflict`: the stable error members followed by the
 * fresh destination facts the browser must show before asking again.
 */
export interface MigrationActivationConflictDto {
  error:
    | 'migration_replacement_confirmation_required'
    | 'migration_destination_conflict';
  message: string;
  destinationRevision?: string | null;
  destinationStatus?: MigrationDestinationStatus | null;
  existingCounts?: MigrationExistingCountsDto | null;
  /**
   * True when the live revision differs from the import-start baseline: the
   * library moved while the import was being prepared, so the browser must
   * review the current facts rather than a cached confirmation (issue #681).
   */
  changedSinceImportStarted: boolean;
}

export interface BrowserMigrationChunk {
  index: number;
  offsetBytes: number;
  lengthBytes: number;
  sha256: string;
  blob: Blob;
}

/** Number of chunks a session of `totalBytes` needs at `chunkSize` (minimum 1). */
export function chunkCount(totalBytes: number, chunkSize: number): number {
  if (!Number.isFinite(totalBytes) || totalBytes <= 0) return 0;
  return Math.max(1, Math.ceil(totalBytes / chunkSize));
}

/** Byte length of chunk `index`; only the final chunk may be short. */
export function chunkLength(index: number, totalBytes: number, chunkSize: number): number {
  const offset = index * chunkSize;
  return Math.max(0, Math.min(chunkSize, totalBytes - offset));
}

/** Mirrors `MigrationContractLimits.IsValidChunkBytes`. */
export function isValidChunkBytes(
  chunkBytes: number,
  chunkSize: number,
  isFinalChunk: boolean,
): boolean {
  const withinSize =
    chunkSize >= MIGRATION_LIMITS.minChunkBytes && chunkSize <= MIGRATION_LIMITS.maxChunkBytes;
  if (!withinSize || chunkBytes <= 0) return false;
  return isFinalChunk ? chunkBytes <= chunkSize : chunkBytes === chunkSize;
}

/** Compresses a sorted list of chunk indexes into inclusive ranges. */
export function toChunkRanges(chunks: readonly number[]): MigrationChunkRangeDto[] {
  const sorted = [...new Set(chunks)].sort((a, b) => a - b);
  const ranges: MigrationChunkRangeDto[] = [];
  for (const index of sorted) {
    const last = ranges.at(-1);
    if (last && index === last.endIndex + 1) {
      last.endIndex = index;
    } else {
      ranges.push({ startIndex: index, endIndex: index });
    }
  }
  return ranges;
}

/** Expands server ranges back into a sorted chunk index list. */
export function fromChunkRanges(ranges: readonly MigrationChunkRangeDto[]): number[] {
  const chunks: number[] = [];
  for (const range of ranges) {
    for (let index = range.startIndex; index <= range.endIndex; index += 1) chunks.push(index);
  }
  return chunks;
}

/** Chunk indexes not covered by `receivedRanges`, in ascending order. */
export function missingChunkIndexes(
  receivedRanges: readonly MigrationChunkRangeDto[],
  totalChunks: number,
): number[] {
  const received = new Set(fromChunkRanges(receivedRanges));
  const missing: number[] = [];
  for (let index = 0; index < totalChunks; index += 1) {
    if (!received.has(index)) missing.push(index);
  }
  return missing;
}

export function isTerminalJobState(state: MigrationJobState): boolean {
  return (
    state === 'Completed' || state === 'Failed' || state === 'Cancelled' || state === 'Expired'
  );
}

export function isRetryableJobState(state: MigrationJobState): boolean {
  return state === 'Failed' || state === 'Cancelled' || state === 'Expired';
}
