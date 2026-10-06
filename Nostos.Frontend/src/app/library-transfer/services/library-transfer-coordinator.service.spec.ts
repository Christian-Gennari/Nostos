import { TestBed } from '@angular/core/testing';

import { FileDigestService } from './file-digest.service';
import { HASH_WORKER_FACTORY } from './hash/hash-worker';
import {
  MockLibraryTransferTransport,
  MockLibraryTransferTransportOptions,
} from '../testing/mock-library-transfer-transport';
import {
  LibraryTransferTransport,
  LIBRARY_TRANSFER_TRANSPORT,
  MigrationTransportError,
} from './library-transfer-transport';
import { LibraryTransferCoordinator } from './library-transfer-coordinator.service';
import { ChunkUploadEngine } from './chunk-upload-engine.service';
import { TransferResumeStore, TRANSFER_RESUME_STORAGE_KEY } from './transfer-resume-store.service';
import {
  MigrationActivateRequestDto,
  MigrationActivationStatusDto,
  MigrationArchiveCountsDto,
  MigrationCreateJobRequestDto,
  MigrationJobStatusResponseDto,
  MigrationPreflightDecision,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationSessionRequestDto,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
  chunkCount,
} from '../models/migration-http.dtos';
import {
  PersistedTransferResumeState,
  LibraryTransferMode,
  TransferFileIdentity,
} from '../models/library-transfer.models';
import { DelegatingTransport } from '../testing/delegating-transport';
import {
  buildZipArchive,
  createFile,
  portableArchiveFixture,
  portableManifest,
} from '../testing/zip-archive.fixture';

const CHUNK = 4 * 1024 * 1024;
const encoder = new TextEncoder();

interface Harness {
  mock: MockLibraryTransferTransport;
  coordinator: LibraryTransferCoordinator;
  digest: FileDigestService;
  store: TransferResumeStore;
}

function configure(mock: MockLibraryTransferTransport, transport: LibraryTransferTransport): Harness {
  TestBed.configureTestingModule({
    providers: [
      { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport },
      { provide: HASH_WORKER_FACTORY, useValue: () => null },
    ],
  });
  return {
    mock,
    coordinator: TestBed.inject(LibraryTransferCoordinator),
    digest: TestBed.inject(FileDigestService),
    store: TestBed.inject(TransferResumeStore),
  };
}

function setup(options: MockLibraryTransferTransportOptions = {}): Harness {
  const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK, ...options });
  return configure(mock, mock);
}

/** Simulated reload: same durable mock/transport, a fresh TestBed and coordinator. */
function reload(mock: MockLibraryTransferTransport, transport: LibraryTransferTransport): Harness {
  TestBed.resetTestingModule();
  return configure(mock, transport);
}

