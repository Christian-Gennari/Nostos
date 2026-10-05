/**
 * Framework-neutral browser models for the library-transfer feature (#680).
 *
 * These types describe what the transfer engine and the future UI state
 * machine need, independent of any transport, component or storage backend.
 */

import type {
  MigrationDestinationStatus,
  MigrationErrorCode,
  MigrationExistingCountsDto,
  MigrationFileIdentityDto,
  MigrationJobState,
  MigrationPreflightDecision,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationSessionRequestDto,
} from './migration-http.dtos';

/** Host-reported activation state rendered by the import flow (slice B8). */
export type HostActivationState = 'idle' | 'in-progress' | 'completed' | 'failed';

/** Destination facts the activation route reports for a re-review (409 body). */
export interface LibraryActivationConflictFacts {
  destinationRevision: string | null;
  destinationStatus: MigrationDestinationStatus | null;
  existingCounts: MigrationExistingCountsDto | null;
}

/** Read block for whole-file hashing: bounded regardless of archive size (plan §11.3). */
export const HASH_READ_BLOCK_BYTES = 4 * 1024 * 1024;

/** Default upload concurrency: enough pipelining, bounded memory (plan §19). */
export const DEFAULT_UPLOAD_CONCURRENCY = 2;

/** Default total attempts per chunk, including the first (plan §22). */
export const DEFAULT_CHUNK_MAX_ATTEMPTS = 4;

export class TransferCancelledError extends Error {
  constructor(message = 'The operation was cancelled.') {
    super(message);
    this.name = 'TransferCancelledError';
  }
}

/** Transient progress of a single in-flight chunk request. */
export interface ChunkProgress {
  chunkIndex: number;
  loadedBytes: number;
  totalBytes: number;
}

/** Aggregated transfer progress suitable for the later progress component (plan §23). */
export interface TransferProgress {
  /** Server-confirmed completed bytes; never decreases. */
  uploadedBytes: number;
  totalBytes: number;
  completedChunks: number;
  totalChunks: number;
  /** Bytes of in-flight requests currently known to be on the wire. */
  inFlightBytes: number;
  /** Rolling transfer rate, null until the estimator has enough samples. */
  rateBytesPerSecond: number | null;
  /** Seconds remaining, null while the rate is unreliable. */
  etaSeconds: number | null;
  paused: boolean;
}

/** UI-facing failure: stable code plus an English message; B4 maps copy. */
export interface LibraryTransferFailure {
  code: MigrationErrorCode;
  message: string;
  retryable: boolean;
  status?: number;
  retryAfterMs?: number;
  /**
   * Structured capacity facts for `migration_storage_exhausted`, so the B4
   * copy can state human-readable amounts instead of the raw backend message
   * (plan §33).
   */
  requiredStorageBytes?: number;
  availableStorageBytes?: number;
}

/** Derived identity of the selected archive. */
export type TransferFileIdentity = MigrationFileIdentityDto;

/** Result of bounded archive inspection (plan §10). */
export type ArchiveInspection =
  | { kind: 'portable'; summary: PortableArchiveSummary }
  | { kind: 'operational-backup' }
  | { kind: 'unsupported'; reason: ArchiveInspectionReason };

export type ArchiveInspectionReason =
  | 'not-a-zip'
  | 'truncated'
  | 'too-many-entries'
  | 'missing-manifest'
  | 'duplicate-manifest'
  | 'manifest-too-large'
  | 'manifest-unreadable'
  | 'unsupported-compression-method'
  | 'malformed-manifest'
  | 'unsupported-format-version'
  | 'unsupported-data-version';

/** Everything preflight and the file identity need, derived without whole-archive reads. */
export interface PortableArchiveSummary {
  formatName: string;
  formatVersion: number;
  dataVersion: number;
  archiveBytes: number;
  mediaBytes: number;
  maxEntryBytes: number;
  counts: MigrationPreflightRequestDto['incomingCounts'];
  mediaEntries: number;
}

export interface FileDigest {
  /** Whole-file streaming SHA-256; bounded reads, progress, cancellable. */
  sha256(
    file: Blob,
    options?: {
      signal?: AbortSignal;
      readBlockBytes?: number;
      onProgress?: (bytesRead: number, totalBytes: number) => void;
    },
  ): Promise<string>;

  /** One-shot SHA-256 of a single bounded chunk. */
  sha256Chunk(blob: Blob): Promise<string>;

  /** Versioned cheap fingerprint over size + head + tail (plan §14). */
  fingerprint(file: Blob): Promise<string>;
}

/**
 * Persisted resume metadata (plan §13, §16, §17). Never contains file bytes or
 * URLs.
 *
 * The record is written in stages so no crash window loses a creation
 * idempotency key:
 *
 * 1. after preflight, before `createJob()`: the job-creation key, reservation,
 *    identity, preflight request and the prepared upload-session request/key;
 * 2. after `createJob()` returns: `jobId` is promoted;
 * 3. after `createUploadSession()` returns: `sessionId` is promoted.
 */
