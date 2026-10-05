/**
 * Direct part-upload mode for the SelfHosted HTTP transport (#680 slice B9;
 * plan §42, §78).
 *
 * The transport keeps the whole existing control plane (`preflight`, jobs,
 * sessions, completion, export download) and only changes how a chunk reaches
 * storage:
 *
 * ```text
 * default off (current SelfHosted behaviour, byte-identical):
 *   PUT /api/portability/migration/jobs/{id}/upload-session/chunks/{index}
 *             application server receives the bytes
 *
 * direct mode (host advertises `supportsDirectPartUpload`):
 *   POST .../parts/{index}/ticket    -> { url, method, requiredHeaders, expiresAtUtc }
 *   PUT  <ticket.url>                -> raw Blob slice, ticket headers only
 *   POST .../parts/{index}/complete  -> { etag, sha256, byteLength }
 * ```
 *
 * The direct PUT goes through `DirectUploadClient` (a bare XMLHttpRequest),
 * never Angular HttpClient, so no application interceptor can attach an
 * authorization header or cookie to the storage target. The chunk upload
 * engine keeps owning retry/backoff/abort/progress and the integrity hash is
 * reported unchanged: the ticket request and the completion report carry the
 * per-chunk SHA-256.
 *
 * Expired tickets are refreshed and retried inside `uploadChunk`; every other
 * transient storage failure is surfaced as a retryable `MigrationTransportError`
 * so the engine's existing backoff applies.
 */

import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { InjectionToken } from '@angular/core';
import { Observable, Subscription } from 'rxjs';

import {
  DIRECT_UPLOAD_COMPLETION_HEADER,
  MigrationPartCompletionRequestDto,
  MigrationPartTicketDto,
  MigrationPartTicketRequestDto,
  isPartTicketExpired,
} from '../models/direct-part-upload.dtos';
import {
  BrowserMigrationChunk,
  MigrationChunkUploadResultDto,
  MigrationCreateJobRequestDto,
  MigrationErrorCode,
  MigrationJobStatusResponseDto,
  MigrationSessionStatusDto,
  MigrationSessionRequestDto,
  MigrationUploadSessionResponseDto,
  SERVER_MIGRATION_ERROR_CODES,
  chunkLength,
} from '../models/migration-http.dtos';
import {
  DirectUploadAbortedError,
  DirectUploadClient,
  DirectUploadClientRequest,
  DirectUploadClientResponse,
  DirectUploadNetworkError,
  XhrDirectUploadClient,
  responseHeader,
} from './direct-upload-client';
import {
  HttpLibraryTransferTransport,
  MIGRATION_BASE_PATH,
} from './http-library-transfer-transport';
import { MigrationTransportError } from './migration-transport-error';

/**
 * Maximum ticket requests per part (the first plus refreshes). Bounds both an
 * expiry-mediated loop and a server that keeps answering with unusable
 * tickets.
 */
export const MAX_DIRECT_UPLOAD_TICKET_REQUESTS = 3;

const SHA256_PATTERN = /^[0-9a-f]{64}$/i;
const SERVER_CODES: ReadonlySet<string> = new Set(SERVER_MIGRATION_ERROR_CODES);

interface TicketErrorBody {
  error: string;
  message: string;
}

/** The upload target refused the part even after a fresh ticket. */
export class DirectUploadRejectedError extends MigrationTransportError {
  constructor(status: number, partIndex: number) {
    super(
      'unexpected_error',
      status,
      `The upload target refused part ${partIndex} of the archive (HTTP ${status}).`,
    );
    this.name = 'DirectUploadRejectedError';
  }
}

/**
 * The target accepted the bytes but did not expose the completion token
 * header, so the application server cannot record the part. Retrying cannot
 * fix a target that does not expose it (usually CORS `Access-Control-Expose-Headers`).
 */
export class DirectUploadTokenMissingError extends MigrationTransportError {
  constructor(partIndex: number, headerName: string) {
    super(
      'unexpected_error',
      0,
      `The upload target accepted part ${partIndex} of the archive but the ` +
        `"${headerName}" response header was not available.`,
    );
    this.name = 'DirectUploadTokenMissingError';
  }

  override get retryable(): boolean {
    return false;
  }
}

/** Tickets kept arriving expired; the host cannot authorize this part. */
export class DirectUploadTicketExpiredError extends MigrationTransportError {
  constructor(partIndex: number) {
    super(
      'migration_session_expired',
      410,
      `The upload ticket for part ${partIndex} of the archive expired before it could be sent.`,
    );
    this.name = 'DirectUploadTicketExpiredError';
  }
}

export interface DirectUploadTransportOptions {
  /**
   * Lazy capability check. Defaults to off, which delegates every chunk to the
   * application server exactly as the plain SelfHosted transport does. A
   * failed check also falls back to the application-server path.
   */
  directPartUpload?: () => boolean | Promise<boolean>;
  /** Storage primitive; production uses the interceptor-free XMLHttpRequest one. */
  uploadClient?: DirectUploadClient;
  /** Injectable clock for ticket-expiry tests. */
  now?: () => number;
  /** Injectable completion-header name for tests; defaults to `ETag`. */
  completionHeaderName?: string;
}