function reset(): void {
  TestBed.resetTestingModule();
  localStorage.removeItem(TRANSFER_RESUME_STORAGE_KEY);
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

function preflightRequest(archiveBytes: number): MigrationPreflightRequestDto {
  return {
    incomingCounts: emptyCounts(),
    declaredArchiveBytes: archiveBytes,
    declaredMediaBytes: 0,
    maxSingleEntryBytes: 0,
    declaredFormatVersion: 1,
    declaredDataVersion: 3,
    declaredFormatName: 'nostos-portable',
    clientDestinationRevision: null,
    isOperationalBackup: false,
  };
}

async function portableFile(overrides: Record<string, unknown> = {}): Promise<File> {
  return createFile(await portableArchiveFixture(portableManifest(overrides)));
}

async function largePortableFile(): Promise<File> {
  const media = new Uint8Array(8 * 1024 * 1024);
  for (let index = 0; index < media.length; index += 1) media[index] = index % 251;
  const archive = await buildZipArchive([
    {
      name: 'manifest.json',
      data: encoder.encode(
        JSON.stringify(
          portableManifest({
            media: [
              {
                bookId: '11111111-1111-4111-8111-111111111111',
                kind: 'book',
                path: 'media/one',
                fileName: 'one.epub',
                contentType: 'application/epub+zip',
                length: media.length,
                sha256: 'b'.repeat(64),
              },
            ],
          }),
        ),
      ),
    },
    { name: 'media/one', data: media },
  ]);
  return createFile(archive);
}

async function identityFor(harness: Harness, file: File): Promise<TransferFileIdentity> {
  return {
    totalSizeBytes: file.size,
    sha256Checksum: await harness.digest.sha256(file),
    clientFingerprint: await harness.digest.fingerprint(file),
  };
}

function networkFailure(): MigrationTransportError {
  return new MigrationTransportError('network_error', 0, 'connection lost');
}

/**
 * A transport wrapper that simulates responses lost after the server has
 * created state, and GETs that resolve after a later operation has won.
 */
class FlakyTransport implements LibraryTransferTransport {
  loseJobCreateResponse = false;
  failSessionCreateBeforeSend = false;
  loseSessionCreateResponse = false;

  private deferNextGetJob = false;
  private pendingGetJob:
    | { resolve: (status: MigrationJobStatusResponseDto) => void; reject: (error: unknown) => void }
    | null = null;

  constructor(private readonly inner: MockLibraryTransferTransport) {}

  preflight(
    request: MigrationPreflightRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationPreflightResponseDto> {
    return this.inner.preflight(request, signal);
  }

  async createJob(
    request: MigrationCreateJobRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    if (!this.loseJobCreateResponse) return this.inner.createJob(request, signal);
    // The server creates the job; the response never reaches the browser.
    await this.inner.createJob(request, signal);
    throw networkFailure();
  }

  getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto> {
    if (this.deferNextGetJob) {
      this.deferNextGetJob = false;
      return new Promise<MigrationJobStatusResponseDto>((resolve, reject) => {
        this.pendingGetJob = { resolve, reject };
      });
    }
    return this.inner.getJob(jobId, signal);
  }

  deferStatusResponse(): void {
    this.deferNextGetJob = true;
  }

  resolveDeferredStatus(status: MigrationJobStatusResponseDto): void {
    this.pendingGetJob?.resolve(status);
    this.pendingGetJob = null;
  }

  cancelJob(
    jobId: string,
    reason?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return this.inner.cancelJob(jobId, reason, signal);
  }

  retryJob(
    jobId: string,
    idempotencyKey?: string,
    signal?: AbortSignal,
  ): Promise<MigrationJobStatusResponseDto> {
    return this.inner.retryJob(jobId, idempotencyKey, signal);
  }

  async createUploadSession(
    jobId: string,
    request: MigrationSessionRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    if (this.failSessionCreateBeforeSend) throw networkFailure();
    const response = await this.inner.createUploadSession(jobId, request, signal);
    if (this.loseSessionCreateResponse) throw networkFailure();
    return response;
  }

  getUploadSession(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationUploadSessionResponseDto> {
    return this.inner.getUploadSession(jobId, signal);
  }

  uploadChunk(
    jobId: string,
    sessionId: string,
    request: Parameters<LibraryTransferTransport['uploadChunk']>[2],
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ) {
    return this.inner.uploadChunk(jobId, sessionId, request, onProgress, signal);
  }

  completeUpload(jobId: string, signal?: AbortSignal): Promise<MigrationSessionStatusDto> {
    return this.inner.completeUpload(jobId, signal);
  }

  activateJob(
    jobId: string,
    request: MigrationActivateRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationActivationStatusDto> {
    return this.inner.activateJob(jobId, request, signal);
  }

  getActivationStatus(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationActivationStatusDto> {
    return this.inner.getActivationStatus(jobId, signal);
  }

  getExportDownloadUrl(jobId: string): string {
    return this.inner.getExportDownloadUrl(jobId);
  }
}

async function stageResumableJob(
  harness: Harness,
  file: File,
  missingStart: number,
): Promise<PersistedTransferResumeState> {
  const preflight = await harness.mock.preflight(preflightRequest(file.size));
  const jobKey = `stage-job-${missingStart}-${Date.now()}`;
  const job = await harness.mock.createJob({
    direction: 'Import',
    idempotencyKey: jobKey,
    reservationId: preflight.reservationId,
  });
  const totalChunks = chunkCount(file.size, CHUNK);
  const identity: TransferFileIdentity = await identityFor(harness, file);
  const sessionKey = `stage-session-${missingStart}-${Date.now()}`;
  const sessionRequest: MigrationSessionRequestDto = {
    purpose: 'Import',
    totalBytes: file.size,
    chunkSize: CHUNK,
    totalChunks,
    fileIdentity: identity,
    idempotencyKey: sessionKey,
  };
  const session = await harness.mock.createUploadSession(job.job.id, sessionRequest);
  await harness.mock.seedReceivedChunks(
    job.job.id,
    Array.from({ length: missingStart }, (_, index) => index),
    file,
  );

  const record: PersistedTransferResumeState = {
    schemaVersion: 1,
    jobId: job.job.id,
    jobCreationIdempotencyKey: jobKey,
    reservationId: preflight.reservationId,
    sessionId: session.session.sessionId,
    sessionCreationIdempotencyKey: sessionKey,
    sessionRequest,
    chunkSizeBytes: CHUNK,
    direction: 'import',
    fileIdentity: identity,
    fileName: file.name,
    preflightRequest: preflightRequest(file.size),
    preflightDecision: 'AllowedEmpty' satisfies MigrationPreflightDecision,
    createdAt: new Date().toISOString(),
  };
  harness.store.save(record);
  return record;
}

describe('LibraryTransferCoordinator — fresh import', () => {
  afterEach(reset);

  it('runs the happy path to ready-empty and persists the resume record', async () => {
    const harness = setup();
    const file = await portableFile();

    await harness.coordinator.startImport(file);

    const state = harness.coordinator.state();
    expect(state.kind).toBe('ready-empty');
    const record = harness.store.load();
    expect(record?.jobId).toBeTruthy();
    expect(record?.sessionId).toBeTruthy();
    expect(record?.jobCreationIdempotencyKey).toBeTruthy();
    expect(record?.sessionCreationIdempotencyKey).toBeTruthy();
    expect(record?.sessionRequest?.idempotencyKey).toBe(record?.sessionCreationIdempotencyKey);
    expect(record?.chunkSizeBytes).toBe(CHUNK);
    expect(record?.fileIdentity.totalSizeBytes).toBe(file.size);
    expect(record?.preflightDecision).toBe('AllowedEmpty');
  });

  it('requires one replacement confirmation for a populated destination', async () => {
    const harness = setup({ destinationStatus: 'Populated', existingCounts: { books: 1 } });
    const file = await portableFile();

    await harness.coordinator.startImport(file);
    expect(harness.coordinator.state().kind).toBe('replacement-confirmation');
  });

  it('fails early for a random file without uploading anything', async () => {
    const harness = setup();
    const random = new Uint8Array(4096).map((_, index) => index % 256);

    await harness.coordinator.startImport(createFile(random));

    expect(harness.coordinator.state()).toMatchObject({
      kind: 'failed',
      failure: { code: 'archive_not_portable' },
    });
    expect(harness.mock.calls.uploadChunk).toBe(0);
    expect(harness.store.load()).toBeNull();
  });

  it('fails early for a local operational backup', async () => {
    const harness = setup();
    const archive = await buildZipArchive([
      {
        name: 'manifest.json',
        data: encoder.encode(
          JSON.stringify({
            version: '1',
            timestamp: 'x',
            databaseSizeBytes: 1,
            checksum: 'a'.repeat(64),
          }),
        ),
      },
    ]);

    await harness.coordinator.startImport(createFile(archive));
    expect(harness.coordinator.state()).toMatchObject({
      kind: 'failed',
      failure: { code: 'archive_operational_backup' },
    });
  });

  it('fails with the storage code when preflight rejects capacity', async () => {
    const harness = setup({ availableStorageBytes: 10 });
    const file = await portableFile();

    await harness.coordinator.startImport(file);
    expect(harness.coordinator.state()).toMatchObject({
      kind: 'failed',
      failure: { code: 'migration_storage_exhausted' },
    });
  });

  it('cancels mid-upload and records durable cancellation', async () => {
    const harness = setup({ latencyMs: 100 });
    const file = await largePortableFile();

    const importing = harness.coordinator.startImport(file);
    await vi.waitFor(() => expect(harness.coordinator.state().kind).toBe('uploading'), {
      timeout: 5_000,
    });
    await harness.coordinator.cancel();
    await importing;

    const state = harness.coordinator.state();
    expect(state.kind).toBe('cancelled');
    const jobId = state.kind === 'cancelled' ? state.jobId : '';
    expect(jobId).toBeTruthy();
    expect((await harness.mock.getJob(jobId)).job.state).toBe('Cancelled');
  });

  it('pauses and resumes an upload through the coordinator', async () => {
    const harness = setup({ latencyMs: 100 });
    const file = await largePortableFile();

    const importing = harness.coordinator.startImport(file);
    await vi.waitFor(() => expect(harness.coordinator.state().kind).toBe('uploading'), {
      timeout: 5_000,
    });

    await harness.coordinator.pauseUpload();
    expect(harness.coordinator.state()).toMatchObject({ kind: 'uploading', paused: true });
    const pausedChunks = harness.mock.uploadedChunks.length;

    await harness.coordinator.resumeUpload();
    await importing;

    expect(harness.coordinator.state().kind).toBe('ready-empty');
    expect(harness.mock.uploadedChunks.length).toBeGreaterThan(pausedChunks);
  });
});

describe('LibraryTransferCoordinator — crash-safe creation', () => {
  afterEach(reset);

  it('persists the job key before createJob and replays it when the response is lost', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const flaky = new FlakyTransport(mock);
    const harness = configure(mock, flaky);
    flaky.loseJobCreateResponse = true;
    const file = await portableFile();

    await harness.coordinator.startImport(file);

    expect(harness.coordinator.state().kind).toBe('failed');
    const provisional = harness.store.load();
    expect(provisional?.jobId).toBeUndefined();
    expect(provisional?.jobCreationIdempotencyKey).toBeTruthy();
    expect(provisional?.sessionRequest?.idempotencyKey).toBe(
      provisional?.sessionCreationIdempotencyKey,
    );

    // Reload: the persisted key must replay the job the server already created.
    flaky.loseJobCreateResponse = false;
    const reloaded = reload(mock, flaky);
    await reloaded.coordinator.resume();

    expect(mock.jobCreationCount).toBe(1);
    expect(reloaded.coordinator.state().kind).toBe('ready-to-upload');

    await reloaded.coordinator.resumeWithFile(file);
    expect(reloaded.coordinator.state().kind).toBe('ready-empty');
    expect(mock.uploadedChunks.length).toBeGreaterThan(0);
  });

  it('creates a missing upload session on reload with the persisted key', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const flaky = new FlakyTransport(mock);
    const harness = configure(mock, flaky);
    flaky.failSessionCreateBeforeSend = true;
    const file = await portableFile();

    await harness.coordinator.startImport(file);

    expect(harness.coordinator.state().kind).toBe('failed');
    expect(mock.calls.createUploadSession).toBe(0);
    const record = harness.store.load();
    expect(record?.jobId).toBeTruthy();
    expect(record?.sessionId).toBeUndefined();
    expect(record?.sessionCreationIdempotencyKey).toBeTruthy();

    flaky.failSessionCreateBeforeSend = false;
    const reloaded = reload(mock, flaky);
    await reloaded.coordinator.resume();

    expect(mock.calls.createUploadSession).toBe(1);
    expect(reloaded.store.load()?.sessionId).toBeTruthy();
    expect(reloaded.coordinator.state().kind).toBe('ready-to-upload');

    await reloaded.coordinator.resumeWithFile(file);
    expect(reloaded.coordinator.state().kind).toBe('ready-empty');
  });

  it('replays the same session key when the session response was lost', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const flaky = new FlakyTransport(mock);
    const harness = configure(mock, flaky);
    flaky.loseSessionCreateResponse = true;
    const file = await portableFile();

    await harness.coordinator.startImport(file);

    expect(harness.coordinator.state().kind).toBe('failed');
    const jobId = harness.store.load()?.jobId ?? '';
    expect(jobId).toBeTruthy();
    const serverSessionId = (await mock.getUploadSession(jobId)).session.sessionId;
    const record = harness.store.load();
    expect(record?.sessionId).toBeUndefined();
    expect(record?.sessionCreationIdempotencyKey).toBeTruthy();

    flaky.loseSessionCreateResponse = false;
    const reloaded = reload(mock, flaky);
    await reloaded.coordinator.resume();
    await reloaded.coordinator.resumeWithFile(file);

    expect(mock.calls.createUploadSession).toBe(2);
    expect(reloaded.store.load()?.sessionId).toBe(serverSessionId);
    expect(reloaded.coordinator.state().kind).toBe('ready-empty');
  });
});

describe('LibraryTransferCoordinator — reattach and resume', () => {
  afterEach(reset);

  it('resumes a saved job on file selection without another preflight or job', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const total = chunkCount(file.size, CHUNK);
    const record = await stageResumableJob(harness, file, total - 1);
    const preflights = harness.mock.calls.preflight;
    const jobs = harness.mock.jobCreationCount;

    await harness.coordinator.startImport(file);

    expect(harness.coordinator.state().kind).toBe('ready-empty');
    expect(harness.store.load()?.jobId).toBe(record.jobId);
    expect(harness.mock.calls.preflight).toBe(preflights);
    expect(harness.mock.jobCreationCount).toBe(jobs);
    expect(harness.mock.uploadedChunks).toEqual([total - 1]);
  });

  it('retries an exhausted browser upload using the same session and missing part', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const total = chunkCount(file.size, CHUNK);
    const record = await stageResumableJob(harness, file, total - 1);
    await harness.coordinator.resume();
    harness.mock.queueFailure({ operation: 'uploadChunk', chunkIndex: total - 1,
      code: 'unexpected_error', status: 503 });
    const start = ChunkUploadEngine.prototype.start;
    const limited = vi.spyOn(ChunkUploadEngine.prototype, 'start')
      .mockImplementationOnce(function (this: ChunkUploadEngine, request) {
        return start.call(this, { ...request, maxAttempts: 1 });
      });
    try {
      await harness.coordinator.resumeWithFile(file);
      expect(harness.coordinator.state()).toMatchObject({
        kind: 'failed', failure: { status: 503, retryable: true },
      });
      const sessionId = harness.store.load()?.sessionId;

      await harness.coordinator.retry();

      expect(harness.coordinator.state().kind).toBe('ready-empty');
      expect(harness.mock.calls.retryJob).toBe(0);
      expect(harness.mock.calls.createUploadSession).toBe(1);
      expect(harness.store.load()?.jobId).toBe(record.jobId);
      expect(harness.store.load()?.sessionId).toBe(sessionId);
      expect(harness.mock.uploadedChunks).toEqual([total - 1]);
    } finally {
      limited.mockRestore();
    }
  });

  it('replays an interrupted completion instead of retrying an active job', async () => {
    const harness = setup();
    const file = await portableFile();
    harness.mock.queueFailure({ operation: 'completeUpload', code: 'network_error', status: 0 });
    await harness.coordinator.startImport(file);
    expect(harness.coordinator.state().kind).toBe('failed');
    const sessionId = harness.store.load()?.sessionId;
    const uploaded = [...harness.mock.uploadedChunks];

    await harness.coordinator.retry();

    expect(harness.coordinator.state().kind).toBe('ready-empty');
    expect(harness.mock.calls.retryJob).toBe(0);
    expect(harness.mock.uploadedChunks).toEqual(uploaded);
    expect(harness.store.load()?.sessionId).toBe(sessionId);
  });

  it('continues completion after reload without a file when every part is received', async () => {
    const harness = setup();
    const file = await portableFile();
    const record = await stageResumableJob(harness, file, chunkCount(file.size, CHUNK));
    const status = await harness.mock.getJob(record.jobId!);
    vi.spyOn(harness.mock, 'getJob').mockResolvedValueOnce({
      ...status, session: { ...status.session!, state: 'Receiving' },
    });
    const reloaded = reload(harness.mock, harness.mock);
    const hash = vi.spyOn(reloaded.digest, 'sha256');
    let finish!: () => void;
    const gate = new Promise<void>(resolve => { finish = resolve; });
    const complete = harness.mock.completeUpload.bind(harness.mock);
    const completion = vi.spyOn(harness.mock, 'completeUpload').mockImplementationOnce(
      async (jobId, signal) => { await gate; return complete(jobId, signal); },
    );

    const resumed = reloaded.coordinator.resume();
    await vi.waitFor(() => expect(completion).toHaveBeenCalledTimes(1));
    expect(reloaded.coordinator.state()).toMatchObject({
      kind: 'checking', jobId: record.jobId,
      progress: { completedChunks: status.session!.totalChunks, uploadedBytes: file.size },
    });
    finish();
    await resumed;

    expect(reloaded.coordinator.state().kind).toBe('ready-empty');
    expect(hash).not.toHaveBeenCalled();
    expect(harness.mock.uploadedChunks).toEqual([]);
    expect(harness.mock.calls.retryJob).toBe(0);
    expect(harness.mock.calls.preflight).toBe(1);
    expect(harness.mock.jobCreationCount).toBe(1);
  });

  it('keeps polling processing when the server has all parts but verification is pending', async () => {
    const harness = setup();
    const file = await portableFile();
    const record = await stageResumableJob(harness, file, chunkCount(file.size, CHUNK));
    const status = await harness.mock.getJob(record.jobId!);
    vi.spyOn(harness.mock, 'getJob').mockResolvedValueOnce({
      ...status, session: { ...status.session!, state: 'Receiving' },
    });

    await harness.coordinator.refreshStatus();

    expect(harness.coordinator.state()).toMatchObject({ kind: 'checking', jobId: record.jobId });
    expect(harness.mock.uploadedChunks).toEqual([]);
  });

  it('reattaches after a reload and uploads only missing chunks', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const totalChunks = chunkCount(file.size, CHUNK);
    expect(totalChunks).toBeGreaterThan(1);
    const staged = await stageResumableJob(harness, file, totalChunks - 1);

    const reloaded = reload(harness.mock, harness.mock);
    await reloaded.coordinator.resume();
    expect(reloaded.coordinator.state()).toMatchObject({
      kind: 'ready-to-upload',
      jobId: staged.jobId,
      reselectionRequired: true,
    });

    await reloaded.coordinator.resumeWithFile(file);
    expect(reloaded.coordinator.state().kind).toBe('ready-empty');
    expect(harness.mock.uploadedChunks).toEqual([totalChunks - 1]);
  });

  it('refuses a different file on resume and keeps the durable job intact', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const totalChunks = chunkCount(file.size, CHUNK);
    await stageResumableJob(harness, file, totalChunks - 1);

    const reloaded = reload(harness.mock, harness.mock);
    await reloaded.coordinator.resume();

    const different = new File(
      [new Uint8Array(file.size).fill(3) as unknown as BlobPart],
      file.name,
    );
    await reloaded.coordinator.resumeWithFile(different);

    const state = reloaded.coordinator.state();
    expect(state.kind).toBe('ready-to-upload');
    if (state.kind === 'ready-to-upload') {
      expect(state.reselectionRequired).toBe(true);
      expect(state.notice?.code).toBe('migration_file_identity_mismatch');
    }
    expect(harness.mock.uploadedChunks).toEqual([]);
  });

  it('fails with an actionable code when the session expired', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const totalChunks = chunkCount(file.size, CHUNK);
    const staged = await stageResumableJob(harness, file, totalChunks - 1);

    const reloaded = reload(harness.mock, harness.mock);
    await reloaded.coordinator.resume();
    harness.mock.expireSession(staged.jobId!);

    await reloaded.coordinator.resumeWithFile(file);

    expect(reloaded.coordinator.state()).toMatchObject({
      kind: 'failed',
      failure: { code: 'migration_session_expired', retryable: true },
    });
  });

  it('recovers an expired session through the server retry flow', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const totalChunks = chunkCount(file.size, CHUNK);
    const staged = await stageResumableJob(harness, file, totalChunks - 1);

    const reloaded = reload(harness.mock, harness.mock);
    await reloaded.coordinator.resume();
    harness.mock.expireSession(staged.jobId!);

    await reloaded.coordinator.resumeWithFile(file);
    expect(reloaded.coordinator.state()).toMatchObject({
      kind: 'failed',
      failure: { code: 'migration_session_expired' },
    });

    // Server rule: the expired session is recreated only after the job is
    // retried (Pending, attempt > 1); the retry discards the expired session
    // exactly as the server's synchronous cleanup does.
    await reloaded.coordinator.retry();
    expect(reloaded.coordinator.state().kind).toBe('ready-to-upload');

    await reloaded.coordinator.resumeWithFile(file);
    expect(reloaded.coordinator.state().kind).toBe('ready-empty');
    // The server's retry cleanup discards the expired session, so its receipts
    // are gone and every chunk is re-uploaded under the recreated session.
    expect(totalChunks).toBe(3);
    expect([...harness.mock.uploadedChunks].sort((a, b) => a - b)).toEqual([0, 1, 2]);
  });

  it('clears a stale record when the job is gone', async () => {
    const harness = setup();
    const file = await portableFile();
    harness.store.save({
      schemaVersion: 1,
      jobId: 'deleted-job',
      jobCreationIdempotencyKey: 'deleted-job-key',
      direction: 'import',
      fileIdentity: { totalSizeBytes: file.size, sha256Checksum: 'a'.repeat(64) },
      fileName: file.name,
      preflightRequest: {} as never,
      createdAt: new Date().toISOString(),
    });

    await harness.coordinator.resume();

    expect(harness.coordinator.state()).toMatchObject({
      kind: 'failed',
      failure: { code: 'migration_not_found' },
    });
    expect(harness.store.load()).toBeNull();
  });

  it('maps a durable provider-cap failure to actionable copy and keeps the import attached', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    class CappedTransport extends DelegatingTransport {
      override async getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto> {
        const status = await super.getJob(jobId, signal);
        return { ...status, job: { ...status.job, state: 'Failed',
          failureCode: 'migration_provider_limit_reached', failureMessage: 'provider limit' } };
      }
    }
    const harness = configure(mock, new CappedTransport(mock));
    const file = await portableFile();
    const preflight = await mock.preflight(preflightRequest(file.size));
    const created = await mock.createJob({ direction: 'Import', idempotencyKey: 'cap-job',
      reservationId: preflight.reservationId });
    harness.store.save({ schemaVersion: 1, jobId: created.job.id,
      jobCreationIdempotencyKey: 'cap-job', direction: 'import',
      fileIdentity: { totalSizeBytes: file.size, sha256Checksum: 'a'.repeat(64) },
      fileName: file.name, preflightRequest: preflightRequest(file.size),
      createdAt: new Date().toISOString() });

    await harness.coordinator.resume();

    expect(harness.coordinator.state()).toMatchObject({ kind: 'failed', jobId: created.job.id,
      failure: { code: 'portable_import_provider_limit_reached' } });
    expect(harness.store.load()?.jobId).toBe(created.job.id);
  });

  it('retries a failed job back into the reselection state', async () => {
    const harness = setup();
    const file = await portableFile();
    harness.store.save({
      schemaVersion: 1,
      jobId: 'retryable',
      jobCreationIdempotencyKey: 'retryable-key',
      direction: 'import',
      fileIdentity: { totalSizeBytes: file.size, sha256Checksum: 'a'.repeat(64) },
      fileName: file.name,
      preflightRequest: {} as never,
      createdAt: new Date().toISOString(),
    });
    const preflight = await harness.mock.preflight(preflightRequest(file.size));
    const job = await harness.mock.createJob({
      direction: 'Import',
      idempotencyKey: 'retryable-job',
      reservationId: preflight.reservationId,
    });
    harness.store.update({ jobId: job.job.id });
    harness.mock.setJobState(job.job.id, 'Failed');

    await harness.coordinator.resume();
    expect(harness.coordinator.state().kind).toBe('failed');

    await harness.coordinator.retry();
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-to-upload' });
    expect((await harness.mock.getJob(job.job.id)).job.state).toBe('Pending');
  });

  it('dismisses a cancelled flow and clears the record', async () => {
    const harness = setup();
    const file = await portableFile();
    const preflight = await harness.mock.preflight(preflightRequest(file.size));
    const job = await harness.mock.createJob({
      direction: 'Import',
      idempotencyKey: 'cancel-job',
      reservationId: preflight.reservationId,
    });
    const identity = await identityFor(harness, file);
    harness.store.save({
      schemaVersion: 1,
      jobId: job.job.id,
      jobCreationIdempotencyKey: 'cancel-job-key',
      reservationId: preflight.reservationId,
      sessionCreationIdempotencyKey: 'cancel-session-key',
      sessionRequest: {
        purpose: 'Import',
        totalBytes: file.size,
        chunkSize: CHUNK,
        totalChunks: 1,
        fileIdentity: identity,
        idempotencyKey: 'cancel-session-key',
      },
      chunkSizeBytes: CHUNK,
      direction: 'import',
      fileIdentity: identity,
      fileName: file.name,
      preflightRequest: preflightRequest(file.size),
      createdAt: new Date().toISOString(),
    });

    await harness.coordinator.resume();
    expect(harness.coordinator.state().kind).toBe('ready-to-upload');
    await harness.coordinator.cancel();
    expect(harness.coordinator.state().kind).toBe('cancelled');

    harness.coordinator.dismiss();
    expect(harness.coordinator.state().kind).toBe('idle');
    expect(harness.store.load()).toBeNull();
  });
});

