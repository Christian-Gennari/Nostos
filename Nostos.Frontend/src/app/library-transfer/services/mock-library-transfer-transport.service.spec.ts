import {
  MIGRATION_LIMITS,
  MigrationArchiveCountsDto,
  MigrationPreflightRequestDto,
  MigrationSessionRequestDto,
} from '../models/migration-http.dtos';
import { MigrationTransportError } from './library-transfer-transport';
import { MockLibraryTransferTransport } from './mock-library-transfer-transport.service';
import { sha256ChunkHex } from './hash/chunk-digest';

const CHUNK = 4 * 1024 * 1024;

let keyCounter = 0;
function key(prefix = 'key'): string {
  keyCounter += 1;
  return `${prefix}-${keyCounter}`;
}

function emptyCounts(): MigrationArchiveCountsDto {
  return {
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
  };
}

function preflightRequest(size = 1000): MigrationPreflightRequestDto {
  return {
    incomingCounts: emptyCounts(),
    declaredArchiveBytes: size,
    declaredMediaBytes: 0,
    maxSingleEntryBytes: 0,
    declaredFormatVersion: 1,
    declaredDataVersion: 3,
    declaredFormatName: 'nostos-portable',
    clientDestinationRevision: null,
    isOperationalBackup: false,
  };
}

function fileOfSize(size: number): File {
  return new File([new Uint8Array(size).fill(9) as unknown as BlobPart], 'library.nostos');
}

async function verifiedSessionRequest(
  file: Blob,
  overrides: Partial<MigrationSessionRequestDto> = {},
): Promise<MigrationSessionRequestDto> {
  const identity = {
    totalSizeBytes: file.size,
    sha256Checksum: await sha256ChunkHex(file),
  };
  return sessionRequest(file, { fileIdentity: identity, ...overrides });
}

function sessionRequest(file: Blob, overrides: Partial<MigrationSessionRequestDto> = {}): MigrationSessionRequestDto {
  return {
    purpose: 'Import',
    totalBytes: file.size,
    chunkSize: CHUNK,
    totalChunks: Math.max(1, Math.ceil(file.size / CHUNK)),
    fileIdentity: { totalSizeBytes: file.size, sha256Checksum: 'a'.repeat(64) },
    idempotencyKey: key('session'),
    ...overrides,
  };
}

async function importJob(mock: MockLibraryTransferTransport, archiveBytes = 1000): Promise<string> {
  const preflight = await mock.preflight(preflightRequest(archiveBytes));
  const created = await mock.createJob({
    direction: 'Import',
    idempotencyKey: key('job'),
    reservationId: preflight.reservationId,
  });
  return created.job.id;
}

function expectTypedError(promise: Promise<unknown>, code: string, status: number): Promise<void> {
  return promise.then(
    () => {
      throw new Error('Expected the transport to reject.');
    },
    (error: unknown) => {
      expect(error).toBeInstanceOf(MigrationTransportError);
      expect((error as MigrationTransportError).code).toBe(code);
      expect((error as MigrationTransportError).status).toBe(status);
    },
  );
}

