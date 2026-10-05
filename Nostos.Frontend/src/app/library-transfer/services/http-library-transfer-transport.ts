/**
 * Real SelfHosted transport for library migration (issue #680, slice B7).
 *
 * Talks to the merged #679 HTTP API at `/api/portability/migration`
 * (`Nostos.Product/Endpoints/MigrationEndpoints.cs`):
 *
 * ```text
 * POST /preflight
 * POST /jobs
 * GET  /jobs/{id}
 * POST /jobs/{id}/cancel
 * POST /jobs/{id}/retry
 * POST /jobs/{id}/upload-session
 * GET  /jobs/{id}/upload-session
 * PUT  /jobs/{id}/upload-session/chunks/{index}   (Blob slice + range/hash headers)
 * POST /jobs/{id}/upload-session/complete
 * GET|HEAD /jobs/{id}/export-download
 * ```
 *
 * The chunk body is the `Blob` slice itself: Angular's XHR backend hands it to
 * `XMLHttpRequest.send`, which streams the file reference instead of
 * materialising an `ArrayBuffer`. The transport never retries; the chunk
 * upload engine and the coordinator own retry/backoff (plan §22).
 *
 * `getExportDownloadUrl` returns the same-origin download URL for the native
 * browser download path; the route is served from the migration group so
 * `Content-Disposition: attachment`, range requests and the migration error
 * body (`migration_not_found`, `migration_export_not_available`,
 * `migration_export_expired`) all apply to the browser's own request.
 */

import {
  HttpClient,
  HttpErrorResponse,
  HttpEvent,
  HttpEventType,
  HttpHeaders,
  HttpResponse,
} from '@angular/common/http';
import { Observable, Subscription } from 'rxjs';

import {
  BrowserMigrationChunk,
  MigrationActivateRequestDto,
  MigrationActivationStatusDto,
  MigrationChunkUploadResultDto,
  MigrationCreateJobRequestDto,
  MigrationDestinationStatus,
  MigrationErrorCode,
  MigrationExistingCountsDto,
  MigrationJobStatusResponseDto,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationSessionRequestDto,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
  SERVER_MIGRATION_ERROR_CODES,
  chunkLength,
  fromChunkRanges,
} from '../models/migration-http.dtos';
import {
  MigrationActivationConflictError,
  MigrationTransportError,
} from './migration-transport-error';
import type { LibraryTransferTransport } from './library-transfer-transport';

export const MIGRATION_BASE_PATH = '/api/portability/migration';

/** Header consumed by `MigrationChunkHeaders.ChunkHashHeaderName`. */
export const CHUNK_HASH_HEADER_NAME = 'X-Nostos-Chunk-SHA256';

const SHA256_PATTERN = /^[0-9a-f]{64}$/i;

const SERVER_CODES: ReadonlySet<string> = new Set(SERVER_MIGRATION_ERROR_CODES);

interface TransportErrorBody {
  error: string;
  message: string;
}

export class HttpLibraryTransferTransport implements LibraryTransferTransport {
  /**
   * Session contracts seen by this browser instance, keyed by session id. The
   * chunk PUT only carries the index/offset/length/hash, so the session's
   * `totalBytes`/`chunkSize` come from the create/get session response; if the
   * session is unknown the transport re-reads the authoritative server state.
   */
  private readonly sessions = new Map<string, MigrationSessionStatusDto>();

  constructor(
    private readonly http: HttpClient,
    private readonly basePath: string = MIGRATION_BASE_PATH,
  ) {}

