import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';

import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
  MigrationTransportError,
} from './library-transfer-transport';
import {
  LIBRARY_REPLACED_STORAGE_KEY,
  LibraryActivationController,
} from './library-activation-controller.service';
import { TransferResumeStore } from './transfer-resume-store.service';
import { TransferTabLease, TRANSFER_TAB_LEASE_KEY } from './transfer-tab-lease.service';
import {
  MockLibraryTransferTransport,
  MockLibraryTransferTransportOptions,
} from '../testing/mock-library-transfer-transport';
import { DelegatingTransport } from '../testing/delegating-transport';
import {
  MigrationActivateRequestDto,
  MigrationActivationStatusDto,
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

function activationStatus(
  jobId: string,
  overrides: Partial<MigrationActivationStatusDto> = {},
): MigrationActivationStatusDto {
  return {
    jobId,
    state: 'ReadyToActivate',
    outcome: 'Running',
    errorCode: null,
    message: null,
    maintenanceRequired: false,
    destinationRevision: 'rev-1',
    existingCounts: null,
    destinationStatus: 'Populated',
    recoveryAvailable: false,
    recoveryExpiresAtUtc: null,
    recoverySizeBytes: null,
    phase: 'Preparing',
    accepted: true,
    canActivate: false,
    ...overrides,
  };
}

/** Holds one job's activation status request so a test can resolve it late. */
class DeferredStatusTransport extends DelegatingTransport {
  private deferredJobId: string | null = null;
  private pending: {
    resolve: (status: MigrationActivationStatusDto) => void;
    reject: (error: unknown) => void;
  } | null = null;

  deferStatusFor(jobId: string): void {
    this.deferredJobId = jobId;
  }

  get pendingStatus(): boolean {
    return this.pending !== null;
  }

  override getActivationStatus(
    jobId: string,
    signal?: AbortSignal,
  ): Promise<MigrationActivationStatusDto> {
    if (this.deferredJobId === jobId) {
      this.deferredJobId = null;
      return new Promise<MigrationActivationStatusDto>((resolve, reject) => {
        this.pending = { resolve, reject };
      });
    }
    return this.inner.getActivationStatus(jobId, signal);
  }

  release(status: MigrationActivationStatusDto): void {
    const pending = this.pending;
    this.pending = null;
    pending?.resolve(status);
  }
}

/** Drops the next activation POST without letting it reach the server. */
class DropActivationTransport extends DelegatingTransport {
  dropNext = false;

  override activateJob(
    jobId: string,
    request: MigrationActivateRequestDto,
    signal?: AbortSignal,
  ): Promise<MigrationActivationStatusDto> {
    if (this.dropNext) {
      this.dropNext = false;
      return Promise.reject(new MigrationTransportError('network_error', 0, 'dropped'));
    }
    return this.inner.activateJob(jobId, request, signal);
  }
}

interface FakeWindow {
  location: { reload: ReturnType<typeof vi.fn> };
  localStorage: Storage;
  addEventListener: (type: string, handler: EventListener) => void;
  removeEventListener: (type: string, handler: EventListener) => void;
}

interface FakeDocumentHarness {
  fakeWindow: FakeWindow;
  reload: ReturnType<typeof vi.fn>;
  dispatchStorage: (event: StorageEvent) => void;
}

function createFakeDocument(): FakeDocumentHarness {
  const reload = vi.fn();
  const listeners = new Map<string, Set<EventListener>>();
  const fakeWindow: FakeWindow = {
    location: { reload },
    localStorage: globalThis.localStorage,
    addEventListener: (type, handler) => {
      const set = listeners.get(type) ?? new Set<EventListener>();
      set.add(handler);
      listeners.set(type, set);
    },
    removeEventListener: (type, handler) => {
      listeners.get(type)?.delete(handler);
    },
  };
  return {
    fakeWindow,
    reload,
    dispatchStorage: (event) => {
      for (const handler of listeners.get('storage') ?? []) handler(event);
    },
  };
}

interface Harness {
  mock: MockLibraryTransferTransport;
  controller: LibraryActivationController;
  store: TransferResumeStore;
  lease: TransferTabLease;
  document: FakeDocumentHarness;
}

function configure(
  options: MockLibraryTransferTransportOptions = {},
  wrap?: (inner: MockLibraryTransferTransport) => LibraryTransferTransport,
  document = createFakeDocument(),
): Harness {
  const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK, ...options });
  const transport: LibraryTransferTransport = wrap ? wrap(mock) : mock;
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport },
      { provide: DOCUMENT, useValue: { defaultView: document.fakeWindow } as unknown as Document },
    ],
  });
  const controller = TestBed.inject(LibraryActivationController);
  controller.pollIntervalMs = 5;
  controller.maxPollIntervalMs = 20;
  controller.completionReloadDelayMs = 5;
  return {
    mock,
    controller,
    store: TestBed.inject(TransferResumeStore),
    lease: TestBed.inject(TransferTabLease),
    document,
  };
}