describe('MockLibraryTransferTransport — preflight', () => {
  it('allows an empty destination and issues a reservation', async () => {
    const mock = new MockLibraryTransferTransport();
    const response = await mock.preflight(preflightRequest());

    expect(response.evaluation.decision).toBe('AllowedEmpty');
    expect(response.evaluation.isAllowed).toBe(true);
    expect(response.reservationId).toBeTruthy();
    expect(response.reservationExpiresAtUtc).toBeTruthy();
    expect(response.chunkSizeBytes).toBe(CHUNK * 4);
  });

  it('requires replacement on a populated destination and estimates recovery', async () => {
    const mock = new MockLibraryTransferTransport({
      destinationStatus: 'Populated',
      existingCounts: { books: 2, notes: 3, totalRows: 5 },
    });
    const response = await mock.preflight(preflightRequest());

    expect(response.evaluation.decision).toBe('AllowedReplacementRequired');
    expect(response.evaluation.estimatedRecoveryBytes).toBe(2 * 50_000_000 + 5 * 1024);
    expect(response.evaluation.existingCounts.books).toBe(2);
    expect(response.evaluation.warnings.length).toBeGreaterThan(0);
  });

  it('rejects an operational backup', async () => {
    const mock = new MockLibraryTransferTransport({ operationalBackup: true });
    const response = await mock.preflight(preflightRequest());
    expect(response.evaluation.decision).toBe('RejectedOperationalBackupNotPortable');
    expect(response.reservationId).toBeNull();
  });

  it('rejects unsupported versions as incompatible', async () => {
    const mock = new MockLibraryTransferTransport();
    const response = await mock.preflight(preflightRequest());
    expect(response.evaluation.isCompatible).toBe(true);

    const incompatible = await mock.preflight({
      ...preflightRequest(),
      declaredFormatVersion: 2,
    });
    expect(incompatible.evaluation.decision).toBe('RejectedIncompatible');
    expect(incompatible.evaluation.errors[0]).toContain('format version');
  });

  it('rejects insufficient storage with required and available amounts', async () => {
    const mock = new MockLibraryTransferTransport({ availableStorageBytes: 10 });
    const response = await mock.preflight(preflightRequest(1000));

    expect(response.evaluation.decision).toBe('RejectedInsufficientStorage');
    expect(response.evaluation.requiredStorageBytes).toBe(1000);
    expect(response.evaluation.availableStorageBytes).toBe(10);
    expect(response.reservationId).toBeNull();
  });

  it('rejects a stale destination revision', async () => {
    const mock = new MockLibraryTransferTransport();
    const response = await mock.preflight({
      ...preflightRequest(),
      clientDestinationRevision: 'stale',
    });
    expect(response.evaluation.decision).toBe('RejectedDestinationConflict');
  });
});

describe('MockLibraryTransferTransport — jobs', () => {
  it('replays the original job for the same idempotency key and direction', async () => {
    const mock = new MockLibraryTransferTransport();
    const preflight = await mock.preflight(preflightRequest());
    const request = {
      direction: 'Import' as const,
      idempotencyKey: 'stable-key',
      reservationId: preflight.reservationId,
    };

    const first = await mock.createJob(request);
    const replay = await mock.createJob(request);

    expect(replay.job.id).toBe(first.job.id);
    expect(mock.jobCreationCount).toBe(1);
  });

  it('rejects the same key with a different direction', async () => {
    const mock = new MockLibraryTransferTransport();
    const preflight = await mock.preflight(preflightRequest());
    await mock.createJob({
      direction: 'Import',
      idempotencyKey: 'shared',
      reservationId: preflight.reservationId,
    });

    await expectTypedError(
      mock.createJob({ direction: 'Export', idempotencyKey: 'shared' }),
      'migration_idempotency_conflict',
      409,
    );
  });

  it('requires a valid reservation for an import', async () => {
    const mock = new MockLibraryTransferTransport();
    await expectTypedError(
      mock.createJob({ direction: 'Import', idempotencyKey: key() }),
      'migration_reservation_required',
      409,
    );
    await expectTypedError(
      mock.createJob({ direction: 'Import', idempotencyKey: key(), reservationId: 'unknown' }),
      'migration_reservation_required',
      409,
    );
  });

  it('rejects an empty idempotency key', async () => {
    const mock = new MockLibraryTransferTransport();
    await expectTypedError(
      mock.createJob({ direction: 'Export', idempotencyKey: '   ' }),
      'migration_invalid_request',
      400,
    );
  });

  it('returns 404 for unknown jobs', async () => {
    const mock = new MockLibraryTransferTransport();
    await expectTypedError(mock.getJob('missing'), 'migration_not_found', 404);
  });

  it('cancels active jobs, is idempotent, and refuses terminal/activating jobs', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);

    const cancelled = await mock.cancelJob(jobId);
    expect(cancelled.job.state).toBe('Cancelled');
    const again = await mock.cancelJob(jobId);
    expect(again.job.state).toBe('Cancelled');

    const second = await importJob(mock);
    mock.setJobState(second, 'Activating');
    await expectTypedError(mock.cancelJob(second), 'migration_cannot_cancel', 409);

    const third = await importJob(mock);
    mock.setJobState(third, 'Completed');
    await expectTypedError(mock.cancelJob(third), 'migration_cannot_cancel', 409);
  });

  it('retries only terminal retryable jobs and preserves receipts', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));

    await expectTypedError(mock.retryJob(jobId), 'migration_not_retryable', 409);

    mock.setJobState(jobId, 'Failed');
    const retried = await mock.retryJob(jobId);

    expect(retried.job.state).toBe('Pending');
    expect(retried.session?.sessionId).toBe(session.session.sessionId);
  });

  it('reactivates an expired session on retry while keeping receipts', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    await uploadSingleChunk(mock, jobId, file);
    mock.expireSession(jobId);
    mock.setJobState(jobId, 'Failed');

    const retried = await mock.retryJob(jobId);
    expect(retried.session?.state).toBe('Created');
    expect(retried.session?.receivedChunkCount).toBe(1);
  });
});

