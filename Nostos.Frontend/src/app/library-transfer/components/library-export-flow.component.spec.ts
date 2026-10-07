import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LibraryExportFlowComponent, isSafeDownloadUrl } from './library-export-flow.component';
import {
  DEFAULT_EXPORT_POLL_MS,
  LibraryExportCoordinator,
} from '../services/library-export-coordinator.service';
import { LIBRARY_TRANSFER_TRANSPORT } from '../services/library-transfer-transport';
import { MockLibraryTransferTransport } from '../testing/mock-library-transfer-transport';
import { ControlledExportTransport } from '../testing/transfer-transport-doubles';

interface Harness {
  mock: MockLibraryTransferTransport;
  transport: ControlledExportTransport;
  coordinator: LibraryExportCoordinator;
  fixture: ComponentFixture<LibraryExportFlowComponent>;
  component: LibraryExportFlowComponent;
}

function setup(): Harness {
  const mock = new MockLibraryTransferTransport();
  const transport = new ControlledExportTransport(mock);
  TestBed.configureTestingModule({
    providers: [{ provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport }],
  });
  const fixture = TestBed.createComponent(LibraryExportFlowComponent);
  const component = fixture.componentInstance;
  fixture.detectChanges();
  return {
    mock,
    transport,
    coordinator: TestBed.inject(LibraryExportCoordinator),
    fixture,
    component,
  };
}

async function waitForReady(harness: Harness): Promise<void> {
  await vi.waitFor(() => expect(harness.coordinator.state().kind).toBe('ready'), {
    timeout: 5_000,
  });
  harness.fixture.detectChanges();
}

function testId(harness: Harness, id: string): HTMLElement | null {
  return harness.fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
}

