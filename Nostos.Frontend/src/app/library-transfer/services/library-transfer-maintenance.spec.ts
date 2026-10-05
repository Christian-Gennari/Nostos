/**
 * Exclusive-maintenance coverage for the import coordinator (review-740).
 *
 * The server answers every migration route with 503
 * `migration_activation_busy` / `migration_storage_contended` while exclusive
 * maintenance is active. The coordinator must wait out the server's
 * `Retry-After` (or a capped fallback backoff), re-attempt the SAME operation
 * with its persisted idempotency keys, and never route the UI's Retry to the
 * job-level `/jobs/{id}/retry` endpoint while the job is still active.
 *
 * This spec uses a fully scripted transport, inspector and digest, so every
 * async edge is under fake timers and nothing depends on jsdom FileReader
 * timing.
 */

import { TestBed } from '@angular/core/testing';

import {
  ArchiveInspection,
  FileDigest,
} from '../models/library-transfer.models';
import {
  BrowserMigrationChunk,
  MigrationArchiveCountsDto,
  MigrationChunkUploadResultDto,
  MigrationCreateJobRequestDto,
  MigrationExistingCountsDto,
  MigrationJobState,
  MigrationJobStatusResponseDto,
  MigrationPreflightRequestDto,
  MigrationPreflightResponseDto,
  MigrationProgressPhase,
  MigrationSessionRequestDto,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
  toChunkRanges,
} from '../models/migration-http.dtos';
import { LIBRARY_TRANSFER_TRANSPORT, MigrationTransportError } from './library-transfer-transport';
import { LibraryTransferCoordinator } from './library-transfer-coordinator.service';
import { FileDigestService } from './file-digest.service';
import { HASH_WORKER_FACTORY } from './hash/hash-worker';
import { PortableArchiveInspector } from './portable-archive-inspector.service';
import {
  TRANSFER_RESUME_STORAGE_KEY,
  TransferResumeStore,
} from './transfer-resume-store.service';

const CHUNK = 4 * 1024 * 1024;

// These specs advance fake time through many coordinator waits; give them
// room when the whole suite runs in parallel.
vi.setConfig({ testTimeout: 30_000 });

type BusyOperation =
  | 'preflight'
  | 'createJob'
  | 'getJob'
  | 'cancelJob'
  | 'retryJob'
  | 'createUploadSession'
  | 'getUploadSession'
  | 'uploadChunk'
  | 'completeUpload';

interface BusyRule {
  remaining: number;
  retryAfterMs?: number;
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

const EXISTING_COUNTS: MigrationExistingCountsDto = {
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
};

/** Fully in-memory transport with per-operation 503 injection and call counts. */
class ScriptedTransport {
  readonly calls: Record<BusyOperation, number> = {
    preflight: 0,
    createJob: 0,
    getJob: 0,
    cancelJob: 0,
    retryJob: 0,
    createUploadSession: 0,
    getUploadSession: 0,
    uploadChunk: 0,
    completeUpload: 0,
  };

  validationPolls = 0;
  jobId = '';
  jobState: MigrationJobState = 'Pending';
  session: {
    sessionId: string;
    totalBytes: number;
    chunkSize: number;
    totalChunks: number;
    receipts: Set<number>;
    state: MigrationSessionStatusDto['state'];
  } | null = null;

  private readonly busyRules = new Map<BusyOperation, BusyRule>();
  private readonly jobsByIdempotencyKey = new Map<string, string>();

  failNextBusy(operation: BusyOperation, options: { times?: number; retryAfterMs?: number } = {}): void {
    this.busyRules.set(operation, {
      remaining: options.times ?? 1,
      retryAfterMs: options.retryAfterMs,
    });
  }

  failAlwaysBusy(operation: BusyOperation, options: { retryAfterMs?: number } = {}): void {
    this.busyRules.set(operation, {
      remaining: Number.POSITIVE_INFINITY,
      retryAfterMs: options.retryAfterMs,
    });
  }

  clearBusy(operation: BusyOperation): void {
    this.busyRules.delete(operation);
  }

  setJobState(state: MigrationJobState): void {
    this.jobState = state;
  }

