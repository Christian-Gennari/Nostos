/**
 * Direct part-upload mode for the SelfHosted HTTP transport (#680 slice B9;
 * plan §42, §78).
 *
 * The transport keeps the whole existing control plane (`preflight`, jobs,
 * sessions, completion, export download) and only changes how a chunk reaches
 * storage:
 *
 * ```text
 * default legacy mode (current SelfHosted behaviour, byte-identical):
 *   PUT /api/portability/migration/jobs/{id}/upload-session/chunks/{index}
 *
 * direct mode (host advertises `supportsDirectPartUpload`):
 *   POST .../upload-session/part-tickets  -> bounded window of targets
 *   PUT  <ticket.uploadUrl>               -> raw File.slice(), no headers
 *   POST .../upload-session/reconcile     -> server verifies and records parts
 * ```
 *
 * Tickets are requested in bounded windows of 1-based part numbers and cached
 * per session until their expiry (with a safety margin). The PUT goes through
 * `DirectUploadClient` (a bare fetch, never Angular HttpClient), so no
 * application interceptor, cookie or header can reach the target, and a
 * redirect is refused instead of replayed. The server's reconcile response is
 * the receipt authority; the browser never reports a provider token.
 *
 * Transient storage failures (network, 408, 429, 500, 502, 503, 504) stay
 * retryable `MigrationTransportError`s, so the chunk upload engine's existing
 * backoff/abort/progress semantics are unchanged. A 403 means the target URL
 * expired: the transport asks for a fresh ticket once, and a second 403 is a
 * typed terminal error.
 */

import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { InjectionToken } from '@angular/core';
import { Observable, Subscription } from 'rxjs';

import {
  MigrationPartClaimDto,
  MigrationPartReconcileRequestDto,
  MigrationPartTicketBatchDto,
  MigrationPartTicketDto,
  MigrationPartTicketRequestDto,
} from '../models/direct-part-upload.dtos';
import {
  BrowserMigrationChunk,
  MigrationChunkUploadResultDto,
  MigrationErrorCode,
  MigrationSessionStatusDto,
  SERVER_MIGRATION_ERROR_CODES,
  chunkLength,
} from '../models/migration-http.dtos';
import type { LibraryTransferMode } from '../models/library-transfer.models';
import {
  DirectUploadAbortedError,
  DirectUploadClient,
  DirectUploadClientRequest,
  DirectUploadClientResponse,
  DirectUploadNetworkError,
  FetchDirectUploadClient,
  UnsafeUploadTargetError,
  validateDirectUploadTarget,
} from './direct-upload-client';
import {
  HttpLibraryTransferTransport,
  MIGRATION_BASE_PATH,
} from './http-library-transfer-transport';
import { MigrationTransportError } from './migration-transport-error';

/** How many upcoming parts one ticket window asks for (the server caps at 16). */
export const DEFAULT_TICKET_WINDOW_PARTS = 8;
const MAX_TICKET_WINDOW_PARTS = 16;

/**
 * A ticket window must not be started with less than this much lifetime left;
 * avoids predictable failed PUTs on slow links.
 */
export const TICKET_EXPIRY_SAFETY_MS = 30_000;

/** How many times a part will ask for a fresh window before giving up. */
const MAX_TICKET_WINDOW_FETCHES = 2;

/** Default attempts for resolving the upload mode before a transfer starts. */
export const DEFAULT_MODE_RESOLUTION_ATTEMPTS = 4;

const SHA256_PATTERN = /^[0-9a-f]{64}$/i;
const SERVER_CODES: ReadonlySet<string> = new Set(SERVER_MIGRATION_ERROR_CODES);

interface TicketErrorBody {
  error: string;
  message: string;
}

/** The host returned an upload target the security policy forbids. */
export class DirectUploadTargetError extends MigrationTransportError {
  constructor(message: string) {
    super('direct_upload_target_invalid', 0, message);
    this.name = 'DirectUploadTargetError';
  }

  override get retryable(): boolean {
    return false;
  }
}

/** A ticket did not describe the exact part/range the session contract requires. */
export class DirectUploadTicketInvalidError extends MigrationTransportError {
  constructor(message: string) {
    super('direct_upload_ticket_invalid', 0, message);
    this.name = 'DirectUploadTicketInvalidError';
  }

  override get retryable(): boolean {
    return false;
  }
}