/**
 * DI seam for the bare storage-target primitive, so a hosted build or a test
 * can replace the XMLHttpRequest implementation without changing the
 * transport factory.
 */
export const DIRECT_UPLOAD_CLIENT = new InjectionToken<DirectUploadClient>(
  'NOSTOS_DIRECT_UPLOAD_CLIENT',
  { providedIn: 'root', factory: () => new XhrDirectUploadClient() },
);

export class DirectUploadLibraryTransferTransport extends HttpLibraryTransferTransport {
  private readonly directHttp: HttpClient;
  private readonly directBasePath: string;
  private readonly directPartUpload: () => boolean | Promise<boolean>;
  private readonly uploadClient: DirectUploadClient;
  private readonly now: () => number;
  private readonly completionHeaderName: string;

  /**
   * Session contracts this transport has observed, so a direct part upload can
   * resolve the chunk size without an extra GET per chunk. The settled session
   * still comes from the base transport's authoritative normalization.
   */
  private readonly directSessions = new Map<string, MigrationSessionStatusDto>();

  constructor(
    http: HttpClient,
    basePath: string = MIGRATION_BASE_PATH,
    options: DirectUploadTransportOptions = {},
  ) {
    super(http, basePath);
    this.directHttp = http;
    this.directBasePath = basePath;
    this.directPartUpload = options.directPartUpload ?? (() => false);
    this.uploadClient = options.uploadClient ?? new XhrDirectUploadClient();
    this.now = options.now ?? (() => Date.now());
    this.completionHeaderName = options.completionHeaderName ?? DIRECT_UPLOAD_COMPLETION_HEADER;
  }

  override createJob(
    request: MigrationCreateJobRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return super.createJob(request, signal).then((status) => {
      if (status.session) this.directSessions.set(status.session.sessionId, status.session);
      return status;
    });
  }

  override getJob(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return super.getJob(jobId, signal).then((status) => {
      if (status.session) this.directSessions.set(status.session.sessionId, status.session);
      return status;
    });
  }

  override createUploadSession(
    jobId: string,
    request: MigrationSessionRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    return super.createUploadSession(jobId, request, signal).then((response) => {
      this.directSessions.set(response.session.sessionId, response.session);
      return response;
    });
  }

  override getUploadSession(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    return super.getUploadSession(jobId, signal).then((response) => {
      this.directSessions.set(response.session.sessionId, response.session);
      return response;
    });
  }

  override async uploadChunk(
    jobId: string,
    sessionId: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    if (signal.aborted) throw abortedDirectError();

    if (!(await this.directPartUploadEnabled())) {
      // Capability absent (or unreadable): exactly the SelfHosted behaviour.
      return super.uploadChunk(jobId, sessionId, request, onProgress, signal);
    }

    const session = await this.directSessionFor(jobId, sessionId, signal);
    if (!isDirectChunkValid(session, request)) {
      throw new MigrationTransportError(
        'migration_chunk_range_invalid',
        416,
        'The chunk range does not match the session contract.',
      );
    }

    return this.uploadPartDirect(
      `${this.directJobUrl(jobId)}/upload-session/parts/${request.index}`,
      request,
      onProgress,
      signal,
    );
  }

  // ----------------------------------------------------------- direct flow --

  private async uploadPartDirect(
    partUrl: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    let ticket = await this.requestPartTicket(partUrl, request, signal);
    let ticketRequests = 1;

    for (;;) {
      if (isPartTicketExpired(ticket, this.now())) {
        if (ticketRequests >= MAX_DIRECT_UPLOAD_TICKET_REQUESTS) {
          throw new DirectUploadTicketExpiredError(request.index);
        }
        ticket = await this.requestPartTicket(partUrl, request, signal);
        ticketRequests += 1;
        continue;
      }

      const response = await this.sendToTarget(ticket, request, onProgress, signal);

      if (response.status >= 200 && response.status < 300) {
        const token = responseHeader(response.headers, this.completionHeaderName);
        if (!token) {
          throw new DirectUploadTokenMissingError(request.index, this.completionHeaderName);
        }
        return this.completePart(partUrl, request, token, signal);
      }

      // A presigned target answers 401/403 for an expired ticket. Refresh and
      // retry once per remaining ticket budget; a second refusal is a real
      // authorization failure.
      if (isTargetRefusal(response.status)) {
        if (ticketRequests >= MAX_DIRECT_UPLOAD_TICKET_REQUESTS) {
          throw new DirectUploadRejectedError(response.status, request.index);
        }
        ticket = await this.requestPartTicket(partUrl, request, signal);
        ticketRequests += 1;
        continue;
      }

      throw storageFailure(response, request.index);
    }
  }

