/**
 * Transport-level error model shared by every `LibraryTransferTransport`
 * implementation (HTTP, mock, future Cloud adapter). It lives in its own
 * module so the real HTTP adapter can import it without importing the
 * transport seam that constructs the adapter in its DI factory.
 */

import {
  LibraryActivationConflictFacts,
  LibraryTransferFailure,
  TransferCancelledError,
} from '../models/library-transfer.models';
import {
  UploadFailure,
} from '../../core/uploads/upload-error';
import type { MigrationErrorCode } from '../models/migration-http.dtos';

// Retry and `Retry-After` policy is shared with every other upload (#1646);
// re-exported here so the transport modules keep one import site.
export { isTransientStatus, parseRetryAfterMs } from '../../core/uploads/upload-error';

/**
 * Transport-level error carrying the stable #679 error code. It is an
 * {@link UploadFailure} so the shared chunk engine can retry it on the same
 * terms as a book or Calibre part.
 */
export class MigrationTransportError extends UploadFailure {
  constructor(
    override readonly code: MigrationErrorCode,
    override readonly status: number,
    message: string,
    options?: { retryAfterMs?: number; cause?: unknown },
  ) {
    super(code, status, message, options);
    this.name = 'MigrationTransportError';
  }
}

/**
 * Activation admission conflict that preserves the destination facts from the
 * 409 body (`destinationRevision`, `destinationStatus`, `existingCounts`) so
 * the UI can re-review with the server's values instead of a stale revision.
 */
export class MigrationActivationConflictError extends MigrationTransportError {
  constructor(
    code: MigrationErrorCode,
    status: number,
    message: string,
    readonly conflict: LibraryActivationConflictFacts,
    options?: { retryAfterMs?: number; cause?: unknown },
  ) {
    super(code, status, message, options);
    this.name = 'MigrationActivationConflictError';
  }
}

/**
 * The two 503 maintenance codes the coordinator waits out and re-attempts on
 * the same operation: exclusive library maintenance and host storage
 * contention.
 */
export function isMaintenanceBusy(code: MigrationErrorCode): boolean {
  return code === 'migration_activation_busy' || code === 'migration_storage_contended';
}

/** Normalises any thrown value into the UI-facing failure model. */
export function toTransferFailure(error: unknown): LibraryTransferFailure {
  if (error instanceof MigrationTransportError) {
    return {
      code: error.code,
      message: error.message,
      retryable: error.retryable,
      status: error.status,
      retryAfterMs: error.retryAfterMs,
    };
  }

  if (error instanceof UploadFailure) {
    return {
      code: error.code as MigrationErrorCode,
      message: error.message,
      retryable: error.retryable,
      status: error.status,
      retryAfterMs: error.retryAfterMs,
    };
  }

  if (error instanceof TransferCancelledError) {
    return { code: 'request_aborted', message: error.message, retryable: false, status: 0 };
  }

  return {
    code: 'unexpected_error',
    message: error instanceof Error ? error.message : 'The transfer failed unexpectedly.',
    retryable: false,
  };
}

/** The `{ error, message }` body every migration endpoint answers a failure with. */
export interface TransportErrorBody {
  error: string;
  message: string;
}

/** Reads a failure body, whether Angular handed it over parsed or as text. */
export function readErrorBody(value: unknown): TransportErrorBody | null {
  if (typeof value === 'string') {
    try {
      return readErrorBody(JSON.parse(value));
    } catch {
      return null;
    }
  }
  if (typeof value !== 'object' || value === null) return null;
  const record = value as Record<string, unknown>;
  if (typeof record['error'] !== 'string') return null;
  return {
    error: record['error'],
    message: typeof record['message'] === 'string' ? record['message'] : record['error'],
  };
}

export function abortedError(): MigrationTransportError {
  return new MigrationTransportError('request_aborted', 0, 'The request was aborted.');
}