export interface PersistedTransferResumeState {
  schemaVersion: 1;
  /** Absent until the create-job response has been observed. */
  jobId?: string;
  jobCreationIdempotencyKey: string;
  reservationId?: string | null;
  sessionId?: string;
  sessionCreationIdempotencyKey?: string;
  sessionRequest?: MigrationSessionRequestDto;
  chunkSizeBytes?: number;
  direction: 'import';
  fileIdentity: TransferFileIdentity;
  fileName: string;
  preflightRequest: MigrationPreflightRequestDto;
  preflightDecision?: MigrationPreflightDecision;
  /**
   * Destination revision the server returned at preflight, i.e. the revision
   * the user reviewed. Activation uses it (never a client-invented value) and
   * re-confirms with the server's fresh revision when it conflicts.
   */
  destinationRevision?: string;
  /**
   * Activation handoff (slice B8). Written only once the server answered 202,
   * so a reload re-attaches to status polling instead of losing the outcome.
   * A request whose 202 was never observed is deliberately not persisted: a
   * destructive confirmation must never be replayed after a reload without
   * the user seeing the current library again (review-748).
   */
  activation?: PersistedActivationResumeState;
  createdAt: string;
}

/** Persisted activation handoff for the resume record. */
export interface PersistedActivationResumeState {
  /** True once the server answered 202 to the activation request. */
  accepted: true;
}

/** Import flow states (§8), extended with the engine-level pause flag and notices. */
export type TransferFlowState =
  | { kind: 'idle' }
  | { kind: 'inspecting'; fileName: string; bytesRead: number; totalBytes: number }
  | { kind: 'preflighting'; summary: PortableArchiveSummary }
  | {
      kind: 'ready-to-upload';
      jobId: string;
      sessionId?: string;
      preflight?: MigrationPreflightResponseDto;
      reselectionRequired: boolean;
      notice?: LibraryTransferFailure;
    }
  | { kind: 'uploading'; jobId: string; progress: TransferProgress; paused: boolean }
  | {
      kind: 'checking';
      jobId: string;
      jobState: MigrationJobState;
      progress: TransferProgress;
      preflight?: MigrationPreflightResponseDto;
    }
  | {
      kind: 'ready-empty';
      jobId: string;
      jobState: MigrationJobState;
      preflight?: MigrationPreflightResponseDto;
      /** Server-derived prepared-import facts when the host reports them. */
      preparedImport?: unknown;
    }
  | {
      kind: 'replacement-confirmation';
      jobId: string;
      jobState: MigrationJobState;
      preflight?: MigrationPreflightResponseDto;
      /** Server-derived prepared-import facts when the host reports them. */
      preparedImport?: unknown;
    }
  | { kind: 'completed'; jobId: string }
  | { kind: 'failed'; jobId?: string; failure: LibraryTransferFailure }
  | { kind: 'cancelled'; jobId: string };

/** Allowed outgoing transitions of the coordinator state machine. */
const FLOW_TRANSITIONS: Record<TransferFlowState['kind'], readonly TransferFlowState['kind'][]> = {
  // Reattachment after a reload moves from idle straight to the durable state.
  idle: [
    'inspecting',
    'ready-to-upload',
    'checking',
    'ready-empty',
    'replacement-confirmation',
    'completed',
    'failed',
    'cancelled',
  ],
  inspecting: ['preflighting', 'failed', 'cancelled'],
  preflighting: ['ready-to-upload', 'failed', 'cancelled'],
  'ready-to-upload': ['uploading', 'failed', 'cancelled', 'ready-to-upload'],
  uploading: ['checking', 'failed', 'cancelled', 'ready-to-upload', 'uploading'],
  checking: [
    'checking',
    'ready-empty',
    'replacement-confirmation',
    'completed',
    'failed',
    'cancelled',
  ],
  // A verified job can still be cancelled while it is waiting to activate,
  // and activation (B8) moves it to checking/completed or fails it (plan §32).
  // Dismissing a verified-but-not-yet-activatable job returns to idle while
  // keeping its resume record (review-730 item 2).
  'ready-empty': ['idle', 'checking', 'completed', 'cancelled', 'failed'],
  'replacement-confirmation': ['idle', 'checking', 'completed', 'cancelled', 'failed'],
  completed: ['idle'],
  // A failed transfer can reattach to any durable server state after the
  // problem is fixed (re-authentication, reconnect, retry): the job may have
  // progressed while the browser showed the failure, so every status target
  // `resume()` can observe must be reachable (review-730 401 recovery note).
  failed: [
    'inspecting',
    'uploading',
    'checking',
    'ready-to-upload',
    'ready-empty',
    'replacement-confirmation',
    'completed',
    'cancelled',
  ],
  cancelled: ['idle', 'inspecting'],
};

export function canTransitionFlow(
  from: TransferFlowState['kind'],
  to: TransferFlowState['kind'],
): boolean {
  return FLOW_TRANSITIONS[from].includes(to);
}

/** True when every chunk of a session has a server receipt. */
export function isUploadComplete(session: {
  totalChunks: number;
  receivedChunkCount: number;
}): boolean {
  return session.receivedChunkCount >= session.totalChunks;
}
