/**
 * Provider-neutral transport seam for library migration (plan §3, §42).
 *
 * Components and the coordinator only ever talk to this interface. The
 * SelfHosted HTTP adapter (#680 slice B7) and the private Cloud adapter
 * implement the same contract; the in-repo mock below implements it today so
 * B1-B3 can be built and tested before #679's endpoints merge.
 */

import { InjectionToken } from '@angular/core';

import {
  BrowserMigrationChunk,
  MigrationCreateJobRequestDto,
  MigrationChunkUploadResultDto,
  MigrationErrorCode,
  MigrationJobStatusResponseDto,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationSessionRequestDto,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
} from '../models/migration-http.dtos';
import { LibraryTransferFailure, TransferCancelledError } from '../models/library-transfer.models';

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
    return isTransientStatus(this.status);
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

export interface LibraryTransferTransport {
  preflight(
    request: MigrationPreflightRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationPreflightResponseDto>;

  createJob(
    request: MigrationCreateJobRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto>;

  getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto>;

  cancelJob(
    jobId: string,
    reason?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto>;

  retryJob(
    jobId: string,
    idempotencyKey?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto>;

  createUploadSession(
    jobId: string,
    request: MigrationSessionRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto>;

  getUploadSession(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto>;

  uploadChunk(
    jobId: string,
    sessionId: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto>;

  completeUpload(jobId: string, signal?: AbortSignal): Promise<MigrationSessionStatusDto>;

  /** Native browser download URL; never fetched into Angular memory (plan §39). */
  getExportDownloadUrl(jobId: string): string;
}

/**
 * The #676 orchestration text calls this seam `PortableTransferClient`; the
 * plan calls it `LibraryTransferTransport`. Both names refer to this interface.
 */
export type PortableTransferClient = LibraryTransferTransport;

/**
 * Production DI default until slice B7 wires the real #679 adapter. It fails
 * closed instead of falling back to an in-memory mock, so a deployment that
 * advertises `supportsLibraryMigration` before B7 lands reports a clear
 * configuration error rather than a fake transfer UI. Tests provide their own
 * transport through `LIBRARY_TRANSFER_TRANSPORT`.
 */
export function createLibraryTransferTransport(): LibraryTransferTransport {
  throw new Error('library transfer transport is not configured');
}

/**
 * DI seam. The real SelfHosted adapter arrives in slice B7 and the private
 * Cloud adapter in B9; until then the production provider above throws and
 * tests inject a fake.
 */
export const LIBRARY_TRANSFER_TRANSPORT = new InjectionToken<LibraryTransferTransport>(
  'NOSTOS_LIBRARY_TRANSFER_TRANSPORT',
  {
    providedIn: 'root',
    factory: createLibraryTransferTransport,
  },
);