/** Re-attaches a fresh controller against the same mock and persisted record. */
function reloadController(
  mock: MockLibraryTransferTransport,
  document = createFakeDocument(),
): Harness {
  const transport: LibraryTransferTransport = mock;
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [
      { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport },
      { provide: DOCUMENT, useValue: { defaultView: document.fakeWindow } as unknown as Document },
    ],
  });
  const controller = TestBed.inject(LibraryActivationController);
  controller.pollIntervalMs = 5;
  controller.maxPollIntervalMs = 20;
  controller.completionReloadDelayMs = 5;
  return {
    mock,
    controller,
    store: TestBed.inject(TransferResumeStore),
    lease: TestBed.inject(TransferTabLease),
    document,
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
    localStorage.removeItem(LIBRARY_REPLACED_STORAGE_KEY);
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

  it('persists only the accepted 202, not the request itself', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);
    harness.store.save(resumeRecord(jobId, 'rev-1'));

    await harness.controller.requestActivation(jobId);

    expect(harness.store.load()?.activation).toEqual({ accepted: true });
    expect(harness.store.load()?.activation).not.toHaveProperty('request');
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

  it('fetches fresh counts before the user confirms (reviewReplacement)', async () => {
    const harness = configure({
      destinationStatus: 'Populated',
      existingCounts: { books: 7, notes: 12, collections: 3 },
    });
    const jobId = await stageReadyJob(harness.mock);
    harness.store.save(resumeRecord(jobId, 'rev-1'));

    await harness.controller.reviewReplacement(jobId);

    const view = harness.controller.view();
    expect(view.state).toBe('idle');
    expect(view.conflict?.existingCounts?.books).toBe(7);
    // The probe never confirms: the destructive request waits for the user.
    expect(harness.mock.activationRequests).toEqual([
      { destinationRevision: 'rev-1', confirmReplacement: false },
    ]);

    await harness.controller.confirmReplacement(jobId);
    expect(harness.mock.activationRequests[1]?.confirmReplacement).toBe(true);
  });

  it('re-confirms a destination conflict with the revision from the 409', async () => {
    const harness = configure();
    // The user reviewed an older revision recorded at preflight.
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

  it('recovers a background confirmation-required failure with fresh 409 facts', async () => {
    const harness = configure({
      destinationStatus: 'Empty',
      existingCounts: { books: 3, notes: 1 },
    });
    const jobId = await stageReadyJob(harness.mock);

    // The 202 is accepted for an empty destination...
    await harness.controller.requestActivation(jobId);
    expect(harness.controller.view().state).toBe('in-progress');

    // ...then a live write populates it before the worker's Phase-A check.
    harness.mock.setDestinationStatus('Populated');
    harness.mock.failActivation(jobId, 'migration_replacement_confirmation_required', true);

    await vi.waitFor(
      () => expect(harness.controller.view().conflict?.existingCounts?.books).toBe(3),
      { timeout: 2_000 },
    );
    expect(harness.controller.view().state).toBe('idle');
    expect(harness.controller.view().conflict?.destinationStatus).toBe('Populated');
    // The probe never confirms; only the user's next action sends confirm=true.
    expect(harness.mock.activationRequests).toEqual([
      { destinationRevision: 'rev-1', confirmReplacement: false },
      { destinationRevision: 'rev-1', confirmReplacement: false },
    ]);

    await harness.controller.confirmReplacement(jobId);
    expect(harness.mock.activationRequests[2]).toEqual({
      destinationRevision: 'rev-1',
      confirmReplacement: true,
    });
  });

  it('stops polling once a background conflict opens the review (no flicker loop)', async () => {
    const harness = configure({
      destinationStatus: 'Populated',
      existingCounts: { books: 2 },
    });
    harness.controller.pollIntervalMs = 10;
    const jobId = await stageReadyJob(harness.mock);
    harness.store.save(resumeRecord(jobId, 'rev-1'));

    await harness.controller.confirmReplacement(jobId);
    expect(harness.controller.view().state).toBe('in-progress');

    // The background run detects a write after admission and fails with fresh
    // facts; the client probes for them exactly once.
    harness.mock.bumpDestinationRevision('rev-2');
    harness.mock.failActivation(jobId, 'migration_destination_conflict', true);
    await vi.waitFor(
      () => expect(harness.controller.view().conflict?.destinationRevision).toBe('rev-2'),
      { timeout: 2_000 },
    );
    expect(harness.controller.view().state).toBe('failed');

    // While the dialog is open, the still-failed durable status must not keep
    // re-triggering probes (the browser QA saw the counts flicker back to the
    // stale preflight values).
    const requests = harness.mock.activationRequests.length;
    await new Promise((resolve) => setTimeout(resolve, 120));
    expect(harness.mock.activationRequests.length).toBe(requests);
    expect(harness.controller.view().conflict?.destinationRevision).toBe('rev-2');
  });

  it('never treats repeated conflicts as terminal: the user can confirm each time', async () => {
    const harness = configure({
      destinationStatus: 'Populated',
      existingCounts: { books: 2 },
    });
    const jobId = await stageReadyJob(harness.mock);
    harness.store.save(resumeRecord(jobId, 'rev-1'));

    await harness.controller.confirmReplacement(jobId);
    expect(harness.controller.view().state).toBe('in-progress');

    for (const revision of ['rev-2', 'rev-3']) {
      harness.mock.bumpDestinationRevision(revision);
      harness.mock.failActivation(jobId, 'migration_destination_conflict', true);
      await vi.waitFor(
        () => expect(harness.controller.view().conflict?.destinationRevision).toBe(revision),
        { timeout: 2_000 },
      );
      expect(harness.controller.view().state).toBe('failed');
      expect(harness.controller.view().canRetry).toBe(true);

      await harness.controller.confirmReplacement(jobId);
      expect(harness.controller.view().state).toBe('in-progress');
    }

    const confirmedRevisions = harness.mock.activationRequests
      .filter((request) => request.confirmReplacement)
      .map((request) => request.destinationRevision);
    expect(confirmedRevisions).toEqual(['rev-1', 'rev-2', 'rev-3']);
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

  it('tolerates 503 maintenance and network errors while polling, clamping Retry-After', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);
    harness.mock.queueFailure({
      operation: 'getActivationStatus',
      code: 'migration_activation_busy',
      status: 503,
      times: 2,
      // Deliberately far above maxPollIntervalMs: the controller must clamp it.
      retryAfterMs: 60_000,
    });
    harness.mock.queueFailure({ operation: 'getActivationStatus', status: 0 });

    harness.mock.completeActivation(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('completed'), {
      timeout: 3_000,
    });

    expect(harness.controller.view().errorCode).toBeNull();
  });

  it('ignores a stale run whose status resolves after a new job started', async () => {
    let deferred!: DeferredStatusTransport;
    const harness = configure({}, (inner) => {
      deferred = new DeferredStatusTransport(inner);
      return deferred;
    });
    const jobA = await stageReadyJob(harness.mock, 'job-a');
    const jobB = await stageReadyJob(harness.mock, 'job-b');
    harness.store.save(resumeRecord(jobA, 'rev-1'));

    deferred.deferStatusFor(jobA);
    const runA = harness.controller.requestActivation(jobA);
    await vi.waitFor(() => expect(deferred.pendingStatus).toBe(true), { timeout: 2_000 });

    // A new job takes ownership of the root-scoped controller.
    await harness.controller.requestActivation(jobB);
    harness.store.save(resumeRecord(jobB, 'rev-1'));
    expect(harness.controller.view().jobId).toBe(jobB);

    // The old run's held GET now resolves; it must change nothing.
    deferred.release(activationStatus(jobA, { outcome: 'Completed', state: 'Completed' }));
    await runA;
    await new Promise((resolve) => setTimeout(resolve, 30));

    expect(harness.controller.view().jobId).toBe(jobB);
    expect(harness.controller.view().state).toBe('in-progress');
    expect(harness.store.load()?.jobId).toBe(jobB);
    expect(harness.store.load()?.activation).toBeUndefined();
    expect(
      harness.mock.activationRequests.every((request) => request.destinationRevision === 'rev-1'),
    ).toBe(true);
    expect(harness.mock.calls.activateJob).toBe(1);
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

  it('does not replay an undelivered confirmed request after a reload', async () => {
    let dropped!: DropActivationTransport;
    const first = configure({}, (inner) => {
      dropped = new DropActivationTransport(inner);
      return dropped;
    });
    first.controller.pollIntervalMs = 10_000;
    first.controller.maxPollIntervalMs = 10_000;
    const jobId = await stageReadyJob(first.mock);
    first.store.save(resumeRecord(jobId, 'rev-1'));

    dropped.dropNext = true;
    await first.controller.confirmReplacement(jobId);
    expect(first.controller.view().state).toBe('in-progress');
    expect(first.store.load()?.activation).toBeUndefined();

    first.controller.reset();

    const second = reloadController(first.mock);
    second.controller.reattach();

    expect(second.controller.view().state).toBe('idle');
    expect(first.mock.calls.activateJob).toBe(0);
    expect(second.store.load()?.activation).toBeUndefined();
  });

  it('respects another tab holding the lease during reattach', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);
    await harness.controller.requestActivation(jobId);
    harness.lease.release();
    harness.store.save({
      ...resumeRecord(jobId, 'rev-1'),
      activation: { accepted: true },
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

  it('reloads exactly once after a completed activation', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);
    harness.mock.completeActivation(jobId);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('completed'), {
      timeout: 2_000,
    });

    await vi.waitFor(() => expect(harness.document.reload).toHaveBeenCalledTimes(1), {
      timeout: 2_000,
    });
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(harness.document.reload).toHaveBeenCalledTimes(1);
    expect(harness.store.load()).toBeNull();
  });

  it('does not reload after a failed activation', async () => {
    const harness = configure();
    const jobId = await stageReadyJob(harness.mock);

    await harness.controller.requestActivation(jobId);
    harness.mock.failActivation(jobId, 'migration_activation_failed', true);
    await vi.waitFor(() => expect(harness.controller.view().state).toBe('failed'), {
      timeout: 2_000,
    });
    await new Promise((resolve) => setTimeout(resolve, 30));

    expect(harness.document.reload).not.toHaveBeenCalled();
  });

  it('reloads this tab when another tab announces the replaced library', async () => {
    const harness = configure();

    harness.document.dispatchStorage(
      new StorageEvent('storage', {
        key: LIBRARY_REPLACED_STORAGE_KEY,
        newValue: '12345',
      }),
    );

    await vi.waitFor(() => expect(harness.document.reload).toHaveBeenCalledTimes(1), {
      timeout: 2_000,
    });
  });
});