  private async requestPartTicket(
    partUrl: string,
    request: BrowserMigrationChunk,
    signal: AbortSignal,
  ): Promise<MigrationPartTicketDto> {
    const body: MigrationPartTicketRequestDto = {
      lengthBytes: request.lengthBytes,
      sha256: request.sha256,
    };
    const ticket = await this.directJson<MigrationPartTicketDto>(
      'POST',
      `${partUrl}/ticket`,
      body,
      signal,
    );
    if (!isTicketShaped(ticket)) {
      throw new MigrationTransportError(
        'unexpected_error',
        0,
        'The host returned an incomplete upload ticket.',
      );
    }
    return ticket;
  }

  private completePart(
    partUrl: string,
    request: BrowserMigrationChunk,
    token: string,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    const body: MigrationPartCompletionRequestDto = {
      etag: token,
      sha256: request.sha256,
      byteLength: request.lengthBytes,
    };
    return this.directJson<MigrationChunkUploadResultDto>(
      'POST',
      `${partUrl}/complete`,
      body,
      signal,
    );
  }

  private sendToTarget(
    ticket: MigrationPartTicketDto,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<DirectUploadClientResponse> {
    const upload: DirectUploadClientRequest = {
      url: ticket.url,
      method: ticket.method,
      headers: ticket.requiredHeaders,
      body: request.blob,
      signal,
      onProgress,
    };
    return this.uploadClient.upload(upload).catch((error: unknown) => {
      if (error instanceof DirectUploadAbortedError) throw abortedDirectError();
      if (error instanceof DirectUploadNetworkError) {
        throw new MigrationTransportError(
          'network_error',
          0,
          'The connection to the upload target failed.',
          { cause: error },
        );
      }
      throw error;
    });
  }

  private async directPartUploadEnabled(): Promise<boolean> {
    try {
      return (await this.directPartUpload()) === true;
    } catch {
      return false;
    }
  }

  /**
   * Resolves the session contract for a direct chunk. Sessions observed on
   * job/session responses are cached; an unknown session falls back to the
   * authoritative GET, exactly as the base transport does for its chunk PUT.
   */
  private async directSessionFor(
    jobId: string,
    sessionId: string,
    signal: AbortSignal,
  ): Promise<MigrationSessionStatusDto> {
    const cached = this.directSessions.get(sessionId);
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

  private directJobUrl(jobId: string): string {
    return `${this.directBasePath}/jobs/${encodeURIComponent(jobId)}`;
  }

  /** Control-plane JSON request with cancellation and typed error mapping. */
  private directJson<T>(
    method: 'GET' | 'POST',
    url: string,
    body: unknown,
    signal?: AbortSignal,
  ): Promise<T> {
    const observable: Observable<T> = this.directHttp.request<T>(method, url, {
      body: body ?? null,
    });
    return new Promise<T>((resolve, reject) => {
      if (signal?.aborted) {
        reject(abortedDirectError());
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
        reject(abortedDirectError());
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
          reject(this.directToTransportError(error));
        },
        complete: () => {
          /* `next` already resolved. */
        },
      });
      signal?.addEventListener('abort', onAbort, { once: true });
      if (signal?.aborted) onAbort();
    });
  }

  private directToTransportError(error: unknown): MigrationTransportError {
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

    if (error.status === 404) {
      return new MigrationTransportError(
        'migration_not_supported',
        404,
        'This Nostos host does not support direct library uploads.',
        { retryAfterMs, cause: error },
      );
    }

    return new MigrationTransportError(
      'unexpected_error',
      error.status,
      'The migration request failed.',
      { retryAfterMs, cause: error },
    );
  }
}

function isTargetRefusal(status: number): boolean {
  return status === 401 || status === 403;
}

function storageFailure(
  response: DirectUploadClientResponse,
  partIndex: number,
): MigrationTransportError {
  const statusText = response.statusText ? ` ${response.statusText}` : '';
  return new MigrationTransportError(
    'unexpected_error',
    response.status,
    `The upload target failed to accept part ${partIndex} of the archive ` +
      `(HTTP ${response.status}${statusText}).`,
  );
}

function isTicketShaped(value: unknown): value is MigrationPartTicketDto {
  if (typeof value !== 'object' || value === null) return false;
  const ticket = value as Partial<MigrationPartTicketDto>;
  return (
    typeof ticket.url === 'string' &&
    ticket.url.length > 0 &&
    typeof ticket.method === 'string' &&
    ticket.method.length > 0 &&
    typeof ticket.expiresAtUtc === 'string' &&
    Number.isFinite(Date.parse(ticket.expiresAtUtc)) &&
    typeof ticket.requiredHeaders === 'object' &&
    ticket.requiredHeaders !== null
  );
}

function isDirectChunkValid(
  session: MigrationSessionStatusDto,
  request: BrowserMigrationChunk,
): boolean {
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

function readErrorBody(value: unknown): TicketErrorBody | null {
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

function parseRetryAfterMs(value: string | null): number | undefined {
  if (!value) return undefined;
  const seconds = Number(value);
  if (Number.isFinite(seconds) && seconds >= 0) return Math.round(seconds * 1000);

  const date = Date.parse(value);
  if (Number.isNaN(date)) return undefined;
  return Math.max(0, date - Date.now());
}

function abortedDirectError(): MigrationTransportError {
  return new MigrationTransportError('request_aborted', 0, 'The request was aborted.');
}