  preflight(
    request: MigrationPreflightRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationPreflightResponseDto> {
    return this.json<MigrationPreflightResponseDto>(
      'POST',
      `${this.basePath}/preflight`,
      request,
      signal,
    );
  }

  createJob(
    request: MigrationCreateJobRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return this.json<MigrationJobStatusResponseDto>(
      'POST',
      `${this.basePath}/jobs`,
      {
        direction: request.direction,
        idempotencyKey: request.idempotencyKey,
        reservationId: request.reservationId ?? null,
      },
      signal,
    ).then((status) => this.rememberStatusSession(status));
  }

  getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto> {
    return this.json<MigrationJobStatusResponseDto>(
      'GET',
      this.jobUrl(jobId),
      undefined,
      signal,
    ).then((status) => this.rememberStatusSession(status));
  }

  cancelJob(
    jobId: string,
    reason?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return this.json<MigrationJobStatusResponseDto>(
      'POST',
      `${this.jobUrl(jobId)}/cancel`,
      reason === undefined ? null : { reason },
      signal,
    ).then((status) => this.rememberStatusSession(status));
  }

  retryJob(
    jobId: string,
    idempotencyKey?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return this.json<MigrationJobStatusResponseDto>(
      'POST',
      `${this.jobUrl(jobId)}/retry`,
      idempotencyKey === undefined ? null : { idempotencyKey },
      signal,
    ).then((status) => this.rememberStatusSession(status));
  }

  createUploadSession(
    jobId: string,
    request: MigrationSessionRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    return this.json<MigrationUploadSessionResponseDto>(
      'POST',
      `${this.jobUrl(jobId)}/upload-session`,
      request,
      signal,
    ).then((response) => this.normalizeUploadSession(response));
  }

  getUploadSession(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    return this.json<MigrationUploadSessionResponseDto>(
      'GET',
      `${this.jobUrl(jobId)}/upload-session`,
      undefined,
      signal,
    ).then((response) => this.normalizeUploadSession(response));
  }

  async uploadChunk(
    jobId: string,
    sessionId: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    if (signal.aborted) throw abortedError();

    const session = await this.sessionFor(jobId, sessionId, signal);
    if (!isChunkValid(session, request)) {
      throw new MigrationTransportError(
        'migration_chunk_range_invalid',
        416,
        'The chunk range does not match the session contract.',
      );
    }

    const end = request.offsetBytes + request.lengthBytes - 1;
    const headers = new HttpHeaders({
      'Content-Type': 'application/octet-stream',
      'Content-Range': `bytes ${request.offsetBytes}-${end}/${session.totalBytes}`,
      [CHUNK_HASH_HEADER_NAME]: request.sha256,
    });

    // `request.blob` is the original `File.slice()`: never copied into an
    // ArrayBuffer, so the browser can stream it straight to the socket.
    const events = this.http.request<MigrationChunkUploadResultDto>(
      'PUT',
      `${this.jobUrl(jobId)}/upload-session/chunks/${request.index}`,
      { body: request.blob, headers, observe: 'events', reportProgress: true },
    );
    return this.awaitUpload(events, onProgress, signal);
  }

  completeUpload(jobId: string, signal?: AbortSignal): Promise<MigrationSessionStatusDto> {
    return this.json<MigrationSessionStatusDto>(
      'POST',
      `${this.jobUrl(jobId)}/upload-session/complete`,
      null,
      signal,
    ).then((session) => {
      this.sessions.set(session.sessionId, session);
      return session;
    });
  }

  getExportDownloadUrl(jobId: string): string {
    // Same-origin and relative, so the export flow's URL validation always
    // accepts it and the browser performs a native streaming download (range
    // requests and resume included) instead of buffering the archive.
    return `${this.jobUrl(jobId)}/export-download`;
  }

  /**
   * Starts (or observes) background activation. A 202 is the current status
   * envelope; the server owns the cutover from there. A 409 confirmation or
   * destination conflict preserves the fresh destination facts.
   */
  activateJob(
    jobId: string,
    request: MigrationActivateRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationActivationStatusDto> {
    return this.awaitHttp(
      this.http.request<MigrationActivationStatusDto>(
        'POST',
        `${this.jobUrl(jobId)}/activate`,
        { body: request },
      ),
      signal,
      (error) => this.toActivationError(error),
    );
  }

  /**
   * Lightweight activation status. It stays answerable while exclusive
   * maintenance has the live database closed, so the browser keeps polling
   * through the window instead of losing the outcome.
   */
  getActivationStatus(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationActivationStatusDto> {
    return this.json<MigrationActivationStatusDto>(
      'GET',
      `${this.jobUrl(jobId)}/activation`,
      undefined,
      signal,
    );
  }

  // --------------------------------------------------------------- internals --

  private jobUrl(jobId: string): string {
    return `${this.basePath}/jobs/${encodeURIComponent(jobId)}`;
  }

  /** JSON control-plane request with cancellation and typed error mapping. */
  private json<T>(
    method: 'GET' | 'POST' | 'PUT',
    url: string,
    body: unknown,
    signal?: AbortSignal,
  ): Promise<T> {
    return this.awaitHttp(this.http.request<T>(method, url, { body: body ?? null }), signal);
  }

  private awaitHttp<T>(
    observable: Observable<T>,
    signal?: AbortSignal,
    mapError: (error: unknown) => Error = (error) => this.toTransportError(error),
  ): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      if (signal?.aborted) {
        reject(abortedError());
        return;
      }

      let settled = false;
      let subscription: Subscription | null = null;
      const finish = () => {
        settled = true;
        signal?.removeEventListener('abort', onAbort);
      };
      const onAbort = () => {
        if (settled) return;
        finish();
        subscription?.unsubscribe();
        reject(abortedError());
      };

      subscription = observable.subscribe({
        next: (value) => {
          if (settled) return;
          finish();
          resolve(value);
        },
        error: (error: unknown) => {
          if (settled) return;
          finish();
          reject(mapError(error));
        },
        complete: () => {
          /* `next` already resolved. */
        },
      });
      signal?.addEventListener('abort', onAbort, { once: true });
      if (signal?.aborted) onAbort();
    });
  }