/** The target refused the part even after a fresh ticket. */
export class DirectUploadRejectedError extends MigrationTransportError {
  constructor(partIndex: number) {
    super(
      'direct_upload_rejected',
      403,
      `The upload target refused part ${partIndex + 1} of the archive (HTTP 403).`,
    );
    this.name = 'DirectUploadRejectedError';
  }
}

/**
 * The PUT reached the target but the server did not record the part. Never
 * treated as success: the engine retries the part.
 */
export class DirectUploadReceiptPendingError extends MigrationTransportError {
  constructor(partIndex: number) {
    super(
      'direct_upload_receipt_pending',
      0,
      `The host did not confirm part ${partIndex + 1} of the archive. Nostos will retry it.`,
    );
    this.name = 'DirectUploadReceiptPendingError';
  }

  override get retryable(): boolean {
    return true;
  }
}

/** Tickets kept arriving unusable; the host cannot authorize this part. */
export class DirectUploadTicketExpiredError extends MigrationTransportError {
  constructor(partIndex: number) {
    super(
      'migration_session_expired',
      410,
      `The upload ticket for part ${partIndex + 1} of the archive expired before it could be sent.`,
    );
    this.name = 'DirectUploadTicketExpiredError';
  }
}

/**
 * A storage response the engine may retry: network failures, 408, 429 and the
 * 5xx statuses that indicate a transient target failure. Deliberately separate
 * from the application-server classification (`isTransientStatus`), which must
 * not change for control-plane routes.
 */
export class DirectUploadStorageError extends MigrationTransportError {
  constructor(status: number, statusText: string, partIndex: number) {
    const suffix = statusText ? ` ${statusText}` : '';
    super(
      'unexpected_error',
      status,
      `The upload target failed to accept part ${partIndex + 1} of the archive ` +
        `(HTTP ${status}${suffix}).`,
    );
    this.name = 'DirectUploadStorageError';
  }

  override get retryable(): boolean {
    return isRetryableStorageStatus(this.status);
  }
}

export function isRetryableStorageStatus(status: number): boolean {
  return (
    status === 0 ||
    status === 408 ||
    status === 429 ||
    status === 500 ||
    status === 502 ||
    status === 503 ||
    status === 504
  );
}

export interface DirectUploadTransportOptions {
  /**
   * Loads the host's direct-upload capability. A rejection is a transient
   * failure and is retried; it is never interpreted as "legacy". Defaults to
   * "not supported" when no loader is supplied.
   */
  loadDirectUploadCapability?: () => Promise<boolean>;
  /** Storage primitive; production uses the credential-free fetch one. */
  uploadClient?: DirectUploadClient;
  /** This application's origin; a target on it is refused. */
  applicationOrigin?: string;
  /** Injectable clock for ticket-expiry tests. */
  now?: () => number;
  /** Ticket window size; bounded by the server contract. */
  ticketWindowParts?: number;
  /** Attempts for the capability lookup before it surfaces a retryable error. */
  maxModeResolutionAttempts?: number;
  /** Delay between capability attempts; injectable for tests. */
  modeRetryDelayMs?: (attempt: number) => number;
  random?: () => number;
}

/**
 * DI seam for the bare storage-target primitive, so a hosted build or a test
 * can replace the fetch implementation without changing the transport factory.
 */
export const DIRECT_UPLOAD_CLIENT = new InjectionToken<DirectUploadClient>(
  'NOSTOS_DIRECT_UPLOAD_CLIENT',
  { providedIn: 'root', factory: () => new FetchDirectUploadClient() },
);

interface CachedTicketBatch {
  expiresAtMs: number;
  /** chunkIndex -> ticket, for the window the host most recently signed. */
  tickets: Map<number, MigrationPartTicketDto>;
}

export class DirectUploadLibraryTransferTransport extends HttpLibraryTransferTransport {
  private readonly directHttp: HttpClient;
  private readonly directBasePath: string;
  private readonly loadDirectUploadCapability: () => Promise<boolean>;
  private readonly uploadClient: DirectUploadClient;
  private readonly applicationOrigin: string;
  private readonly now: () => number;
  private readonly ticketWindowParts: number;
  private readonly maxModeResolutionAttempts: number;
  private readonly modeRetryDelay: (attempt: number) => number;
  private readonly random: () => number;

  /**
   * Pinned upload mode per session; a resolved session never switches paths.
   */
  private readonly sessionModes = new Map<string, LibraryTransferMode>();
  private readonly modeResolutions = new Map<string, Promise<LibraryTransferMode>>();

