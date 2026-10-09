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
 * This spec uses the shared in-memory transport with queued maintenance
 * responses, so every async edge is under fake timers.
 */

import { TestBed } from '@angular/core/testing';

import { ArchiveInspection } from '../models/library-transfer.models';
import { MigrationArchiveCountsDto } from '../models/migration-http.dtos';
import {
  MockLibraryTransferTransport,
  MockLibraryTransferTransportOptions,
} from '../testing/mock-library-transfer-transport';
import { LIBRARY_TRANSFER_TRANSPORT } from './library-transfer-transport';
import {
  DEFAULT_STATUS_POLL_MS,
  LibraryTransferCoordinator,
  MAINTENANCE_MAX_WAIT_MS,
} from './library-transfer-coordinator.service';
import { HASH_WORKER_FACTORY } from './hash/hash-worker';
import { PortableArchiveInspector } from './portable-archive-inspector.service';
import { TRANSFER_RESUME_STORAGE_KEY } from './transfer-resume-store.service';

const CHUNK = 4 * 1024 * 1024;

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

// These specs advance fake time through many coordinator waits; give them
// room when the whole suite runs in parallel.
vi.setConfig({ testTimeout: 30_000 });

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
  transport: MockLibraryTransferTransport;
  coordinator: LibraryTransferCoordinator;
}

function setup(options: MockLibraryTransferTransportOptions = {}): Harness {
  const transport = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK, ...options });
  TestBed.configureTestingModule({
    providers: [
      { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport },
      { provide: PortableArchiveInspector, useValue: STUB_INSPECTOR },
      { provide: HASH_WORKER_FACTORY, useValue: () => null },
    ],
  });
  return {
    transport,
    coordinator: TestBed.inject(LibraryTransferCoordinator),
  };
}

function fileOfSize(size = 1024): File {
  const bytes = new Uint8Array(size);
  const file = new File([bytes as unknown as BlobPart], 'library.nostos');
  const nativeSlice = file.slice.bind(file);
  file.slice = (start = 0, end = file.size, contentType?: string): Blob => {
    const blob = nativeSlice(start, end, contentType);
    const normalizedStart = start < 0 ? Math.max(size + start, 0) : Math.min(start, size);
    const normalizedEnd = end < 0 ? Math.max(size + end, 0) : Math.min(end, size);
    const sliceBytes = bytes.slice(normalizedStart, Math.max(normalizedStart, normalizedEnd));
    Object.defineProperty(blob, 'arrayBuffer', {
      value: () => Promise.resolve(sliceBytes.buffer),
    });
    return blob;
  };
  return file;
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
    harness.transport.queueFailure({
      operation: 'preflight',
      code: 'migration_activation_busy',
      status: 503,
    });

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
    harness.transport.queueFailure({
      operation: 'createJob',
      code: 'migration_activation_busy',
      status: 503,
      retryAfterMs: 1500,
    });

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
    harness.transport.queueFailure({
      operation: 'createUploadSession',
      code: 'migration_activation_busy',
      status: 503,
      retryAfterMs: 250,
    });

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
    harness.transport.queueFailure({
      operation: 'getUploadSession',
      code: 'migration_activation_busy',
      status: 503,
      retryAfterMs: 300,
    });

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
    harness.transport.queueFailure({
      operation: 'uploadChunk',
      code: 'migration_activation_busy',
      status: 503,
      times: 4,
      retryAfterMs: 1000,
    });

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
    harness.transport.queueFailure({
      operation: 'completeUpload',
      code: 'migration_activation_busy',
      status: 503,
      retryAfterMs: 1000,
    });

    const running = harness.coordinator.startImport(fileOfSize());
    await settleUntil(
      () => harness.coordinator.maintenanceWaiting()?.operation === 'completeUpload',
      2_000,
    );
    expect(harness.coordinator.maintenanceWaiting()?.operation)
      .toBe('completeUpload');
    expect(harness.transport.calls.completeUpload).toBe(1);

    await vi.advanceTimersByTimeAsync(1000);
    await advanceUntil(() => isTerminal(harness.coordinator.state().kind));
    await running;

    expect(harness.transport.calls.completeUpload).toBe(2);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
    expect(harness.transport.calls.retryJob).toBe(0);
  });

  it('keeps polling through a busy getJob while the durable job progresses', async () => {
    const harness = setup({ validationPolls: 2 });
    harness.transport.queueFailure({
      operation: 'getJob',
      code: 'migration_activation_busy',
      status: 503,
      retryAfterMs: 2000,
    });

    const running = harness.coordinator.startImport(fileOfSize());
    await settleUntil(() => harness.coordinator.maintenanceWaiting()?.operation === 'getJob');
    expect(harness.transport.calls.getJob).toBe(1);

    await vi.advanceTimersByTimeAsync(2000);
    await settleUntil(() => harness.transport.calls.getJob >= 2);
    await vi.advanceTimersByTimeAsync(DEFAULT_STATUS_POLL_MS);
    await settleUntil(() => isTerminal(harness.coordinator.state().kind));
    await running;

    expect(harness.transport.calls.getJob).toBeGreaterThanOrEqual(3);
    expect(harness.coordinator.state()).toMatchObject({ kind: 'ready-empty' });
    expect(harness.transport.calls.retryJob).toBe(0);
  });

  it('waits out maintenance before recording the durable cancellation', async () => {
    const harness = setup({ validationPolls: 3 });

    const running = harness.coordinator.startImport(fileOfSize());
    await advanceUntil(() => harness.coordinator.state().kind === 'checking', 1500, 500);
    harness.transport.queueFailure({
      operation: 'cancelJob',
      code: 'migration_activation_busy',
      status: 503,
      retryAfterMs: 1500,
    });

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
      harness.transport.queueFailure({
        operation: 'completeUpload',
        code: 'migration_activation_busy',
        status: 503,
        times: Infinity,
        retryAfterMs: MAINTENANCE_MAX_WAIT_MS - 50_000,
      });

      const running = harness.coordinator.startImport(fileOfSize());
      await settleUntil(
        () => harness.coordinator.maintenanceWaiting()?.operation === 'completeUpload',
      );
      await vi.advanceTimersByTimeAsync(MAINTENANCE_MAX_WAIT_MS - 50_000);
      await settleUntil(() => harness.coordinator.state().kind === 'failed');
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
      harness.transport.clearFailures('completeUpload');
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
    harness.transport.queueFailure({
      operation: 'completeUpload',
      code: 'migration_activation_busy',
      status: 503,
      times: Infinity,
      retryAfterMs: 5000,
    });

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
});
