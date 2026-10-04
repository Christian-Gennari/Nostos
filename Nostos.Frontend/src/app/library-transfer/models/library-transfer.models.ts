/**
 * Framework-neutral browser models for the library-transfer feature (#680).
 *
 * These types describe what the transfer engine and the future UI state
 * machine need, independent of any transport, component or storage backend.
 */

import type {
  MigrationErrorCode,
  MigrationFileIdentityDto,
  MigrationJobState,
  MigrationPreflightDecision,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
} from './migration-http.dtos';

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

/** Persisted resume metadata (plan §13). Never contains file bytes or URLs. */
export interface PersistedTransferResumeState {
  schemaVersion: 1;
  jobId: string;
  sessionId?: string;
  direction: 'import';
  fileIdentity: TransferFileIdentity;
  fileName: string;
  preflightRequest: MigrationPreflightRequestDto;
  preflightDecision?: MigrationPreflightDecision;
  createdAt: string;
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
    }
  | {
      kind: 'replacement-confirmation';
      jobId: string;
      jobState: MigrationJobState;
      preflight?: MigrationPreflightResponseDto;
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
  inspecting: ['preflighting', 'failed'],
  preflighting: ['ready-to-upload', 'failed'],
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
  'ready-empty': [],
  'replacement-confirmation': [],
  completed: ['idle'],
  failed: ['inspecting', 'uploading', 'checking', 'ready-to-upload'],
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
