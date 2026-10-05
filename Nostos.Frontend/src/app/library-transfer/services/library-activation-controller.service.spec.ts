import { TestBed } from '@angular/core/testing';

import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
} from './library-transfer-transport';
import {
  LibraryActivationController,
} from './library-activation-controller.service';
import { TransferResumeStore } from './transfer-resume-store.service';
import { TransferTabLease, TRANSFER_TAB_LEASE_KEY } from './transfer-tab-lease.service';
import {
  MockLibraryTransferTransport,
  MockLibraryTransferTransportOptions,
} from '../testing/mock-library-transfer-transport';
import {
  MigrationPreflightRequestDto,
} from '../models/migration-http.dtos';
import { PersistedTransferResumeState } from '../models/library-transfer.models';

const CHUNK = 4 * 1024 * 1024;

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

function resumeRecord(jobId: string, destinationRevision?: string): PersistedTransferResumeState {
  return {
    schemaVersion: 1,
    jobId,
    jobCreationIdempotencyKey: `job-key-${jobId}`,
    direction: 'import',
    fileIdentity: { totalSizeBytes: 10, sha256Checksum: 'a'.repeat(64) },
    fileName: 'library.nostos',
    preflightRequest: preflightRequest(),
    destinationRevision,
    createdAt: new Date().toISOString(),
  };
}

interface Harness {
  mock: MockLibraryTransferTransport;
  controller: LibraryActivationController;
  store: TransferResumeStore;
  lease: TransferTabLease;
}

function configure(options: MockLibraryTransferTransportOptions = {}): Harness {
  const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK, ...options });
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [{ provide: LIBRARY_TRANSFER_TRANSPORT, useValue: mock }],
  });
  const controller = TestBed.inject(LibraryActivationController);
  controller.pollIntervalMs = 5;
  controller.maxPollIntervalMs = 20;
  return {
    mock,
    controller,
    store: TestBed.inject(TransferResumeStore),
    lease: TestBed.inject(TransferTabLease),
  };
}

/** Re-attaches a fresh controller against the same mock and persisted record. */
function reloadController(mock: MockLibraryTransferTransport): Harness {
  const transport: LibraryTransferTransport = mock;
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [{ provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport }],
  });
  const controller = TestBed.inject(LibraryActivationController);
  controller.pollIntervalMs = 5;
  controller.maxPollIntervalMs = 20;
  return {
    mock,
    controller,
    store: TestBed.inject(TransferResumeStore),
    lease: TestBed.inject(TransferTabLease),
  };
}

async function stageReadyJob(
  mock: MockLibraryTransferTransport,
  key = `job-${Math.random().toString(36).slice(2)}`,
): Promise<string> {
  const preflight = await mock.preflight(preflightRequest());
  const created = await mock.createJob({
    direction: 'Import',
    idempotencyKey: key,
    reservationId: preflight.reservationId,
  });
  mock.setJobState(created.job.id, 'ReadyToActivate');
  return created.job.id;
}