  /** The most recent signed ticket window per session. */
  private readonly ticketBatches = new Map<string, CachedTicketBatch>();

  constructor(
    http: HttpClient,
    basePath: string = MIGRATION_BASE_PATH,
    options: DirectUploadTransportOptions = {},
  ) {
    super(http, basePath);
    this.directHttp = http;
    this.directBasePath = basePath;
    this.loadDirectUploadCapability =
      options.loadDirectUploadCapability ?? (() => Promise.resolve(false));
    this.uploadClient = options.uploadClient ?? new FetchDirectUploadClient();
    this.applicationOrigin =
      options.applicationOrigin ??
      (typeof location === 'undefined' ? '' : location.origin);
    this.now = options.now ?? (() => Date.now());
    this.ticketWindowParts = clampWindowParts(
      options.ticketWindowParts ?? DEFAULT_TICKET_WINDOW_PARTS,
    );
    this.maxModeResolutionAttempts = Math.max(
      1,
      options.maxModeResolutionAttempts ?? DEFAULT_MODE_RESOLUTION_ATTEMPTS,
    );
    this.random = options.random ?? (() => Math.random());
    this.modeRetryDelay =
      options.modeRetryDelayMs ??
      ((attempt) => this.modeBackoffMs(attempt));
  }

  // ------------------------------------------------------------ mode pinning --

  /**
   * Resolves and pins the part-upload mode for a session. A failed capability
   * lookup is retried with the engine's backoff shape and, if it keeps
   * failing, surfaces a retryable error; it is never treated as "legacy".
   */
  async resolveUploadMode(
    sessionId: string,
    signal?: AbortSignal,
  ): Promise<LibraryTransferMode> {
    const pinned = this.sessionModes.get(sessionId);
    if (pinned) return pinned;

    let pending = this.modeResolutions.get(sessionId);
    if (!pending) {
      pending = this.loadModeWithRetry(signal).then(
        (mode) => {
          this.sessionModes.set(sessionId, mode);
          this.modeResolutions.delete(sessionId);
          return mode;
        },
        (error: unknown) => {
          this.modeResolutions.delete(sessionId);
          throw error;
        },
      );
      this.modeResolutions.set(sessionId, pending);
    }
    return pending;
  }

  /** Pins a mode persisted by an earlier session so a reload cannot switch. */
  pinUploadMode(sessionId: string, mode: LibraryTransferMode): void {
    this.modeResolutions.delete(sessionId);
    this.sessionModes.set(sessionId, mode);
  }

  private async loadModeWithRetry(signal?: AbortSignal): Promise<LibraryTransferMode> {
    let lastError: unknown;
    for (let attempt = 1; attempt <= this.maxModeResolutionAttempts; attempt += 1) {
      if (signal?.aborted) throw abortedDirectError();
      try {
        const direct = await this.loadDirectUploadCapability();
        return direct ? 'direct' : 'application-server';
      } catch (error) {
        lastError = error;
        if (attempt < this.maxModeResolutionAttempts) {
          await this.sleep(this.modeRetryDelay(attempt), signal);
        }
      }
    }

    throw new MigrationTransportError(
      'migration_transport_mode_unavailable',
      0,
      'This host did not answer how library parts should be uploaded. Retry in a moment.',
      { cause: lastError },
    );
  }

  private modeBackoffMs(attempt: number): number {
    const base = Math.min(500 * 2 ** (attempt - 1), 8_000);
    const jitter = 0.8 + 0.4 * this.random();
    return Math.round(base * jitter);
  }

  // ------------------------------------------------------------ control plane --

  override async uploadChunk(
    jobId: string,
    sessionId: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    if (signal.aborted) throw abortedDirectError();

    const mode =
      this.sessionModes.get(sessionId) ??
      (await this.resolveUploadMode(sessionId, signal));

    if (mode === 'application-server') {
      // Exactly the SelfHosted behaviour: the app server receives the bytes.
      return super.uploadChunk(jobId, sessionId, request, onProgress, signal);
    }

    const session = await this.sessionFor(jobId, sessionId, signal);
    if (!isDirectChunkValid(session, request)) {
      throw new MigrationTransportError(
        'migration_chunk_range_invalid',
        416,
        'The chunk range does not match the session contract.',
      );
    }

    return this.uploadPartDirect(jobId, session, request, onProgress, signal);
  }