async function uploadSingleChunk(
  mock: MockLibraryTransferTransport,
  jobId: string,
  file: Blob,
): Promise<void> {
  const session = await mock.createUploadSession(jobId, await verifiedSessionRequest(file));
  const blob = file.slice(0, file.size);
  await mock.uploadChunk(
    jobId,
    session.session.sessionId,
    {
      index: 0,
      offsetBytes: 0,
      lengthBytes: file.size,
      sha256: await sha256ChunkHex(blob),
      blob,
    },
    () => undefined,
    new AbortController().signal,
  );
}

describe('MockLibraryTransferTransport — upload sessions', () => {
  it('validates chunk size, chunk count and identity size', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);

    await expectTypedError(
      mock.createUploadSession(jobId, sessionRequest(file, { chunkSize: 1024 })),
      'migration_invalid_request',
      400,
    );
    await expectTypedError(
      mock.createUploadSession(jobId, sessionRequest(file, { totalChunks: 7 })),
      'migration_invalid_request',
      400,
    );
    await expectTypedError(
      mock.createUploadSession(
        jobId,
        sessionRequest(file, {
          fileIdentity: { totalSizeBytes: 999, sha256Checksum: 'a'.repeat(64) },
        }),
      ),
      'migration_file_identity_mismatch',
      409,
    );
    await expectTypedError(
      mock.createUploadSession(
        jobId,
        sessionRequest(file, {
          fileIdentity: { totalSizeBytes: file.size, sha256Checksum: 'not-hex' },
        }),
      ),
      'migration_invalid_request',
      400,
    );
  });

  it('rejects an export-purpose session on an import job', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    await expectTypedError(
      mock.createUploadSession(jobId, sessionRequest(file, { purpose: 'Export' })),
      'migration_invalid_state',
      409,
    );
  });

  it('replays an identical session and rejects key reuse with a different payload', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const request = sessionRequest(file, { idempotencyKey: 'session-key' });

    const first = await mock.createUploadSession(jobId, request);
    const replay = await mock.createUploadSession(jobId, request);
    expect(replay.session.sessionId).toBe(first.session.sessionId);

    await expectTypedError(
      mock.createUploadSession(jobId, {
        ...request,
        totalBytes: 200,
        totalChunks: 1,
        fileIdentity: { totalSizeBytes: 200, sha256Checksum: 'b'.repeat(64) },
      }),
      'migration_idempotency_conflict',
      409,
    );
  });

  it('rejects a different file against an existing session with a new key', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    await mock.createUploadSession(jobId, sessionRequest(fileOfSize(100)));

    await expectTypedError(
      mock.createUploadSession(
        jobId,
        sessionRequest(fileOfSize(100), {
          fileIdentity: { totalSizeBytes: 100, sha256Checksum: 'c'.repeat(64) },
        }),
      ),
      'migration_file_identity_mismatch',
      409,
    );
  });

  it('reports authoritative received ranges and refuses expired sessions', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    await mock.seedReceivedChunks(jobId, [0]);

    const fetched = await mock.getUploadSession(jobId);
    expect(fetched.session.receivedChunkCount).toBe(1);
    expect(fetched.receivedRanges).toEqual([{ startIndex: 0, endIndex: 0 }]);
    expect(fetched.session.sessionId).toBe(session.session.sessionId);

    mock.expireSession(jobId);
    await expectTypedError(mock.getUploadSession(jobId), 'migration_session_expired', 410);
  });
});