describe('LibraryExportFlowComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('offers the portable-export explanation and action while idle', () => {
    const harness = setup();

    expect(testId(harness, 'export-idle')?.textContent).toContain('portable .nostos archive');
    expect(testId(harness, 'export-idle')?.textContent).toContain(
      'move your library to another Nostos installation',
    );
    expect(testId(harness, 'export-start')?.textContent).toContain('Export library');
  });

  it('attempts one native download while keeping the fallback link usable', async () => {
    const harness = setup();
    harness.transport.readyOnCreate = true;
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click');

    (testId(harness, 'export-start') as HTMLButtonElement).click();
    await waitForReady(harness);

    expect(click).toHaveBeenCalled();
    expect(
      harness.fixture.nativeElement.querySelector('[data-testid="library-export-download"]'),
    ).toBeTruthy();
  });

  it('reaches ready with a native download link and never buffers the archive', async () => {
    const harness = setup();
    harness.fixture.componentRef.setInput('autoDownload', false);
    harness.transport.readyOnCreate = true;
    harness.transport.expiresAt = '2026-10-06T09:30:00Z';
    const createObjectURL = vi.spyOn(URL, 'createObjectURL');

    (testId(harness, 'export-start') as HTMLButtonElement).click();
    await waitForReady(harness);

    const link = harness.fixture.nativeElement.querySelector(
      '[data-testid="library-export-download"]',
    ) as HTMLAnchorElement;
    expect(link.tagName).toBe('A');
    expect(link.getAttribute('href')).toContain('/api/portability/migration/jobs/');
    expect(link.getAttribute('href')).toContain('/export-download');
    expect(link.textContent).toContain('Download archive');
    expect(testId(harness, 'export-ready')?.textContent).toContain(
      'If your browser did not start the download automatically, use Download archive.',
    );
    expect(testId(harness, 'export-expiry')?.textContent).toContain('2026');
    expect(createObjectURL).not.toHaveBeenCalled();

    (testId(harness, 'export-close') as HTMLButtonElement).click();
    harness.fixture.detectChanges();
    expect(testId(harness, 'export-idle')).toBeTruthy();
  });

  it('accepts only same-origin http or https download URLs', () => {
    expect(isSafeDownloadUrl('/api/portability/migration/jobs/1/export-download')).toBe(true);
    expect(isSafeDownloadUrl('https://cdn.example.com/archive.nostos')).toBe(true);
    expect(isSafeDownloadUrl(`${globalThis.location.origin}/download`)).toBe(true);
    expect(isSafeDownloadUrl('javascript:alert(1)')).toBe(false);
    expect(isSafeDownloadUrl('ftp://example.com/archive.nostos')).toBe(false);
    expect(isSafeDownloadUrl('http://evil.example/archive.nostos')).toBe(false);
    expect(isSafeDownloadUrl(null)).toBe(false);
  });

  it('refuses an unsafe automatic download URL and shows the fallback error instead of an anchor', async () => {
    const harness = setup();
    harness.transport.readyOnCreate = true;
    harness.transport.overrideDownloadUrl = 'javascript:alert(1)';
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click');

    (testId(harness, 'export-start') as HTMLButtonElement).click();
    await waitForReady(harness);

    expect(testId(harness, 'export-url-error')?.getAttribute('role')).toBe('alert');
    expect(
      harness.fixture.nativeElement.querySelector('[data-testid="library-export-download"]'),
    ).toBeNull();
    expect(testId(harness, 'export-restart')).toBeTruthy();
    expect(click).not.toHaveBeenCalled();
  });

  it('shows the preparing phase and cancels the export', async () => {
    const harness = setup();

    (testId(harness, 'export-start') as HTMLButtonElement).click();
    await vi.waitFor(
      () => {
        const state = harness.coordinator.state();
        expect(state.kind).toBe('preparing');
        if (state.kind === 'preparing') expect(state.jobId).toBeTruthy();
      },
      { timeout: 5_000 },
    );
    harness.fixture.detectChanges();

    expect(testId(harness, 'export-preparing')).toBeTruthy();
    expect(testId(harness, 'export-preparing')?.textContent).toContain('Preparing archive…');

    (testId(harness, 'export-cancel') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(harness.coordinator.state().kind).toBe('cancelled'), {
      timeout: 5_000,
    });
    harness.fixture.detectChanges();

    expect(testId(harness, 'export-cancelled')).toBeTruthy();
    expect(harness.mock.calls.cancelJob).toBe(1);
  });

  it('shows the server checking phase before the download is ready', async () => {
    vi.useFakeTimers();
    const harness = setup();
    harness.coordinator.pollIntervalMs = DEFAULT_EXPORT_POLL_MS;
    harness.transport.setValidatingOnGetJob = 2;
    let jobId = '';
    harness.transport.onCreated = (id) => {
      jobId = id;
    };

    (testId(harness, 'export-start') as HTMLButtonElement).click();
    await vi.advanceTimersByTimeAsync(1);
    harness.fixture.detectChanges();

    expect(testId(harness, 'export-preparing')).toBeTruthy();

    await vi.advanceTimersByTimeAsync(DEFAULT_EXPORT_POLL_MS);
    harness.fixture.detectChanges();
    expect(testId(harness, 'export-checking')?.textContent).toContain('Checking archive…');

    harness.mock.markExportReady(jobId);
    await vi.advanceTimersByTimeAsync(DEFAULT_EXPORT_POLL_MS);
    harness.fixture.detectChanges();
    expect(testId(harness, 'export-ready')).toBeTruthy();
  });

  it('renders an actionable failure and retries the export', async () => {
    const harness = setup();
    harness.transport.failCreate = true;

    (testId(harness, 'export-start') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(harness.coordinator.state().kind).toBe('failed'), {
      timeout: 5_000,
    });
    harness.fixture.detectChanges();

    expect(testId(harness, 'export-failed')?.getAttribute('role')).toBe('alert');
    expect(testId(harness, 'export-failed')?.textContent).toContain('The transfer didn’t finish');

    const start = vi.spyOn(harness.coordinator, 'startExport').mockResolvedValue();
    (testId(harness, 'export-retry') as HTMLButtonElement).click();
    expect(start).toHaveBeenCalledTimes(1);
  });
});