  preflight(
    request: MigrationPreflightRequestDto,
  ): Promise<MigrationPreflightResponseDto> {
    this.gate('preflight');
    return Promise.resolve({
      evaluation: {
        decision: 'AllowedEmpty',
        isCompatible: true,
        isAllowed: true,
        incomingCounts: request.incomingCounts,
        existingCounts: EXISTING_COUNTS,
        declaredArchiveBytes: request.declaredArchiveBytes,
        declaredMediaBytes: request.declaredMediaBytes,
        estimatedRecoveryBytes: 0,
        requiredStorageBytes: request.declaredArchiveBytes + request.declaredMediaBytes,
        availableStorageBytes: 1024 ** 4,
        errors: [],
        warnings: [],
        destinationRevision: 'rev-1',
      },
      reservationId: 'reservation-1',
      reservationExpiresAtUtc: new Date(900_000).toISOString(),
      chunkSizeBytes: CHUNK,
    });
  }

  createJob(request: MigrationCreateJobRequestDto): Promise<MigrationJobStatusResponseDto> {
    this.gate('createJob');
    const existing = this.jobsByIdempotencyKey.get(request.idempotencyKey);
    if (existing) {
      this.jobId = existing;
      return Promise.resolve(this.status());
    }
    this.jobId = this.jobId || `job-${this.jobsByIdempotencyKey.size + 1}`;
    this.jobState = 'Pending';
    this.jobsByIdempotencyKey.set(request.idempotencyKey, this.jobId);
    return Promise.resolve(this.status());
  }

  getJob(): Promise<MigrationJobStatusResponseDto> {
    this.gate('getJob');
    if (this.jobState === 'Validating' && this.validationPolls > 0) {
      this.validationPolls -= 1;
      if (this.validationPolls === 0) this.jobState = 'ReadyToActivate';
    }
    return Promise.resolve(this.status());
  }

  cancelJob(): Promise<MigrationJobStatusResponseDto> {
    this.gate('cancelJob');
    this.jobState = 'Cancelled';
    if (this.session) this.session.state = 'Cancelled';
    return Promise.resolve(this.status());
  }

  retryJob(): Promise<MigrationJobStatusResponseDto> {
    this.gate('retryJob');
    this.jobState = 'Pending';
    return Promise.resolve(this.status());
  }

  createUploadSession(
    _jobId: string,
    request: MigrationSessionRequestDto,
  ): Promise<MigrationUploadSessionResponseDto> {
    this.gate('createUploadSession');
    if (!this.session) {
      this.session = {
        sessionId: 'session-1',
        totalBytes: request.totalBytes,
        chunkSize: request.chunkSize,
        totalChunks: request.totalChunks,
        receipts: new Set<number>(),
        state: 'Created',
      };
    }
    return Promise.resolve(this.sessionResponse());
  }

  getUploadSession(): Promise<MigrationUploadSessionResponseDto> {
    this.gate('getUploadSession');
    if (!this.session) throw this.error('migration_invalid_state', 409);
    return Promise.resolve(this.sessionResponse());
  }

  uploadChunk(
    _jobId: string,
    _sessionId: string,
    request: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
  ): Promise<MigrationChunkUploadResultDto> {
    this.gate('uploadChunk');
    if (!this.session) throw this.error('migration_invalid_state', 409);
    const alreadyPresent = this.session.receipts.has(request.index);
    this.session.receipts.add(request.index);
    this.session.state = 'Receiving';
    onProgress(request.lengthBytes, request.lengthBytes);
    return Promise.resolve({
      sessionId: this.session.sessionId,
      chunkIndex: request.index,
      alreadyPresent,
    });
  }

  completeUpload(): Promise<MigrationSessionStatusDto> {
    this.gate('completeUpload');
    if (!this.session || this.session.receipts.size !== this.session.totalChunks) {
      throw this.error('migration_invalid_state', 409);
    }
    this.session.state = 'Complete';
    this.jobState = this.validationPolls > 0 ? 'Validating' : 'ReadyToActivate';
    return Promise.resolve(this.sessionDto());
  }

  getExportDownloadUrl(jobId: string): string {
    return `/api/portability/migration/jobs/${jobId}/export-download`;
  }

  private gate(operation: BusyOperation): void {
    this.calls[operation] += 1;
    const rule = this.busyRules.get(operation);
    if (!rule || rule.remaining <= 0) return;
    if (rule.remaining !== Number.POSITIVE_INFINITY) rule.remaining -= 1;
    throw new MigrationTransportError(
      'migration_activation_busy',
      503,
      'The library is in maintenance. Try again later.',
      rule.retryAfterMs === undefined ? undefined : { retryAfterMs: rule.retryAfterMs },
    );
  }

  private error(code: 'migration_invalid_state', status: number): MigrationTransportError {
    return new MigrationTransportError(code, status, 'scripted failure');
  }