describe('LibraryTransferCoordinator — stale status responses', () => {
  afterEach(reset);

  it('discards a getJob response that resolves after a newer operation wins', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const flaky = new FlakyTransport(mock);
    const harness = configure(mock, flaky);
    const file = await largePortableFile();
    const totalChunks = chunkCount(file.size, CHUNK);
    await stageResumableJob(harness, file, totalChunks - 1);

    await harness.coordinator.resume();
    expect(harness.coordinator.state().kind).toBe('ready-to-upload');

    const jobId = harness.store.load()?.jobId ?? '';
    const current = await mock.getJob(jobId);

    flaky.deferStatusResponse();
    const stale = harness.coordinator.refreshStatus();
    await harness.coordinator.cancel();
    expect(harness.coordinator.state().kind).toBe('cancelled');

    // The old GET resolves with a pre-cancellation ReadyToActivate status. It
    // must not overwrite the newer cancelled state (nor throw).
    flaky.resolveDeferredStatus({
      ...current,
      job: { ...current.job, state: 'ReadyToActivate' },
    });
    await stale;

    expect(harness.coordinator.state().kind).toBe('cancelled');
  });
});

/**
 * Transport wrapper that records mode resolution and pinning so the
 * coordinator's freeze-once-per-session behaviour is observable.
 */