  // ----------------------------------------------------------- direct flow --

  private async uploadPartDirect(
    jobId: string,
    session: MigrationSessionStatusDto,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    let refreshedAfter403 = false;

    for (;;) {
      const ticket = await this.ticketFor(jobId, session, request.index, signal);
      const response = await this.sendToTarget(ticket.uploadUrl, request, signal);

      if (response.status >= 200 && response.status < 300) {
        // Progress is reported per completed part; byte-level progress is not
        // available on the credential-free fetch path, and part completion is
        // the only durable milestone the engine consumes.
        onProgress(request.lengthBytes, request.lengthBytes);
        return this.reconcilePart(jobId, session, request, signal);
      }

      if (response.status === 403 && !refreshedAfter403) {
        // A presigned target answers 403 when its URL expired. Ask for a fresh
        // window once; a second refusal is terminal.
        refreshedAfter403 = true;
        this.ticketBatches.delete(session.sessionId);
        continue;
      }

      if (response.status === 403) throw new DirectUploadRejectedError(request.index);

      throw new DirectUploadStorageError(response.status, response.statusText, request.index);
    }
  }

  /**
   * Returns the cached ticket for a part, fetching a fresh bounded window when
   * the cached one is missing, expired (with safety margin) or already
   * unusable. A window that arrives already inside the margin is retried once.
   */
  private async ticketFor(
    jobId: string,
    session: MigrationSessionStatusDto,
    chunkIndex: number,
    signal: AbortSignal,
  ): Promise<MigrationPartTicketDto> {
    for (let fetchAttempt = 0; fetchAttempt < MAX_TICKET_WINDOW_FETCHES; fetchAttempt += 1) {
      const cached = this.ticketBatches.get(session.sessionId);
      if (cached && !this.batchExpired(cached)) {
        const ticket = cached.tickets.get(chunkIndex);
        if (ticket) return ticket;
      }

      await this.fetchTicketWindow(jobId, session, chunkIndex, signal);

      const batch = this.ticketBatches.get(session.sessionId);
      if (batch && !this.batchExpired(batch)) {
        const ticket = batch.tickets.get(chunkIndex);
        if (!ticket) {
          throw new DirectUploadTicketInvalidError(
            `The host did not return a ticket for part ${chunkIndex + 1} of the archive.`,
          );
        }
        return ticket;
      }
    }

    throw new DirectUploadTicketExpiredError(chunkIndex);
  }

  private async fetchTicketWindow(
    jobId: string,
    session: MigrationSessionStatusDto,
    startChunkIndex: number,
    signal: AbortSignal,
  ): Promise<void> {
    const end = Math.min(startChunkIndex + this.ticketWindowParts, session.totalChunks);
    const partNumbers: number[] = [];
    for (let index = startChunkIndex; index < end; index += 1) partNumbers.push(index + 1);

    const request: MigrationPartTicketRequestDto = {
      sessionId: session.sessionId,
      partNumbers,
    };
    const batch = await this.directJson<MigrationPartTicketBatchDto>(
      'POST',
      `${this.directJobUrl(jobId)}/upload-session/part-tickets`,
      request,
      signal,
    );

    this.validateTicketBatch(session, startChunkIndex, batch);

    const tickets = new Map<number, MigrationPartTicketDto>();
    for (const ticket of batch.tickets) tickets.set(ticket.chunkIndex, ticket);
    this.ticketBatches.set(session.sessionId, {
      expiresAtMs: Date.parse(batch.expiresAtUtc),
      tickets,
    });
  }

  private validateTicketBatch(
    session: MigrationSessionStatusDto,
    startChunkIndex: number,
    batch: MigrationPartTicketBatchDto,
  ): void {
    if (
      typeof batch !== 'object' ||
      batch === null ||
      typeof batch.sessionId !== 'string' ||
      batch.sessionId.toLowerCase() !== session.sessionId.toLowerCase() ||
      !Array.isArray(batch.tickets) ||
      !Number.isFinite(Date.parse(batch.expiresAtUtc))
    ) {
      throw new DirectUploadTicketInvalidError(
        'The host returned a malformed upload ticket window.',
      );
    }

    const seen = new Set<number>();
    for (const ticket of batch.tickets) {
      if (!isTicketValid(session, ticket) || seen.has(ticket.chunkIndex)) {
        throw new DirectUploadTicketInvalidError(
          'The host returned an upload ticket that does not match the session contract.',
        );
      }
      seen.add(ticket.chunkIndex);

      try {
        validateDirectUploadTarget(ticket.uploadUrl, this.applicationOrigin);
      } catch (error) {
        if (error instanceof UnsafeUploadTargetError) {
          throw new DirectUploadTargetError(error.message);
        }
        throw error;
      }
    }

    if (!seen.has(startChunkIndex)) {
      throw new DirectUploadTicketInvalidError(
        `The host did not return a ticket for part ${startChunkIndex + 1} of the archive.`,
      );
    }
  }

