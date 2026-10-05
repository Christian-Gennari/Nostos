import { HttpClient, HttpRequest, provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  RequestMatch,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { FileDigest } from '../models/library-transfer.models';
import {
  BrowserMigrationChunk,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
  toChunkRanges,
} from '../models/migration-http.dtos';
import { MigrationPartTicketDto } from '../models/direct-part-upload.dtos';
import { MockDirectUploadClient } from '../testing/mock-direct-upload-client';
import { ChunkUploadEngine } from './chunk-upload-engine.service';
import {
  DirectUploadLibraryTransferTransport,
  DirectUploadRejectedError,
  DirectUploadTokenMissingError,
  MAX_DIRECT_UPLOAD_TICKET_REQUESTS,
} from './direct-upload-library-transfer-transport';
import { MIGRATION_BASE_PATH } from './http-library-transfer-transport';
import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
  MigrationTransportError,
} from './library-transfer-transport';

const JOB_ID = '11111111-1111-4111-8111-111111111111';
const SESSION_ID = '22222222-2222-4222-8222-222222222222';
const CHUNK = 4 * 1024 * 1024;
const NOW = Date.parse('2026-01-01T00:00:00.000Z');

const SESSION_URL = `${MIGRATION_BASE_PATH}/jobs/${JOB_ID}/upload-session`;
const ticketUrl = (index: number): string => `${SESSION_URL}/parts/${index}/ticket`;
const completeUrl = (index: number): string => `${SESSION_URL}/parts/${index}/complete`;
const chunkUrl = (index: number): string => `${SESSION_URL}/chunks/${index}`;

const TICKET_MATCH = (request: HttpRequest<unknown>): boolean =>
  request.method === 'POST' && /\/upload-session\/parts\/\d+\/ticket$/.test(request.url);
const COMPLETE_MATCH = (request: HttpRequest<unknown>): boolean =>
  request.method === 'POST' && /\/upload-session\/parts\/\d+\/complete$/.test(request.url);

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

function validTicket(overrides: Partial<MigrationPartTicketDto> = {}): MigrationPartTicketDto {
  return {
    url: 'https://storage.example/part?sig=test-signature',
    method: 'PUT',
    requiredHeaders: {
      'x-storage-signature': 'test-signature',
      'Content-Type': 'application/octet-stream',
    },
    expiresAtUtc: new Date(NOW + 60_000).toISOString(),
    ...overrides,
  };
}

function fileOfSize(size: number): File {
  const bytes = new Uint8Array(size);
  for (let index = 0; index < size; index += 1) bytes[index] = (index * 31) % 256;
  return new File([bytes as unknown as BlobPart], 'library.nostos');
}

