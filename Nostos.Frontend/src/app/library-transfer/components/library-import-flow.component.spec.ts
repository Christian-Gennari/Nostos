import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { LibraryImportFlowComponent } from './library-import-flow.component';
import {
  LibraryTransferCoordinator,
} from '../services/library-transfer-coordinator.service';
import {
  TransferResumeStore,
  TRANSFER_RESUME_STORAGE_KEY,
} from '../services/transfer-resume-store.service';
import {
  TransferTabLease,
  TransferTabLeaseRecord,
  TRANSFER_TAB_LEASE_KEY,
  TRANSFER_TAB_LEASE_TTL_MS,
} from '../services/transfer-tab-lease.service';
import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
  MigrationTransportError,
} from '../services/library-transfer-transport';
import { MockLibraryTransferTransport } from '../testing/mock-library-transfer-transport';
import { FileDigestService } from '../services/file-digest.service';
import { FileDigest, PersistedTransferResumeState, TransferFileIdentity } from '../models/library-transfer.models';
import { HASH_WORKER_FACTORY } from '../services/hash/hash-worker';
import { DelegatingTransport } from '../testing/delegating-transport';
import {
  buildZipArchive,
  createFile,
  portableArchiveFixture,
  portableManifest,
} from '../testing/zip-archive.fixture';
import {
  BrowserMigrationChunk,
  MigrationChunkUploadResultDto,
  MigrationJobStatusResponseDto,
  MigrationPreflightRequestDto,
  MigrationSessionRequestDto,
  MigrationSessionStatusDto,
  chunkCount,
} from '../models/migration-http.dtos';

const CHUNK = 4 * 1024 * 1024;
const encoder = new TextEncoder();

interface Harness {
  mock: MockLibraryTransferTransport;
  transport: LibraryTransferTransport;
  coordinator: LibraryTransferCoordinator;
  store: TransferResumeStore;
  lease: TransferTabLease;
  fixture: ComponentFixture<LibraryImportFlowComponent>;
  component: LibraryImportFlowComponent;
}

function configure(
  mock: MockLibraryTransferTransport,
  transport: LibraryTransferTransport,
  digest?: FileDigest,
): Harness {
  TestBed.configureTestingModule({
    providers: [
      { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport },
      { provide: HASH_WORKER_FACTORY, useValue: () => null },
      ...(digest ? [{ provide: FileDigestService, useValue: digest }] : []),
    ],
  });
  const fixture = TestBed.createComponent(LibraryImportFlowComponent);
  const component = fixture.componentInstance;
  fixture.detectChanges();
  return {
    mock,
    transport,
    coordinator: TestBed.inject(LibraryTransferCoordinator),
    store: TestBed.inject(TransferResumeStore),
    lease: TestBed.inject(TransferTabLease),
    fixture,
    component,
  };
}

function setup(
  options: ConstructorParameters<typeof MockLibraryTransferTransport>[0] = {},
): Harness {
  const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK, ...options });
  return configure(mock, mock);
}

