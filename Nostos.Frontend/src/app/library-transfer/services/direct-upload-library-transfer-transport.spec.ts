import { HttpClient, HttpRequest, provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  RequestMatch,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import {
  DirectUploadTransportOptions,
  DirectUploadLibraryTransferTransport,
  DirectUploadReceiptPendingError,
  DirectUploadRejectedError,
  DirectUploadStorageError,
  DirectUploadTargetError,
  DirectUploadTicketInvalidError,
} from './direct-upload-library-transfer-transport';
import { MIGRATION_BASE_PATH } from './http-library-transfer-transport';
import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
  MigrationTransportError,
} from './library-transfer-transport';
import { ChunkUploadEngine } from './chunk-upload-engine.service';
import { FileDigest } from '../models/library-transfer.models';
import {
  BrowserMigrationChunk,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
  toChunkRanges,
} from '../models/migration-http.dtos';
import { MockDirectUploadClient } from '../testing/mock-direct-upload-client';
import {
  FIXTURE_JOB_ID,
  FIXTURE_SESSION_ID,
  PART_TICKETS_SEGMENT,
  RECONCILE_SEGMENT,
  partTicketBatchFixture,
} from '../testing/direct-upload-contract.fixture';

const JOB_ID = FIXTURE_JOB_ID;
const SESSION_ID = FIXTURE_SESSION_ID;
const CHUNK = 4 * 1024 * 1024;
const NOW = Date.parse('2026-01-01T00:00:00.000Z');
const APP_ORIGIN = 'https://app.nostos.example';

const SESSION_URL = `${MIGRATION_BASE_PATH}/jobs/${JOB_ID}/upload-session`;
const TICKETS_URL = `${SESSION_URL}/${PART_TICKETS_SEGMENT}`;
const RECONCILE_URL = `${SESSION_URL}/${RECONCILE_SEGMENT}`;
const chunkUrl = (index: number): string => `${SESSION_URL}/chunks/${index}`;

const TICKET_MATCH = (request: HttpRequest<unknown>): boolean =>
  request.method === 'POST' && request.url === TICKETS_URL;
const RECONCILE_MATCH = (request: HttpRequest<unknown>): boolean =>
  request.method === 'POST' && request.url === RECONCILE_URL;

function sessionStatus(
  totalBytes: number,
  chunkSize = CHUNK,
  receivedChunks: number[] = [],
): MigrationSessionStatusDto {
  return {
    sessionId: SESSION_ID,
    purpose: 'Import',
    state: receivedChunks.length > 0 ? 'Receiving' : 'Created',
    totalBytes,
    chunkSize,
    totalChunks: Math.max(1, Math.ceil(totalBytes / chunkSize)),
    fileIdentity: { totalSizeBytes: totalBytes, sha256Checksum: 'a'.repeat(64) },
    receivedChunks,
    receivedChunkCount: receivedChunks.length,
    createdAtUtc: new Date(0).toISOString(),
    expiresAtUtc: new Date(60_000).toISOString(),
  };
}

function sessionResponse(session: MigrationSessionStatusDto): MigrationUploadSessionResponseDto {
  return { session, receivedRanges: toChunkRanges(session.receivedChunks) };
}

function fileOfSize(size: number): File {
  const bytes = new Uint8Array(size);
  for (let index = 0; index < size; index += 1) bytes[index] = (index * 31) % 256;
  return new File([bytes as unknown as BlobPart], 'library.nostos');
}

function chunkAt(
  file: Blob,
  session: MigrationSessionStatusDto,
  index: number,
): BrowserMigrationChunk {
  const offsetBytes = index * session.chunkSize;
  const lengthBytes = Math.min(session.chunkSize, session.totalBytes - offsetBytes);
  return {
    index,
    offsetBytes,
    lengthBytes,
    sha256: 'b'.repeat(64),
    blob: file.slice(offsetBytes, offsetBytes + lengthBytes),
  };
}

function stubDigest(): FileDigest {
  return {
    sha256: async () => 'a'.repeat(64),
    // The transport validates the chunk contract, so the stub must produce a
    // well-formed 64-hex digest; varying by size keeps slices distinguishable.
    sha256Chunk: async (blob) => blob.size.toString(16).padStart(64, '0'),
    fingerprint: async () => 'nostos-fp-v1:stub',
  };
}