function chunkAt(file: Blob, session: MigrationSessionStatusDto, index: number): BrowserMigrationChunk {
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

function partIndexFromUrl(url: string): number {
  const match = /\/parts\/(\d+)\/(?:ticket|complete)$/.exec(url);
  expect(match).not.toBeNull();
  return Number(match![1]);
}

interface ServeLog {
  ticketIndexes: number[];
  ticketLengths: number[];
  completeIndexes: number[];
}

/**
 * Plays the application server while an engine run is in flight: every ticket
 * request is answered with a fresh ticket and every completion report is
 * acknowledged. Polls briefly so retry backoff has time to elapse.
 */
async function serveEngine(
  http: HttpTestingController,
  done: Promise<unknown>,
): Promise<ServeLog> {
  const log: ServeLog = { ticketIndexes: [], ticketLengths: [], completeIndexes: [] };
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
      const body = request.request.body as { lengthBytes: number };
      log.ticketIndexes.push(partIndexFromUrl(request.request.url));
      log.ticketLengths.push(body.lengthBytes);
      request.flush(validTicket());
    }
    for (const request of http.match(COMPLETE_MATCH)) {
      log.completeIndexes.push(partIndexFromUrl(request.request.url));
      request.flush({
        sessionId: SESSION_ID,
        chunkIndex: partIndexFromUrl(request.request.url),
        alreadyPresent: false,
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
    transport = new DirectUploadLibraryTransferTransport(
      TestBed.inject(HttpClient),
      MIGRATION_BASE_PATH,
      {
        directPartUpload: () => true,
        uploadClient: storage,
        now: () => NOW,
      },
    );
  });

  afterEach(() => {
    try {
      http.verify({ ignoreCancelled: true });
    } finally {
      TestBed.resetTestingModule();
    }
  });

  it('requests a ticket, PUTs to the target with exactly the ticket headers and reports the token', async () => {
    const session = sessionStatus(CHUNK * 2);
    await expect(prime(transport, http, session)).resolves.toBeUndefined();

    const file = fileOfSize(session.totalBytes);
    const chunk = chunkAt(file, session, 0);
    storage.queueSuccess('"etag-0"');

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunk,
      () => undefined,
      new AbortController().signal,
    );

    const ticketRequest = await waitForRequest(http, ticketUrl(0));
    expect(ticketRequest.request.body).toEqual({ lengthBytes: CHUNK, sha256: 'b'.repeat(64) });
    // The control plane stays on the application's authenticated HTTP stack.
    expect(ticketRequest.request.headers.get('Authorization')).toBe('Bearer app-token');
    ticketRequest.flush(validTicket());

    await waitForMicrotasks();
    expect(storage.requests).toHaveLength(1);
    expect(storage.requests[0].url).toBe('https://storage.example/part?sig=test-signature');
    expect(storage.requests[0].method).toBe('PUT');
    // Exactly the ticket headers: no Authorization, no cookie, no interceptor.
    expect(storage.requests[0].headers).toEqual({
      'x-storage-signature': 'test-signature',
      'Content-Type': 'application/octet-stream',
    });
    expect(storage.requests[0].headers['Authorization']).toBeUndefined();
    expect(storage.requests[0].headers['Cookie']).toBeUndefined();
    // The engine's File.slice() is the body, never a copy.
    expect(storage.requests[0].body).toBe(chunk.blob);

    const completionRequest = await waitForRequest(http, completeUrl(0));
    expect(completionRequest.request.body).toEqual({
      etag: '"etag-0"',
      sha256: 'b'.repeat(64),
      byteLength: CHUNK,
    });
    expect(completionRequest.request.headers.get('Authorization')).toBe('Bearer app-token');
    completionRequest.flush({ sessionId: SESSION_ID, chunkIndex: 0, alreadyPresent: false });

    await expect(promise).resolves.toEqual({
      sessionId: SESSION_ID,
      chunkIndex: 0,
      alreadyPresent: false,
    });
    http.expectNone({ method: 'PUT', url: chunkUrl(0) });
  });

  it('refreshes a ticket that arrives already expired', async () => {
    const session = sessionStatus(CHUNK);
    await prime(transport, http, session);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );

    (await waitForRequest(http, ticketUrl(0))).flush(
      validTicket({ expiresAtUtc: new Date(NOW - 1_000).toISOString() }),
    );
    (await waitForRequest(http, ticketUrl(0))).flush(validTicket());

    const completion = await waitForRequest(http, completeUrl(0));
    completion.flush({ sessionId: SESSION_ID, chunkIndex: 0, alreadyPresent: false });
    await promise;

    expect(storage.requests).toHaveLength(1);
  });

  it('refreshes the ticket and retries once when the target refuses with 403 (expired URL)', async () => {
    const session = sessionStatus(CHUNK);
    await prime(transport, http, session);
    storage.queueRefusal(403);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );

    (await waitForRequest(http, ticketUrl(0))).flush(validTicket());
    (await waitForRequest(http, ticketUrl(0))).flush(validTicket());

    const completion = await waitForRequest(http, completeUrl(0));
    completion.flush({ sessionId: SESSION_ID, chunkIndex: 0, alreadyPresent: false });
    await promise;

    expect(storage.requests).toHaveLength(2);
  });

  it('surfaces a typed, non-retryable error when the target keeps refusing the part', async () => {
    const session = sessionStatus(CHUNK);
    await prime(transport, http, session);
    storage.queueRefusal(403, MAX_DIRECT_UPLOAD_TICKET_REQUESTS + 1);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );

    for (let attempt = 0; attempt < MAX_DIRECT_UPLOAD_TICKET_REQUESTS; attempt += 1) {
      (await waitForRequest(http, ticketUrl(0))).flush(validTicket());
    }

    const error = await rejectionOf(promise);
    expect(error).toBeInstanceOf(DirectUploadRejectedError);
    expect(error.status).toBe(403);
    expect(error.retryable).toBe(false);
    expect(storage.requests).toHaveLength(MAX_DIRECT_UPLOAD_TICKET_REQUESTS);
    http.expectNone(completeUrl(0));
  });

  it('names the missing completion header when the target does not expose it', async () => {
    const session = sessionStatus(CHUNK);
    await prime(transport, http, session);
    storage.queueMissingCompletionToken();

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );

    (await waitForRequest(http, ticketUrl(0))).flush(validTicket());

    const error = await rejectionOf(promise);
    expect(error).toBeInstanceOf(DirectUploadTokenMissingError);
    expect(error.message).toContain('ETag');
    expect(error.retryable).toBe(false);
    expect(storage.requests).toHaveLength(1);
    http.expectNone(completeUrl(0));
  });

  it('surfaces a storage 5xx as a retryable transport error', async () => {
    const session = sessionStatus(CHUNK);
    await prime(transport, http, session);
    storage.queueServerError(503);

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      new AbortController().signal,
    );

    (await waitForRequest(http, ticketUrl(0))).flush(validTicket());

    const error = await rejectionOf(promise);
    expect(error).toBeInstanceOf(MigrationTransportError);
    expect(error.status).toBe(503);
    expect(error.retryable).toBe(true);
    http.expectNone(completeUrl(0));
  });

  it('aborts the in-flight target PUT when the signal aborts', async () => {
    const session = sessionStatus(CHUNK);
    await prime(transport, http, session);
    storage.latencyMs = 60;

    const controller = new AbortController();
    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunkAt(fileOfSize(CHUNK), session, 0),
      () => undefined,
      controller.signal,
    );

    (await waitForRequest(http, ticketUrl(0))).flush(validTicket());
    await new Promise<void>((resolve) => setTimeout(resolve, 10));
    controller.abort();

    const error = await rejectionOf(promise);
    expect(error.code).toBe('request_aborted');
    expect(storage.abortedCount).toBe(1);
    http.expectNone(completeUrl(0));
  });

  it('keeps the application-server chunk path byte-identical when the capability is off', async () => {
    const plain = new DirectUploadLibraryTransferTransport(
      TestBed.inject(HttpClient),
      MIGRATION_BASE_PATH,
      { directPartUpload: () => false },
    );
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
    http.expectNone(ticketUrl(0));
    expect(storage.requests).toHaveLength(0);
  });

  it('drives the engine through tickets and skips parts the server already received', async () => {
    const session = sessionStatus(CHUNK * 3, CHUNK, [0]);
    await prime(transport, http, session);
    const engine = new ChunkUploadEngine(transport, stubDigest(), { random: () => 0.5 });

    const run = engine.start({
      jobId: JOB_ID,
      session,
      file: fileOfSize(session.totalBytes),
      concurrency: 1,
    });
    const log = await serveEngine(http, run.done);
    await expect(run.done).resolves.toEqual({ kind: 'completed' });

    expect(log.ticketIndexes).toEqual([1, 2]);
    expect(log.completeIndexes.sort((a, b) => a - b)).toEqual([1, 2]);
    expect(storage.requests).toHaveLength(2);
  });

  it('honours the server-dictated chunk size and slices only that much per part', async () => {
    const dictated = 8 * 1024 * 1024;
    const session = sessionStatus(20 * 1024 * 1024, dictated);
    await prime(transport, http, session);
    const engine = new ChunkUploadEngine(transport, stubDigest(), { random: () => 0.5 });

    const run = engine.start({
      jobId: JOB_ID,
      session,
      file: fileOfSize(session.totalBytes),
      concurrency: 1,
    });
    const log = await serveEngine(http, run.done);
    await run.done;

    expect(log.ticketLengths).toEqual([dictated, dictated, 4 * 1024 * 1024]);
    expect(storage.requests.map((request) => request.body.size)).toEqual([
      dictated,
      dictated,
      4 * 1024 * 1024,
    ]);
  });

  it('applies engine backoff to a storage 5xx and completes on the retry', async () => {
    const session = sessionStatus(CHUNK);
    await prime(transport, http, session);
    storage.queueServerError(503);
    const engine = new ChunkUploadEngine(transport, stubDigest(), { random: () => 0.5 });

    const started = Date.now();
    const run = engine.start({
      jobId: JOB_ID,
      session,
      file: fileOfSize(CHUNK),
      concurrency: 1,
    });
    await serveEngine(http, run.done);
    await expect(run.done).resolves.toEqual({ kind: 'completed' });

    expect(storage.requests).toHaveLength(2);
    expect(Date.now() - started).toBeGreaterThanOrEqual(400);
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

    // The capability is read lazily on the first upload; false keeps the
    // application-server chunk path even though the transport is the direct
    // subclass.
    const capabilities = await waitForRequest(http, '/api/runtime/capabilities');
    capabilities.flush({ deploymentMode: 'SelfHosted', supportsDirectPartUpload: false });

    const put = await waitForRequest(http, { method: 'PUT', url: chunkUrl(0) });
    put.flush({ sessionId: SESSION_ID, chunkIndex: 0, alreadyPresent: false });
    await promise;

    http.expectNone(ticketUrl(0));
  });
});

async function prime(
  transport: DirectUploadLibraryTransferTransport,
  http: HttpTestingController,
  session: MigrationSessionStatusDto,
): Promise<void> {
  const primed = transport.getUploadSession(JOB_ID);
  http.expectOne(SESSION_URL).flush(sessionResponse(session));
  await primed;
}

async function waitForMicrotasks(): Promise<void> {
  for (let attempt = 0; attempt < 20; attempt += 1) await Promise.resolve();
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