  private awaitUpload(
    source: Observable<HttpEvent<MigrationChunkUploadResultDto>>,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    return new Promise<MigrationChunkUploadResultDto>((resolve, reject) => {
      if (signal.aborted) {
        reject(abortedError());
        return;
      }

      let settled = false;
      let subscription: Subscription | null = null;
      const finish = () => {
        settled = true;
        signal.removeEventListener('abort', onAbort);
      };
      const onAbort = () => {
        if (settled) return;
        finish();
        subscription?.unsubscribe();
        reject(abortedError());
      };

      subscription = source.subscribe({
        next: (event) => {
          if (settled) return;
          if (event.type === HttpEventType.UploadProgress) {
            onProgress(event.loaded, event.total ?? event.loaded);
            return;
          }
          if (event instanceof HttpResponse) {
            finish();
            resolve(event.body as MigrationChunkUploadResultDto);
          }
        },
        error: (error: unknown) => {
          if (settled) return;
          finish();
          reject(this.toTransportError(error));
        },
        complete: () => {
          /* The HttpResponse event already resolved. */
        },
      });
      signal.addEventListener('abort', onAbort, { once: true });
      if (signal.aborted) onAbort();
    });
  }

  /**
   * Resolves the session contract for a chunk PUT. `receivedRanges` is the
   * server's compressed receipt representation, so the session the engine sees
   * always takes its missing-chunk set from the authoritative ranges.
   */
  private normalizeUploadSession(
    response: MigrationUploadSessionResponseDto,
  ): MigrationUploadSessionResponseDto {
    const ranges = response.receivedRanges;
    if (Array.isArray(ranges)) {
      if (!areRangesValid(ranges, response.session.totalChunks)) {
        throw new MigrationTransportError(
          'migration_invalid_state',
          409,
          'The server returned inconsistent upload receipt ranges.',
        );
      }
      const receivedChunks = fromChunkRanges(ranges);
      const session: MigrationSessionStatusDto = {
        ...response.session,
        receivedChunks,
        receivedChunkCount: receivedChunks.length,
      };
      this.sessions.set(session.sessionId, session);
      return { session, receivedRanges: ranges };
    }

    this.sessions.set(response.session.sessionId, response.session);
    return response;
  }

  private rememberStatusSession(
    status: MigrationJobStatusResponseDto,
  ): MigrationJobStatusResponseDto {
    if (status.session) this.sessions.set(status.session.sessionId, status.session);
    return status;
  }

  private async sessionFor(
    jobId: string,
    sessionId: string,
    signal?: AbortSignal,
  ): Promise<MigrationSessionStatusDto> {
    const cached = this.sessions.get(sessionId);
    if (cached) return cached;

    const response = await this.getUploadSession(jobId, signal);
    if (response.session.sessionId !== sessionId) {
      throw new MigrationTransportError(
        'migration_invalid_state',
        409,
        'The upload session does not match this transfer.',
      );
    }
    return response.session;
  }

  /**
   * Activation-specific error mapping: the two 409 admission conflicts carry
   * the fresh destination revision, status and counts, which the generic
   * parser would discard.
   */
  private toActivationError(error: unknown): MigrationTransportError {
    if (error instanceof HttpErrorResponse) {
      const body = readActivationErrorBody(error.error);
      if (
        body &&
        (body.error === 'migration_replacement_confirmation_required' ||
          body.error === 'migration_destination_conflict')
      ) {
        return new MigrationActivationConflictError(
          body.error,
          error.status,
          body.message,
          {
            destinationRevision: body.destinationRevision,
            destinationStatus: body.destinationStatus,
            existingCounts: body.existingCounts,
          },
          { retryAfterMs: parseRetryAfterMs(error.headers?.get('Retry-After')), cause: error },
        );
      }
    }
    return this.toTransportError(error);
  }