async function waitForRequest(
  http: HttpTestingController,
  match: string | RequestMatch | ((request: HttpRequest<unknown>) => boolean),
): Promise<TestRequest> {
  for (let attempt = 0; attempt < 50; attempt += 1) {
    const found = http.match(match);
    if (found.length > 0) return found[0];
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  return http.expectOne(match);
}

async function rejectionOf(promise: Promise<unknown>): Promise<MigrationTransportError> {
  try {
    await promise;
  } catch (error) {
    expect(error).toBeInstanceOf(MigrationTransportError);
    return error as MigrationTransportError;
  }
  throw new Error('Expected the transport to reject.');
}

interface ServeLog {
  ticketWindows: number[][];
  ticketLengths: number[];
  reconciledParts: number[];
}

/**
 * Plays the application server while an engine run is in flight: every ticket
 * request gets a fresh window, every reconcile claim is verified and added to
 * the authoritative receipt set. Polls briefly so retry backoff can elapse.
 */
async function serveEngine(
  http: HttpTestingController,
  session: MigrationSessionStatusDto,
  done: Promise<unknown>,
): Promise<ServeLog> {
  const log: ServeLog = { ticketWindows: [], ticketLengths: [], reconciledParts: [] };
  const received = new Set<number>(session.receivedChunks);
  let settled = false;
  let failure: unknown;
  void done.then(
    () => {
      settled = true;
    },
    (error: unknown) => {
      settled = true;
      failure = error;
    },
  );

  const started = Date.now();
  while (!settled && Date.now() - started < 10_000) {
    for (const request of http.match(TICKET_MATCH)) {
      const body = request.request.body as { partNumbers: number[] };
      log.ticketWindows.push(body.partNumbers);
      const indexes = body.partNumbers.map((number) => number - 1);
      for (const index of indexes) {
        log.ticketLengths[index] = Math.min(
          session.chunkSize,
          session.totalBytes - index * session.chunkSize,
        );
      }
      request.flush(
        partTicketBatchFixture(
          session.sessionId,
          session,
          indexes,
          new Date(NOW + 15 * 60_000).toISOString(),
        ),
      );
    }
    for (const request of http.match(RECONCILE_MATCH)) {
      const body = request.request.body as {
        parts: { partNumber: number; sha256: string; lengthBytes: number }[];
      };
      for (const part of body.parts) received.add(part.partNumber - 1);
      log.reconciledParts.push(...body.parts.map((part) => part.partNumber - 1));
      request.flush({
        ...session,
        state: 'Receiving',
        receivedChunks: [...received].sort((a, b) => a - b),
        receivedChunkCount: received.size,
      });
    }
    await new Promise<void>((resolve) => setTimeout(resolve, 5));
  }

  if (failure !== undefined) throw failure;
  return log;
}

describe('DirectUploadLibraryTransferTransport', () => {
  let http: HttpTestingController;
  let storage: MockDirectUploadClient;
  let transport: DirectUploadLibraryTransferTransport;

  function transportFor(
    overrides: Partial<DirectUploadTransportOptions> = {},
  ): DirectUploadLibraryTransferTransport {
    return new DirectUploadLibraryTransferTransport(
      TestBed.inject(HttpClient),
      MIGRATION_BASE_PATH,
      {
        loadDirectUploadCapability: async () => true,
        uploadClient: storage,
        applicationOrigin: APP_ORIGIN,
        now: () => NOW,
        modeRetryDelayMs: () => 0,
        ...overrides,
      },
    );
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(
          withInterceptors([
            (request, next) =>
              next(request.clone({ setHeaders: { Authorization: 'Bearer app-token' } })),
          ]),
        ),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    storage = new MockDirectUploadClient();
    transport = transportFor();
  });

  afterEach(() => {
    try {
      http.verify({ ignoreCancelled: true });
    } finally {
      TestBed.resetTestingModule();
    }
  });

  async function prime(session: MigrationSessionStatusDto): Promise<void> {
    const primed = transport.getUploadSession(JOB_ID);
    http.expectOne(SESSION_URL).flush(sessionResponse(session));
    await primed;
  }

  it('requests a bounded part window, PUTs the slice with the exact init and reconciles', async () => {
    const session = sessionStatus(CHUNK * 2);
    await prime(session);

    const file = fileOfSize(session.totalBytes);
    const chunk = chunkAt(file, session, 0);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunk,
      () => undefined,
      new AbortController().signal,
    );

    const ticketsRequest = await waitForRequest(http, TICKETS_URL);
    expect(ticketsRequest.request.body).toEqual({
      sessionId: SESSION_ID,
      partNumbers: [1, 2],
    });
    // The control plane stays on the application's authenticated HTTP stack.
    expect(ticketsRequest.request.headers.get('Authorization')).toBe('Bearer app-token');
    ticketsRequest.flush(
      partTicketBatchFixture(SESSION_ID, session, [0, 1], new Date(NOW + 900_000).toISOString()),
    );

    await waitForMicrotasks();
    expect(storage.requests).toHaveLength(1);
    expect(storage.requests[0].url).toBe('https://storage.example/part-1?sig=test-signature');
    // The engine's File.slice() is the body, never a copy; the client request
    // carries nothing but the URL, body and abort signal.
    expect(storage.requests[0].body).toBe(chunk.blob);
    expect(Object.keys(storage.requests[0]).sort()).toEqual(['body', 'url']);

    const reconcileRequest = await waitForRequest(http, RECONCILE_URL);
    expect(reconcileRequest.request.body).toEqual({
      sessionId: SESSION_ID,
      parts: [{ partNumber: 1, sha256: 'b'.repeat(64), lengthBytes: CHUNK }],
    });
    reconcileRequest.flush({
      ...session,
      state: 'Receiving',
      receivedChunks: [0],
      receivedChunkCount: 1,
    });

    await expect(promise).resolves.toEqual({
      sessionId: SESSION_ID,
      chunkIndex: 0,
      alreadyPresent: false,
    });
    http.expectNone({ method: 'PUT', url: chunkUrl(0) });
  });

  it('serves later parts from the cached ticket window without a second request', async () => {
    const session = sessionStatus(CHUNK * 3);
    await prime(session);
    const file = fileOfSize(session.totalBytes);

    const first = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(file, session, 0),
      () => undefined,
      new AbortController().signal,
    );
    const ticketsRequest = await waitForRequest(http, TICKETS_URL);
    ticketsRequest.flush(
      partTicketBatchFixture(SESSION_ID, session, [0, 1, 2], new Date(NOW + 900_000).toISOString()),
    );
    const firstReconcile = await waitForRequest(http, RECONCILE_URL);
    firstReconcile.flush({
      ...session,
      state: 'Receiving',
      receivedChunks: [0],
      receivedChunkCount: 1,
    });
    await first;

    const second = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(file, session, 1),
      () => undefined,
      new AbortController().signal,
    );
    const secondReconcile = await waitForRequest(http, RECONCILE_URL);
    secondReconcile.flush({
      ...session,
      state: 'Receiving',
      receivedChunks: [0, 1],
      receivedChunkCount: 2,
    });
    await second;

    expect(http.match(TICKETS_URL)).toHaveLength(0);
    expect(storage.requests.map((request) => request.url)).toEqual([
      'https://storage.example/part-1?sig=test-signature',
      'https://storage.example/part-2?sig=test-signature',
    ]);
  });

  it('refreshes a window that arrives already inside the expiry safety margin', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );

    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 10_000).toISOString()),
    );
    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );
    const reconcile = await waitForRequest(http, RECONCILE_URL);
    reconcile.flush({
      ...session,
      state: 'Receiving',
      receivedChunks: [0],
      receivedChunkCount: 1,
    });
    await promise;

    expect(storage.requests).toHaveLength(1);
  });

  it('refreshes the window once after a 403 and succeeds on the fresh target', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);
    storage.queueRefusal(403);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );

    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );
    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );
    const reconcile = await waitForRequest(http, RECONCILE_URL);
    reconcile.flush({
      ...session,
      state: 'Receiving',
      receivedChunks: [0],
      receivedChunkCount: 1,
    });
    await promise;

    expect(storage.requests).toHaveLength(2);
  });

  it('surfaces a typed terminal error when the target keeps refusing after a refresh', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);
    storage.queueRefusal(403, 4);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );

    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );
    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );

    const error = await rejectionOf(promise);
    expect(error).toBeInstanceOf(DirectUploadRejectedError);
    expect(error.code).toBe('direct_upload_rejected');
    expect(error.status).toBe(403);
    expect(error.retryable).toBe(false);
    expect(storage.requests).toHaveLength(2);
    http.expectNone(RECONCILE_URL);
  });

  it.each([500, 502, 503, 504, 408, 429])(
    'treats storage HTTP %s as retryable',
    async (status) => {
      const session = sessionStatus(CHUNK);
      await prime(session);
      storage.queueServerError(status);

      const promise = transport.uploadChunk(
        JOB_ID,
        SESSION_ID,
        chunkAt(fileOfSize(CHUNK), session, 0),
        () => undefined,
        new AbortController().signal,
      );
      (await waitForRequest(http, TICKETS_URL)).flush(
        partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
      );

      const error = await rejectionOf(promise);
      expect(error).toBeInstanceOf(DirectUploadStorageError);
      expect(error.status).toBe(status);
      expect(error.retryable).toBe(true);
      http.expectNone(RECONCILE_URL);
    },
  );

  it.each([400, 404, 413, 501])('treats storage HTTP %s as terminal', async (status) => {
    const session = sessionStatus(CHUNK);
    await prime(session);
    storage.queueServerError(status);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );
    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );

    const error = await rejectionOf(promise);
    expect(error).toBeInstanceOf(DirectUploadStorageError);
    expect(error.status).toBe(status);
    expect(error.retryable).toBe(false);
    http.expectNone(RECONCILE_URL);
  });

  it('maps an unreachable target to a retryable network error without re-sending the body', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);
    storage.queueNetworkError();

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );
    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );

    const error = await rejectionOf(promise);
    expect(error.code).toBe('network_error');
    expect(error.retryable).toBe(true);
    expect(storage.requests).toHaveLength(1);
    http.expectNone(RECONCILE_URL);
  });

  it('refuses a same-origin upload target before any byte is sent', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );
    (await waitForRequest(http, TICKETS_URL)).flush({
      sessionId: SESSION_ID,
      jobId: JOB_ID,
      expiresAtUtc: new Date(NOW + 900_000).toISOString(),
      tickets: [
        {
          partNumber: 1,
          chunkIndex: 0,
          offsetBytes: 0,
          lengthBytes: CHUNK,
          uploadUrl: `${APP_ORIGIN}/upload/part-1`,
        },
      ],
    });

    const error = await rejectionOf(promise);
    expect(error).toBeInstanceOf(DirectUploadTargetError);
    expect(error.code).toBe('direct_upload_target_invalid');
    expect(error.retryable).toBe(false);
    expect(storage.requests).toHaveLength(0);
  });

  it('refuses a plain-HTTP public upload target before any byte is sent', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );
    (await waitForRequest(http, TICKETS_URL)).flush({
      sessionId: SESSION_ID,
      jobId: JOB_ID,
      expiresAtUtc: new Date(NOW + 900_000).toISOString(),
      tickets: [
        {
          partNumber: 1,
          chunkIndex: 0,
          offsetBytes: 0,
          lengthBytes: CHUNK,
          uploadUrl: 'http://storage.example/part-1',
        },
      ],
    });

    const error = await rejectionOf(promise);
    expect(error).toBeInstanceOf(DirectUploadTargetError);
    expect(storage.requests).toHaveLength(0);
  });

  it('accepts a loopback HTTP target for local development', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );
    (await waitForRequest(http, TICKETS_URL)).flush({
      sessionId: SESSION_ID,
      jobId: JOB_ID,
      expiresAtUtc: new Date(NOW + 900_000).toISOString(),
      tickets: [
        {
          partNumber: 1,
          chunkIndex: 0,
          offsetBytes: 0,
          lengthBytes: CHUNK,
          uploadUrl: 'http://127.0.0.1:9000/part-1',
        },
      ],
    });
    const reconcile = await waitForRequest(http, RECONCILE_URL);
    reconcile.flush({
      ...session,
      state: 'Receiving',
      receivedChunks: [0],
      receivedChunkCount: 1,
    });
    await promise;

    expect(storage.requests[0].url).toBe('http://127.0.0.1:9000/part-1');
  });

  it('fails closed when a ticket does not match the session contract', async () => {
    const session = sessionStatus(CHUNK * 2);
    await prime(session);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(session.totalBytes), session, 0),
      () => undefined,
      new AbortController().signal,
    );
    (await waitForRequest(http, TICKETS_URL)).flush({
      sessionId: SESSION_ID,
      jobId: JOB_ID,
      expiresAtUtc: new Date(NOW + 900_000).toISOString(),
      tickets: [
        {
          partNumber: 1,
          chunkIndex: 0,
          offsetBytes: 123,
          lengthBytes: CHUNK,
          uploadUrl: 'https://storage.example/part-1',
        },
      ],
    });

    const error = await rejectionOf(promise);
    expect(error).toBeInstanceOf(DirectUploadTicketInvalidError);
    expect(error.retryable).toBe(false);
    expect(storage.requests).toHaveLength(0);
  });

  it('treats a reconcile response that does not list the part as a retryable pending receipt', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );
    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );
    const reconcile = await waitForRequest(http, RECONCILE_URL);
    reconcile.flush({ ...session, receivedChunks: [], receivedChunkCount: 0 });

    const error = await rejectionOf(promise);
    expect(error).toBeInstanceOf(DirectUploadReceiptPendingError);
    expect(error.code).toBe('direct_upload_receipt_pending');
    expect(error.retryable).toBe(true);
    expect(storage.requests).toHaveLength(1);
  });

  it('aborts the in-flight target PUT when the signal aborts', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);
    storage.latencyMs = 60;

    const controller = new AbortController();
    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      controller.signal,
    );
    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );
    await new Promise<void>((resolve) => setTimeout(resolve, 10));
    controller.abort();

    const error = await rejectionOf(promise);
    expect(error.code).toBe('request_aborted');
    expect(storage.abortedCount).toBe(1);
    http.expectNone(RECONCILE_URL);
  });

  it('drives the engine through tickets and skips parts the server already received', async () => {
    const session = sessionStatus(CHUNK * 3, CHUNK, [0]);
    await prime(session);
    const engine = new ChunkUploadEngine(transport, stubDigest(), { random: () => 0.5 });

    const run = engine.start({
      jobId: JOB_ID,
      session,
      file: fileOfSize(session.totalBytes),
      concurrency: 1,
    });
    const log = await serveEngine(http, session, run.done);
    await expect(run.done).resolves.toEqual({ kind: 'completed' });

    expect(log.ticketWindows[0]).toEqual([2, 3]);
    expect(log.reconciledParts.sort((a, b) => a - b)).toEqual([1, 2]);
    expect(storage.requests).toHaveLength(2);
  });

  it('honours the server-dictated chunk size and slices only that much per part', async () => {
    const dictated = 8 * 1024 * 1024;
    const session = sessionStatus(20 * 1024 * 1024, dictated);
    await prime(session);
    const engine = new ChunkUploadEngine(transport, stubDigest(), { random: () => 0.5 });

    const run = engine.start({
      jobId: JOB_ID,
      session,
      file: fileOfSize(session.totalBytes),
      concurrency: 1,
    });
    const log = await serveEngine(http, session, run.done);
    await run.done;

    expect(log.ticketLengths).toEqual([dictated, dictated, 4 * 1024 * 1024]);
    expect(storage.requests.map((request) => request.body.size)).toEqual([
      dictated,
      dictated,
      4 * 1024 * 1024,
    ]);
  });

  it('applies engine backoff to a storage 500 and completes on the retry', async () => {
    const session = sessionStatus(CHUNK);
    await prime(session);
    storage.queueServerError(500);
    const engine = new ChunkUploadEngine(transport, stubDigest(), { random: () => 0.5 });

    const started = Date.now();
    const run = engine.start({
      jobId: JOB_ID,
      session,
      file: fileOfSize(CHUNK),
      concurrency: 1,
    });
    await serveEngine(http, session, run.done);
    await expect(run.done).resolves.toEqual({ kind: 'completed' });

    expect(storage.requests).toHaveLength(2);
    expect(Date.now() - started).toBeGreaterThanOrEqual(400);
  });

  it('retries a transient capability failure and then pins direct mode', async () => {
    let calls = 0;
    const modeTransport = transportFor({
      loadDirectUploadCapability: async () => {
        calls += 1;
        if (calls < 3) throw new Error('capabilities unavailable');
        return true;
      },
    });

    await expect(modeTransport.resolveUploadMode(SESSION_ID)).resolves.toBe('direct');
    expect(calls).toBe(3);
    // Pinned: a later lookup failure cannot flip the session.
    await expect(modeTransport.resolveUploadMode(SESSION_ID)).resolves.toBe('direct');
    expect(calls).toBe(3);
  });

  it('surfaces a retryable typed error and never falls back to legacy when the capability stays unavailable', async () => {
    let calls = 0;
    const modeTransport = transportFor({
      loadDirectUploadCapability: async () => {
        calls += 1;
        throw new Error('capabilities unavailable');
      },
      maxModeResolutionAttempts: 3,
    });

    await expect(
      modeTransport.uploadChunk(
        JOB_ID,
        SESSION_ID,
        chunkAt(fileOfSize(CHUNK), sessionStatus(CHUNK), 0),
        () => undefined,
        new AbortController().signal,
      ),
    ).rejects.toMatchObject({
      code: 'migration_transport_mode_unavailable',
      retryable: true,
    });
    expect(calls).toBe(3);
    http.expectNone({ method: 'PUT', url: chunkUrl(0) });
    expect(storage.requests).toHaveLength(0);
  });

  it('re-pins the persisted direct mode after a reload without re-reading the capability', async () => {
    let calls = 0;
    const reloaded = transportFor({
      loadDirectUploadCapability: async () => {
        calls += 1;
        return false;
      },
    });
    reloaded.pinUploadMode(SESSION_ID, 'direct');

    const session = sessionStatus(CHUNK);
    const primed = reloaded.getUploadSession(JOB_ID);
    http.expectOne(SESSION_URL).flush(sessionResponse(session));
    await primed;

    const promise = reloaded.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );
    (await waitForRequest(http, TICKETS_URL)).flush(
      partTicketBatchFixture(SESSION_ID, session, [0], new Date(NOW + 900_000).toISOString()),
    );
    const reconcile = await waitForRequest(http, RECONCILE_URL);
    reconcile.flush({
      ...session,
      state: 'Receiving',
      receivedChunks: [0],
      receivedChunkCount: 1,
    });
    await promise;

    expect(calls).toBe(0);
    expect(storage.requests).toHaveLength(1);
  });

  it('keeps the application-server chunk path byte-identical when the capability is false', async () => {
    const plain = transportFor({ loadDirectUploadCapability: async () => false });
    const session = sessionStatus(CHUNK);
    const primed = plain.getUploadSession(JOB_ID);
    http.expectOne(SESSION_URL).flush(sessionResponse(session));
    await primed;

    const file = fileOfSize(CHUNK);
    const chunk = chunkAt(file, session, 0);
    const promise = plain.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunk,
      () => undefined,
      new AbortController().signal,
    );

    const put = await waitForRequest(http, { method: 'PUT', url: chunkUrl(0) });
    expect(put.request.body).toBe(chunk.blob);
    expect(put.request.headers.get('Content-Range')).toBe(`bytes 0-${CHUNK - 1}/${CHUNK}`);
    expect(put.request.headers.get('X-Nostos-Chunk-SHA256')).toBe('b'.repeat(64));
    expect(put.request.headers.get('Authorization')).toBe('Bearer app-token');
    put.flush({ sessionId: SESSION_ID, chunkIndex: 0, alreadyPresent: false });

    await promise;
    http.expectNone(TICKETS_URL);
    expect(storage.requests).toHaveLength(0);
  });

  it('is selected by the deployment capability through the DI factory', async () => {
    const factoryTransport: LibraryTransferTransport = TestBed.inject(LIBRARY_TRANSFER_TRANSPORT);
    expect(factoryTransport).toBeInstanceOf(DirectUploadLibraryTransferTransport);

    const session = sessionStatus(CHUNK);
    const primed = factoryTransport.getUploadSession(JOB_ID);
    http.expectOne(SESSION_URL).flush(sessionResponse(session));
    await primed;

    const promise = factoryTransport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );
    const capabilities = await waitForRequest(http, '/api/runtime/capabilities');
    capabilities.flush({ deploymentMode: 'SelfHosted', supportsDirectPartUpload: false });

    const put = await waitForRequest(http, { method: 'PUT', url: chunkUrl(0) });
    put.flush({ sessionId: SESSION_ID, chunkIndex: 0, alreadyPresent: false });
    await promise;

    http.expectNone(TICKETS_URL);
  });
});

async function waitForMicrotasks(): Promise<void> {
  for (let attempt = 0; attempt < 20; attempt += 1) await Promise.resolve();
}