class ModeTransport extends DelegatingTransport {
  readonly resolveCalls: string[] = [];
  readonly pins: Array<{ sessionId: string; mode: LibraryTransferMode }> = [];
  resolveResult: LibraryTransferMode = 'direct';
  resolveError: Error | null = null;

  async resolveUploadMode(sessionId: string): Promise<LibraryTransferMode> {
    this.resolveCalls.push(sessionId);
    if (this.resolveError) throw this.resolveError;
    return this.resolveResult;
  }

  pinUploadMode(sessionId: string, mode: LibraryTransferMode): void {
    this.pins.push({ sessionId, mode });
  }
}

describe('LibraryTransferCoordinator — upload mode pinning', () => {
  afterEach(reset);

  it('resolves the upload mode once for the session and persists it', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const modes = new ModeTransport(mock);
    modes.resolveResult = 'application-server';
    const harness = configure(mock, modes);
    const file = await portableFile();

    await harness.coordinator.startImport(file);

    expect(modes.resolveCalls).toHaveLength(1);
    const record = harness.store.load();
    expect(modes.resolveCalls[0]).toBe(record?.sessionId);
    expect(record?.transportMode).toBe('application-server');
    expect(modes.pins).toEqual([
      { sessionId: record?.sessionId, mode: 'application-server' },
    ]);
  });

  it('re-pins the persisted mode after a reload instead of resolving again', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const totalChunks = chunkCount(file.size, CHUNK);
    const staged = await stageResumableJob(harness, file, totalChunks - 1);
    harness.store.update({ transportMode: 'direct' });

    const modes = new ModeTransport(harness.mock);
    modes.resolveError = new Error('must not resolve again after a reload');
    const reloaded = reload(harness.mock, modes);

    await reloaded.coordinator.resume();
    await reloaded.coordinator.resumeWithFile(file);

    expect(reloaded.coordinator.state().kind).toBe('ready-empty');
    expect(modes.resolveCalls).toEqual([]);
    expect(modes.pins).toEqual([{ sessionId: staged.sessionId, mode: 'direct' }]);
  });

  it('fails retryably and reattaches instead of calling /retry when the mode stays unavailable', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const modes = new ModeTransport(mock);
    modes.resolveError = new MigrationTransportError(
      'migration_transport_mode_unavailable',
      0,
      'capabilities unavailable',
    );
    const harness = configure(mock, modes);
    const file = await portableFile();

    await harness.coordinator.startImport(file);

    expect(harness.coordinator.state()).toMatchObject({
      kind: 'failed',
      failure: { code: 'migration_transport_mode_unavailable', retryable: true },
    });
    // Nothing reached the application-server chunk path.
    expect(mock.uploadedChunks).toEqual([]);

    await harness.coordinator.retry();

    expect(mock.calls.retryJob).toBe(0);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-to-upload' });
  });
});
