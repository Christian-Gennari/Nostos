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
import type { MigrationErrorCode } from '../models/migration-http.dtos';

/** Transport-level error carrying the stable #679 error code. */
export class MigrationTransportError extends Error {
  constructor(
    readonly code: MigrationErrorCode,
    readonly status: number,
    message: string,
    options?: { retryAfterMs?: number; cause?: unknown },
  ) {
    super(message, options?.cause === undefined ? undefined : { cause: options.cause });
    this.name = 'MigrationTransportError';
    this.retryAfterMs = options?.retryAfterMs;
  }

  readonly retryAfterMs?: number;

  /** True only for the transient statuses the plan allows to be retried (plan §22). */
  get retryable(): boolean {
    // A browser cancellation is a deliberate act, never a transient failure,
    // even though it carries status 0.
    if (this.code === 'request_aborted') return false;
    return isTransientStatus(this.status);
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

/** Network failure (no HTTP status) or the retryable statuses from plan §22. */
export function isTransientStatus(status: number): boolean {
  return (
    status === 0 || // connection reset / network failure
    status === 408 ||
    status === 429 ||
    status === 502 ||
    status === 503 ||
    status === 504
  );
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

  if (error instanceof TransferCancelledError) {
    return { code: 'request_aborted', message: error.message, retryable: false, status: 0 };
  }

  return {
    code: 'unexpected_error',
    message: error instanceof Error ? error.message : 'The transfer failed unexpectedly.',
    retryable: false,
  };
}