describe('MockLibraryTransferTransport — chunks', () => {
  it('validates the exact chunk range rules', async () => {
    const mock = new MockLibraryTransferTransport();
    const totalBytes = CHUNK + 100;
    const jobId = await importJob(mock, totalBytes);
    const file = fileOfSize(totalBytes);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    const signal = new AbortController().signal;
    const blob = file.slice(0, CHUNK);
    const sha256 = await sha256ChunkHex(blob);

    await expectTypedError(
      mock.uploadChunk(
        jobId,
        session.session.sessionId,
        { index: 5, offsetBytes: 5 * CHUNK, lengthBytes: CHUNK, sha256, blob },
        () => undefined,
        signal,
      ),
      'migration_chunk_range_invalid',
      416,
    );
    await expectTypedError(
      mock.uploadChunk(
        jobId,
        session.session.sessionId,
        { index: 0, offsetBytes: 10, lengthBytes: CHUNK, sha256, blob },
        () => undefined,
        signal,
      ),
      'migration_chunk_range_invalid',
      416,
    );
    await expectTypedError(
      mock.uploadChunk(
        jobId,
        session.session.sessionId,
        { index: 0, offsetBytes: 0, lengthBytes: CHUNK - 1, sha256, blob: file.slice(0, CHUNK - 1) },
        () => undefined,
        signal,
      ),
      'migration_chunk_range_invalid',
      416,
    );
    await expectTypedError(
      mock.uploadChunk(
        jobId,
        session.session.sessionId,
        { index: 0, offsetBytes: 0, lengthBytes: 0, sha256, blob: new Blob([]) },
        () => undefined,
        signal,
      ),
      'migration_chunk_range_invalid',
      416,
    );
  });

  it('accepts a short whole-file final chunk', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    const blob = file.slice(0, 100);

    const result = await mock.uploadChunk(
      jobId,
      session.session.sessionId,
      { index: 0, offsetBytes: 0, lengthBytes: 100, sha256: await sha256ChunkHex(blob), blob },
      () => undefined,
      new AbortController().signal,
    );

    expect(result).toEqual({
      sessionId: session.session.sessionId,
      chunkIndex: 0,
      alreadyPresent: false,
    });
  });

  it('rejects a checksum mismatch with 422', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));

    await expectTypedError(
      mock.uploadChunk(
        jobId,
        session.session.sessionId,
        { index: 0, offsetBytes: 0, lengthBytes: 100, sha256: '0'.repeat(64), blob: file },
        () => undefined,
        new AbortController().signal,
      ),
      'migration_chunk_hash_mismatch',
      422,
    );
  });

  it('is idempotent for an identical re-send and conflicts on different bytes', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    const blob = file.slice(0, 100);
    const chunk = {
      index: 0,
      offsetBytes: 0,
      lengthBytes: 100,
      sha256: await sha256ChunkHex(blob),
      blob,
    };

    const first = await mock.uploadChunk(
      jobId,
      session.session.sessionId,
      chunk,
      () => undefined,
      new AbortController().signal,
    );
    const duplicate = await mock.uploadChunk(
      jobId,
      session.session.sessionId,
      chunk,
      () => undefined,
      new AbortController().signal,
    );
    expect(first.alreadyPresent).toBe(false);
    expect(duplicate.alreadyPresent).toBe(true);

    const differentBlob = new Blob([new Uint8Array(100).fill(5) as unknown as BlobPart]);
    await expectTypedError(
      mock.uploadChunk(
        jobId,
        session.session.sessionId,
        {
          index: 0,
          offsetBytes: 0,
          lengthBytes: 100,
          sha256: await sha256ChunkHex(differentBlob),
          blob: differentBlob,
        },
        () => undefined,
        new AbortController().signal,
      ),
      'migration_chunk_conflict',
      409,
    );
  });

  it('reports upload progress including intermediate steps', async () => {
    const mock = new MockLibraryTransferTransport({ progressSteps: 4 });
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    const progress: number[] = [];

    await mock.uploadChunk(
      jobId,
      session.session.sessionId,
      { index: 0, offsetBytes: 0, lengthBytes: 100, sha256: await sha256ChunkHex(file), blob: file },
      (loaded) => progress.push(loaded),
      new AbortController().signal,
    );

    expect(progress).toEqual([25, 50, 75, 100]);
  });

  it('throws queued transient failures once and then succeeds', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    mock.queueFailure({ operation: 'uploadChunk', status: 503, times: 1 });

    const blob = file.slice(0, 100);
    const chunk = { index: 0, offsetBytes: 0, lengthBytes: 100, sha256: await sha256ChunkHex(blob), blob };

    await expectTypedError(
      mock.uploadChunk(jobId, session.session.sessionId, chunk, () => undefined, new AbortController().signal),
      'network_error',
      503,
    );
    const retry = await mock.uploadChunk(
      jobId,
      session.session.sessionId,
      chunk,
      () => undefined,
      new AbortController().signal,
    );
    expect(retry.alreadyPresent).toBe(false);
  });

  it('passes Retry-After through the typed error', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    mock.queueFailure({ operation: 'uploadChunk', status: 429, retryAfterMs: 3000 });

    const error = await mock
      .uploadChunk(
        jobId,
        session.session.sessionId,
        { index: 0, offsetBytes: 0, lengthBytes: 100, sha256: 'a'.repeat(64), blob: file },
        () => undefined,
        new AbortController().signal,
      )
      .catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(MigrationTransportError);
    expect((error as MigrationTransportError).retryAfterMs).toBe(3000);
    expect((error as MigrationTransportError).retryable).toBe(true);
  });

  it('aborts in-flight latency when the signal fires', async () => {
    const mock = new MockLibraryTransferTransport({ latencyMs: 30 });
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    const controller = new AbortController();

    const promise = mock.uploadChunk(
      jobId,
      session.session.sessionId,
      { index: 0, offsetBytes: 0, lengthBytes: 100, sha256: 'a'.repeat(64), blob: file },
      () => undefined,
      controller.signal,
    );
    controller.abort();

    await expect(promise).rejects.toMatchObject({ code: 'request_aborted', status: 0 });
  });
});

