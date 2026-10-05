import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { signal } from '@angular/core';
import { vi } from 'vitest';
import { CloudEntryComponent } from './cloud-entry.component';
import { CloudEntryService, CloudEntryView } from '../core/services/cloud-entry.service';
import { LibraryImportFlowComponent } from '../library-transfer/components/library-import-flow.component';
import { LibraryTransferCoordinator } from '../library-transfer/services/library-transfer-coordinator.service';
import {
  LIBRARY_TRANSFER_TRANSPORT,
  MigrationTransportError,
} from '../library-transfer/services/library-transfer-transport';
import { MockLibraryTransferTransport } from '../library-transfer/testing/mock-library-transfer-transport';
import { HASH_WORKER_FACTORY } from '../library-transfer/services/hash/hash-worker';
import {
  TRANSFER_RESUME_STORAGE_KEY,
} from '../library-transfer/services/transfer-resume-store.service';
import {
  TRANSFER_TAB_LEASE_KEY,
  TransferTabLease,
} from '../library-transfer/services/transfer-tab-lease.service';
import { DelegatingTransport } from '../library-transfer/testing/delegating-transport';
import {
  BrowserMigrationChunk,
  MigrationChunkUploadResultDto,
} from '../library-transfer/models/migration-http.dtos';
import {
  createFile,
  portableArchiveFixture,
  portableManifest,
} from '../library-transfer/testing/zip-archive.fixture';

