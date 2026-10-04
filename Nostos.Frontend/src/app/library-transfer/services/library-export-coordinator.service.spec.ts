import { TestBed } from '@angular/core/testing';

import { DelegatingTransport } from '../testing/delegating-transport';
import { LIBRARY_TRANSFER_TRANSPORT, MigrationTransportError } from './library-transfer-transport';
import {
  DEFAULT_EXPORT_POLL_MS,
  LibraryExportCoordinator,
} from './library-export-coordinator.service';
import { MockLibraryTransferTransport } from './mock-library-transfer-transport.service';

/** Test control surface over the real mock transport. */
class ControlledExportTransport extends DelegatingTransport {
  readyOnCreate = false;
  failCreate = false;
  onCreated: ((jobId: string) => void) | null = null;
  artifactExpiresAtUtc: string | null = null;
  /** Sets the job to Validating on this getJob call number (createJob counts as 1). */
  setValidatingOnGetJob: number | null = null;

  private getJobCalls = 0;

  override async createJob(
    request: Parameters<DelegatingTransport['createJob']>[0],
    signal?: AbortSignal,
  ) {
    if (this.failCreate) {
      throw new MigrationTransportError('network_error', 0, 'connection lost');
    }
    const created = await this.inner.createJob(request, signal);
    this.onCreated?.(created.job.id);
    if (this.readyOnCreate) this.inner.markExportReady(created.job.id);
    return this.getJob(created.job.id, signal);
  }

  override async getJob(jobId: string, signal?: AbortSignal) {
    this.getJobCalls += 1;
    if (this.setValidatingOnGetJob === this.getJobCalls) {
      this.inner.setJobState(jobId, 'Validating');
    }
    const status = await this.inner.getJob(jobId, signal);
    return this.artifactExpiresAtUtc
      ? { ...status, artifactExpiresAtUtc: this.artifactExpiresAtUtc }
      : status;
  }
}

function setup() {
  const mock = new MockLibraryTransferTransport();
  const transport = new ControlledExportTransport(mock);
  TestBed.configureTestingModule({
    providers: [{ provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport }],
  });
  return {
    mock,
    transport,
    coordinator: TestBed.inject(LibraryExportCoordinator),
  };
}

describe('LibraryExportCoordinator', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
  });

  it('creates the export job and reaches ready with the native download URL', async () => {
    const { mock, transport, coordinator } = setup();
    transport.readyOnCreate = true;
    transport.artifactExpiresAtUtc = '2026-10-05T12:00:00Z';

    await coordinator.startExport();

    const state = coordinator.state();
    expect(state.kind).toBe('ready');
    if (state.kind === 'ready') {
      expect(state.downloadUrl).toContain('/api/portability/migration/jobs/');
      expect(state.downloadUrl).toContain('/export-download');
      expect(state.expiresAt).toBe('2026-10-05T12:00:00Z');
    }
    expect(mock.calls.getExportDownloadUrl).toBe(1);
    expect(mock.calls.getJob).toBeGreaterThan(0);
  });

  it('maps server phases to preparing and checking before ready', async () => {
    vi.useFakeTimers();
    const { mock, transport, coordinator } = setup();
    transport.setValidatingOnGetJob = 2;
    let jobId = '';
    transport.onCreated = (id) => {
      jobId = id;
    };
    coordinator.pollIntervalMs = DEFAULT_EXPORT_POLL_MS;

    const running = coordinator.startExport();

    await vi.advanceTimersByTimeAsync(1);
    expect(coordinator.state().kind).toBe('preparing');

    await vi.advanceTimersByTimeAsync(DEFAULT_EXPORT_POLL_MS);
    expect(coordinator.state().kind).toBe('checking');

    mock.markExportReady(jobId);
    await vi.advanceTimersByTimeAsync(DEFAULT_EXPORT_POLL_MS);
    expect(coordinator.state().kind).toBe('ready');

    await running;
  });

  it('fails with portable_export_failed when the job fails on the server', async () => {
    vi.useFakeTimers();
    const { mock, transport, coordinator } = setup();
    transport.onCreated = (jobId) => mock.setJobState(jobId, 'Failed');

    const running = coordinator.startExport();
    await vi.advanceTimersByTimeAsync(1);
    await vi.advanceTimersByTimeAsync(DEFAULT_EXPORT_POLL_MS);

    expect(coordinator.state()).toMatchObject({
      kind: 'failed',
      failure: { code: 'portable_export_failed', retryable: true },
    });
    await running;
  });

  it('cancels in preparing and records durable cancellation', async () => {
    vi.useFakeTimers();
    const { mock, coordinator } = setup();

    const running = coordinator.startExport();
    await vi.advanceTimersByTimeAsync(1);

    const state = coordinator.state();
    expect(state.kind).toBe('preparing');
    const jobId = state.kind === 'preparing' ? state.jobId : null;
    expect(jobId).toBeTruthy();
    expect(mock.calls.cancelJob).toBe(0);

    await coordinator.cancelExport();
    await running;

    expect(coordinator.state().kind).toBe('cancelled');
    expect(mock.calls.cancelJob).toBe(1);
    expect((await mock.getJob(jobId!)).job.state).toBe('Cancelled');
  });

  it('maps an expired job to a retryable session failure', async () => {
    vi.useFakeTimers();
    const { transport, coordinator } = setup();
    transport.onCreated = (jobId) => transport.inner.setJobState(jobId, 'Expired');

    const running = coordinator.startExport();
    await vi.advanceTimersByTimeAsync(1);
    await vi.advanceTimersByTimeAsync(DEFAULT_EXPORT_POLL_MS);

    expect(coordinator.state()).toMatchObject({
      kind: 'failed',
      failure: { code: 'migration_session_expired', retryable: true },
    });
    await running;
  });

  it('creates a fresh job for a new export after a terminal state', async () => {
    const { mock, transport, coordinator } = setup();
    transport.readyOnCreate = true;

    await coordinator.startExport();
    coordinator.dismiss();
    expect(coordinator.state().kind).toBe('idle');

    await coordinator.startExport();
    expect(coordinator.state().kind).toBe('ready');
    expect(mock.jobCreationCount).toBe(2);
  });
});
