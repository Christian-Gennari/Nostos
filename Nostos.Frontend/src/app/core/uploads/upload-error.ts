/**
 * One typed failure for every upload the browser starts, and the adapter that
 * reads the two wire shapes into it (#1646).
 *
 * The endpoints never agreed on a body: resumable books answer
 * `{ error, message }`, Calibre answers `{ code, message, missingParts }`, and
 * the legacy whole-request book upload uses both depending on the failure.
 * Readers only ever see {@link UploadFailure}; the copy map in
 * `upload-error-copy.ts` turns its code into one sentence.
 */

import { HttpErrorResponse } from '@angular/common/http';

import { TransferCancelledError } from './upload.models';

const UNEXPECTED_MESSAGE = 'The upload failed unexpectedly.';

/** The fields both wire bodies can carry, whichever spelling the server used. */
export interface UploadErrorBody {
  code: string;
  message?: string;
  /** Calibre's `calibre_upload_incomplete` names the part indexes still needed. */
  missingParts?: number[];
}

export interface UploadFailureOptions {
  retryAfterMs?: number;
  cause?: unknown;
  missingParts?: number[];
}

/**
 * A stable upload code plus the facts the UI needs to explain it: the HTTP
 * status (0 when the request never reached the server), whether a retry is
 * allowed, and Calibre's missing part indexes.
 */
export class UploadFailure extends Error {
  readonly retryAfterMs?: number;
  readonly missingParts?: number[];

  constructor(
    readonly code: string,
    readonly status: number,
    message: string,
    options: UploadFailureOptions = {},
  ) {
    super(message, options.cause === undefined ? undefined : { cause: options.cause });
    this.name = 'UploadFailure';
    this.retryAfterMs = options.retryAfterMs;
    this.missingParts = options.missingParts;
  }

  /** Only transient statuses may be retried; a cancellation never may. */
  get retryable(): boolean {
    if (this.code === 'request_aborted') return false;
    return isTransientStatus(this.status);
  }
}

/** A deliberate stop, never a transport failure. */
export function uploadCancelled(message = 'The upload was cancelled.'): TransferCancelledError {
  return new TransferCancelledError(message);
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

/** `Retry-After` is either delay-seconds (the middleware's `5`) or an HTTP date. */
export function parseRetryAfterMs(value: string | null): number | undefined {
  if (!value) return undefined;
  const seconds = Number(value);
  if (Number.isFinite(seconds) && seconds >= 0) return Math.round(seconds * 1000);

  const date = Date.parse(value);
  if (Number.isNaN(date)) return undefined;
  return Math.max(0, date - Date.now());
}

/**
 * Reads a failure body, whether Angular handed it over parsed or as text, and
 * whichever spelling it used: `{ error }` (books, migrations) or `{ code }`
 * (Calibre, the legacy book upload).
 */
export function readUploadErrorBody(value: unknown): UploadErrorBody | null {
  if (typeof value === 'string') {
    try {
      return readUploadErrorBody(JSON.parse(value) as unknown);
    } catch {
      return null;
    }
  }
  if (typeof value !== 'object' || value === null) return null;

  const record = value as Record<string, unknown>;
  const code =
    typeof record['code'] === 'string'
      ? record['code']
      : typeof record['error'] === 'string'
        ? record['error']
        : null;
  if (!code) return null;

  const missingParts = Array.isArray(record['missingParts'])
    ? record['missingParts'].filter((part): part is number => typeof part === 'number')
    : undefined;

  return {
    code,
    message: typeof record['message'] === 'string' ? record['message'] : undefined,
    missingParts,
  };
}

/**
 * Normalises any thrown value — an {@link UploadFailure}, an Angular HTTP error,
 * a plain `{ code, status }` object from a fake, or anything else — into the one
 * typed failure the upload UI reads.
 */
export function toUploadFailure(error: unknown): UploadFailure {
  if (error instanceof UploadFailure) return error;

  if (error instanceof TransferCancelledError) {
    return new UploadFailure('request_aborted', 0, error.message || 'The upload was cancelled.', {
      cause: error,
    });
  }

  if (error instanceof HttpErrorResponse) {
    const body = readUploadErrorBody(error.error);
    return new UploadFailure(
      body?.code ?? (error.status === 0 ? 'network_error' : 'unexpected_error'),
      error.status,
      body?.message ?? defaultMessageForStatus(error.status),
      {
        retryAfterMs: parseRetryAfterMs(error.headers?.get('Retry-After')),
        missingParts: body?.missingParts,
        cause: error,
      },
    );
  }

  const record = isRecord(error) ? error : undefined;
  const body = readUploadErrorBody(record?.['error']) ?? readUploadErrorBody(error);
  return new UploadFailure(
    body?.code ?? 'unexpected_error',
    typeof record?.['status'] === 'number' ? record['status'] : 0,
    body?.message ?? messageOf(record),
    {
      retryAfterMs:
        typeof record?.['retryAfterMs'] === 'number' ? record['retryAfterMs'] : undefined,
      missingParts: body?.missingParts,
      cause: error,
    },
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function messageOf(record: Record<string, unknown> | undefined): string {
  const message = record?.['message'];
  return typeof message === 'string' && message.trim().length > 0 ? message : UNEXPECTED_MESSAGE;
}

function defaultMessageForStatus(status: number): string {
  if (status === 0) return 'The connection was interrupted.';
  if (status === 413) return 'This file is larger than this server allows.';
  return 'The server did not accept this file.';
}
