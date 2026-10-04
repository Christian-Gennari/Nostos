import { HttpClient, HttpEventType, provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  RequestMatch,
  TestRequest,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import {
  BrowserMigrationChunk,
  MigrationCreateJobRequestDto,
  MigrationErrorCode,
  MigrationJobStatusResponseDto,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationSessionRequestDto,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
  SERVER_MIGRATION_ERROR_CODES,
} from '../models/migration-http.dtos';
import {
  CHUNK_HASH_HEADER_NAME,
  HttpLibraryTransferTransport,
  MIGRATION_BASE_PATH,
} from './http-library-transfer-transport';
import { MigrationTransportError } from './migration-transport-error';

const JOB_ID = '11111111-1111-4111-8111-111111111111';
const SESSION_ID = '22222222-2222-4222-8222-222222222222';
const CHUNK = 4 * 1024 * 1024;

const JOB_URL = `${MIGRATION_BASE_PATH}/jobs/${JOB_ID}`;
const SESSION_URL = `${JOB_URL}/upload-session`;

function fileIdentity(totalSizeBytes: number) {
  return { totalSizeBytes, sha256Checksum: 'a'.repeat(64) };
}

function sessionStatus(
  overrides: Partial<MigrationSessionStatusDto> = {},
): MigrationSessionStatusDto {
  const totalBytes = overrides.totalBytes ?? 100;
  const chunkSize = overrides.chunkSize ?? CHUNK;
  const receivedChunks = overrides.receivedChunks ?? [];
  return {
    sessionId: SESSION_ID,
    purpose: 'Import',
    state: 'Created',
    totalBytes,
    chunkSize,
    totalChunks: Math.max(1, Math.ceil(totalBytes / chunkSize)),
    fileIdentity: fileIdentity(totalBytes),
    receivedChunks,
    receivedChunkCount: receivedChunks.length,
    createdAtUtc: new Date(0).toISOString(),
    expiresAtUtc: new Date(86_400_000).toISOString(),
    ...overrides,
  };
}

function jobStatus(
  overrides: Partial<MigrationJobStatusResponseDto> = {},
): MigrationJobStatusResponseDto {
  return {
    job: {
      id: JOB_ID,
      direction: 'Import',
      state: 'Pending',
      recoveryStatus: 'NotRequired',
      createdAtUtc: new Date(0).toISOString(),
      updatedAtUtc: new Date(0).toISOString(),
      leaseToken: null,
      leaseExpiresAtUtc: null,
      failureCode: null,
      failureMessage: null,
    },
    progress: { phase: 'Pending', bytesProcessed: 0, totalBytes: null },
    session: null,
    downloadAvailable: false,
    artifactExpiresAtUtc: null,
    preparedImport: null,
    ...overrides,
  };
}

function preflightRequest(): MigrationPreflightRequestDto {
  return {
    incomingCounts: {
      works: 0,
      books: 0,
      notes: 0,
      topics: 0,
      noteTopics: 0,
      writings: 0,
      writingNotes: 0,
      collections: 0,
      collectionMemberships: 0,
      acquisitions: 0,
      assistantSettings: 0,
      noteImportBookLinks: 0,
      mediaEntries: 0,
      totalRows: 0,
    },
    declaredArchiveBytes: 1024,
    declaredMediaBytes: 0,
    maxSingleEntryBytes: 0,
    declaredFormatVersion: 1,
    declaredDataVersion: 3,
    declaredFormatName: 'nostos-portable',
    clientDestinationRevision: null,
    isOperationalBackup: false,
  };
}

function sessionRequest(totalBytes = 100): MigrationSessionRequestDto {
  return {
    purpose: 'Import',
    totalBytes,
    chunkSize: CHUNK,
    totalChunks: Math.max(1, Math.ceil(totalBytes / CHUNK)),
    fileIdentity: fileIdentity(totalBytes),
    idempotencyKey: 'session-key-1',
  };
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

async function waitForRequest(
  http: HttpTestingController,
  match: RequestMatch | string,
): Promise<TestRequest> {
  for (let attempt = 0; attempt < 50; attempt += 1) {
    const found = http.match(match);
    if (found.length > 0) return found[0];
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  }
  return http.expectOne(match);
}

/** Every server code with the status the merged backend maps it to. */
const SERVER_CODE_STATUS: ReadonlyArray<readonly [MigrationErrorCode, number]> = [
  ['migration_not_found', 404],
  ['migration_idempotency_conflict', 409],
  ['migration_invalid_state', 409],
  ['migration_lease_conflict', 409],
  ['migration_reservation_required', 409],
  ['migration_file_identity_mismatch', 409],
  ['migration_chunk_conflict', 409],
  ['migration_chunk_hash_mismatch', 422],
  ['migration_chunk_range_invalid', 416],
  ['migration_session_expired', 410],
  ['migration_storage_exhausted', 507],
  ['migration_cannot_cancel', 409],
  ['migration_not_retryable', 409],
  ['migration_invalid_request', 400],
  ['migration_storage_contended', 503],
  ['migration_import_preparation_unavailable', 409],
  ['migration_export_artifact_unavailable', 409],
  ['migration_export_not_available', 404],
  ['migration_export_expired', 410],
  ['migration_activation_busy', 503],
  ['unexpected_error', 500],
];

describe('HttpLibraryTransferTransport', () => {
  let http: HttpTestingController;
  let transport: HttpLibraryTransferTransport;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    transport = new HttpLibraryTransferTransport(TestBed.inject(HttpClient));
  });

  afterEach(() => {
    http.verify({ ignoreCancelled: true });
    TestBed.resetTestingModule();
  });

  it('POSTs preflight with the exact body and maps the response', async () => {
    const request = preflightRequest();
    const response: MigrationPreflightResponseDto = {
      evaluation: {
        decision: 'AllowedEmpty',
        isCompatible: true,
        isAllowed: true,
        incomingCounts: request.incomingCounts,
        existingCounts: {
          works: 0,
          books: 0,
          notes: 0,
          topics: 0,
          noteTopics: 0,
          writings: 0,
          writingNotes: 0,
          collections: 0,
          bookCollections: 0,
          acquisitions: 0,
          noteImportBookLinks: 0,
          assistantSettings: 0,
          totalRows: 0,
        },
        declaredArchiveBytes: 1024,
        declaredMediaBytes: 0,
        estimatedRecoveryBytes: 0,
        requiredStorageBytes: 1024,
        availableStorageBytes: 1024 * 1024,
        errors: [],
        warnings: [],
        destinationRevision: 'rev-1',
      },
      reservationId: '33333333-3333-4333-8333-333333333333',
      reservationExpiresAtUtc: new Date(900_000).toISOString(),
      chunkSizeBytes: CHUNK,
    };

    const promise = transport.preflight(request);
    const req = http.expectOne(`${MIGRATION_BASE_PATH}/preflight`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(response);

    await expect(promise).resolves.toEqual(response);
  });

  it('POSTs a job with its direction, key and reservation', async () => {
    const request: MigrationCreateJobRequestDto = {
      direction: 'Import',
      idempotencyKey: 'job-key-1',
      reservationId: 'reservation-1',
    };
    const status = jobStatus();

    const promise = transport.createJob(request);
    const req = http.expectOne(`${MIGRATION_BASE_PATH}/jobs`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      direction: 'Import',
      idempotencyKey: 'job-key-1',
      reservationId: 'reservation-1',
    });
    req.flush(status, { status: 201, statusText: 'Created' });

    await expect(promise).resolves.toEqual(status);
  });

  it('sends a null reservation for an export job', async () => {
    const promise = transport.createJob({ direction: 'Export', idempotencyKey: 'job-key-2' });
    const req = http.expectOne(`${MIGRATION_BASE_PATH}/jobs`);
    expect(req.request.body).toEqual({
      direction: 'Export',
      idempotencyKey: 'job-key-2',
      reservationId: null,
    });
    req.flush(jobStatus());
    await promise;
  });

  it('GETs a job by id', async () => {
    const promise = transport.getJob(JOB_ID);
    const req = http.expectOne(JOB_URL);
    expect(req.request.method).toBe('GET');
    req.flush(jobStatus());
    await expect(promise).resolves.toMatchObject({ job: { id: JOB_ID } });
  });

  it('POSTs cancellation with and without a reason', async () => {
    const withReason = transport.cancelJob(JOB_ID, 'user cancelled');
    const first = http.expectOne(`${JOB_URL}/cancel`);
    expect(first.request.method).toBe('POST');
    expect(first.request.body).toEqual({ reason: 'user cancelled' });
    first.flush(jobStatus({ job: { ...jobStatus().job, state: 'Cancelled' } }));
    await withReason;

    const withoutReason = transport.cancelJob(JOB_ID);
    const second = http.expectOne(`${JOB_URL}/cancel`);
    expect(second.request.body).toBeNull();
    second.flush(jobStatus({ job: { ...jobStatus().job, state: 'Cancelled' } }));
    await withoutReason;
  });

  it('POSTs a retry with the idempotency key', async () => {
    const promise = transport.retryJob(JOB_ID, 'retry-key-1');
    const req = http.expectOne(`${JOB_URL}/retry`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ idempotencyKey: 'retry-key-1' });
    req.flush(jobStatus());
    await promise;
  });

  it('creates an upload session and takes receivedRanges as the receipt authority', async () => {
    const totalBytes = CHUNK * 17;
    const request = sessionRequest(totalBytes);
    const response: MigrationUploadSessionResponseDto = {
      session: sessionStatus({
        totalBytes,
        totalChunks: 17,
        receivedChunks: [],
        receivedChunkCount: 0,
      }),
      receivedRanges: [
        { startIndex: 0, endIndex: 14 },
        { startIndex: 16, endIndex: 16 },
      ],
    };

    const promise = transport.createUploadSession(JOB_ID, request);
    const req = http.expectOne(SESSION_URL);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(request);
    req.flush(response);

    const result = await promise;
    expect(result.receivedRanges).toEqual(response.receivedRanges);
    expect(result.session.receivedChunks).toEqual([
      0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 16,
    ]);
    expect(result.session.receivedChunkCount).toBe(16);
  });

  it('GETs the upload session and expands ranges the same way', async () => {
    const promise = transport.getUploadSession(JOB_ID);
    const req = http.expectOne(SESSION_URL);
    expect(req.request.method).toBe('GET');
    req.flush({
      session: sessionStatus({ totalBytes: CHUNK * 4, totalChunks: 4, receivedChunks: [] }),
      receivedRanges: [{ startIndex: 2, endIndex: 3 }],
    });

    const result = await promise;
    expect(result.session.receivedChunks).toEqual([2, 3]);
    expect(result.session.receivedChunkCount).toBe(2);
  });

  it('fails closed when server receipt ranges do not fit the session contract', async () => {
    const promise = transport.getUploadSession(JOB_ID);
    http.expectOne(SESSION_URL).flush({
      session: sessionStatus({ totalBytes: 100, totalChunks: 1, receivedChunks: [] }),
      receivedRanges: [{ startIndex: 0, endIndex: 14 }],
    });

    const error = await rejectionOf(promise);
    expect(error.code).toBe('migration_invalid_state');
    expect(error.status).toBe(409);
  });

  it('PUTs the Blob slice itself with exact range and hash headers and reports progress', async () => {
    const totalBytes = CHUNK + 7;
    const session = sessionStatus({ totalBytes, totalChunks: 2 });

    const seeded = transport.getUploadSession(JOB_ID);
    http.expectOne(SESSION_URL).flush({ session, receivedRanges: [] });
    await seeded;

    const file = new File([new Uint8Array(totalBytes)], 'library.nostos');
    const blob = file.slice(0, CHUNK);
    const progress: Array<[number, number]> = [];
    const chunk: BrowserMigrationChunk = {
      index: 0,
      offsetBytes: 0,
      lengthBytes: CHUNK,
      sha256: 'b'.repeat(64),
      blob,
    };

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      chunk,
      (loaded, total) => progress.push([loaded, total]),
      new AbortController().signal,
    );
    const req = await waitForRequest(http, { method: 'PUT', url: `${SESSION_URL}/chunks/0` });
    expect(req.request.method).toBe('PUT');
    // Identity: the engine's File.slice() is the request body, never a copy.
    expect(req.request.body).toBe(blob);
    expect(req.request.body).toBeInstanceOf(Blob);
    expect(req.request.headers.get('Content-Type')).toBe('application/octet-stream');
    expect(req.request.headers.get('Content-Range')).toBe(`bytes 0-${CHUNK - 1}/${totalBytes}`);
    expect(req.request.headers.get(CHUNK_HASH_HEADER_NAME)).toBe('b'.repeat(64));

    req.event({ type: HttpEventType.UploadProgress, loaded: 1024, total: CHUNK });
    req.flush({ sessionId: SESSION_ID, chunkIndex: 0, alreadyPresent: false });

    await expect(promise).resolves.toEqual({
      sessionId: SESSION_ID,
      chunkIndex: 0,
      alreadyPresent: false,
    });
    expect(progress).toEqual([[1024, CHUNK]]);
  });

  it('re-reads the authoritative session when a chunk URL arrives without one', async () => {
    const totalBytes = 100;
    const session = sessionStatus({ totalBytes });
    const file = new File([new Uint8Array(totalBytes)], 'library.nostos');

    const promise = transport.uploadChunk(
      JOB_ID,
      SESSION_ID,
      {
        index: 0,
        offsetBytes: 0,
        lengthBytes: totalBytes,
        sha256: 'c'.repeat(64),
        blob: file,
      },
      () => undefined,
      new AbortController().signal,
    );

    http.expectOne(SESSION_URL).flush({ session, receivedRanges: [] });
    const put = await waitForRequest(http, { method: 'PUT', url: `${SESSION_URL}/chunks/0` });
    put.flush({ sessionId: SESSION_ID, chunkIndex: 0, alreadyPresent: false });

    await expect(promise).resolves.toMatchObject({ chunkIndex: 0 });
  });

  it('rejects a chunk whose range does not match the session before sending it', async () => {
    const session = sessionStatus({ totalBytes: CHUNK * 2, totalChunks: 2 });
    const seeded = transport.getUploadSession(JOB_ID);
    http.expectOne(SESSION_URL).flush({ session, receivedRanges: [] });
    await seeded;

    const file = new File([new Uint8Array(10)], 'library.nostos');
    const error = await rejectionOf(
      transport.uploadChunk(
        JOB_ID,
        SESSION_ID,
        { index: 1, offsetBytes: 0, lengthBytes: 10, sha256: 'd'.repeat(64), blob: file },
        () => undefined,
        new AbortController().signal,
      ),
    );

    expect(error.code).toBe('migration_chunk_range_invalid');
    expect(error.status).toBe(416);
    http.expectNone(`${SESSION_URL}/chunks/1`);
  });

  it('POSTs completion with no body and maps the session status', async () => {
    const complete = sessionStatus({
      state: 'Complete',
      receivedChunks: [0],
      receivedChunkCount: 1,
    });

    const promise = transport.completeUpload(JOB_ID);
    const req = http.expectOne(`${SESSION_URL}/complete`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
    req.flush(complete);

    await expect(promise).resolves.toMatchObject({ state: 'Complete' });
  });

  it('maps every server error code to a typed error with its status', async () => {
    expect(SERVER_CODE_STATUS.map(([code]) => code).sort()).toEqual(
      [...SERVER_MIGRATION_ERROR_CODES].sort(),
    );

    for (const [code, status] of SERVER_CODE_STATUS) {
      const promise = transport.getJob(JOB_ID);
      const req = http.expectOne(JOB_URL);
      req.flush({ error: code, message: `message for ${code}` }, { status, statusText: 'Error' });

      const error = await rejectionOf(promise);
      expect(error.code, code).toBe(code);
      expect(error.status, code).toBe(status);
      expect(error.message, code).toBe(`message for ${code}`);
    }
  });

  it('maps the 413 chunk-too-large response through its migration body', async () => {
    const promise = transport.getJob(JOB_ID);
    http.expectOne(JOB_URL).flush(
      {
        error: 'migration_invalid_request',
        message: 'The chunk request body exceeds the maximum accepted size.',
      },
      { status: 413, statusText: 'Payload Too Large' },
    );

    const error = await rejectionOf(promise);
    expect(error.code).toBe('migration_invalid_request');
    expect(error.status).toBe(413);
  });

  it('surfaces Retry-After as a delay for the engine backoff', async () => {
    const promise = transport.getJob(JOB_ID);
    http.expectOne(JOB_URL).flush(
      { error: 'migration_activation_busy', message: 'The library is in maintenance.' },
      { status: 503, statusText: 'Service Unavailable', headers: { 'Retry-After': '5' } },
    );

    const error = await rejectionOf(promise);
    expect(error.code).toBe('migration_activation_busy');
    expect(error.retryable).toBe(true);
    expect(error.retryAfterMs).toBe(5000);
  });

  it('parses an HTTP-date Retry-After header', async () => {
    const promise = transport.getJob(JOB_ID);
    http.expectOne(JOB_URL).flush(
      { error: 'migration_storage_contended', message: 'busy' },
      {
        status: 503,
        statusText: 'Service Unavailable',
        headers: { 'Retry-After': new Date(Date.now() + 3000).toUTCString() },
      },
    );

    const error = await rejectionOf(promise);
    expect(error.code).toBe('migration_storage_contended');
    expect(error.retryAfterMs).toBeGreaterThan(0);
    expect(error.retryAfterMs).toBeLessThanOrEqual(3000);
  });

  it('maps a network failure to the retryable network_error code', async () => {
    const promise = transport.getJob(JOB_ID);
    http.expectOne(JOB_URL).error(new ProgressEvent('error'));

    const error = await rejectionOf(promise);
    expect(error.code).toBe('network_error');
    expect(error.status).toBe(0);
    expect(error.retryable).toBe(true);
  });

  it('preserves the status of an empty non-migration error body', async () => {
    const promise = transport.getJob(JOB_ID);
    http.expectOne(JOB_URL).flush(null, { status: 401, statusText: 'Unauthorized' });

    const error = await rejectionOf(promise);
    expect(error.code).toBe('unexpected_error');
    expect(error.status).toBe(401);
    expect(error.retryable).toBe(false);
  });

  it('maps a non-JSON error body without losing a transient status', async () => {
    const promise = transport.getJob(JOB_ID);
    http.expectOne(JOB_URL).flush('<html>bad gateway</html>', {
      status: 502,
      statusText: 'Bad Gateway',
    });

    const error = await rejectionOf(promise);
    expect(error.code).toBe('unexpected_error');
    expect(error.status).toBe(502);
    expect(error.retryable).toBe(true);
  });

  it('maps an unknown stable-code string to unexpected_error but keeps the status', async () => {
    const promise = transport.getJob(JOB_ID);
    http.expectOne(JOB_URL).flush(
      { error: 'some_future_code', message: 'not known yet' },
      { status: 409, statusText: 'Conflict' },
    );

    const error = await rejectionOf(promise);
    expect(error.code).toBe('unexpected_error');
    expect(error.status).toBe(409);
  });

  it('treats a framework 404 with no migration body as an unsupported host', async () => {
    const promise = transport.getJob(JOB_ID);
    http.expectOne(JOB_URL).flush(null, { status: 404, statusText: 'Not Found' });

    const error = await rejectionOf(promise);
    expect(error.code).toBe('migration_not_supported');
    expect(error.status).toBe(404);
    expect(error.message).toContain('does not support library migration');
  });

  it('rejects with request_aborted and cancels the in-flight request', async () => {
    const controller = new AbortController();
    const promise = transport.getJob(JOB_ID, controller.signal);
    const req = http.expectOne(JOB_URL);

    const rejection = rejectionOf(promise);
    controller.abort();

    const error = await rejection;
    expect(error.code).toBe('request_aborted');
    expect(error.status).toBe(0);
    expect(error.retryable).toBe(false);
    expect(req.cancelled).toBe(true);
  });

  it('never issues a request for an already-aborted signal', async () => {
    const error = await rejectionOf(transport.getJob(JOB_ID, AbortSignal.abort()));
    expect(error.code).toBe('request_aborted');
    http.expectNone(JOB_URL);
  });

  it('returns the same-origin native download URL for a sealed export', () => {
    expect(transport.getExportDownloadUrl(JOB_ID)).toBe(
      `${MIGRATION_BASE_PATH}/jobs/${JOB_ID}/export-download`,
    );
  });
});
