/**
 * Provider-neutral transport seam for library migration (plan §3, §42).
 *
 * Components and the coordinator only ever talk to this interface. The
 * SelfHosted HTTP adapter (#680 slice B7) and the private Cloud adapter
 * implement the same contract; the in-repo mock below implements it today so
 * B1-B3 can be built and tested before #679's endpoints merge.
 */

import { InjectionToken, isDevMode } from '@angular/core';

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
import { MockLibraryTransferTransport } from './mock-library-transfer-transport.service';

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
 * Explicit opt-in that lets a production-configured build deliberately run the
 * in-memory mock (local smoke tests only). Tests and dev builds are covered by
 * `isDevMode()` and never need this flag.
 */
export const MOCK_TRANSFER_TRANSPORT_OPT_IN = '__NOSTOS_ALLOW_MOCK_LIBRARY_TRANSFER__';

/** True when the in-memory mock may be the active transport. */
export function mockTransferTransportAllowed(devMode: boolean, explicitOptIn: boolean): boolean {
  return devMode || explicitOptIn;
}

function mockTransferTransportOptedIn(): boolean {
  return (
    (globalThis as Record<string, unknown>)[MOCK_TRANSFER_TRANSPORT_OPT_IN] === true
  );
}

/**
 * Creates the transport the DI token provides. The in-memory mock must never
 * be the active transport of a real deployment: until slice B7 wires the real
 * #679 API this factory fails closed outside dev/test builds unless a caller
 * explicitly opts in (plan §4; B5/B6 capability gating).
 */
export function createLibraryTransferTransport(
  devMode: boolean = isDevMode(),
  explicitOptIn: boolean = mockTransferTransportOptedIn(),
): LibraryTransferTransport {
  if (!mockTransferTransportAllowed(devMode, explicitOptIn)) {
    throw new Error(
      'The in-memory library-transfer transport cannot be the active transport in a ' +
        `production build. Wire the real #679 transport (slice B7) or set ` +
        `globalThis.${MOCK_TRANSFER_TRANSPORT_OPT_IN} = true for a deliberate local run.`,
    );
  }
  return new MockLibraryTransferTransport();
}

/**
 * DI seam. Until #679's HTTP endpoints and #681's activation land, the default
 * provider is the in-memory mock (dev/test only); slice B7 replaces this
 * factory with the real SelfHosted adapter and B8 extends the interface with
 * activation.
 */
export const LIBRARY_TRANSFER_TRANSPORT = new InjectionToken<LibraryTransferTransport>(
  'NOSTOS_LIBRARY_TRANSFER_TRANSPORT',
  {
    providedIn: 'root',
    factory: createLibraryTransferTransport,
  },
);