  private batchExpired(batch: CachedTicketBatch): boolean {
    if (!Number.isFinite(batch.expiresAtMs)) return true;
    return batch.expiresAtMs - this.now() <= TICKET_EXPIRY_SAFETY_MS;
  }

  private sendToTarget(
    uploadUrl: string,
    request: BrowserMigrationChunk,
    signal: AbortSignal,
  ): Promise<DirectUploadClientResponse> {
    const upload: DirectUploadClientRequest = {
      url: uploadUrl,
      body: request.blob,
      signal,
    };
    return this.uploadClient.upload(upload).catch((error: unknown) => {
      if (error instanceof DirectUploadAbortedError) throw abortedDirectError();
      if (error instanceof DirectUploadNetworkError) {
        throw new MigrationTransportError('network_error', 0, error.message, { cause: error });
      }
      throw error;
    });
  }

  /**
   * Reports a finished part and takes the server's session response as the
   * receipt authority. The server verifies the bytes itself; a response that
   * does not list the part is never treated as success.
   */
  private async reconcilePart(
    jobId: string,
    session: MigrationSessionStatusDto,
    request: BrowserMigrationChunk,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    const claim: MigrationPartClaimDto = {
      partNumber: request.index + 1,
      sha256: request.sha256,
      lengthBytes: request.lengthBytes,
    };
    const body: MigrationPartReconcileRequestDto = {
      sessionId: session.sessionId,
      parts: [claim],
    };
    const status = await this.directJson<MigrationSessionStatusDto>(
      'POST',
      `${this.directJobUrl(jobId)}/upload-session/reconcile`,
      body,
      signal,
    );

    const received = Array.isArray(status?.receivedChunks) ? status.receivedChunks : [];
    if (
      typeof status?.sessionId !== 'string' ||
      status.sessionId.toLowerCase() !== session.sessionId.toLowerCase() ||
      !received.includes(request.index)
    ) {
      throw new DirectUploadReceiptPendingError(request.index);
    }

    return { sessionId: status.sessionId, chunkIndex: request.index, alreadyPresent: false };
  }

  private directJobUrl(jobId: string): string {
    return `${this.directBasePath}/jobs/${encodeURIComponent(jobId)}`;
  }

  // --------------------------------------------------------------- internals --

  private sleep(ms: number, signal?: AbortSignal): Promise<void> {
    if (ms <= 0) return Promise.resolve();
    return new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        signal?.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
      const onAbort = () => {
        clearTimeout(timer);
        reject(abortedDirectError());
      };
      signal?.addEventListener('abort', onAbort, { once: true });
      if (signal?.aborted) onAbort();
    });
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

function isTicketValid(
  session: MigrationSessionStatusDto,
  ticket: MigrationPartTicketDto,
): boolean {
  if (
    !Number.isInteger(ticket.partNumber) ||
    !Number.isInteger(ticket.chunkIndex) ||
    ticket.partNumber !== ticket.chunkIndex + 1 ||
    ticket.chunkIndex < 0 ||
    ticket.chunkIndex >= session.totalChunks
  ) {
    return false;
  }
  if (!Number.isInteger(ticket.lengthBytes) || typeof ticket.uploadUrl !== 'string') {
    return false;
  }
  const expectedOffset = ticket.chunkIndex * session.chunkSize;
  const expectedLength = chunkLength(ticket.chunkIndex, session.totalBytes, session.chunkSize);
  return ticket.offsetBytes === expectedOffset && ticket.lengthBytes === expectedLength;
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

function clampWindowParts(value: number): number {
  if (!Number.isFinite(value)) return DEFAULT_TICKET_WINDOW_PARTS;
  return Math.min(MAX_TICKET_WINDOW_PARTS, Math.max(1, Math.floor(value)));
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