  private status(): MigrationJobStatusResponseDto {
    const progress: { phase: MigrationProgressPhase; bytesProcessed: number } = {
      phase: 'Pending',
      bytesProcessed: 0,
    };
    return {
      job: {
        id: this.jobId,
        direction: 'Import',
        state: this.jobState,
        recoveryStatus: 'NotRequired',
        createdAtUtc: new Date(0).toISOString(),
        updatedAtUtc: new Date(0).toISOString(),
        leaseToken: null,
        leaseExpiresAtUtc: null,
        failureCode: null,
        failureMessage: null,
      },
      progress: { ...progress, totalBytes: this.session?.totalBytes ?? null },
      session: this.session ? this.sessionDto() : null,
      downloadAvailable: false,
      artifactExpiresAtUtc: null,
      preparedImport: null,
    };
  }

  private sessionResponse(): MigrationUploadSessionResponseDto {
    return {
      session: this.sessionDto(),
      receivedRanges: toChunkRanges([...this.session!.receipts]),
    };
  }

  private sessionDto(): MigrationSessionStatusDto {
    const session = this.session!;
    const receivedChunks = [...session.receipts].sort((left, right) => left - right);
    return {
      sessionId: session.sessionId,
      purpose: 'Import',
      state: session.state,
      totalBytes: session.totalBytes,
      chunkSize: session.chunkSize,
      totalChunks: session.totalChunks,
      fileIdentity: { totalSizeBytes: session.totalBytes, sha256Checksum: 'a'.repeat(64) },
      receivedChunks,
      receivedChunkCount: receivedChunks.length,
      createdAtUtc: new Date(0).toISOString(),
      expiresAtUtc: new Date(86_400_000).toISOString(),
    };
  }
}

const STUB_DIGEST: FileDigest = {
  sha256: () => Promise.resolve('a'.repeat(64)),
  sha256Chunk: () => Promise.resolve('b'.repeat(64)),
  fingerprint: () => Promise.resolve('nostos-fp-v1:stub'),
};

const STUB_INSPECTOR = {
  inspect(file: Blob): Promise<ArchiveInspection> {
    return Promise.resolve({
      kind: 'portable',
      summary: {
        formatName: 'nostos-portable',
        formatVersion: 1,
        dataVersion: 1,
        archiveBytes: file.size,
        mediaBytes: 0,
        maxEntryBytes: 0,
        counts: emptyCounts(),
        mediaEntries: 0,
      },
    });
  },
} as unknown as PortableArchiveInspector;

interface Harness {
  transport: ScriptedTransport;
  coordinator: LibraryTransferCoordinator;
  store: TransferResumeStore;
}

function setup(): Harness {
  const transport = new ScriptedTransport();
  TestBed.configureTestingModule({
    providers: [
      { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport },
      { provide: FileDigestService, useValue: STUB_DIGEST },
      { provide: PortableArchiveInspector, useValue: STUB_INSPECTOR },
      { provide: HASH_WORKER_FACTORY, useValue: () => null },
    ],
  });
  return {
    transport,
    coordinator: TestBed.inject(LibraryTransferCoordinator),
    store: TestBed.inject(TransferResumeStore),
  };
}

function fileOfSize(size = 1024): File {
  return new File([new Uint8Array(size) as unknown as BlobPart], 'library.nostos');
}

async function settleUntil(predicate: () => boolean, attempts = 400): Promise<void> {
  for (let attempt = 0; attempt < attempts && !predicate(); attempt += 1) {
    await vi.advanceTimersByTimeAsync(1);
  }
}

async function advanceUntil(
  predicate: () => boolean,
  stepMs = 10_000,
  maxSteps = 200,
): Promise<void> {
  for (let step = 0; step < maxSteps && !predicate(); step += 1) {
    await vi.advanceTimersByTimeAsync(stepMs);
  }
}

function isTerminal(kind: ReturnType<LibraryTransferCoordinator['state']>['kind']): boolean {
  return (
    kind === 'ready-empty' ||
    kind === 'replacement-confirmation' ||
    kind === 'completed' ||
    kind === 'failed' ||
    kind === 'cancelled'
  );
}

describe('LibraryTransferCoordinator — exclusive server maintenance', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    TestBed.resetTestingModule();
    localStorage.removeItem(TRANSFER_RESUME_STORAGE_KEY);
  });

  it('re-attempts preflight after the capped fallback delay without Retry-After', async () => {
    const harness = setup();
    harness.transport.failNextBusy('preflight');

    const running = harness.coordinator.startImport(fileOfSize());
    await settleUntil(
      () => harness.coordinator.maintenanceWaiting()?.operation === 'preflight',
    );

    expect(harness.transport.calls.preflight).toBe(1);
    expect(harness.coordinator.maintenanceWaiting()?.retryAfterMs).toBe(500);

    await vi.advanceTimersByTimeAsync(500);
    await advanceUntil(() => isTerminal(harness.coordinator.state().kind));
    await running;

    expect(harness.transport.calls.preflight).toBe(2);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
    expect(harness.coordinator.maintenanceWaiting()).toBeNull();
    expect(harness.transport.calls.retryJob).toBe(0);
  });

  it('honours Retry-After before re-attempting createJob', async () => {
    const harness = setup();
    harness.transport.failNextBusy('createJob', { retryAfterMs: 1500 });

    const running = harness.coordinator.startImport(fileOfSize());
    await settleUntil(() => harness.coordinator.maintenanceWaiting()?.operation === 'createJob');
    expect(harness.transport.calls.createJob).toBe(1);

    await vi.advanceTimersByTimeAsync(1000);
    expect(harness.transport.calls.createJob).toBe(1);

    await vi.advanceTimersByTimeAsync(500);
    await advanceUntil(() => isTerminal(harness.coordinator.state().kind));
    await running;

    expect(harness.transport.calls.createJob).toBe(2);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
  });

  it('re-attempts createUploadSession with the persisted session key', async () => {
    const harness = setup();
    harness.transport.failNextBusy('createUploadSession', { retryAfterMs: 250 });

    const running = harness.coordinator.startImport(fileOfSize());
    await settleUntil(
      () => harness.coordinator.maintenanceWaiting()?.operation === 'createUploadSession',
    );

    await vi.advanceTimersByTimeAsync(200);
    expect(harness.transport.calls.createUploadSession).toBe(1);

    await vi.advanceTimersByTimeAsync(50);
    await advanceUntil(() => isTerminal(harness.coordinator.state().kind));
    await running;

    expect(harness.transport.calls.createUploadSession).toBe(2);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
  });

  it('re-attempts getUploadSession while waiting for authoritative receipts', async () => {
    const harness = setup();
    harness.transport.failNextBusy('getUploadSession', { retryAfterMs: 300 });

    const running = harness.coordinator.startImport(fileOfSize());
    await settleUntil(
      () => harness.coordinator.maintenanceWaiting()?.operation === 'getUploadSession',
    );

    await vi.advanceTimersByTimeAsync(300);
    await advanceUntil(() => isTerminal(harness.coordinator.state().kind));
    await running;

    expect(harness.transport.calls.getUploadSession).toBe(2);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
  });

  it('re-runs the upload when chunk PUTs exhaust their own retries on 503', async () => {
    const harness = setup();
    harness.transport.failNextBusy('uploadChunk', { times: 4, retryAfterMs: 1000 });

    const running = harness.coordinator.startImport(fileOfSize());
    await advanceUntil(() => isTerminal(harness.coordinator.state().kind));
    await running;

    // Four engine attempts fail busy, then the coordinator waits and re-runs
    // the upload phase; the fifth chunk call succeeds.
    expect(harness.transport.calls.uploadChunk).toBe(5);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
    expect(harness.transport.calls.retryJob).toBe(0);
  });

  it('re-attempts completeUpload after maintenance instead of failing', async () => {
    const harness = setup();
    harness.transport.failNextBusy('completeUpload', { retryAfterMs: 1000 });

    const running = harness.coordinator.startImport(fileOfSize());
    await settleUntil(
      () => harness.coordinator.maintenanceWaiting()?.operation === 'completeUpload',
    );
    expect(harness.transport.calls.completeUpload).toBe(1);

    await vi.advanceTimersByTimeAsync(1000);
    await advanceUntil(() => isTerminal(harness.coordinator.state().kind));
    await running;

    expect(harness.transport.calls.completeUpload).toBe(2);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
    expect(harness.transport.calls.retryJob).toBe(0);
  });

  it('keeps polling through a busy getJob while the durable job progresses', async () => {
    const harness = setup();
    harness.transport.validationPolls = 2;
    harness.transport.failNextBusy('getJob', { retryAfterMs: 2000 });

    const running = harness.coordinator.startImport(fileOfSize());
    await advanceUntil(() => isTerminal(harness.coordinator.state().kind));
    await running;

    expect(harness.transport.calls.getJob).toBeGreaterThanOrEqual(3);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
    expect(harness.transport.calls.retryJob).toBe(0);
  });

  it('waits out maintenance before recording the durable cancellation', async () => {
    const harness = setup();
    harness.transport.validationPolls = 3;

    const running = harness.coordinator.startImport(fileOfSize());
    await advanceUntil(() => harness.coordinator.state().kind === 'checking', 1500, 500);
    harness.transport.failNextBusy('cancelJob', { retryAfterMs: 1500 });

    const cancelling = harness.coordinator.cancel();
    await advanceUntil(() => harness.coordinator.state().kind === 'cancelled', 10_000, 200);
    await cancelling;
    await running;

    // The first cancellation hit maintenance; the second recorded it.
    expect(harness.transport.calls.cancelJob).toBe(2);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'cancelled' });
    expect(harness.coordinator.maintenanceWaiting()).toBeNull();
  });

  it(
    'fails with a non-destructive timeout after the cap and retry repeats the operation',
    { timeout: 20_000 },
    async () => {
      const harness = setup();
      harness.transport.failAlwaysBusy('completeUpload', { retryAfterMs: 5000 });

      const running = harness.coordinator.startImport(fileOfSize());
      await advanceUntil(() => harness.coordinator.state().kind === 'failed', 60_000, 100);
      await running;

      expect(harness.coordinator.state()).toMatchObject({
        kind: 'failed',
        failure: { code: 'migration_maintenance_timeout', retryable: true },
      });
      expect(harness.coordinator.hasInterruptedOperation()).toBe(true);
      expect(harness.transport.calls.retryJob).toBe(0);
      const attemptsAtTimeout = harness.transport.calls.completeUpload;
      expect(attemptsAtTimeout).toBeGreaterThan(1);

      // The UI Retry repeats the interrupted operation (completion), never the
      // job-level retry endpoint.
      harness.transport.clearBusy('completeUpload');
      const retrying = harness.coordinator.retry();
      await advanceUntil(() => isTerminal(harness.coordinator.state().kind));
      await retrying;

      expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
      expect(harness.transport.calls.completeUpload).toBeGreaterThan(attemptsAtTimeout);
      expect(harness.transport.calls.retryJob).toBe(0);
      expect(harness.coordinator.hasInterruptedOperation()).toBe(false);
    },
  );

  it('stays cancellable while waiting for maintenance', async () => {
    const harness = setup();
    harness.transport.failAlwaysBusy('completeUpload', { retryAfterMs: 5000 });

    const running = harness.coordinator.startImport(fileOfSize());
    await settleUntil(
      () => harness.coordinator.maintenanceWaiting()?.operation === 'completeUpload',
    );

    const cancelling = harness.coordinator.cancel();
    await advanceUntil(() => harness.coordinator.state().kind === 'cancelled');
    await cancelling;
    await running;

    expect(harness.coordinator.state()).toMatchObject({ kind: 'cancelled' });
    expect(harness.coordinator.maintenanceWaiting()).toBeNull();
    expect(harness.transport.calls.retryJob).toBe(0);
  });

  it('calls the job-level retry only for an actually retryable terminal job', async () => {
    const harness = setup();
    const file = fileOfSize();
    vi.useRealTimers();

    const preflight = await harness.transport.preflight({
      incomingCounts: emptyCounts(),
      declaredArchiveBytes: file.size,
      declaredMediaBytes: 0,
      maxSingleEntryBytes: 0,
      declaredFormatVersion: 1,
      declaredDataVersion: 1,
      declaredFormatName: 'nostos-portable',
      clientDestinationRevision: null,
      isOperationalBackup: false,
    });
    const created = await harness.transport.createJob({
      direction: 'Import',
      idempotencyKey: 'terminal-retry-key',
      reservationId: preflight.reservationId,
    });
    harness.store.save({
      schemaVersion: 1,
      jobId: created.job.id,
      jobCreationIdempotencyKey: 'terminal-retry-key',
      direction: 'import',
      fileIdentity: { totalSizeBytes: file.size, sha256Checksum: 'a'.repeat(64) },
      fileName: file.name,
      preflightRequest: {} as never,
      createdAt: new Date().toISOString(),
    });
    harness.transport.setJobState('Failed');

    await harness.coordinator.resume();
    expect(harness.coordinator.state()).toMatchObject({ kind: 'failed' });

    await harness.coordinator.retry();
    expect(harness.transport.calls.retryJob).toBe(1);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-to-upload' });
  });
});