describe('MockLibraryTransferTransport — completion and export', () => {
  it('refuses completion until every chunk receipt exists', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    await mock.createUploadSession(jobId, sessionRequest(file));

    await expectTypedError(mock.completeUpload(jobId), 'migration_invalid_state', 409);
  });

  it('completes an import upload and moves the durable job to ReadyToActivate', async () => {
    const mock = new MockLibraryTransferTransport();
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    await uploadSingleChunk(mock, jobId, file);

    const completed = await mock.completeUpload(jobId);
    expect(completed.state).toBe('Complete');

    const status = await mock.getJob(jobId);
    expect(status.job.state).toBe('ReadyToActivate');
    expect(status.progress.phase).toBe('Validating');
  });

  it('exposes the native export URL only when the artifact is available', async () => {
    const mock = new MockLibraryTransferTransport();
    const created = await mock.createJob({ direction: 'Export', idempotencyKey: key('export') });
    const jobId = created.job.id;

    await expectTypedError(
      Promise.resolve().then(() => mock.getExportDownloadUrl(jobId)),
      'migration_export_not_available',
      404,
    );

    mock.markExportReady(jobId);
    expect(mock.getExportDownloadUrl(jobId)).toBe(
      `/api/portability/migration/jobs/${jobId}/export-download`,
    );
  });

  it('verifies the whole-file SHA-256 across multiple chunks at completion', async () => {
    const mock = new MockLibraryTransferTransport();
    const file = fileOfSize(CHUNK + 100);
    const jobId = await importJob(mock, file.size);
    const session = await mock.createUploadSession(jobId, await verifiedSessionRequest(file));
    const signal = new AbortController().signal;

    for (const [index, start] of [
      [0, 0],
      [1, CHUNK],
    ] as const) {
      const blob = file.slice(start, start + (index === 0 ? CHUNK : 100));
      await mock.uploadChunk(
        jobId,
        session.session.sessionId,
        {
          index,
          offsetBytes: start,
          lengthBytes: blob.size,
          sha256: await sha256ChunkHex(blob),
          blob,
        },
        () => undefined,
        signal,
      );
    }

    const completed = await mock.completeUpload(jobId);
    expect(completed.state).toBe('Complete');
    expect(completed.receivedChunkCount).toBe(2);
  });

  it('rejects completion when the full identity hash differs despite valid chunk hashes', async () => {
    const mock = new MockLibraryTransferTransport();
    const file = fileOfSize(100);
    const jobId = await importJob(mock);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    const blob = file.slice(0, 100);

    await mock.uploadChunk(
      jobId,
      session.session.sessionId,
      { index: 0, offsetBytes: 0, lengthBytes: 100, sha256: await sha256ChunkHex(blob), blob },
      () => undefined,
      new AbortController().signal,
    );

    await expectTypedError(
      mock.completeUpload(jobId),
      'migration_file_identity_mismatch',
      409,
    );
    expect((await mock.getJob(jobId)).session?.state).toBe('Receiving');
  });

  it('rejects the same session key reused with a different chunk sizing', async () => {
    const mock = new MockLibraryTransferTransport();
    const file = fileOfSize(CHUNK + 100);
    const jobId = await importJob(mock, file.size);
    const first = sessionRequest(file, { idempotencyKey: 'sizing-key' });
    await mock.createUploadSession(jobId, first);

    await expectTypedError(
      mock.createUploadSession(jobId, {
        ...first,
        chunkSize: 8 * 1024 * 1024,
        totalChunks: 1,
      }),
      'migration_idempotency_conflict',
      409,
    );
  });

  it('rejects a new session key whose chunking differs from the existing session', async () => {
    const mock = new MockLibraryTransferTransport();
    const file = fileOfSize(CHUNK + 100);
    const jobId = await importJob(mock, file.size);
    await mock.createUploadSession(jobId, sessionRequest(file));

    await expectTypedError(
      mock.createUploadSession(
        jobId,
        sessionRequest(file, {
          chunkSize: 8 * 1024 * 1024,
          totalChunks: 1,
        }),
      ),
      'migration_file_identity_mismatch',
      409,
    );
  });

  it('rejects the same job key reused with a different reservation payload', async () => {
    const mock = new MockLibraryTransferTransport();
    const firstReservation = await mock.preflight(preflightRequest());
    const secondReservation = await mock.preflight(preflightRequest());
    const request = {
      direction: 'Import' as const,
      idempotencyKey: 'job-payload-key',
      reservationId: firstReservation.reservationId,
    };
    await mock.createJob(request);

    await expectTypedError(
      mock.createJob({ ...request, reservationId: secondReservation.reservationId }),
      'migration_idempotency_conflict',
      409,
    );
  });

  it('rejects negative and out-of-range chunk indexes', async () => {
    const mock = new MockLibraryTransferTransport();
    const totalBytes = CHUNK + 100;
    const file = fileOfSize(totalBytes);
    const jobId = await importJob(mock, totalBytes);
    const session = await mock.createUploadSession(jobId, sessionRequest(file));
    const signal = new AbortController().signal;

    await expectTypedError(
      mock.uploadChunk(
        jobId,
        session.session.sessionId,
        { index: -1, offsetBytes: -CHUNK, lengthBytes: CHUNK, sha256: 'a'.repeat(64), blob: file.slice(0, CHUNK) },
        () => undefined,
        signal,
      ),
      'migration_chunk_range_invalid',
      416,
    );
    await expectTypedError(
      mock.uploadChunk(
        jobId,
        session.session.sessionId,
        { index: 2, offsetBytes: 2 * CHUNK, lengthBytes: 100, sha256: 'a'.repeat(64), blob: file.slice(CHUNK + 100) },
        () => undefined,
        signal,
      ),
      'migration_chunk_range_invalid',
      416,
    );
  });

  it('refuses a configured chunk size above the contract maximum', () => {
    expect(
      () => new MockLibraryTransferTransport({ chunkSizeBytes: MIGRATION_LIMITS.maxChunkBytes + 1 }),
    ).toThrow(/outside the contract bounds/);
    expect(
      () => new MockLibraryTransferTransport({ chunkSizeBytes: MIGRATION_LIMITS.minChunkBytes - 1 }),
    ).toThrow(/outside the contract bounds/);
  });

  it('keeps a validation window before ReadyToActivate when configured', async () => {
    const mock = new MockLibraryTransferTransport({ validationPolls: 2 });
    const jobId = await importJob(mock);
    const file = fileOfSize(100);
    await uploadSingleChunk(mock, jobId, file);
    await mock.completeUpload(jobId);

    expect((await mock.getJob(jobId)).job.state).toBe('Validating');
    expect((await mock.getJob(jobId)).job.state).toBe('ReadyToActivate');
    expect((await mock.getJob(jobId)).job.state).toBe('ReadyToActivate');
    expect(mock.calls.getJob).toBe(3);
  });
});