  private toTransportError(error: unknown): MigrationTransportError {
    if (error instanceof MigrationTransportError) return error;

    if (!(error instanceof HttpErrorResponse)) {
      return new MigrationTransportError(
        'unexpected_error',
        0,
        error instanceof Error ? error.message : 'The migration request failed.',
        { cause: error },
      );
    }

    const retryAfterMs = parseRetryAfterMs(error.headers?.get('Retry-After'));

    if (error.status === 0) {
      return new MigrationTransportError('network_error', 0, 'The network request failed.', {
        retryAfterMs,
        cause: error,
      });
    }

    const body = readErrorBody(error.error);
    if (body) {
      return new MigrationTransportError(
        SERVER_CODES.has(body.error) ? (body.error as MigrationErrorCode) : 'unexpected_error',
        error.status,
        body.message,
        { retryAfterMs, cause: error },
      );
    }

    // A bare framework 404 on a migration route means this server does not
    // expose the migration API at all (older/mismatched host). Say so instead
    // of a generic transfer failure.
    if (error.status === 404) {
      return new MigrationTransportError(
        'migration_not_supported',
        404,
        'This Nostos host does not support library migration.',
        { retryAfterMs, cause: error },
      );
    }

    // Empty/non-JSON bodies (HTML proxy errors, auth middleware pages) still
    // become a typed, status-preserving failure instead of an unhandled
    // rejection.
    return new MigrationTransportError(
      'unexpected_error',
      error.status,
      'The migration request failed.',
      { retryAfterMs, cause: error },
    );
  }
}

function isChunkValid(session: MigrationSessionStatusDto, request: BrowserMigrationChunk): boolean {
  if (
    !Number.isInteger(request.index) ||
    request.index < 0 ||
    request.index >= session.totalChunks
  ) {
    return false;
  }
  const expectedOffset = request.index * session.chunkSize;
  const expectedLength = chunkLength(request.index, session.totalBytes, session.chunkSize);
  return (
    request.offsetBytes === expectedOffset &&
    request.lengthBytes === expectedLength &&
    request.blob.size === expectedLength &&
    SHA256_PATTERN.test(request.sha256)
  );
}

/** Receipt ranges are inclusive chunk indexes and must fit the session contract. */
function areRangesValid(
  ranges: readonly { startIndex: number; endIndex: number }[],
  totalChunks: number,
): boolean {
  if (!Number.isInteger(totalChunks) || totalChunks < 0) return false;
  return ranges.every(
    (range) =>
      Number.isInteger(range.startIndex) &&
      Number.isInteger(range.endIndex) &&
      range.startIndex >= 0 &&
      range.startIndex <= range.endIndex &&
      range.endIndex < totalChunks,
  );
}

function readErrorBody(value: unknown): TransportErrorBody | null {
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

interface ActivationErrorBody extends TransportErrorBody {
  destinationRevision: string | null;
  destinationStatus: MigrationDestinationStatus | null;
  existingCounts: MigrationExistingCountsDto | null;
}

/** Conflict body parser that keeps the fresh destination facts. */
function readActivationErrorBody(value: unknown): ActivationErrorBody | null {
  const base = readErrorBody(value);
  if (!base) return null;
  const record = value as Record<string, unknown>;

  const destinationRevision =
    typeof record['destinationRevision'] === 'string' ? record['destinationRevision'] : null;
  const destinationStatus =
    record['destinationStatus'] === 'Empty' || record['destinationStatus'] === 'Populated'
      ? record['destinationStatus']
      : null;
  const existingCounts = readExistingCounts(record['existingCounts']);

  return { ...base, destinationRevision, destinationStatus, existingCounts };
}

function readExistingCounts(value: unknown): MigrationExistingCountsDto | null {
  if (typeof value !== 'object' || value === null) return null;
  const record = value as Record<string, unknown>;
  if (typeof record['totalRows'] !== 'number') return null;
  return value as MigrationExistingCountsDto;
}

/** `Retry-After` is either delay-seconds (the middleware's `5`) or an HTTP date. */
function parseRetryAfterMs(value: string | null): number | undefined {
  if (!value) return undefined;
  const seconds = Number(value);
  if (Number.isFinite(seconds) && seconds >= 0) return Math.round(seconds * 1000);

  const date = Date.parse(value);
  if (Number.isNaN(date)) return undefined;
  return Math.max(0, date - Date.now());
}

function abortedError(): MigrationTransportError {
  return new MigrationTransportError('request_aborted', 0, 'The request was aborted.');
}