function reload(mock: MockLibraryTransferTransport, transport: LibraryTransferTransport): Harness {
  TestBed.resetTestingModule();
  return configure(mock, transport);
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

function selectFile(harness: Harness, file: File): void {
  const event = { target: { files: [file], value: 'picked' } } as unknown as Event;
  harness.component.onFileSelected(event);
  harness.fixture.detectChanges();
}

async function waitForKind(
  harness: Harness,
  kind: ReturnType<LibraryTransferCoordinator['state']>['kind'],
): Promise<void> {
  await vi.waitFor(() => expect(harness.coordinator.state().kind).toBe(kind), { timeout: 5_000 });
  harness.fixture.detectChanges();
}

function testId(harness: Harness, id: string): HTMLElement | null {
  return harness.fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
}

/** Advances promise and macrotask work with real timers, rendering after each turn. */
async function flushReal(harness: Harness, rounds = 40): Promise<void> {
  for (let index = 0; index < rounds; index += 1) {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
    harness.fixture.detectChanges();
  }
}

/** Flushes microtasks and timers under fake timers, rendering after each turn. */
async function flushFake(harness: Harness, rounds = 10): Promise<void> {
  for (let index = 0; index < rounds; index += 1) {
    await vi.advanceTimersByTimeAsync(0);
    harness.fixture.detectChanges();
  }
}

function preflightRequest(archiveBytes: number): MigrationPreflightRequestDto {
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

/** Stages a durable import whose upload is missing `missing` chunks. */
async function stageResumable(
  harness: Harness,
  file: File,
  seededChunks: number,
): Promise<PersistedTransferResumeState> {
  const digest = TestBed.inject(FileDigestService);
  const preflight = await harness.mock.preflight(preflightRequest(file.size));
  const jobKey = `stage-job-${seededChunks}`;
  const job = await harness.mock.createJob({
    direction: 'Import',
    idempotencyKey: jobKey,
    reservationId: preflight.reservationId,
  });
  const identity: TransferFileIdentity = {
    totalSizeBytes: file.size,
    sha256Checksum: await digest.sha256(file),
    clientFingerprint: await digest.fingerprint(file),
  };
  const sessionKey = `stage-session-${seededChunks}`;
  const sessionRequest: MigrationSessionRequestDto = {
    purpose: 'Import',
    totalBytes: file.size,
    chunkSize: CHUNK,
    totalChunks: chunkCount(file.size, CHUNK),
    fileIdentity: identity,
    idempotencyKey: sessionKey,
  };
  const session = await harness.mock.createUploadSession(job.job.id, sessionRequest);
  await harness.mock.seedReceivedChunks(
    job.job.id,
    Array.from({ length: seededChunks }, (_, index) => index),
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
    preflightDecision: 'AllowedEmpty',
    createdAt: new Date().toISOString(),
  };
  harness.store.save(record);
  return record;
}

function stageRecord(): PersistedTransferResumeState {
  return {
    schemaVersion: 1,
    jobId: 'other-job',
    jobCreationIdempotencyKey: 'other-job-key',
    direction: 'import',
    fileIdentity: { totalSizeBytes: 1024, sha256Checksum: 'a'.repeat(64) },
    fileName: 'other.nostos',
    preflightRequest: {} as never,
    createdAt: new Date().toISOString(),
  };
}

function dispatchLease(record: TransferTabLeaseRecord | null): void {
  if (record) localStorage.setItem(TRANSFER_TAB_LEASE_KEY, JSON.stringify(record));
  else localStorage.removeItem(TRANSFER_TAB_LEASE_KEY);
  window.dispatchEvent(
    new StorageEvent('storage', {
      key: TRANSFER_TAB_LEASE_KEY,
      newValue: record ? JSON.stringify(record) : null,
      storageArea: localStorage,
    }),
  );
}

/** Uploads that the test holds until it is ready to complete them. */
interface PendingUpload {
  jobId: string;
  sessionId: string;
  request: BrowserMigrationChunk;
  onProgress: (loaded: number, total: number) => void;
  signal: AbortSignal;
  resolve: (result: MigrationChunkUploadResultDto) => void;
  reject: (error: unknown) => void;
}

class GatedUploadTransport extends DelegatingTransport {
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

class ControlledDigest implements FileDigest {
  onProgress: ((bytesRead: number, totalBytes: number) => void) | null = null;

  sha256(
    _blob: Blob,
    options: {
      signal?: AbortSignal;
      readBlockBytes?: number;
      onProgress?: (bytesRead: number, totalBytes: number) => void;
    } = {},
  ): Promise<string> {
    this.onProgress = options.onProgress ?? null;
    return new Promise<string>((_resolve, reject) => {
      options.signal?.addEventListener(
        'abort',
        () => reject(new MigrationTransportError('request_aborted', 0, 'aborted')),
        { once: true },
      );
    });
  }

  sha256Chunk(blob: Blob): Promise<string> {
    void blob;
    return Promise.resolve('a'.repeat(64));
  }

  fingerprint(file: Blob): Promise<string> {
    void file;
    return Promise.resolve(`nostos-fp-v1:${'b'.repeat(64)}`);
  }
}

/** Holds the preflight response until the test releases it. */
class DeferredPreflightTransport extends DelegatingTransport {
  private gate: (() => void) | null = null;

  override async preflight(
    request: MigrationPreflightRequestDto,
    signal?: AbortSignal,
  ): Promise<Awaited<ReturnType<DelegatingTransport['preflight']>>> {
    const response = await this.inner.preflight(request, signal);
    await new Promise<void>((resolve, reject) => {
      const onAbort = () => reject(new MigrationTransportError('request_aborted', 0, 'aborted'));
      signal?.addEventListener('abort', onAbort, { once: true });
      this.gate = () => {
        signal?.removeEventListener('abort', onAbort);
        resolve();
      };
    });
    return response;
  }

  release(): void {
    this.gate?.();
    this.gate = null;
  }
}

class FailingCompleteTransport extends DelegatingTransport {
  override completeUpload(): Promise<MigrationSessionStatusDto> {
    return Promise.reject(new MigrationTransportError('network_error', 0, 'connection lost'));
  }
}

/** Rejects the first resume with a 401, then behaves normally (re-authenticated). */
class UnauthorizedOnceTransport extends DelegatingTransport {
  unauthorized = true;

  override getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto> {
    if (this.unauthorized) {
      return Promise.reject(
        new MigrationTransportError('unexpected_error', 401, 'sign in required'),
      );
    }
    return this.inner.getJob(jobId, signal);
  }
}

/** Holds status responses so the checking phase can be inspected before it ends. */
class GatedStatusTransport extends DelegatingTransport {
  private readonly pendingGets: Array<{
    jobId: string;
    signal?: AbortSignal;
    resolve: (status: MigrationJobStatusResponseDto) => void;
    reject: (error: unknown) => void;
  }> = [];

  override getJob(jobId: string, signal?: AbortSignal): Promise<MigrationJobStatusResponseDto> {
    return new Promise<MigrationJobStatusResponseDto>((resolve, reject) => {
      signal?.addEventListener(
        'abort',
        () => reject(new MigrationTransportError('request_aborted', 0, 'aborted')),
        { once: true },
      );
      this.pendingGets.push({ jobId, signal, resolve, reject });
    });
  }

  async releaseAll(): Promise<void> {
    const items = this.pendingGets.splice(0);
    await Promise.all(
      items.map(async (item) => {
        try {
          item.resolve(await this.inner.getJob(item.jobId, item.signal));
        } catch (error) {
          item.reject(error);
        }
      }),
    );
  }
}

describe('LibraryImportFlowComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(TRANSFER_RESUME_STORAGE_KEY);
    localStorage.removeItem(TRANSFER_TAB_LEASE_KEY);
    vi.useRealTimers();
  });

  it('renders a real file input and the import picker while idle', () => {
    const harness = setup();

    const input = harness.fixture.nativeElement.querySelector(
      'input[type="file"]',
    ) as HTMLInputElement;
    expect(input).toBeTruthy();
    expect(input.accept).toContain('.nostos');
    expect(testId(harness, 'import-idle')).toBeTruthy();

    const button = testId(harness, 'import-choose-file') as HTMLButtonElement;
    expect(button.tagName).toBe('BUTTON');
    expect(button.type).toBe('button');
  });

  it('runs the empty-destination happy path to ready without any confirmation', async () => {
    const harness = setup();
    const completed = vi.fn();
    const confirmed = vi.fn();
    harness.component.importCompleted.subscribe(completed);
    harness.component.replacementConfirmed.subscribe(confirmed);

    const file = await portableFile();
    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');

    expect(testId(harness, 'import-ready-empty')).toBeTruthy();
    expect(harness.mock.uploadedChunks.length).toBeGreaterThan(0);
    expect(confirmed).not.toHaveBeenCalled();
    expect(completed).not.toHaveBeenCalled();
  });

  it('shows hashing progress with a cancel option while the digest runs', async () => {
    const digest = new ControlledDigest();
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const harness = configure(mock, mock, digest);
    const file = await portableFile();

    selectFile(harness, file);
    await vi.waitFor(() => expect(digest.onProgress).toBeTruthy());

    digest.onProgress?.(50, 100);
    harness.fixture.detectChanges();

    expect(testId(harness, 'import-inspecting')).toBeTruthy();
    expect(
      harness.fixture.nativeElement.querySelector('.transfer-progress-label').textContent,
    ).toContain('Checking file');
    expect(
      harness.fixture.nativeElement.querySelector('.transfer-progress-percent').textContent,
    ).toContain('50%');
    expect(testId(harness, 'import-cancel')).toBeTruthy();

    (testId(harness, 'import-cancel') as HTMLButtonElement).click();
    await waitForKind(harness, 'cancelled');

    expect(mock.calls.preflight).toBe(0);
    expect(testId(harness, 'import-cancelled')).toBeTruthy();
  });

  it('renders the preflight phase and cancels before any job is created', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const transport = new DeferredPreflightTransport(mock);
    const harness = configure(mock, transport);
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'preflighting');

    expect(testId(harness, 'import-preflighting')).toBeTruthy();
    expect(testId(harness, 'import-cancel')).toBeTruthy();

    (testId(harness, 'import-cancel') as HTMLButtonElement).click();
    await waitForKind(harness, 'cancelled');
    expect(mock.calls.createJob).toBe(0);
  });

  it('cancels an in-flight upload and keeps the existing library untouched', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const transport = new GatedUploadTransport(mock);
    const harness = configure(mock, transport);
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'uploading');
    await vi.waitFor(() => expect(transport.pending.length).toBeGreaterThan(0));

    (testId(harness, 'import-cancel') as HTMLButtonElement).click();
    await waitForKind(harness, 'cancelled');

    expect(mock.calls.cancelJob).toBe(1);
    expect(testId(harness, 'import-cancelled')?.textContent).toContain(
      'Your existing library was not changed.',
    );
  });

  it('pauses and resumes a multi-chunk upload from the controls', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const transport = new GatedUploadTransport(mock);
    const harness = configure(mock, transport);
    const file = await largePortableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'uploading');
    await vi.waitFor(() => expect(transport.pending.length).toBe(2));

    const pausing = harness.component.pause();
    await transport.releaseAll();
    await pausing;
    harness.fixture.detectChanges();

    expect(harness.coordinator.state()).toMatchObject({ kind: 'uploading', paused: true });
    expect(testId(harness, 'import-resume-upload')).toBeTruthy();
    expect(testId(harness, 'import-uploading')?.textContent).toContain('Import paused.');

    void harness.component.resume();
    await vi.waitFor(() => expect(transport.pending.length).toBe(1));
    await transport.releaseAll();
    await waitForKind(harness, 'ready-empty');
  });

  it('reattaches after a reload and accepts the same file to finish the upload', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const totalChunks = chunkCount(file.size, CHUNK);
    await stageResumable(harness, file, totalChunks - 1);

    const reloaded = reload(harness.mock, harness.mock);
    await waitForKind(reloaded, 'ready-to-upload');

    expect(testId(reloaded, 'import-resume-file')).toBeTruthy();
    expect(testId(reloaded, 'import-reselect')?.textContent).toContain(
      'Select the same file again to resume.',
    );

    selectFile(reloaded, file);
    await waitForKind(reloaded, 'ready-empty');

    expect(harness.mock.uploadedChunks).toEqual([totalChunks - 1]);
  });

  it('rejects a different file on resume and never uploads it', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const totalChunks = chunkCount(file.size, CHUNK);
    await stageResumable(harness, file, totalChunks - 1);

    const reloaded = reload(harness.mock, harness.mock);
    await waitForKind(reloaded, 'ready-to-upload');

    const different = new File([new Uint8Array(file.size).fill(3) as unknown as BlobPart], file.name);
    selectFile(reloaded, different);

    await vi.waitFor(() =>
      expect(testId(reloaded, 'import-reselect-notice')).toBeTruthy(),
    );
    reloaded.fixture.detectChanges();

    expect(reloaded.coordinator.state().kind).toBe('ready-to-upload');
    expect(testId(reloaded, 'import-reselect-notice')?.textContent).toContain(
      'no longer matches this upload',
    );
    expect(harness.mock.uploadedChunks).toEqual([]);
  });

  it('gates replacement confirmation when safe activation is unavailable and enables it when available', async () => {
    const harness = setup({ destinationStatus: 'Populated', existingCounts: { books: 1 } });
    const confirmed = vi.fn();
    harness.component.replacementConfirmed.subscribe(confirmed);
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'replacement-confirmation');

    const dialog = harness.fixture.nativeElement.querySelector(
      'app-library-replacement-dialog',
    ) as HTMLElement;
    expect(dialog).toBeTruthy();
    expect(testId(harness, 'replacement-blocked')).toBeTruthy();

    const confirmButton = () =>
      harness.fixture.nativeElement.querySelector('.replacement-confirm') as HTMLButtonElement;
    expect(confirmButton().disabled).toBe(true);
    confirmButton().click();
    expect(confirmed).not.toHaveBeenCalled();

    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    harness.fixture.detectChanges();
    expect(confirmButton().disabled).toBe(false);

    confirmButton().click();
    confirmButton().click();
    expect(confirmed).toHaveBeenCalledTimes(1);
    expect(confirmed).toHaveBeenCalledWith(
      harness.coordinator.state().kind === 'replacement-confirmation'
        ? (harness.coordinator.state() as { jobId: string }).jobId
        : expect.any(String),
    );
  });

  it('seals the replacement dialog after confirmation: Escape, backdrop, Cancel and repeat confirms are inert', async () => {
    const harness = setup({ destinationStatus: 'Populated', existingCounts: { books: 1 } });
    const confirmed = vi.fn();
    harness.component.replacementConfirmed.subscribe(confirmed);
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'replacement-confirmation');
    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    harness.fixture.detectChanges();

    const confirmButton = () =>
      harness.fixture.nativeElement.querySelector('.replacement-confirm') as HTMLButtonElement;
    const cancelButton = () =>
      harness.fixture.nativeElement.querySelector('.replacement-cancel') as HTMLButtonElement;

    confirmButton().click();
    harness.fixture.detectChanges();

    expect(confirmed).toHaveBeenCalledTimes(1);
    expect(testId(harness, 'replacement-sealed')).toBeTruthy();
    expect(cancelButton().disabled).toBe(true);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    const backdrop = harness.fixture.nativeElement.querySelector('.modal-backdrop') as HTMLElement;
    for (const type of ['pointerdown', 'pointerup'] as const) {
      const event = new Event(type, { bubbles: true }) as PointerEvent;
      Object.defineProperty(event, 'pointerId', { value: 1 });
      backdrop.dispatchEvent(event);
    }
    cancelButton().click();
    confirmButton().click();
    harness.fixture.detectChanges();

    expect(confirmed).toHaveBeenCalledTimes(1);
    expect(harness.coordinator.state().kind).toBe('replacement-confirmation');
    expect(testId(harness, 'import-completed')).toBeNull();
  });

  it('unseals the replacement dialog on a host activation failure and allows retry or cancel', async () => {
    const harness = setup({ destinationStatus: 'Populated', existingCounts: { books: 1 } });
    const confirmed = vi.fn();
    harness.component.replacementConfirmed.subscribe(confirmed);
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'replacement-confirmation');
    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    harness.fixture.detectChanges();

    const confirmButton = () =>
      harness.fixture.nativeElement.querySelector('.replacement-confirm') as HTMLButtonElement;
    const cancelButton = () =>
      harness.fixture.nativeElement.querySelector('.replacement-cancel') as HTMLButtonElement;

    confirmButton().click();
    harness.fixture.detectChanges();

    harness.fixture.componentRef.setInput('activationState', 'failed');
    harness.fixture.componentRef.setInput('activationErrorCode', 'portable_import_failed');
    harness.fixture.detectChanges();

    expect(testId(harness, 'replacement-error')).toBeTruthy();
    expect(testId(harness, 'replacement-sealed')).toBeNull();
    expect(cancelButton().disabled).toBe(false);

    confirmButton().click();
    expect(confirmed).toHaveBeenCalledTimes(2);

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await waitForKind(harness, 'cancelled');
  });

  it('shows the other-tab notice from a storage event and restores the picker when cleared', () => {
    const harness = setup();

    dispatchLease({ tabId: 'other-tab', updatedAt: Date.now(), fileName: 'elsewhere.nostos' });
    harness.fixture.detectChanges();

    expect(testId(harness, 'transfer-other-tab')).toBeTruthy();
    expect(testId(harness, 'transfer-other-tab')?.textContent).toContain(
      'An import is already in progress in another tab.',
    );
    expect(testId(harness, 'transfer-other-tab')?.textContent).toContain('elsewhere.nostos');
    expect(testId(harness, 'import-choose-file')).toBeNull();

    dispatchLease(null);
    harness.fixture.detectChanges();
    expect(testId(harness, 'transfer-other-tab')).toBeNull();
    expect(testId(harness, 'import-choose-file')).toBeTruthy();
  });

  it('does not clobber another tab’s resume record or auto-resume while its lease is live', async () => {
    const harness = setup();
    const record = stageRecord();
    harness.store.save(record);
    dispatchLease({ tabId: 'other-tab', updatedAt: Date.now(), fileName: record.fileName });

    const second = reload(harness.mock, harness.mock);

    expect(testId(second, 'transfer-other-tab')).toBeTruthy();
    expect(second.store.load()).toEqual(record);
    expect(harness.mock.calls.getJob).toBe(0);

    // Attempting to pick a file in this tab must not start a competing import.
    const file = await portableFile();
    selectFile(second, file);

    expect(second.coordinator.state().kind).toBe('idle');
    expect(second.store.load()).toEqual(record);
    expect(harness.mock.calls.createJob).toBe(0);
  });

  it('claims the lease for auto-resume so a second tab cannot resume the same record', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const total = chunkCount(file.size, CHUNK);
    const record = await stageResumable(harness, file, total - 1);

    // Tab A mounts with the record and must claim before resuming.
    const first = reload(harness.mock, harness.mock);
    await waitForKind(first, 'ready-to-upload');
    const ownedLease = JSON.parse(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)!);
    expect(ownedLease.tabId).toBeTruthy();
    const resumedByFirst = harness.mock.calls.getJob;
    expect(resumedByFirst).toBeGreaterThan(0);

    // Tab A is torn down host-side while the root-scoped transfer is active:
    // the lease must survive so the transfer is not silently offered elsewhere.
    first.fixture.destroy();
    TestBed.resetTestingModule();
    expect(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)).not.toBeNull();

    // Tab B mounts: it shows the notice and never resumes.
    const second = configure(harness.mock, harness.mock);
    second.fixture.detectChanges();
    expect(testId(second, 'transfer-other-tab')).toBeTruthy();
    expect(second.coordinator.state().kind).toBe('idle');
    expect(harness.mock.calls.getJob).toBe(resumedByFirst);
    expect(second.store.load()).toEqual(record);
  });

  it('unblocks and resumes automatically when the owner lease expires, without a storage event', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const total = chunkCount(file.size, CHUNK);
    await stageResumable(harness, file, total - 1);

    // Freeze the clock only after staging: the digest reads real Blob data.
    const frozenNow = Date.now();
    vi.useFakeTimers();
    vi.setSystemTime(frozenNow);

    // A foreign tab owns a live lease over the same record.
    localStorage.setItem(
      TRANSFER_TAB_LEASE_KEY,
      JSON.stringify({ tabId: 'other-tab', updatedAt: Date.now(), fileName: file.name }),
    );

    const blocked = reload(harness.mock, harness.mock);
    blocked.fixture.detectChanges();
    expect(testId(blocked, 'transfer-other-tab')).toBeTruthy();
    expect(blocked.coordinator.state().kind).toBe('idle');

    // The owner crashes: no heartbeat, no storage event, only time passes.
    await vi.advanceTimersByTimeAsync(TRANSFER_TAB_LEASE_TTL_MS + 100);
    await flushFake(blocked, 20);

    expect(blocked.coordinator.state().kind).toBe('ready-to-upload');
    expect(testId(blocked, 'transfer-other-tab')).toBeNull();
    expect(testId(blocked, 'import-resume-file')).toBeTruthy();
  });

  it('frees a blocked tab as soon as the owner releases the lease', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const total = chunkCount(file.size, CHUNK);
    await stageResumable(harness, file, total - 1);

    dispatchLease({ tabId: 'other-tab', updatedAt: Date.now(), fileName: file.name });
    const blocked = reload(harness.mock, harness.mock);
    blocked.fixture.detectChanges();
    expect(testId(blocked, 'transfer-other-tab')).toBeTruthy();

    // The owner finishes: the lease is removed and the storage event arrives.
    dispatchLease(null);
    await flushReal(blocked);

    expect(testId(blocked, 'transfer-other-tab')).toBeNull();
    expect(blocked.coordinator.state().kind).toBe('ready-to-upload');
  });

  it('renders actionable copy for an unsupported file and offers a different file', async () => {
    const harness = setup();
    const openPicker = vi.spyOn(harness.component, 'openPicker');

    selectFile(harness, createFile(new Uint8Array(4096).fill(7)));
    await waitForKind(harness, 'failed');

    expect(testId(harness, 'import-failed')?.getAttribute('role')).toBe('alert');
    expect(testId(harness, 'import-failed')?.textContent).toContain(
      'Not a portable library archive',
    );

    const action = testId(harness, 'import-failure-action');
    expect(action?.textContent).toContain('Choose a different file');
    (action as HTMLButtonElement).click();
    expect(openPicker).toHaveBeenCalledTimes(1);
  });

  it('explains storage exhaustion with human-readable amounts', async () => {
    const harness = setup({ availableStorageBytes: 1 });
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'failed');

    const state = harness.coordinator.state();
    expect(state.kind).toBe('failed');
    if (state.kind === 'failed') {
      expect(state.failure.requiredStorageBytes).toBeGreaterThan(1);
      expect(state.failure.availableStorageBytes).toBe(1);
    }
    expect(testId(harness, 'import-failed')?.textContent).toContain('Not enough storage');
    expect(testId(harness, 'import-failed')?.textContent).toContain('currently available');
    expect(testId(harness, 'import-failure-action')?.textContent).toContain('Try again');
  });

  it('distinguishes an operational backup from a portable archive', async () => {
    const harness = setup({ operationalBackup: true });
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'failed');

    const text = testId(harness, 'import-failed')?.textContent ?? '';
    expect(text).toContain('This is a backup, not a portable archive');
    expect(text).toContain('portable library archive');
  });

  it('offers retry for a retryable failure that has a durable job', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const transport = new FailingCompleteTransport(mock);
    const harness = configure(mock, transport);
    const retry = vi.spyOn(harness.coordinator, 'retry').mockResolvedValue();
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'failed');

    const state = harness.coordinator.state();
    expect(state.kind).toBe('failed');
    if (state.kind === 'failed') expect(state.jobId).toBeTruthy();

    const action = testId(harness, 'import-failure-action') as HTMLButtonElement;
    expect(action.textContent).toContain('Retry import');
    action.click();
    expect(retry).toHaveBeenCalledTimes(1);
  });

  it('keeps the original job when choosing a file after an unrecognised host failure', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const total = chunkCount(file.size, CHUNK);
    const record = await stageResumable(harness, file, total - 1);
    harness.mock.queueFailure({ operation: 'getJob', code: 'unexpected_error', status: 400 });
    const preflights = harness.mock.calls.preflight;
    const reloaded = reload(harness.mock, harness.mock);
    await waitForKind(reloaded, 'failed');
    const openPicker = vi.spyOn(reloaded.component, 'openPicker');

    (testId(reloaded, 'import-failure-action') as HTMLButtonElement).click();
    expect(openPicker).toHaveBeenCalledTimes(1);
    expect(reloaded.store.load()).toEqual(record);
    selectFile(reloaded, file);
    await waitForKind(reloaded, 'ready-empty');

    expect(reloaded.store.load()?.jobId).toBe(record.jobId);
    expect(reloaded.mock.calls.preflight).toBe(preflights);
    expect(reloaded.mock.uploadedChunks).toEqual([total - 1]);
  });

  it('offers sign-in recovery after a 401, preserves the record and resumes after re-authentication', async () => {
    const harness = setup();
    const file = await largePortableFile();
    const total = chunkCount(file.size, CHUNK);
    const record = await stageResumable(harness, file, total - 1);

    const transport = new UnauthorizedOnceTransport(harness.mock);
    const reloaded = reload(harness.mock, transport);
    await waitForKind(reloaded, 'failed');

    expect(testId(reloaded, 'import-failed')?.textContent).toContain('Sign in again');
    const action = testId(reloaded, 'import-failure-action') as HTMLButtonElement;
    expect(action.textContent).toContain('Try again');
    expect(reloaded.store.load()).toEqual(record);

    transport.unauthorized = false;
    action.click();
    await waitForKind(reloaded, 'ready-to-upload');

    expect(testId(reloaded, 'import-resume-file')).toBeTruthy();
  });

  it('shows the checking phase while the server validates, then completes once', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const transport = new GatedStatusTransport(mock);
    const harness = configure(mock, transport);
    const completed = vi.fn();
    harness.component.importCompleted.subscribe(completed);
    const file = await portableFile();

    selectFile(harness, file);
    await flushReal(harness, 80);
    expect(harness.coordinator.state().kind).toBe('checking');

    expect(testId(harness, 'import-checking')).toBeTruthy();
    expect(testId(harness, 'import-checking')?.textContent).toContain(
      'Checking archive, library relationships, and media…',
    );
    expect(testId(harness, 'import-server-note')?.textContent).toContain(
      'processing continues on the server',
    );
    expect(testId(harness, 'import-cancel')).toBeTruthy();

    const jobId = (harness.coordinator.state() as { jobId: string }).jobId;
    mock.setJobState(jobId, 'Completed');
    await transport.releaseAll();
    await flushReal(harness, 20);

    expect(harness.coordinator.state().kind).toBe('completed');
    expect(testId(harness, 'import-completed')).toBeTruthy();
    expect(completed).toHaveBeenCalledTimes(1);

    (testId(harness, 'import-done') as HTMLButtonElement).click();
    harness.fixture.detectChanges();
    expect(testId(harness, 'import-idle')).toBeTruthy();
  });

  it('hides cancellation and shows importing copy once the server is activating', async () => {
    const harness = setup();
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');

    const jobId = (harness.coordinator.state() as { jobId: string }).jobId;
    harness.mock.setJobState(jobId, 'Activating');
    await harness.coordinator.refreshStatus();
    harness.fixture.detectChanges();

    expect(testId(harness, 'import-checking')).toBeTruthy();
    expect(testId(harness, 'import-checking')?.textContent).toContain('Importing your library…');
    expect(testId(harness, 'import-cancel')).toBeNull();
    expect(testId(harness, 'import-server-note')?.textContent).toContain(
      'switching your library on the server',
    );
  });

  it('completes from ready-empty and emits importCompleted exactly once', async () => {
    const harness = setup();
    const completed = vi.fn();
    harness.component.importCompleted.subscribe(completed);
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');

    const jobId = (harness.coordinator.state() as { jobId: string }).jobId;
    harness.mock.setJobState(jobId, 'Completed');
    await harness.coordinator.refreshStatus();
    harness.fixture.detectChanges();
    harness.fixture.detectChanges();

    expect(testId(harness, 'import-completed')).toBeTruthy();
    expect(completed).toHaveBeenCalledTimes(1);

    (testId(harness, 'import-done') as HTMLButtonElement).click();
    harness.fixture.detectChanges();
    expect(testId(harness, 'import-idle')).toBeTruthy();
    expect(harness.store.load()).toBeNull();
  });

  it('hands an empty-destination job to the host exactly once with activationRequested', async () => {
    const harness = setup();
    const requested = vi.fn();
    harness.component.activationRequested.subscribe(requested);
    const file = await portableFile();

    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');
    harness.fixture.detectChanges();

    const jobId = (harness.coordinator.state() as { jobId: string }).jobId;
    expect(requested).toHaveBeenCalledTimes(1);
    expect(requested).toHaveBeenCalledWith(jobId);
  });

  it('never asks the host to activate when the capability is off', async () => {
    const harness = setup();
    const requested = vi.fn();
    harness.component.activationRequested.subscribe(requested);
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');
    harness.fixture.detectChanges();

    expect(requested).not.toHaveBeenCalled();
    expect(testId(harness, 'import-activation-unavailable')).toBeTruthy();
  });

  it('shows an in-progress waiting state and hides Cancel while the host activates', async () => {
    const harness = setup();
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');
    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    harness.fixture.componentRef.setInput('activationState', 'in-progress');
    harness.fixture.detectChanges();

    expect(testId(harness, 'import-ready-empty')?.textContent).toContain(
      'Import prepared — finishing…',
    );
    expect(testId(harness, 'transfer-progress')).toBeTruthy();
    expect(testId(harness, 'import-cancel')).toBeNull();
    expect(testId(harness, 'import-dismiss')).toBeNull();
  });

  it('explains gated activation and Dismiss returns to idle while keeping the record', async () => {
    const harness = setup();
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');
    harness.fixture.detectChanges();

    expect(testId(harness, 'import-activation-unavailable')?.textContent).toContain(
      'can’t finish the import automatically yet',
    );
    const record = harness.store.load();
    expect(record).not.toBeNull();

    (testId(harness, 'import-dismiss') as HTMLButtonElement).click();
    harness.fixture.detectChanges();

    expect(harness.coordinator.state().kind).toBe('idle');
    expect(testId(harness, 'import-idle')).toBeTruthy();
    expect(harness.store.load()).toEqual(record);
  });

  it('reports a host activation failure with retry and cancel', async () => {
    const harness = setup();
    const requested = vi.fn();
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');
    harness.component.activationRequested.subscribe(requested);

    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    harness.fixture.componentRef.setInput('activationState', 'failed');
    harness.fixture.componentRef.setInput('activationErrorCode', 'migration_storage_exhausted');
    harness.fixture.detectChanges();

    expect(testId(harness, 'import-activation-failed')?.getAttribute('role')).toBe('alert');
    expect(testId(harness, 'import-activation-failed')?.textContent).toContain('Not enough storage');

    (testId(harness, 'import-activation-retry') as HTMLButtonElement).click();
    expect(requested).toHaveBeenCalledTimes(1);
  });

  it('finishes an empty-destination import when the host reports completion', async () => {
    const harness = setup();
    const completed = vi.fn();
    harness.component.importCompleted.subscribe(completed);
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');

    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    harness.fixture.componentRef.setInput('activationState', 'completed');
    harness.fixture.detectChanges();
    harness.fixture.detectChanges();

    expect(testId(harness, 'import-completed')).toBeTruthy();
    expect(completed).toHaveBeenCalledTimes(1);

    (testId(harness, 'import-done') as HTMLButtonElement).click();
    harness.fixture.detectChanges();
    expect(testId(harness, 'import-idle')).toBeTruthy();
    expect(harness.store.load()).toBeNull();
  });

  it('renders the transient upload-start state for a ready job that is not reselecting', () => {
    const state = signal({
      kind: 'ready-to-upload' as const,
      jobId: 'job-1',
      reselectionRequired: false,
    });
    TestBed.configureTestingModule({
      providers: [
        { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: new MockLibraryTransferTransport() },
        {
          provide: LibraryTransferCoordinator,
          useValue: {
            state: state.asReadonly(),
            maintenanceWaiting: signal(null),
            hasInterruptedOperation: signal(false),
          },
        },      ],
    });
    const fixture = TestBed.createComponent(LibraryImportFlowComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="import-starting"]')).toBeTruthy();
  });

  it('keeps the picker available at a narrow viewport', () => {
    const previous = window.innerWidth;
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 390 });
    window.dispatchEvent(new Event('resize'));

    const harness = setup();

    expect(testId(harness, 'import-choose-file')).toBeTruthy();
    expect(harness.fixture.nativeElement.querySelector('input[type="file"]')).toBeTruthy();

    Object.defineProperty(window, 'innerWidth', { configurable: true, value: previous });
  });

  it('exposes the progress bar with progressbar semantics while uploading', async () => {
    const mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK });
    const transport = new GatedUploadTransport(mock);
    const harness = configure(mock, transport);
    const file = await portableFile();

    selectFile(harness, file);
    await waitForKind(harness, 'uploading');

    const bar = harness.fixture.nativeElement.querySelector(
      '[data-testid="transfer-progress"]',
    ) as HTMLElement;
    expect(bar.getAttribute('role')).toBe('progressbar');
    expect(bar.getAttribute('aria-valuemax')).toBe('100');

    await transport.releaseAll();
    await waitForKind(harness, 'ready-empty');
  });

  it('seals the replacement dialog while activation is in progress before submission', async () => {
    const harness = setup({ destinationStatus: 'Populated', existingCounts: { books: 1 } });
    const file = await portableFile();

    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    selectFile(harness, file);
    await waitForKind(harness, 'replacement-confirmation');
    harness.fixture.componentRef.setInput('activationState', 'in-progress');
    harness.fixture.detectChanges();

    expect(testId(harness, 'replacement-sealed')).toBeTruthy();
    const cancel = harness.fixture.nativeElement.querySelector(
      '.replacement-cancel',
    ) as HTMLButtonElement;
    expect(cancel.disabled).toBe(true);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    cancel.click();
    harness.fixture.detectChanges();

    expect(harness.coordinator.state().kind).toBe('replacement-confirmation');
    expect(testId(harness, 'import-cancelled')).toBeNull();
  });

  it('shows the server phase text while the host activates', async () => {
    const harness = setup();
    const file = await portableFile();

    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');
    harness.fixture.componentRef.setInput('activationState', 'in-progress');
    harness.fixture.componentRef.setInput('activationPhase', 'Activating');
    harness.fixture.detectChanges();

    expect(testId(harness, 'import-activation-phase')?.textContent).toContain(
      'Switching libraries…',
    );
  });

  it('shows the server counts and re-arms confirmation after a destination conflict', async () => {
    const harness = setup({ destinationStatus: 'Populated', existingCounts: { books: 1 } });
    const confirmed = vi.fn();
    harness.component.replacementConfirmed.subscribe(confirmed);
    const file = await portableFile();

    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    selectFile(harness, file);
    await waitForKind(harness, 'replacement-confirmation');
    harness.fixture.detectChanges();

    (harness.fixture.nativeElement.querySelector(
      '.replacement-confirm',
    ) as HTMLButtonElement).click();
    expect(confirmed).toHaveBeenCalledTimes(1);

    harness.fixture.componentRef.setInput('activationState', 'failed');
    harness.fixture.componentRef.setInput(
      'activationErrorCode',
      'migration_destination_conflict',
    );
    harness.fixture.componentRef.setInput('activationCanRetry', true);
    harness.fixture.componentRef.setInput('activationConflict', {
      destinationRevision: 'rev-9',
      destinationStatus: 'Populated',
      existingCounts: {
        works: 1,
        books: 9,
        notes: 12,
        topics: 0,
        noteTopics: 0,
        writings: 0,
        writingNotes: 0,
        collections: 2,
        bookCollections: 3,
        acquisitions: 0,
        noteImportBookLinks: 0,
        assistantSettings: 0,
        totalRows: 27,
      },
    });
    harness.fixture.detectChanges();

    expect(testId(harness, 'replacement-conflict')).toBeTruthy();
    expect(testId(harness, 'replacement-existing')?.textContent).toContain('9 books');
    expect(testId(harness, 'replacement-error')).toBeNull();

    (harness.fixture.nativeElement.querySelector(
      '.replacement-confirm',
    ) as HTMLButtonElement).click();
    expect(confirmed).toHaveBeenCalledTimes(2);
  });

  it('shows both count sides when an empty destination reports populated after preflight', async () => {
    const harness = setup();
    const file = await portableFile();

    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');
    harness.fixture.detectChanges();

    harness.fixture.componentRef.setInput('activationConflict', {
      destinationRevision: 'rev-9',
      destinationStatus: 'Populated',
      existingCounts: {
        works: 1,
        books: 9,
        notes: 12,
        topics: 0,
        noteTopics: 0,
        writings: 0,
        writingNotes: 0,
        collections: 2,
        bookCollections: 3,
        acquisitions: 0,
        noteImportBookLinks: 0,
        assistantSettings: 0,
        totalRows: 27,
      },
      changedSinceImportStarted: true,
    });
    harness.fixture.detectChanges();

    expect(testId(harness, 'replacement-existing')?.textContent).toContain('9 books');
    // The dialog must reuse the flow's own archive facts instead of blanking
    // the incoming side (the coordinator state still holds them).
    expect(testId(harness, 'replacement-incoming')?.textContent).toContain('3 books');
    expect(testId(harness, 'replacement-conflict')?.textContent).toContain(
      'changed since the import started',
    );
  });

  it('renders the fail-closed recovery failure without a retry action', async () => {
    const harness = setup();
    const requested = vi.fn();
    harness.component.activationRequested.subscribe(requested);
    const file = await portableFile();

    harness.fixture.componentRef.setInput('supportsSafeActivation', true);
    selectFile(harness, file);
    await waitForKind(harness, 'ready-empty');
    expect(requested).toHaveBeenCalledTimes(1);

    harness.fixture.componentRef.setInput('activationState', 'failed');
    harness.fixture.componentRef.setInput(
      'activationErrorCode',
      'migration_activation_recovery_failed',
    );
    harness.fixture.componentRef.setInput('activationCanRetry', false);
    harness.fixture.detectChanges();

    expect(testId(harness, 'import-activation-failed')?.textContent).toContain('Restart');
    expect(testId(harness, 'import-activation-retry')).toBeNull();
    expect(requested).toHaveBeenCalledTimes(1);
  });
});