describe('CloudEntryComponent', () => {
  let mockEntry: {
    view: ReturnType<typeof signal<CloudEntryView>>;
    actionPending: ReturnType<typeof signal<boolean>>;
    actionError: ReturnType<typeof signal<string | null>>;
    checkoutRedirect: ReturnType<typeof signal<string | null>>;
    productReady: ReturnType<typeof signal<boolean>>;
    selectedOffer: ReturnType<typeof signal<any>>;
    supportsLibraryMigration: ReturnType<typeof signal<boolean>>;
    supportsSafeActivation: ReturnType<typeof signal<boolean>>;
    beginCheckout: ReturnType<typeof vi.fn>;
    checkSubscription: ReturnType<typeof vi.fn>;
    loginUrl: ReturnType<typeof vi.fn>;
    startFresh: ReturnType<typeof vi.fn>;
    finishFirstRunAfterImport: ReturnType<typeof vi.fn>;
    importPortableArchive: ReturnType<typeof vi.fn>;
  };
  let mock: MockLibraryTransferTransport;

  beforeEach(() => {
    localStorage.clear();
    mock = new MockLibraryTransferTransport({ chunkSizeBytes: 4 * 1024 * 1024 });
    mockEntry = {
      view: signal<CloudEntryView>({
        kind: 'subscription_required',
        onboarding: {
          state: 'subscription_required',
          subscriptionStatus: 'None',
          ready: false,
          canCheckout: true,
          canCheckSubscription: false,
          canManageSubscription: false,
          canRetry: false,
          selectedOffer: {
            offerId: 'standard-monthly',
            planName: 'Standard',
            billingCadence: 'Monthly',
          },
        },
      }),
      actionPending: signal(false),
      actionError: signal<string | null>(null),
      checkoutRedirect: signal<string | null>(null),
      productReady: signal(false),
      selectedOffer: signal({
        offerId: 'standard-monthly',
        planName: 'Standard',
        billingCadence: 'Monthly',
      }),
      supportsLibraryMigration: signal(false),
      supportsSafeActivation: signal(false),
      beginCheckout: vi.fn(),
      checkSubscription: vi.fn(),
      loginUrl: vi.fn().mockReturnValue('/api/auth/login'),
      startFresh: vi.fn(),
      finishFirstRunAfterImport: vi.fn(),
      importPortableArchive: vi.fn(),
    };

    TestBed.configureTestingModule({
      imports: [CloudEntryComponent],
      providers: [
        { provide: CloudEntryService, useValue: mockEntry },
        { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: mock },
        { provide: HASH_WORKER_FACTORY, useValue: () => null },
      ],
    });
  });

  afterEach(() => {
    localStorage.removeItem(TRANSFER_RESUME_STORAGE_KEY);
    localStorage.removeItem(TRANSFER_TAB_LEASE_KEY);
  });

  function renderFirstRun(): void {
    mockEntry.view.set({ kind: 'first_run' });
  }

  async function portableFile(): Promise<File> {
    return createFile(await portableArchiveFixture(portableManifest()));
  }

  function testId(fixture: ReturnType<typeof TestBed.createComponent>, id: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  }

  function importChoice(
    fixture: ReturnType<typeof TestBed.createComponent>,
  ): HTMLButtonElement {
    const buttons = Array.from(
      fixture.nativeElement.querySelectorAll('button'),
    ) as HTMLButtonElement[];
    const choice = buttons.find((button) =>
      (button.textContent ?? '').includes('Import an existing Nostos library'),
    );
    if (!choice) throw new Error('Import choice button not found');
    return choice;
  }

  interface PendingUpload {
    jobId: string;
    sessionId: string;
    request: BrowserMigrationChunk;
    onProgress: (loaded: number, total: number) => void;
    signal: AbortSignal;
    resolve: (result: MigrationChunkUploadResultDto) => void;
    reject: (error: unknown) => void;
  }

  /** Holds upload requests so a transfer can be observed while it is active. */
  class HeldUploadTransport extends DelegatingTransport {
    readonly pending: PendingUpload[] = [];

    override uploadChunk(
      jobId: string,
      sessionId: string,
      request: BrowserMigrationChunk,
      onProgress: (loaded: number, total: number) => void,
      signal: AbortSignal,
    ): Promise<MigrationChunkUploadResultDto> {
      return new Promise<MigrationChunkUploadResultDto>((resolve, reject) => {
        signal.addEventListener(
          'abort',
          () => reject(new MigrationTransportError('request_aborted', 0, 'aborted')),
          { once: true },
        );
        this.pending.push({ jobId, sessionId, request, onProgress, signal, resolve, reject });
      });
    }

    async releaseAll(): Promise<void> {
      const items = this.pending.splice(0);
      await Promise.all(
        items.map(async (item) => {
          try {
            const result = await this.inner.uploadChunk(
              item.jobId,
              item.sessionId,
              item.request,
              item.onProgress,
              item.signal,
            );
            item.resolve(result);
          } catch (error) {
            item.reject(error);
          }
        }),
      );
    }
  }

  it('renders plan summary and active Continue to checkout button when selectedOffer is present', () => {
    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('Continue with Standard on Monthly billing');

    const button = compiled.querySelector('button.nostos-button--primary') as HTMLButtonElement;
    expect(button).toBeTruthy();
    expect(button.textContent).toContain('Continue to checkout');
    expect(button.disabled).toBe(false);

    button.click();
    expect(mockEntry.beginCheckout).toHaveBeenCalledWith('standard-monthly');
  });

  it('renders View Cloud plans link when selectedOffer is null (direct login)', () => {
    mockEntry.selectedOffer.set(null);
    mockEntry.view.set({
      kind: 'subscription_required',
      onboarding: {
        state: 'subscription_required',
        subscriptionStatus: 'None',
        ready: false,
        canCheckout: true,
        canCheckSubscription: false,
        canManageSubscription: false,
        canRetry: false,
        selectedOffer: null,
      },
    });

    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('Choose a plan on our pricing page to start your Cloud library');

    const link = compiled.querySelector('a.nostos-button--primary') as HTMLAnchorElement;
    expect(link).toBeTruthy();
    expect(link.textContent).toContain('View Cloud plans');
    expect(link.getAttribute('href')).toBe('https://nostos.page/pricing');
  });

  it('renders Check subscription button if canCheckSubscription is true without changing view state', () => {
    mockEntry.view.set({
      kind: 'subscription_required',
      onboarding: {
        state: 'subscription_pending',
        subscriptionStatus: 'Pending',
        ready: false,
        canCheckout: true,
        canCheckSubscription: true,
        canManageSubscription: false,
        canRetry: false,
        selectedOffer: {
          offerId: 'standard-monthly',
          planName: 'Standard',
          billingCadence: 'Monthly',
        },
      },
    });

    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const checkBtn = compiled.querySelector('button.nostos-button--secondary') as HTMLButtonElement;
    expect(checkBtn).toBeTruthy();
    expect(checkBtn.textContent).toContain('Check subscription');

    checkBtn.click();
    expect(mockEntry.checkSubscription).toHaveBeenCalledTimes(1);
  });

  it('omits Continue to checkout button when canCheckout is false', () => {
    mockEntry.view.set({
      kind: 'subscription_required',
      onboarding: {
        state: 'subscription_required',
        subscriptionStatus: 'None',
        ready: false,
        canCheckout: false,
        canCheckSubscription: true,
        canManageSubscription: false,
        canRetry: false,
        selectedOffer: {
          offerId: 'standard-monthly',
          planName: 'Standard',
          billingCadence: 'Monthly',
        },
      },
    });

    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const checkoutBtn = compiled.querySelector('button.nostos-button--primary') as HTMLButtonElement;
    expect(checkoutBtn).toBeFalsy();

    const checkBtn = compiled.querySelector('button.nostos-button--secondary') as HTMLButtonElement;
    expect(checkBtn).toBeTruthy();
  });

  it('redirects when checkoutRedirect signal emits a url', () => {
    const fixture = TestBed.createComponent(CloudEntryComponent);
    const navigateSpy = vi.spyOn(fixture.componentInstance, 'navigateTo').mockImplementation(() => {});

    TestBed.flushEffects();

    mockEntry.checkoutRedirect.set('https://nostos.page/pay?_ptxn=txn_123');
    TestBed.flushEffects();

    expect(navigateSpy).toHaveBeenCalledWith('https://nostos.page/pay?_ptxn=txn_123');
  });

  it('keeps the legacy first-run import picker while migration is off', () => {
    renderFirstRun();
    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    expect(importChoice(fixture)).toBeTruthy();
    expect(fixture.nativeElement.querySelector('app-library-import-flow')).toBeNull();
    const input = fixture.nativeElement.querySelector(
      'input[type="file"]',
    ) as HTMLInputElement;
    expect(input).toBeTruthy();
    expect(input.accept).toContain('.nostos');

    const file = new File(['archive'], 'library.nostos');
    fixture.componentInstance.importSelected({
      target: { files: [file] },
    } as unknown as Event);

    expect(mockEntry.importPortableArchive).toHaveBeenCalledWith(file);
    expect(mockEntry.finishFirstRunAfterImport).not.toHaveBeenCalled();
  });

  it('opens the shared import flow when the host advertises migration and finishes first run on completion', async () => {
    mockEntry.supportsLibraryMigration.set(true);
    renderFirstRun();
    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    importChoice(fixture).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('app-library-import-flow')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('input.cloud-entry__file')).toBeNull();
    expect(
      fixture.nativeElement.querySelector(
        'input[data-testid="library-import-file-input"]',
      ),
    ).toBeTruthy();

    const flow = fixture.debugElement.query(By.directive(LibraryImportFlowComponent))
      .componentInstance as LibraryImportFlowComponent;
    // Slice B8: the onboarding host forwards the server capability instead of
    // hard-coding safe activation off.
    mockEntry.supportsSafeActivation.set(true);
    fixture.detectChanges();
    expect(flow.supportsSafeActivation()).toBe(true);

    const file = await portableFile();
    flow.onFileSelected({ target: { files: [file], value: 'picked' } } as unknown as Event);

    const coordinator = TestBed.inject(LibraryTransferCoordinator);
    await vi.waitFor(() => expect(coordinator.state().kind).toBe('ready-empty'), {
      timeout: 5_000,
    });
    fixture.detectChanges();
    expect(testId(fixture, 'import-activation-unavailable')).toBeNull();

    await vi.waitFor(() => expect(mock.calls.activateJob).toBe(1), { timeout: 5_000 });
    const state = coordinator.state();
    const jobId = state.kind === 'ready-empty' ? state.jobId : '';
    mock.completeActivation(jobId);

    await vi.waitFor(() => expect(mockEntry.finishFirstRunAfterImport).toHaveBeenCalledTimes(1), {
      timeout: 5_000,
    });
  });

  it('keeps the first-run choice when a shared import fails', async () => {
    mockEntry.supportsLibraryMigration.set(true);
    mock.queueFailure({ operation: 'preflight', code: 'migration_storage_exhausted', status: 507 });
    renderFirstRun();
    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    importChoice(fixture).click();
    fixture.detectChanges();

    const flow = fixture.debugElement.query(By.directive(LibraryImportFlowComponent))
      .componentInstance as LibraryImportFlowComponent;
    const file = await portableFile();
    flow.onFileSelected({ target: { files: [file], value: 'picked' } } as unknown as Event);

    const coordinator = TestBed.inject(LibraryTransferCoordinator);
    await vi.waitFor(() => expect(coordinator.state().kind).toBe('failed'), { timeout: 5_000 });
    fixture.detectChanges();

    expect(mockEntry.finishFirstRunAfterImport).not.toHaveBeenCalled();
    expect(mockEntry.view().kind).toBe('first_run');
    expect(testId(fixture, 'cloud-entry-import-back')).toBeTruthy();

    (testId(fixture, 'cloud-entry-import-back') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(importChoice(fixture)).toBeTruthy();
  });

  it('adopts the tab lease after Back and releases it when the import terminates', async () => {
    mockEntry.supportsLibraryMigration.set(true);
    const held = new HeldUploadTransport(mock);
    TestBed.overrideProvider(LIBRARY_TRANSFER_TRANSPORT, { useValue: held });
    renderFirstRun();
    const fixture = TestBed.createComponent(CloudEntryComponent);
    fixture.detectChanges();

    importChoice(fixture).click();
    fixture.detectChanges();

    const flow = fixture.debugElement.query(By.directive(LibraryImportFlowComponent))
      .componentInstance as LibraryImportFlowComponent;
    const file = await portableFile();
    flow.onFileSelected({ target: { files: [file], value: 'picked' } } as unknown as Event);

    const coordinator = TestBed.inject(LibraryTransferCoordinator);
    const lease = TestBed.inject(TransferTabLease);
    await vi.waitFor(() => expect(coordinator.state().kind).toBe('uploading'), {
      timeout: 5_000,
    });
    await vi.waitFor(() => expect(held.pending.length).toBeGreaterThan(0));
    expect(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)).not.toBeNull();
    expect(lease.heartbeatActive).toBe(true);

    // Leaving the flow keeps the root-scoped transfer and its lease.
    (testId(fixture, 'cloud-entry-import-back') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('app-library-import-flow')).toBeNull();
    expect(coordinator.state().kind).toBe('uploading');
    expect(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)).not.toBeNull();

    // Re-entering adopts the same-tab lease and shows the running state.
    importChoice(fixture).click();
    fixture.detectChanges();
    expect(testId(fixture, 'import-uploading')).toBeTruthy();

    await held.releaseAll();
    await vi.waitFor(() => expect(coordinator.state().kind).toBe('ready-empty'), {
      timeout: 5_000,
    });
    fixture.detectChanges();

    (testId(fixture, 'import-cancel') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(coordinator.state().kind).toBe('cancelled'), {
      timeout: 5_000,
    });
    fixture.detectChanges();

    expect(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)).toBeNull();
    expect(lease.heartbeatActive).toBe(false);
  });
});