describe('LibraryActivationController', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  afterEach(() => {
    localStorage.removeItem(TRANSFER_TAB_LEASE_KEY);
    TestBed.resetTestingModule();
  });

  it('auto-activates an empty destination and reaches completed', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);
    harness.store.save(resumeRecord(jobId, 'rev-1'));

    await harness.controller.requestActivation(jobId);
    expect(harness.controller.view().state).toBe('in-progress');
    expect(harness.mock.activationRequests[0]).toEqual({
      destinationRevision: 'rev-1',
      confirmReplacement: false,
    });
    expect(harness.mock.calls.activateJob).toBe(1);

    harness.mock.completeActivation(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('completed'), {
      timeout: 2_000,
    });

    expect(harness.controller.view().recovery).toEqual({
      available: false,
      expiresAtUtc: null,
      sizeBytes: null,
    });
    expect(harness.store.load()).toBeNull();
    expect(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)).toBeNull();
  });

  it('reports the server recovery expiry on completion', async () => {
    const expiresAt = new Date(Date.now() + 86_400_000).toISOString();
    const harness = configure({
      recoveryAvailable: true,
      recoveryExpiresAtUtc: expiresAt,
      recoverySizeBytes: 4096,
    });
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);
    harness.mock.completeActivation(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('completed'), {
      timeout: 2_000,
    });

    expect(harness.controller.view().recovery).toEqual({
      available: true,
      expiresAtUtc: expiresAt,
      sizeBytes: 4096,
    });
  });

  it('shows the server counts when an empty path hits a populated destination', async () => {
    const harness = configure({
      destinationStatus: 'Populated',
      existingCounts: { books: 4, notes: 9, collections: 2 },
    });
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);

    const view = harness.controller.view();
    expect(view.state).toBe('idle');
    expect(view.conflict?.destinationStatus).toBe('Populated');
    expect(view.conflict?.existingCounts?.books).toBe(4);
    expect(view.conflict?.destinationRevision).toBe('rev-1');

    // The user confirms with the revision the server returned in the 409.
    await harness.controller.confirmReplacement(jobId);
    expect(harness.mock.activationRequests[1]).toEqual({
      destinationRevision: 'rev-1',
      confirmReplacement: true,
    });

    harness.mock.completeActivation(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('completed'), {
      timeout: 2_000,
    });
  });

  it('re-confirms a destination conflict with the revision from the 409', async () => {
    const harness = configure();
    // The job baseline is captured after the library moved; the user reviewed
    // the older revision recorded at preflight.
    harness.mock.bumpDestinationRevision('rev-2');
    const jobId = await stageReadyJob(harness.mock);
    harness.store.save(resumeRecord(jobId, 'rev-1'));

    await harness.controller.confirmReplacement(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('failed'), {
      timeout: 2_000,
    });

    const view = harness.controller.view();
    expect(view.errorCode).toBe('migration_destination_conflict');
    expect(view.canRetry).toBe(true);
    expect(view.conflict?.destinationRevision).toBe('rev-2');

    await harness.controller.confirmReplacement(jobId);
    expect(harness.mock.activationRequests).toEqual([
      { destinationRevision: 'rev-1', confirmReplacement: true },
      { destinationRevision: 'rev-2', confirmReplacement: true },
    ]);

    harness.mock.completeActivation(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('completed'), {
      timeout: 2_000,
    });
  });

  it('surfaces a second destination conflict as terminal (no retry)', async () => {
    const harness = configure();
    harness.mock.bumpDestinationRevision('rev-2');
    const jobId = await stageReadyJob(harness.mock);
    harness.store.save(resumeRecord(jobId, 'rev-1'));

    await harness.controller.confirmReplacement(jobId);
    await vi.waitFor(() => expect(harness.controller.view().canRetry).toBe(true), {
      timeout: 2_000,
    });

    // The server's fresh revision no longer matches the durable baseline.
    harness.mock.bumpDestinationRevision('rev-3');
    await harness.controller.confirmReplacement(jobId);

    expect(harness.controller.view().state).toBe('failed');
    expect(harness.controller.view().errorCode).toBe('migration_destination_conflict');
    expect(harness.controller.view().canRetry).toBe(false);
  });

  it('maps a failed activation with canActivate to a retryable failure', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);
    harness.mock.failActivation(jobId, 'migration_activation_failed', true);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('failed'), {
      timeout: 2_000,
    });

    expect(harness.controller.view().canRetry).toBe(true);
    expect(harness.controller.view().errorCode).toBe('migration_activation_failed');
  });

  it('maps a failed activation without canActivate to a terminal failure', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);
    harness.mock.failActivation(jobId, 'migration_activation_failed', false);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('failed'), {
      timeout: 2_000,
    });

    expect(harness.controller.view().canRetry).toBe(false);
  });

  it('presents RecoveryFailed as fail-closed and never retries', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);
    harness.mock.failActivationRecovery(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('failed'), {
      timeout: 2_000,
    });

    const view = harness.controller.view();
    expect(view.errorCode).toBe('migration_activation_recovery_failed');
    expect(view.canRetry).toBe(false);
    expect(view.maintenanceRequired).toBe(true);

    const attempts = harness.mock.calls.activateJob;
    // Even an explicit retry request cannot be admitted while fail-closed: the
    // status route replays the fail-closed run and no POST is sent.
    await harness.controller.requestActivation(jobId);
    await new Promise((resolve) => setTimeout(resolve, 40));
    expect(harness.controller.view().canRetry).toBe(false);
    expect(harness.mock.calls.activateJob).toBe(attempts);
    expect(harness.controller.view().errorCode).toBe('migration_activation_recovery_failed');
  });

  it('re-issues a lost accepted run exactly once, then surfaces', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);
    expect(harness.mock.calls.activateJob).toBe(1);

    harness.mock.loseActivation(jobId);
    await vi.waitFor(() => expect(harness.mock.calls.activateJob).toBe(2), { timeout: 2_000 });

    harness.mock.loseActivation(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('failed'), {
      timeout: 2_000,
    });

    expect(harness.controller.view().canRetry).toBe(true);
    expect(harness.mock.calls.activateJob).toBe(2);
  });

  it('tolerates 503 maintenance and network errors while polling', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);
    harness.mock.queueFailure({
      operation: 'getActivationStatus',
      code: 'migration_activation_busy',
      status: 503,
      times: 2,
      retryAfterMs: 1,
    });
    harness.mock.queueFailure({ operation: 'getActivationStatus', status: 0 });

    harness.mock.completeActivation(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('completed'), {
      timeout: 3_000,
    });

    expect(harness.controller.view().errorCode).toBeNull();
  });

  it('re-attaches to a persisted accepted activation after a reload', async () => {
    const first = configure();
    const jobId = await stageReadyJob(first.mock);
    first.store.save(resumeRecord(jobId, 'rev-1'));

    await first.controller.requestActivation(jobId);
    expect(first.store.load()?.activation?.accepted).toBe(true);

    // A real reload releases the old tab's lease on pagehide.
    first.lease.release();

    const second = reloadController(first.mock);
    expect(second.store.load()?.activation?.accepted).toBe(true);

    second.controller.reattach();
    expect(second.controller.view().state).toBe('in-progress');

    first.mock.completeActivation(jobId);
    await vi.waitFor(() => expect(second.controller.view().state).toBe('completed'), {
      timeout: 2_000,
    });
    expect(second.store.load()).toBeNull();
  });

  it('respects another tab holding the lease during reattach', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);
    await harness.controller.requestActivation(jobId);
    harness.lease.release();
    harness.store.save({
      ...resumeRecord(jobId, 'rev-1'),
      activation: {
        request: { destinationRevision: 'rev-1', confirmReplacement: false },
        accepted: true,
      },
    });

    const second = reloadController(harness.mock);
    // Simulate the owning tab's live lease in shared storage.
    localStorage.setItem(
      TRANSFER_TAB_LEASE_KEY,
      JSON.stringify({ tabId: 'other-tab', updatedAt: Date.now() }),
    );
    second.lease.refresh();

    second.controller.reattach();
    expect(second.controller.view().state).toBe('idle');

    localStorage.removeItem(TRANSFER_TAB_LEASE_KEY);
    second.lease.refresh();
    second.controller.reattach();
    expect(second.controller.view().state).toBe('in-progress');
  });
});
