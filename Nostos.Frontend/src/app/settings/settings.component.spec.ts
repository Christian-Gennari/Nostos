import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable, Subject, of, throwError } from 'rxjs';
import {
  HttpErrorResponse,
  HttpEvent,
  HttpEventType,
  HttpHeaders,
  HttpResponse,
} from '@angular/common/http';

import { SettingsComponent } from './settings.component';
import { BackupService } from '../core/services/backup.service';
import { OpdsService } from '../core/services/opds.service';
import { ToastService } from '../core/services/toast.service';
import { ManagedOpdsAccess, OpdsInfo } from '../core/dtos/opds.dtos';
import {
  LIBRARY_PREFERENCES_STORAGE_KEY,
  LibraryPreferencesService,
} from '../core/services/library-preferences.service';
import { AssistantStatusService } from '../ui/assistant/assistant-status.service';
import { AssistantSettingsService } from '../ui/assistant/assistant-settings.service';
import { ProcessingMode } from '../ui/assistant/assistant-settings.service';
import { AiProviderService } from '../core/services/ai-provider.service';
import { ProviderSettingsService } from '../core/services/provider-settings.service';
import { ProviderSettingsItem, ProviderSettingsResponse } from '../core/dtos/provider.dtos';
import { DeploymentCapabilitiesService } from '../core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from '../core/dtos/deployment-capabilities.dtos';
import { CloudAiRefillService } from '../core/services/cloud-ai-refill.service';
import { CloudAuthService } from '../core/services/cloud-auth.service';
import { PortableLibraryService } from '../core/services/portable-library.service';
import { LibraryImportFlowComponent } from '../library-transfer/components/library-import-flow.component';
import {
  LIBRARY_TRANSFER_TRANSPORT,
  LibraryTransferTransport,
  MigrationTransportError,
} from '../library-transfer/services/library-transfer-transport';
import { MockLibraryTransferTransport } from '../library-transfer/testing/mock-library-transfer-transport';
import { LibraryTransferCoordinator } from '../library-transfer/services/library-transfer-coordinator.service';
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
  MigrationJobStatusResponseDto,
} from '../library-transfer/models/migration-http.dtos';
import {
  createFile,
  portableArchiveFixture,
  portableManifest,
} from '../library-transfer/testing/zip-archive.fixture';
import { CloudManagedAiUsage } from '../core/dtos/cloud-ai-refill.dtos';
import {
  AiProviderModelsRequest,
  AiProviderModelsResponse,
  AiProviderSettings,
  AiProviderTestRequest,
  AiProviderTestResult,
  AiProviderUpdate,
} from '../core/dtos/ai-provider.dtos';

const toastMock = { error: vi.fn(), success: vi.fn(), info: vi.fn() };
const routerMock = { navigate: vi.fn() };

const selfHostedCapabilities: DeploymentCapabilities = {
  deploymentMode: 'SelfHosted',
  requiresAuthentication: false,
  canConfigureAiProvider: true,
  managedAi: false,
  managedVoiceTranscription: false,
  usesCloudStorage: false,
  supportsLocalBackupConfiguration: true,
  supportsPrivateNetworkAccess: true,
  supportsEreaderAccess: true,
  usageMeteringAvailable: false,
  accountManagementUrl: null,
  feedbackUrl: null,
};

const cloudCapabilities: DeploymentCapabilities = {
  deploymentMode: 'Cloud',
  requiresAuthentication: true,
  canConfigureAiProvider: false,
  managedAi: true,
  managedVoiceTranscription: true,
  usesCloudStorage: true,
  supportsLocalBackupConfiguration: false,
  supportsPrivateNetworkAccess: false,
  supportsEreaderAccess: true,
  usageMeteringAvailable: true,
  accountManagementUrl: 'https://nostos.page/account',
  feedbackUrl: 'https://nostos.page/feedback?from=settings',
};

const capabilitiesServiceMock = {
  get: vi.fn((): Observable<DeploymentCapabilities> => of(selfHostedCapabilities)),
};

const managedAiUsage: CloudManagedAiUsage = {
  state: 'near_limit',
  renewsAtUtc: '2026-10-01T00:00:00Z',
  refill: {
    available: false,
    state: 'empty',
  },
};

const portableLibraryServiceMock = {
  exportArchive: vi.fn(
    (): Observable<HttpEvent<Blob>> =>
      of(
        new HttpResponse({
          body: new Blob(['portable']),
          headers: new HttpHeaders({
            'content-disposition': 'attachment; filename="nostos-export-test.nostos"',
          }),
        }),
      ),
  ),
};

const cloudAuthServiceMock = {
  getSession: vi.fn(() =>
    of({
      authenticated: true,
      accountState: 'Active' as const,
      account: {
        id: '1e4df713-1a34-4fc7-9a90-c45169256845',
        displayName: 'Reader',
        email: 'reader@example.test',
      },
    }),
  ),
  logout: vi.fn(),
};

const cloudAiRefillServiceMock = {
  getUsage: vi.fn((): Observable<CloudManagedAiUsage> => of(managedAiUsage)),
};

/** A reachable catalog address, as the server reports it behind its proxy. */
const remoteInfo: OpdsInfo = {
  enabled: true,
  catalogUrl: 'https://omenhub.example.ts.net:5215/opds/',
  urlSource: 'request',
  localOnly: false,
};

const managedDisabled: ManagedOpdsAccess = {
  enabled: false,
  username: null,
  password: null,
  createdAtUtc: null,
  rotatedAtUtc: null,
  revokedAtUtc: null,
};

const managedEnabled: ManagedOpdsAccess = {
  enabled: true,
  username: 'reader-example',
  password: null,
  createdAtUtc: '2026-09-24T17:00:00Z',
  rotatedAtUtc: '2026-09-24T17:00:00Z',
  revokedAtUtc: null,
};

const managedIssued: ManagedOpdsAccess = {
  ...managedEnabled,
  password: 'one-time-reader-password',
};

const managedRotated: ManagedOpdsAccess = {
  ...managedEnabled,
  password: 'replacement-reader-password',
  rotatedAtUtc: '2026-09-24T18:00:00Z',
};

const opdsServiceMock = {
  getInfo: vi.fn(() => of(remoteInfo)),
  getManagedAccess: vi.fn((): Observable<ManagedOpdsAccess> => of(managedDisabled)),
  enableManagedAccess: vi.fn((): Observable<ManagedOpdsAccess> => of(managedIssued)),
  rotateManagedPassword: vi.fn((): Observable<ManagedOpdsAccess> => of(managedRotated)),
  revokeManagedAccess: vi.fn(
    (): Observable<ManagedOpdsAccess> =>
      of({
        ...managedEnabled,
        enabled: false,
        password: null,
        revokedAtUtc: '2026-09-24T19:00:00Z',
      }),
  ),
};

/**
 * Effective AI provider settings. The LLM's key is stored server-side; the
 * voice key comes from the server environment. Both password fields must still
 * render empty — the API never returns a key.
 */
const aiProviderSettings: AiProviderSettings = {
  llm: {
    enabled: true,
    baseUrl: 'http://omenhub:20128/v1',
    model: 'qwen3-32b',
    hasKey: true,
    keyFromServerEnv: false,
  },
  stt: {
    enabled: false,
    baseUrl: 'http://omenhub:20128',
    model: 'groq/whisper-large-v3-turbo',
    hasKey: true,
    keyFromServerEnv: true,
  },
  embedding: {
    enabled: false,
    baseUrl: 'https://ai-gateway.vercel.sh/v1',
    model: 'alibaba/qwen3-embedding-0-6b',
    hasKey: false,
    keyFromServerEnv: false,
  },
};

const aiProviderServiceMock = {
  get: vi.fn((): Observable<AiProviderSettings> => of(aiProviderSettings)),
  update: vi.fn(
    (_update: AiProviderUpdate): Observable<AiProviderSettings> => of(aiProviderSettings),
  ),
  loadModels: vi.fn(
    (_request: AiProviderModelsRequest): Observable<AiProviderModelsResponse> =>
      of({ models: ['qwen3-32b', 'gpt-4o'] }),
  ),
  test: vi.fn(
    (_request: AiProviderTestRequest): Observable<AiProviderTestResult> =>
      of({ ok: true, detail: 'Reached the endpoint.' }),
  ),
};

/**
 * One enabled and one disabled source, as the management view sees them
 * (issue #774). Fresh objects per call so a test can mutate one without
 * leaking into the next.
 */
function providerSettingsFixture(): ProviderSettingsItem[] {
  return [
    {
      id: 'gutenberg',
      displayName: 'Project Gutenberg',
      description: 'Public-domain ebooks in many languages.',
      capabilities: ['search', 'ebookacquisition'],
      rightsNotice: 'Public domain in the USA (Project Gutenberg)',
      enabled: true,
      enabledByDefault: true,
    },
    {
      id: 'wikisource',
      displayName: 'Wikisource',
      description: 'Transcribed public-domain texts in many languages.',
      capabilities: ['search'],
      rightsNotice: null,
      enabled: false,
      enabledByDefault: true,
    },
  ];
}

const providerSettingsServiceMock = {
  list: vi.fn((): Observable<ProviderSettingsResponse> =>
    of({ providers: providerSettingsFixture() })),
  setEnabled: vi.fn(
    (providerId: string, enabled: boolean): Observable<ProviderSettingsItem> =>
      of({ ...providerSettingsFixture().find((p) => p.id === providerId)!, enabled }),
  ),
};

const backupServiceMock = {
  getStatus: vi.fn(() =>
    of({
      isEnabled: false,
      provider: 'Local',
      lastBackupAt: null,
      lastBackupStatus: null,
      includeBookFiles: true,
      intervalHours: 168,
      maxBackups: 3,
    }),
  ),
  getSettings: vi.fn(() =>
    of({
      isEnabled: false,
      provider: 'Local',
      includeBookFiles: true,
      intervalHours: 168,
      maxBackups: 3,
    }),
  ),
  getHistory: vi.fn(() => of([])),
  updateSettings: vi.fn(() => of({})),
  triggerBackup: vi.fn(() => of({ status: 'Completed', sizeBytes: 0 })),
  getDownloadUrl: vi.fn(() => ''),
  importExisting: vi.fn(() => of([])),
  restore: vi.fn(() => of({ success: true, message: 'Restored.' })),
  deleteBackup: vi.fn(() => of(null)),
  getProgress: vi.fn(() => of({})),
};

/**
 * The assistant's server availability, driven by the test. Real requests are
 * covered by `AssistantStatusService`'s own spec; here the card only needs the
 * answer to change.
 */
const assistantStatusMock = {
  available: signal(true),
  ensureLoaded: vi.fn(),
  refresh: vi.fn(),
};

/**
 * The stored capture-processing choice, driven by the test. The real GET/PUT
 * round-tripped by `AssistantSettingsService`'s own spec; here the card only
 * needs the stored value and its failure flags to change.
 */
const assistantSettingsMock = {
  captureProcessingMode: signal<ProcessingMode>('verbatim'),
  loadFailed: signal(false),
  saveFailed: signal(false),
  ensureLoaded: vi.fn(),
  refresh: vi.fn(),
  setCaptureProcessingMode: vi.fn((mode: ProcessingMode) => {
    assistantSettingsMock.captureProcessingMode.set(mode);
  }),
};

describe('SettingsComponent backup-only surface', () => {
  let fixture: ComponentFixture<SettingsComponent>;
  const routeStub = { snapshot: { data: { manageLibraryPage: false } } };

  async function configure(): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        { provide: ActivatedRoute, useValue: routeStub },
        { provide: Router, useValue: routerMock },
        { provide: BackupService, useValue: backupServiceMock },
        { provide: OpdsService, useValue: opdsServiceMock },
        { provide: ToastService, useValue: toastMock },
        { provide: AssistantStatusService, useValue: assistantStatusMock },
        { provide: AssistantSettingsService, useValue: assistantSettingsMock },
        { provide: AiProviderService, useValue: aiProviderServiceMock },
        { provide: ProviderSettingsService, useValue: providerSettingsServiceMock },
        { provide: DeploymentCapabilitiesService, useValue: capabilitiesServiceMock },
        { provide: CloudAiRefillService, useValue: cloudAiRefillServiceMock },
        { provide: CloudAuthService, useValue: cloudAuthServiceMock },
        { provide: PortableLibraryService, useValue: portableLibraryServiceMock },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
  }

  function openManageLibraryPage(): void {
    const capabilities = fixture.componentInstance.deploymentCapabilities();
    if (capabilities) capabilitiesServiceMock.get.mockReturnValue(of(capabilities));
    fixture.destroy();
    routeStub.snapshot.data.manageLibraryPage = true;
    fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
  }

  beforeEach(async () => {
    routeStub.snapshot.data.manageLibraryPage = false;
    localStorage.clear();
    capabilitiesServiceMock.get.mockClear();
    capabilitiesServiceMock.get.mockReturnValue(of(selfHostedCapabilities));
    portableLibraryServiceMock.exportArchive.mockClear();
    portableLibraryServiceMock.exportArchive.mockReturnValue(
      of(
        new HttpResponse({
          body: new Blob(['portable']),
          headers: new HttpHeaders({
            'content-disposition': 'attachment; filename="nostos-export-test.nostos"',
          }),
        }),
      ),
    );
    cloudAuthServiceMock.getSession.mockClear();
    cloudAuthServiceMock.getSession.mockReturnValue(
      of({
        authenticated: true,
        accountState: 'Active',
        account: {
          id: '1e4df713-1a34-4fc7-9a90-c45169256845',
          displayName: 'Reader',
          email: 'reader@example.test',
        },
      }),
    );
    cloudAuthServiceMock.logout.mockClear();
    cloudAiRefillServiceMock.getUsage.mockClear();
    cloudAiRefillServiceMock.getUsage.mockReturnValue(of(managedAiUsage));
    opdsServiceMock.getInfo.mockClear();
    opdsServiceMock.getInfo.mockReturnValue(of(remoteInfo));
    opdsServiceMock.getManagedAccess.mockClear();
    opdsServiceMock.getManagedAccess.mockReturnValue(of(managedDisabled));
    opdsServiceMock.enableManagedAccess.mockClear();
    opdsServiceMock.enableManagedAccess.mockReturnValue(of(managedIssued));
    opdsServiceMock.rotateManagedPassword.mockClear();
    opdsServiceMock.rotateManagedPassword.mockReturnValue(of(managedRotated));
    opdsServiceMock.revokeManagedAccess.mockClear();
    opdsServiceMock.revokeManagedAccess.mockReturnValue(
      of({
        ...managedEnabled,
        enabled: false,
        password: null,
        revokedAtUtc: '2026-09-24T19:00:00Z',
      }),
    );
    toastMock.error.mockClear();
    toastMock.success.mockClear();
    assistantStatusMock.available.set(true);
    assistantStatusMock.refresh.mockClear();
    assistantSettingsMock.captureProcessingMode.set('verbatim');
    assistantSettingsMock.loadFailed.set(false);
    assistantSettingsMock.saveFailed.set(false);
    assistantSettingsMock.refresh.mockClear();
    assistantSettingsMock.setCaptureProcessingMode.mockClear();
    aiProviderServiceMock.get.mockClear();
    aiProviderServiceMock.get.mockReturnValue(of(aiProviderSettings));
    aiProviderServiceMock.update.mockClear();
    aiProviderServiceMock.update.mockReturnValue(of(aiProviderSettings));
    aiProviderServiceMock.loadModels.mockClear();
    aiProviderServiceMock.loadModels.mockReturnValue(
      of<AiProviderModelsResponse>({ models: ['qwen3-32b', 'gpt-4o'] }),
    );
    aiProviderServiceMock.test.mockClear();
    aiProviderServiceMock.test.mockReturnValue(
      of<AiProviderTestResult>({ ok: true, detail: 'Reached the endpoint.' }),
    );
    providerSettingsServiceMock.list.mockClear();
    providerSettingsServiceMock.list.mockReturnValue(
      of<ProviderSettingsResponse>({ providers: providerSettingsFixture() }),
    );
    providerSettingsServiceMock.setEnabled.mockClear();
    providerSettingsServiceMock.setEnabled.mockImplementation(
      (providerId: string, enabled: boolean) =>
        of({ ...providerSettingsFixture().find((p) => p.id === providerId)!, enabled }),
    );

    await configure();
  });

  afterEach(() => {
    localStorage.clear();
    delete (navigator as { clipboard?: unknown }).clipboard;
  });

  it('switches settings surfaces locally without hash navigation', () => {
    const nav = fixture.debugElement.queryAll(By.css('.settings-nav-item'));
    expect(
      nav.map((item) =>
        item.query(By.css('.settings-nav-copy')).nativeElement.textContent.trim(),
      ),
    ).toEqual(['Library & data', 'Book providers', 'Assistant', 'Appearance']);
    expect(fixture.componentInstance.activeSettingsSection()).toBe('library');
    expect(fixture.nativeElement.querySelector('#library-data').hidden).toBe(false);
    expect(fixture.nativeElement.querySelector('#assistant').hidden).toBe(true);
    expect(fixture.nativeElement.querySelectorAll('.settings-nav a').length).toBe(0);

    const hashBefore = window.location.hash;
    nav[2].nativeElement.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.activeSettingsSection()).toBe('assistant');
    expect(fixture.nativeElement.querySelector('#library-data').hidden).toBe(true);
    expect(fixture.nativeElement.querySelector('#assistant').hidden).toBe(false);
    expect(window.location.hash).toBe(hashBefore);
  });

  it('loads book providers when the tab opens and lists enabled and disabled sources', () => {
    expect(providerSettingsServiceMock.list).not.toHaveBeenCalled();

    const tab = navItem('Book providers');
    tab.click();
    fixture.detectChanges();

    expect(providerSettingsServiceMock.list).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.activeSettingsSection()).toBe('providers');
    expect((fixture.nativeElement.querySelector('#providers') as HTMLElement).hidden).toBe(
      false,
    );

    const card = fixture.nativeElement.querySelector(
      '[data-testid="book-providers-card"]',
    ) as HTMLElement;
    expect(card.textContent).toContain('Project Gutenberg');
    expect(card.textContent).toContain('Wikisource');
    expect(card.textContent).toContain('Public-domain ebooks in many languages.');
    expect(providerToggle('gutenberg').checked).toBe(true);
    expect(providerToggle('wikisource').checked).toBe(false);
    expect(providerToggle('gutenberg').getAttribute('aria-label')).toBe(
      'Disable Project Gutenberg',
    );
    expect(providerToggle('wikisource').getAttribute('aria-label')).toBe('Enable Wikisource');

    // Reopening a successfully loaded tab does not re-fetch.
    tab.click();
    fixture.detectChanges();
    expect(providerSettingsServiceMock.list).toHaveBeenCalledTimes(1);
  });

  it('toggling a source calls setEnabled and shows the server answer', () => {
    openProviders();
    providerSettingsServiceMock.setEnabled.mockReturnValue(
      of({ ...providerSettingsFixture()[0], enabled: false }),
    );

    const toggle = providerToggle('gutenberg');
    toggle.checked = false;
    toggle.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(providerSettingsServiceMock.setEnabled).toHaveBeenCalledWith('gutenberg', false);
    expect(providerToggle('gutenberg').checked).toBe(false);
    expect(providerToggle('gutenberg').getAttribute('aria-label')).toBe(
      'Enable Project Gutenberg',
    );
  });

  it('reverts the checkbox and toasts when a save fails', () => {
    openProviders();
    const result = new Subject<ProviderSettingsItem>();
    providerSettingsServiceMock.setEnabled.mockReturnValue(result);

    const toggle = providerToggle('gutenberg');
    expect(toggle.checked).toBe(true);

    toggle.checked = false;
    toggle.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(toggle.checked).toBe(false);
    expect(toggle.disabled).toBe(true);

    result.error(new Error('nope'));
    fixture.detectChanges();

    expect(toggle.checked).toBe(true);
    expect(toggle.disabled).toBe(false);
    expect(toggle.getAttribute('aria-label')).toBe('Disable Project Gutenberg');
    expect(toastMock.error).toHaveBeenCalledTimes(1);
    expect(
      fixture.nativeElement.querySelector('[data-testid="book-providers-save-failed"]'),
    ).toBeTruthy();
  });

  it('renders an in-place error when the provider list fails to load and retries on reopen', () => {
    providerSettingsServiceMock.list.mockReturnValue(throwError(() => new Error('down')));
    openProviders();

    expect(
      fixture.nativeElement.querySelector('[data-testid="book-providers-load-failed"]'),
    ).toBeTruthy();
    expect(providerSettingsServiceMock.list).toHaveBeenCalledTimes(1);

    // Leaving and reopening retries rather than leaving the tab stuck.
    navItem('Library & data').click();
    fixture.detectChanges();
    navItem('Book providers').click();
    fixture.detectChanges();

    expect(providerSettingsServiceMock.list).toHaveBeenCalledTimes(2);
  });

  function navItem(label: string): HTMLButtonElement {
    const items = Array.from(
      fixture.nativeElement.querySelectorAll('.settings-nav-item'),
    ) as HTMLButtonElement[];
    return items.find((item) => (item.textContent ?? '').includes(label))!;
  }

  function providerToggle(id: string): HTMLInputElement {
    return fixture.nativeElement.querySelector(
      `[data-testid="provider-toggle-${id}"]`,
    ) as HTMLInputElement;
  }

  function openProviders(): void {
    navItem('Book providers').click();
    fixture.detectChanges();
  }

  it('keeps Settings as a quiet utility surface without redundant page or section marketing', () => {
    expect(fixture.nativeElement.querySelector('.settings-header')).toBeNull();
    expect(fixture.nativeElement.querySelector('.settings-overview')).toBeNull();

    const pageText = (fixture.nativeElement.textContent ?? '').replace(/\s+/g, ' ');
    expect(pageText).not.toContain('Shape the quiet systems behind your reading space.');
    expect(pageText).not.toContain('Keep your library safe and reachable.');

    expect(
      fixture.nativeElement.querySelector('#library-data-heading')?.textContent.trim(),
    ).toBe('Library & data');

    const activeNav = fixture.nativeElement.querySelector(
      '.settings-nav-item.is-active',
    ) as HTMLElement;
    expect(activeNav.textContent?.replace(/\s+/g, ' ').trim()).toBe('Library & data');
    expect(activeNav.querySelector('small')).toBeNull();
  });

  it('does not show or load Cloud account controls in self-hosted mode', () => {
    const navText = fixture.nativeElement.querySelector('.settings-nav')?.textContent ?? '';
    expect(navText).not.toContain('Account');
    expect(fixture.nativeElement.querySelector('[data-testid="cloud-account-settings"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="cloud-account-management-link"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="managed-ai-refill-link"]')).toBeNull();
    expect(cloudAuthServiceMock.getSession).not.toHaveBeenCalled();
  });

  it('shows the authenticated Cloud identity and signs out through the BFF', () => {
    capabilitiesServiceMock.get.mockReturnValue(of(cloudCapabilities));
    render();

    expect(cloudAuthServiceMock.getSession).toHaveBeenCalledTimes(1);
    const accountTab = Array.from(
      fixture.nativeElement.querySelectorAll('.settings-nav-item'),
    ).find((item: any) => (item.textContent ?? '').includes('Account')) as HTMLButtonElement | undefined;
    expect(accountTab).toBeTruthy();

    accountTab!.click();
    fixture.detectChanges();

    const card = fixture.nativeElement.querySelector(
      '[data-testid="cloud-account-settings"]',
    ) as HTMLElement;
    expect(card.textContent).toContain('Reader');
    expect(card.textContent).toContain('reader@example.test');

    const manageAccount = card.querySelector(
      '[data-testid="cloud-account-management-link"]',
    ) as HTMLAnchorElement;
    expect(manageAccount.textContent).toContain('Manage account & billing');
    expect(manageAccount.href).toBe('https://nostos.page/account');
    expect(manageAccount.target).toBe('_blank');

    const signOut = Array.from(card.querySelectorAll('button')).find((button: any) =>
      (button.textContent ?? '').includes('Sign out'),
    ) as HTMLButtonElement;
    signOut.click();

    expect(cloudAuthServiceMock.logout).toHaveBeenCalledTimes(1);
  });

  it('renders the Appearance surface with a working Light/Dark choice', () => {
    fixture.componentInstance.setSettingsSection('appearance');
    fixture.detectChanges();

    const headers = fixture.debugElement
      .queryAll(By.css('.card-header h2'))
      .map((h) => h.nativeElement.textContent.trim());
    expect(headers).toContain('Colour theme');

    const options = fixture.debugElement.queryAll(By.css('.theme-card'));
    expect(
      options.map((o) => o.nativeElement.querySelector('strong')?.textContent.trim()),
    ).toEqual(['Light', 'Dark']);

    // Defaults to light in a test environment (no stored choice, and
    // matchMedia reports no dark preference).
    expect(options[0].nativeElement.classList.contains('is-active')).toBe(true);
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();

    options[1].nativeElement.click();
    fixture.detectChanges();

    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem('nostos.theme')).toBe('dark');
  });

  it('reverts to light and clears the attribute when Light is chosen', () => {
    fixture.componentInstance.setTheme('dark');
    fixture.detectChanges();
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');

    fixture.componentInstance.setTheme('light');
    fixture.detectChanges();
    // Removing the attribute (not setting 'light') keeps `:root` the single
    // owner of the light values.
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
    expect(localStorage.getItem('nostos.theme')).toBe('light');
  });

  it('shows no owner-only controls or owner API calls while capabilities are loading', () => {
    const pending = new Subject<DeploymentCapabilities>();
    capabilitiesServiceMock.get.mockReturnValueOnce(pending.asObservable());
    backupServiceMock.getStatus.mockClear();
    backupServiceMock.getSettings.mockClear();
    backupServiceMock.getHistory.mockClear();
    opdsServiceMock.getInfo.mockClear();
    aiProviderServiceMock.get.mockClear();

    render();

    expect(
      fixture.nativeElement.querySelector('[data-testid="settings-capabilities-loading"]'),
    ).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-provider-settings-card"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('#library-data')).toBeNull();
    expect(backupServiceMock.getStatus).not.toHaveBeenCalled();
    expect(backupServiceMock.getSettings).not.toHaveBeenCalled();
    expect(backupServiceMock.getHistory).not.toHaveBeenCalled();
    expect(opdsServiceMock.getInfo).not.toHaveBeenCalled();
    expect(aiProviderServiceMock.get).not.toHaveBeenCalled();
  });

  it('fails closed when the capability manifest cannot be loaded', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(
      throwError(() => new Error('capabilities offline')),
    );
    backupServiceMock.getStatus.mockClear();
    backupServiceMock.getSettings.mockClear();
    backupServiceMock.getHistory.mockClear();
    opdsServiceMock.getInfo.mockClear();
    aiProviderServiceMock.get.mockClear();

    render();

    expect(
      fixture.nativeElement.querySelector('[data-testid="settings-capabilities-error"]'),
    ).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-provider-settings-card"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('#library-data')).toBeNull();
    expect(backupServiceMock.getStatus).not.toHaveBeenCalled();
    expect(backupServiceMock.getSettings).not.toHaveBeenCalled();
    expect(backupServiceMock.getHistory).not.toHaveBeenCalled();
    expect(opdsServiceMock.getInfo).not.toHaveBeenCalled();
    expect(aiProviderServiceMock.get).not.toHaveBeenCalled();
  });

  it('keeps Cloud Library settings for managed e-reader access without server-owner plumbing', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    backupServiceMock.getStatus.mockClear();
    backupServiceMock.getSettings.mockClear();
    backupServiceMock.getHistory.mockClear();
    opdsServiceMock.getInfo.mockClear();
    opdsServiceMock.getManagedAccess.mockClear();
    aiProviderServiceMock.get.mockClear();

    render();

    expect(fixture.componentInstance.deploymentCapabilities()?.deploymentMode).toBe('Cloud');
    expect(fixture.componentInstance.activeSettingsSection()).toBe('library');
    expect(
      fixture.debugElement
        .queryAll(By.css('.settings-nav-copy'))
        .map((item) => item.nativeElement.textContent.trim()),
    ).toEqual(['Library & data', 'Book providers', 'Assistant', 'Account', 'Appearance']);

    expect(fixture.nativeElement.querySelector('#library-data')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="ereader-access-card"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="ai-provider-settings-card"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('#ai-llm-base-url')).toBeNull();
    expect(fixture.nativeElement.querySelector('#ai-llm-model')).toBeNull();
    expect(fixture.nativeElement.querySelector('#ai-llm-api-key')).toBeNull();
    expect(fixture.nativeElement.querySelector('#ai-stt-base-url')).toBeNull();
    expect(fixture.nativeElement.querySelector('#ai-stt-model')).toBeNull();
    expect(fixture.nativeElement.querySelector('#ai-stt-api-key')).toBeNull();

    const pageText = (fixture.nativeElement.textContent ?? '').replace(/\s+/g, ' ');
    expect(pageText).toContain('E-reader access');
    expect(pageText).toContain('never your Nostos account password');
    expect(pageText).not.toContain('Import from Disk');
    expect(pageText).not.toContain('Tailscale');
    expect(pageText).not.toContain('Opds:Enabled');
    expect(pageText).not.toContain(aiProviderSettings.llm.baseUrl);

    expect(backupServiceMock.getStatus).not.toHaveBeenCalled();
    expect(backupServiceMock.getSettings).not.toHaveBeenCalled();
    expect(backupServiceMock.getHistory).not.toHaveBeenCalled();
    expect(opdsServiceMock.getInfo).toHaveBeenCalledTimes(1);
    expect(opdsServiceMock.getManagedAccess).toHaveBeenCalledTimes(1);
    expect(aiProviderServiceMock.get).not.toHaveBeenCalled();
  });

  it('keeps qualitative Ask Nostos usage state and hands refill management to the account site', () => {
    expect(fixture.nativeElement.querySelector('[data-testid="managed-ai-usage-card"]')).toBeNull();
    expect(cloudAiRefillServiceMock.getUsage).not.toHaveBeenCalled();

    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    render();

    const card = fixture.nativeElement.querySelector(
      '[data-testid="managed-ai-usage-card"]',
    ) as HTMLElement;
    const text = (card.textContent ?? '').replace(/\s+/g, ' ').trim();
    const refillLink = card.querySelector(
      '[data-testid="managed-ai-refill-link"]',
    ) as HTMLAnchorElement;

    expect(card).not.toBeNull();
    expect(text).toContain('Your included Ask Nostos allowance is nearly used.');
    expect(text).toContain('Renews Oct 1.');
    expect(text).toContain('You do not currently have purchased AI refill capacity.');
    expect(text).toContain('Manage refills');
    expect(text).not.toContain('Add refill');
    expect(text).not.toContain('Paddle');
    expect(card.querySelector('[data-testid="ai-refill-pack"]')).toBeNull();
    expect(refillLink.href).toBe('https://nostos.page/account');
    expect(cloudAiRefillServiceMock.getUsage).toHaveBeenCalledTimes(1);
  });

  it('fails closed for refill usage when usage metering is not advertised', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(
      of({ ...cloudCapabilities, usageMeteringAvailable: false }),
    );
    render();

    expect(fixture.nativeElement.querySelector('[data-testid="managed-ai-usage-card"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="managed-ai-refill-link"]')).toBeNull();
    expect(cloudAiRefillServiceMock.getUsage).not.toHaveBeenCalled();
  });

  it('keeps Cloud Library & data available for portable export without e-reader access', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(
      of({ ...cloudCapabilities, supportsEreaderAccess: false }),
    );
    opdsServiceMock.getInfo.mockClear();
    opdsServiceMock.getManagedAccess.mockClear();

    render();

    expect(fixture.componentInstance.activeSettingsSection()).toBe('library');
    expect(
      fixture.debugElement
        .queryAll(By.css('.settings-nav-copy'))
        .map((item) => item.nativeElement.textContent.trim()),
    ).toEqual(['Library & data', 'Book providers', 'Assistant', 'Account', 'Appearance']);
    expect(fixture.nativeElement.querySelector('#library-data')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="manage-library-summary-card"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.manage-library-groups')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="ereader-access-card"]')).toBeNull();
    expect(opdsServiceMock.getInfo).not.toHaveBeenCalled();
    expect(opdsServiceMock.getManagedAccess).not.toHaveBeenCalled();

    openManageLibraryPage();
    expect(fixture.nativeElement.querySelector('[data-testid="cloud-portable-export-card"]')).toBeTruthy();
  });

  it('offers one Cloud export action and keeps it out of SelfHosted settings', () => {
    expect(fixture.nativeElement.querySelector('[data-testid="cloud-portable-export-card"]')).toBeNull();

    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    render();
    openManageLibraryPage();

    const card = fixture.nativeElement.querySelector(
      '[data-testid="cloud-portable-export-card"]',
    ) as HTMLElement;
    const action = card.querySelector(
      '[data-testid="cloud-portable-export-action"]',
    ) as HTMLButtonElement;

    expect(card.textContent).toContain('one portable .nostos file');
    expect(card.textContent).toContain('stored EPUB, PDF, and audiobook files');
    expect(action.textContent).toContain('Export all my Nostos data');
  });

  it('shows export progress and completes the download through the portability service', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    const pending = new Subject<HttpEvent<Blob>>();
    portableLibraryServiceMock.exportArchive.mockReturnValueOnce(pending.asObservable());
    render();
    openManageLibraryPage();

    const component = fixture.componentInstance;
    const saveSpy = vi
      .spyOn(
        component as unknown as {
          savePortableArchive: (blob: Blob | null, disposition: string | null) => void;
        },
        'savePortableArchive',
      )
      .mockImplementation(() => {});

    const action = fixture.nativeElement.querySelector(
      '[data-testid="cloud-portable-export-action"]',
    ) as HTMLButtonElement;
    action.click();
    fixture.detectChanges();

    expect(portableLibraryServiceMock.exportArchive).toHaveBeenCalledTimes(1);
    expect(action.disabled).toBe(true);
    expect(
      fixture.nativeElement.querySelector('.portable-export-status')?.textContent,
    ).toContain('Preparing your archive');

    pending.next({
      type: HttpEventType.DownloadProgress,
      loaded: 50,
      total: 100,
    });
    fixture.detectChanges();
    expect(
      fixture.nativeElement.querySelector('.portable-export-status')?.textContent,
    ).toContain('50%');

    const blob = new Blob(['portable']);
    pending.next(
      new HttpResponse({
        body: blob,
        headers: new HttpHeaders({
          'content-disposition': 'attachment; filename="nostos-export-test.nostos"',
        }),
      }),
    );
    pending.complete();
    fixture.detectChanges();

    expect(saveSpy).toHaveBeenCalledWith(
      blob,
      'attachment; filename="nostos-export-test.nostos"',
    );
    expect(component.portableExportBusy()).toBe(false);
    expect(component.portableExportProgress()).toBeNull();
    expect(toastMock.success).toHaveBeenCalledWith('Your Nostos export is ready.');
  });

  it('leaves a clear inline error when Cloud export fails', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    portableLibraryServiceMock.exportArchive.mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 500, statusText: 'Server error' })),
    );
    render();
    openManageLibraryPage();

    const action = fixture.nativeElement.querySelector(
      '[data-testid="cloud-portable-export-action"]',
    ) as HTMLButtonElement;
    action.click();
    fixture.detectChanges();

    const error = fixture.nativeElement.querySelector(
      '.portable-export-status.is-error',
    ) as HTMLElement;
    expect(error.getAttribute('role')).toBe('alert');
    expect(error.textContent).toContain('Your data was not changed');
    expect(fixture.componentInstance.portableExportBusy()).toBe(false);
    expect(toastMock.error).toHaveBeenCalledWith('Could not export your Nostos data.');
  });

  it('keeps the Cloud voice toggle as product intent instead of provider configuration', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    aiProviderServiceMock.update.mockClear();
    render();

    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="managed-voice-transcription-toggle"]',
    ) as HTMLInputElement;
    expect(toggle.checked).toBe(true);

    toggle.checked = false;
    toggle.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(TestBed.inject(LibraryPreferencesService).assistantVoiceEnabled()).toBe(false);
    expect(aiProviderServiceMock.update).not.toHaveBeenCalled();
  });

  it('shows one capability-aware Manage library entry in Library & data', () => {
    const summary = fixture.nativeElement.querySelector(
      '[data-testid="manage-library-summary-card"]',
    ) as HTMLElement;
    expect(summary).toBeTruthy();
    expect(summary.textContent).toContain('Backups help you recover this SelfHosted installation.');
    expect(summary.querySelectorAll('button')).toHaveLength(1);
    expect(summary.querySelector('[data-testid="manage-library-open"]')?.textContent).toContain(
      'Manage library',
    );
    expect(fixture.nativeElement.querySelector('.manage-library-groups')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="ereader-access-card"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="highlight-import-card"]')).toBeTruthy();
  });

  it('opens the dedicated Manage library route from the summary button', () => {
    routerMock.navigate.mockClear();
    fixture.nativeElement
      .querySelector('[data-testid="manage-library-open"]')
      .click();

    expect(routerMock.navigate).toHaveBeenCalledWith(['/settings/library']);
  });

  it('renders the existing Backup and Backup History cards on Manage library', () => {
    openManageLibraryPage();
    const headers = fixture.debugElement
      .queryAll(By.css('.card-header h2'))
      .map((h) => h.nativeElement.textContent.trim());
    expect(headers).toContain('Backup');
    expect(headers).toContain('Backup History');
    // The dedicated page starts with its first capability-gated task group.
    expect(headers[0]).toBe('Backup');
  });

  it('exposes the automatic-backup toggle and manual backup action', () => {
    openManageLibraryPage();
    const toggles = fixture.debugElement.queryAll(By.css('input[type="checkbox"]'));
    // Automatic Backup + Include Book Files + the Reading assistant toggle (W1)
    // + the AI provider card's voice transcription and embeddings toggles.
    expect(toggles.length).toBe(5);
    expect(fixture.debugElement.queryAll(By.css('label.nostos-switch')).length).toBe(5);
    const buttons = fixture.debugElement
      .queryAll(By.css('button'))
      .map((b) => b.nativeElement.textContent.trim());
    expect(buttons).toContain('Back up now');
    expect(buttons).toContain('Scan for Backups');
  });

  it('uses the canonical button primitive instead of a Settings-local btn family', () => {
    expect(fixture.debugElement.queryAll(By.css('button.btn')).length).toBe(0);
    expect(fixture.debugElement.queryAll(By.css('button.nostos-button')).length).toBeGreaterThan(0);
  });

  it('asks through ConfirmModal before restoring (no direct restore)', () => {
    openManageLibraryPage();
    const component = fixture.componentInstance;
    backupServiceMock.restore.mockClear();

    component.restoreBackup('b1');
    expect(component.pendingRestore()).toBe('b1');
    expect(backupServiceMock.restore).not.toHaveBeenCalled();

    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeTruthy();

    component.confirmRestore();
    expect(backupServiceMock.restore).toHaveBeenCalledWith('b1');
    expect(component.pendingRestore()).toBeNull();
  });

  it('cancelling restore performs nothing', () => {
    openManageLibraryPage();
    const component = fixture.componentInstance;
    backupServiceMock.restore.mockClear();

    component.restoreBackup('b1');
    component.cancelRestore();
    expect(component.pendingRestore()).toBeNull();
    expect(backupServiceMock.restore).not.toHaveBeenCalled();
  });

  it('asks through ConfirmModal before deleting a backup', () => {
    openManageLibraryPage();
    const component = fixture.componentInstance;
    backupServiceMock.deleteBackup.mockClear();

    component.deleteBackup('b9');
    expect(component.pendingBackupDelete()).toBe('b9');
    expect(backupServiceMock.deleteBackup).not.toHaveBeenCalled();

    component.confirmBackupDelete();
    expect(backupServiceMock.deleteBackup).toHaveBeenCalledWith('b9');
    expect(component.pendingBackupDelete()).toBeNull();
  });

  // ------------------------------------------------------------------
  // E-reader access (issue #187)
  // ------------------------------------------------------------------

  it('renders the E-reader access card with the catalog address and one copy action', () => {
    const headers = cardHeaders();
    expect(headers).toContain('E-reader access');
    expect(
      fixture.nativeElement.querySelector('#library-data')?.contains(erCard()),
    ).toBe(true);

    expect(catalogUrlText()).toBe(remoteInfo.catalogUrl);

    const copy = copyButton();
    expect(copy).toBeTruthy();
    expect(copy!.textContent).toContain('Copy URL');
  });

  it('leads with plain language and keeps OPDS as the secondary protocol name', () => {
    const heading = cardHeading('E-reader access');
    expect(heading).toContain('E-reader access');
    expect(heading).not.toMatch(/OPDS/u);

    const text = cardBodyText();
    expect(text).toContain('e-reader');
    expect(text).toMatch(/OPDS catalog/u);
  });

  it('copies the address in one action and confirms it', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    installClipboard(writeText);

    copyButton()!.click();
    await flush();

    expect(writeText).toHaveBeenCalledWith(remoteInfo.catalogUrl);
    expect(fixture.componentInstance.copied()).toBe(true);
    fixture.detectChanges();
    expect(copyButton()!.textContent).toContain('Copied');
    expect(toastMock.success).toHaveBeenCalled();
  });

  it('reports a refused clipboard instead of silently doing nothing', async () => {
    installClipboard(vi.fn(() => Promise.reject(new Error('denied'))));

    copyButton()!.click();
    await flush();

    expect(fixture.componentInstance.copied()).toBe(false);
    expect(toastMock.error).toHaveBeenCalled();
    // The address stays on screen, selectable, as the manual fallback.
    expect(catalogUrlText()).toBe(remoteInfo.catalogUrl);
  });

  it('warns when the address is only reachable from this computer', () => {
    renderWith({
      enabled: true,
      catalogUrl: 'http://localhost:5214/opds/',
      urlSource: 'request',
      localOnly: true,
    });

    const warning = fixture.nativeElement.querySelector('.catalog-note--warning');
    expect(warning).toBeTruthy();
    expect((warning as HTMLElement).textContent).toContain('only works on this computer');

    // Still shown — a reader running on this machine can use it — but never
    // without that warning.
    expect(catalogUrlText()).toContain('localhost');
    expect(fixture.nativeElement.querySelectorAll('.catalog-note--warning').length).toBe(1);
  });

  it('offers no connection address when the server has e-reader access turned off', () => {
    renderWith({ enabled: false, catalogUrl: null, urlSource: 'request', localOnly: false });

    expect(fixture.nativeElement.querySelector('.catalog-url')).toBeNull();
    expect(copyButton()).toBeNull();
    expect(cardBodyText()).toContain('turned off');
  });

  it('says the setting could not be read rather than presenting a guess', () => {
    opdsServiceMock.getInfo.mockReturnValue(throwError(() => new Error('offline')));
    render();

    expect(cardBodyText()).toContain('Could not read this setting');
    expect(fixture.nativeElement.querySelector('.catalog-url')).toBeNull();
  });

  it('shows the managed Cloud enable state and catalog without fabricating credentials', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    opdsServiceMock.getManagedAccess.mockReturnValueOnce(of(managedDisabled));

    render();

    expect(catalogUrlText()).toBe(remoteInfo.catalogUrl);
    expect(fixture.nativeElement.querySelector('[data-testid="managed-opds-disabled"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="enable-managed-opds"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.managed-opds-username')).toBeNull();
    expect(fixture.nativeElement.querySelector('.managed-opds-password')).toBeNull();
  });

  it('shows and copies a newly-created managed password only from the create response', async () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    opdsServiceMock.getManagedAccess.mockReturnValueOnce(of(managedDisabled));
    const writeText = vi.fn(() => Promise.resolve());
    installClipboard(writeText);

    render();

    (fixture.nativeElement.querySelector(
      '[data-testid="enable-managed-opds"]',
    ) as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(opdsServiceMock.enableManagedAccess).toHaveBeenCalledTimes(1);
    expect(
      (fixture.nativeElement.querySelector('.managed-opds-username') as HTMLElement).textContent,
    ).toContain(managedIssued.username);
    expect(
      (fixture.nativeElement.querySelector('.managed-opds-password') as HTMLElement).textContent,
    ).toContain(managedIssued.password);
    expect(
      fixture.nativeElement.querySelector('[data-testid="managed-opds-one-time-secret"]'),
    ).toBeTruthy();

    (fixture.nativeElement.querySelector(
      '[data-testid="copy-managed-opds-details"]',
    ) as HTMLButtonElement).click();
    await flush();

    expect(writeText).toHaveBeenCalledWith(
      `Catalog: ${remoteInfo.catalogUrl}\nUsername: ${managedIssued.username}\nPassword: ${managedIssued.password}`,
    );
  });

  it('does not pretend it can reveal an existing managed password later', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    opdsServiceMock.getManagedAccess.mockReturnValueOnce(of(managedEnabled));

    render();

    expect(fixture.nativeElement.querySelector('.managed-opds-password')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="copy-managed-opds-details"]')).toBeNull();
    expect(cardBodyText()).toContain('password is not shown again');
    expect(cardBodyText()).toContain('regenerate it');
  });

  it('regenerates the managed password and explains that the old password stops working', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    opdsServiceMock.getManagedAccess.mockReturnValueOnce(of(managedEnabled));

    render();

    (fixture.nativeElement.querySelector(
      '[data-testid="rotate-managed-opds-password"]',
    ) as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(opdsServiceMock.rotateManagedPassword).toHaveBeenCalledTimes(1);
    expect(
      (fixture.nativeElement.querySelector('.managed-opds-password') as HTMLElement).textContent,
    ).toContain(managedRotated.password);
    expect(cardBodyText()).toContain('immediately disconnects readers');
  });

  it('requires confirmation before revoking managed e-reader access', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    opdsServiceMock.getManagedAccess.mockReturnValueOnce(of(managedEnabled));

    render();

    (fixture.nativeElement.querySelector(
      '[data-testid="revoke-managed-opds"]',
    ) as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(fixture.componentInstance.pendingOpdsRevoke()).toBe(true);
    expect(opdsServiceMock.revokeManagedAccess).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeTruthy();

    fixture.componentInstance.confirmManagedOpdsRevoke();
    fixture.detectChanges();

    expect(opdsServiceMock.revokeManagedAccess).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.pendingOpdsRevoke()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="managed-opds-disabled"]')).toBeTruthy();
  });

  it('fails closed when managed credential state cannot be read', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    opdsServiceMock.getManagedAccess.mockReturnValueOnce(
      throwError(() => new Error('managed OPDS offline')),
    );

    render();

    expect(cardBodyText()).toContain('Could not read this setting');
    expect(fixture.nativeElement.querySelector('.managed-opds-username')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="enable-managed-opds"]')).toBeNull();
  });

  it('keeps the current managed state when a management request fails', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    opdsServiceMock.getManagedAccess.mockReturnValueOnce(of(managedDisabled));
    opdsServiceMock.enableManagedAccess.mockReturnValueOnce(
      throwError(() => new Error('enable failed')),
    );

    render();

    (fixture.nativeElement.querySelector(
      '[data-testid="enable-managed-opds"]',
    ) as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(toastMock.error).toHaveBeenCalledWith('Could not enable e-reader access.');
    expect(fixture.nativeElement.querySelector('[data-testid="managed-opds-disabled"]')).toBeTruthy();
  });

  // ------------------------------------------------------------------
  // Reading assistant (W1)
  // ------------------------------------------------------------------

  it('renders the Reading assistant card with the available copy and an off toggle by default', () => {
    expect(cardHeaders()).toContain('Reading assistant');

    const card = assistantCard();
    expect(card).not.toBeNull();
    expect(card!.textContent).toContain(
      'Show the dock capsule for capturing thoughts while reading.',
    );

    const toggle = assistantToggle();
    expect(toggle.disabled).toBe(false);
    expect(toggle.getAttribute('aria-disabled')).toBeNull();
    expect(toggle.checked).toBe(false);
  });

  it('records the assistant choice through the existing preferences service', () => {
    const preferences = TestBed.inject(LibraryPreferencesService);
    expect(preferences.assistantEnabled()).toBe(false);

    assistantToggle().checked = true;
    assistantToggle().dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(preferences.assistantEnabled()).toBe(true);
  });

  it('persists enabling the assistant across a reload', async () => {
    assistantToggle().checked = true;
    assistantToggle().dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(JSON.parse(localStorage.getItem(LIBRARY_PREFERENCES_STORAGE_KEY)!).assistantEnabled).toBe(
      true,
    );

    // A reload is a fresh injector reading the same localStorage.
    TestBed.resetTestingModule();
    await configure();

    expect(TestBed.inject(LibraryPreferencesService).assistantEnabled()).toBe(true);
    expect(assistantToggle().checked).toBe(true);
  });

  it('defaults the assistant toggle off for preferences stored before it existed', async () => {
    localStorage.setItem(
      LIBRARY_PREFERENCES_STORAGE_KEY,
      JSON.stringify({
        viewMode: 'list',
        sort: 'lastread',
        pageSize: 50,
        sidebarExpanded: false,
        groupByWork: false,
      }),
    );

    TestBed.resetTestingModule();
    await configure();

    const preferences = TestBed.inject(LibraryPreferencesService);
    expect(preferences.assistantEnabled()).toBe(false);
    expect(preferences.assistantVoiceEnabled()).toBe(true);
    // The older choices survive: the missing fields are not corruption.
    expect(preferences.viewMode()).toBe('list');
    expect(preferences.pageSize()).toBe(50);
    expect(assistantToggle().checked).toBe(false);
  });

  it('renders the toggle off and non-interactive when the server is unavailable', () => {
    assistantStatusMock.available.set(false);
    render();

    // The card stays visible; the supporting sentence is substituted in place.
    const card = assistantCard();
    expect(card).not.toBeNull();
    expect(card!.textContent).toContain(
      'Set up an AI provider in Settings to enable this.',
    );
    expect(card!.textContent).not.toContain('Show the dock capsule');

    const toggle = assistantToggle();
    expect(toggle.disabled).toBe(true);
    expect(toggle.getAttribute('aria-disabled')).toBe('true');
    expect(toggle.checked).toBe(false);
    // Muted, never an alarm: the canonical switch keeps the native checkbox disabled.
    expect(toggle.closest('label.nostos-switch')).not.toBeNull();
  });

  it('uses product copy for an unavailable managed Cloud assistant', () => {
    capabilitiesServiceMock.get.mockReturnValueOnce(of(cloudCapabilities));
    assistantStatusMock.available.set(false);
    render();

    const card = assistantCard();
    expect(card).not.toBeNull();
    expect(card!.textContent).toContain('Ask Nostos is temporarily unavailable.');
    expect(card!.textContent).not.toContain('Set up an AI provider');
  });

  it('never records a preference while the assistant is unavailable', () => {
    assistantStatusMock.available.set(false);
    render();

    const preferences = TestBed.inject(LibraryPreferencesService);
    preferences.setAssistantEnabled(false);

    // The guard is belt-and-braces behind the disabled control.
    fixture.componentInstance.setAssistantEnabled({
      target: { checked: true },
    } as unknown as Event);

    expect(preferences.assistantEnabled()).toBe(false);
  });

  it('renders the stored capture-processing value with its description', () => {
    assistantSettingsMock.captureProcessingMode.set('light_polish');
    render();

    const dropdown = captureModeDropdown();
    const trigger = dropdown!.querySelector('.nostos-dropdown__trigger') as HTMLButtonElement;
    expect(trigger.textContent).toContain('Light polish');
    expect(trigger.getAttribute('aria-label')).toBe('Saved thought wording');

    const card = assistantCard();
    expect(card!.textContent).toContain('Ask AI to tidy grammar and filler');
    expect(card!.textContent).toContain(
      'For thoughts saved through Ask Nostos, typed or spoken. Quotes stay exact.',
    );
  });

  it('stores a changed capture-processing value through the settings service', () => {
    render();

    const dropdown = captureModeDropdown()!;
    (dropdown.querySelector('.nostos-dropdown__trigger') as HTMLButtonElement).click();
    fixture.detectChanges();
    const clarify = Array.from(dropdown.querySelectorAll('[role="option"]')).find((option) =>
      option.textContent?.includes('Clarify'),
    ) as HTMLElement;
    clarify.click();
    fixture.detectChanges();

    expect(assistantSettingsMock.setCaptureProcessingMode).toHaveBeenCalledWith('clarify');
  });

  it('says plainly when the capture-processing setting could not be read', () => {
    assistantSettingsMock.loadFailed.set(true);
    render();

    const card = assistantCard();
    expect(card!.textContent).toContain('Could not read this setting');
    expect(captureModeDropdown()).toBeNull();
  });

  // ------------------------------------------------------------------
  // AI provider
  // ------------------------------------------------------------------

  it('renders the AI provider card with the effective values and both keys empty', () => {
    const card = aiCard();
    expect(card).not.toBeNull();

    expect(inputValue('#ai-llm-base-url')).toBe(aiProviderSettings.llm.baseUrl);
    expect(inputValue('#ai-llm-model')).toBe(aiProviderSettings.llm.model);
    expect(inputValue('#ai-stt-base-url')).toBe(aiProviderSettings.stt.baseUrl);
    expect(inputValue('#ai-stt-model')).toBe(aiProviderSettings.stt.model);

    // The API never returns a key, so neither password field may carry one.
    expect(inputValue('#ai-llm-api-key')).toBe('');
    expect(inputValue('#ai-stt-api-key')).toBe('');
    expect(input('#ai-llm-api-key').type).toBe('password');
    expect(input('#ai-stt-api-key').type).toBe('password');
  });

  it('says a key is configured, and where it comes from, without rendering it', () => {
    const llm = cardSection('ai-provider-llm');
    expect(llm.textContent).toContain('Configured');
    expect(llm.textContent).not.toContain('server environment variable');
    expect(buttonByText('ai-provider-llm', 'Clear')).toBeTruthy();

    const stt = cardSection('ai-provider-stt');
    expect(stt.textContent).toContain('using the server environment variable');
    expect(buttonByText('ai-provider-stt', 'Clear')).toBeTruthy();
  });

  it('renders the reviewed intro and field helpers on both sections', () => {
    const card = aiCard()!;
    expect(card.textContent).toContain(
      'Notes, voice recordings and book passages are sent directly to the OpenAI-compatible endpoints configured below.',
    );
    expect(card.textContent).toContain('Base URL, including /v1.');
    expect(card.textContent).toContain('Exact model name expected by the endpoint.');
    expect(card.textContent).toContain('Stored on your server. Never returned to the browser.');
    expect(card.textContent).toContain('Send voice recordings to the transcription endpoint.');
    expect(card.textContent).toContain('Enable voice transcription');
  });

  it('offers the reviewed placeholders, and calls out an already-configured key', () => {
    expect(input('#ai-llm-base-url').placeholder).toBe('https://api.openai.com/v1');
    expect(input('#ai-stt-base-url').placeholder).toBe('https://api.openai.com/v1');
    expect(input('#ai-llm-model').placeholder).toBe('e.g. gpt-4o-mini');
    expect(input('#ai-stt-model').placeholder).toBe('e.g. whisper-1');
    // Both sections have a key already (one stored, one from the environment).
    expect(input('#ai-llm-api-key').placeholder).toBe('Configured on server (leave blank to keep)');
    expect(input('#ai-stt-api-key').placeholder).toBe('Configured on server (leave blank to keep)');
  });

  it('offers the unauthenticated key placeholder when no key is configured', () => {
    aiProviderServiceMock.get.mockReturnValueOnce(
      of<AiProviderSettings>({
        llm: { enabled: false, baseUrl: '', model: '', hasKey: false, keyFromServerEnv: false },
        stt: { enabled: false, baseUrl: '', model: '', hasKey: false, keyFromServerEnv: false },
      }),
    );
    render();

    expect(input('#ai-llm-api-key').placeholder).toBe('Leave empty if unauthenticated');
    expect(input('#ai-stt-api-key').placeholder).toBe('Leave empty if unauthenticated');
  });

  it('omits apiKey when the key was never touched', () => {
    setInputValue('#ai-llm-model', 'gpt-4o');
    clickSave();

    expect(aiProviderServiceMock.update).toHaveBeenCalledTimes(1);
    const body = aiProviderServiceMock.update.mock.calls[0][0];
    expect(body.llm).toEqual({ model: 'gpt-4o' });
    expect(body.stt).toBeUndefined();
    expect(Object.prototype.hasOwnProperty.call(body.llm, 'apiKey')).toBe(false);
    expect(saveStatusText()).toBe('Saved.');
  });

  it('sends apiKey: "" only after Clear, and says what will be used instead', () => {
    clickButton('ai-provider-llm', 'Clear');
    expect(llmStatusText()).toBe('Key removed. Falling back to environment variable if present.');

    clickSave();
    expect(aiProviderServiceMock.update.mock.calls[0][0].llm).toEqual({ apiKey: '' });
    expect(saveStatusText()).toBe('Saved.');
  });

  it('sends a typed key, then never leaves it in the DOM after the save', () => {
    setInputValue('#ai-llm-api-key', 'sk-typed');
    clickSave();

    expect(aiProviderServiceMock.update.mock.calls[0][0].llm).toEqual({ apiKey: 'sk-typed' });
    // The typed value is spent: a successful save re-seeds from the response,
    // which never carries a key.
    expect(inputValue('#ai-llm-api-key')).toBe('');
  });

  it('loads models into the datalist while keeping the field free-text', () => {
    aiProviderServiceMock.loadModels.mockReturnValueOnce(
      of<AiProviderModelsResponse>({ models: ['alpha', 'beta', 'gamma'] }),
    );

    clickButton('ai-provider-llm', 'Load models');

    const options = Array.from(
      fixture.nativeElement.querySelectorAll('#ai-llm-model-options option'),
    ).map((option) => (option as HTMLOptionElement).value);
    expect(options).toEqual(['alpha', 'beta', 'gamma']);
    expect(llmStatusText()).toBe('Loaded 3 models.');

    const model = input('#ai-llm-model');
    expect(model.getAttribute('list')).toBe('ai-llm-model-options');
    expect(model.readOnly).toBe(false);
    expect(model.disabled).toBe(false);
  });

  it('says so when the endpoint returns no models', () => {
    aiProviderServiceMock.loadModels.mockReturnValueOnce(
      of<AiProviderModelsResponse>({ models: [] }),
    );
    clickButton('ai-provider-llm', 'Load models');
    expect(llmStatusText()).toBe('No models returned by endpoint.');
  });

  it('renders the success detail inline after Test connection', () => {
    aiProviderServiceMock.test.mockReturnValueOnce(
      of<AiProviderTestResult>({ ok: true, detail: 'Connected. 42 models.' }),
    );
    clickButton('ai-provider-llm', 'Test connection');

    expect(aiProviderServiceMock.test.mock.calls[0][0]).toEqual({
      kind: 'llm',
      baseUrl: aiProviderSettings.llm.baseUrl,
      model: aiProviderSettings.llm.model,
      apiKey: undefined,
    });
    expect(llmStatusText()).toBe('Connected. 42 models.');
    expect(statusElement('ai-provider-llm').classList.contains('is-ok')).toBe(true);
  });

  it('renders the error inline when the test reports failure', () => {
    aiProviderServiceMock.test.mockReturnValueOnce(
      of<AiProviderTestResult>({ ok: false, error: '401 Unauthorized' }),
    );
    clickButton('ai-provider-llm', 'Test connection');

    expect(llmStatusText()).toBe('401 Unauthorized');
    expect(statusElement('ai-provider-llm').classList.contains('is-error')).toBe(true);
  });

  it('wraps a transport failure rather than showing a bare HTTP message', () => {
    aiProviderServiceMock.test.mockReturnValueOnce(
      throwError(() => ({ error: { error: 'Connection refused' } })),
    );
    clickButton('ai-provider-llm', 'Test connection');

    expect(llmStatusText()).toBe('Connection failed: Connection refused');
  });

  it('wraps a failed model lookup with the load-specific message', () => {
    aiProviderServiceMock.loadModels.mockReturnValueOnce(
      throwError(() => ({ error: { error: 'Endpoint rejected the request' } })),
    );
    clickButton('ai-provider-llm', 'Load models');

    expect(llmStatusText()).toBe('Could not load models: Endpoint rejected the request');
    expect(statusElement('ai-provider-llm').classList.contains('is-error')).toBe(true);
  });

  it('uses the reviewed in-flight label while a connection test runs', () => {
    const pending = new Subject<AiProviderTestResult>();
    aiProviderServiceMock.test.mockReturnValueOnce(pending);

    clickButton('ai-provider-llm', 'Test connection');
    expect(buttonByText('ai-provider-llm', 'Testing connection…')!.disabled).toBe(true);

    pending.next({ ok: true, detail: 'Connection verified.' });
    pending.complete();
    fixture.detectChanges();

    expect(llmStatusText()).toBe('Connection verified.');
  });

  it('sends the voice toggle as the stt section', () => {
    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="voice-transcription-toggle"]',
    ) as HTMLInputElement;
    expect(toggle.checked).toBe(false);

    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    clickSave();
    expect(aiProviderServiceMock.update.mock.calls[0][0]).toEqual({ stt: { enabled: true } });
  });

  it('renders the embeddings section with its effective values and an empty key', () => {
    const section = cardSection('ai-provider-embedding');
    expect(section).not.toBeNull();
    expect(section.textContent).toContain('Embeddings');
    expect(section.textContent).toContain('Ask Nostos keeps using keyword search');

    expect(inputValue('#ai-embedding-base-url')).toBe(aiProviderSettings.embedding!.baseUrl);
    expect(inputValue('#ai-embedding-model')).toBe(aiProviderSettings.embedding!.model);
    expect(inputValue('#ai-embedding-api-key')).toBe('');
    expect(input('#ai-embedding-api-key').type).toBe('password');
    expect(input('#ai-embedding-base-url').placeholder).toBe('https://ai-gateway.vercel.sh/v1');
    expect(input('#ai-embedding-model').placeholder).toBe('e.g. alibaba/qwen3-embedding-0-6b');
    // No key is configured for this section, so there is nothing to clear.
    expect(buttonByText('ai-provider-embedding', 'Clear')).toBeNull();
  });

  it('hides the embeddings section when the host does not report one', () => {
    aiProviderServiceMock.get.mockReturnValueOnce(
      of<AiProviderSettings>({ llm: aiProviderSettings.llm, stt: aiProviderSettings.stt }),
    );
    render();

    expect(cardSection('ai-provider-embedding')).toBeNull();
    expect(cardSection('ai-provider-llm')).not.toBeNull();

    setInputValue('#ai-llm-model', 'gpt-4o');
    clickSave();
    expect(aiProviderServiceMock.update.mock.calls[0][0]).toEqual({ llm: { model: 'gpt-4o' } });
  });

  it('saves the embedding endpoint, model, key and toggle as the embedding section only', () => {
    setInputValue('#ai-embedding-base-url', 'http://ollama.lan:11434/v1');
    setInputValue('#ai-embedding-model', 'qwen3-embedding:0.6b');
    setInputValue('#ai-embedding-api-key', 'sk-embedding');
    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="embedding-toggle"]',
    ) as HTMLInputElement;
    expect(toggle.checked).toBe(false);
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    clickSave();

    expect(aiProviderServiceMock.update.mock.calls[0][0]).toEqual({
      embedding: {
        enabled: true,
        baseUrl: 'http://ollama.lan:11434/v1',
        model: 'qwen3-embedding:0.6b',
        apiKey: 'sk-embedding',
      },
    });
    // Re-seeded from the response: the typed key never stays in the DOM.
    expect(inputValue('#ai-embedding-api-key')).toBe('');
  });

  it('tests and loads models for the embedding kind with the unsaved field values', () => {
    aiProviderServiceMock.test.mockReturnValueOnce(
      of<AiProviderTestResult>({
        ok: true,
        detail: 'Reached alibaba/qwen3-embedding-0-6b (1024 dimensions).',
      }),
    );

    clickButton('ai-provider-embedding', 'Test connection');

    expect(aiProviderServiceMock.test.mock.calls[0][0]).toEqual({
      kind: 'embedding',
      baseUrl: aiProviderSettings.embedding!.baseUrl,
      model: aiProviderSettings.embedding!.model,
      apiKey: undefined,
    });
    const status = statusElement('ai-provider-embedding');
    expect(status.textContent).toContain('1024 dimensions');
    expect(status.classList.contains('is-ok')).toBe(true);
    // The outcome belongs to this section, not its neighbours.
    expect(statusElement('ai-provider-llm')).toBeNull();

    aiProviderServiceMock.loadModels.mockReturnValueOnce(
      of<AiProviderModelsResponse>({ models: ['alibaba/qwen3-embedding-0-6b'] }),
    );
    clickButton('ai-provider-embedding', 'Load models');

    expect(aiProviderServiceMock.loadModels.mock.calls[0][0]).toMatchObject({ kind: 'embedding' });
    expect(
      fixture.nativeElement.querySelectorAll('#ai-embedding-model-options option').length,
    ).toBe(1);
  });

  it('disables Save, Load and Test while a request is in flight', () => {
    const pending = new Subject<AiProviderModelsResponse>();
    aiProviderServiceMock.loadModels.mockReturnValueOnce(pending);

    clickButton('ai-provider-llm', 'Load models');

    expect(buttonByText('ai-provider-llm', 'Loading models…')!.disabled).toBe(true);
    expect(saveButton().disabled).toBe(true);
    expect(buttonByText('ai-provider-stt', 'Test connection')!.disabled).toBe(true);

    pending.next({ models: ['one'] });
    pending.complete();
    fixture.detectChanges();

    expect(saveButton().disabled).toBe(false);
    expect(llmStatusText()).toBe('Loaded 1 models.');
  });

  it('reports an unreadable provider setting in place instead of guessing', () => {
    aiProviderServiceMock.get.mockReturnValueOnce(throwError(() => new Error('offline')));
    render();

    const card = aiCard();
    expect(card!.textContent).toContain('Could not load the AI provider settings.');
    expect(card!.querySelector('#ai-llm-base-url')).toBeNull();
  });

  // ------------------------------------------------------------------

  function aiCard(): HTMLElement | null {
    return fixture.nativeElement.querySelector(
      '[data-testid="ai-provider-settings-card"]',
    ) as HTMLElement | null;
  }

  function cardSection(testid: string): HTMLElement {
    return fixture.nativeElement.querySelector(`[data-testid="${testid}"]`) as HTMLElement;
  }

  function input(id: string): HTMLInputElement {
    return fixture.nativeElement.querySelector(id) as HTMLInputElement;
  }

  function inputValue(id: string): string {
    return input(id).value;
  }

  function setInputValue(id: string, value: string): void {
    const element = input(id);
    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function buttonByText(testid: string, text: string): HTMLButtonElement | null {
    const buttons = Array.from(
      cardSection(testid).querySelectorAll('button'),
    ) as HTMLButtonElement[];
    return buttons.find((button) => (button.textContent ?? '').trim().includes(text)) ?? null;
  }

  function clickButton(testid: string, text: string): void {
    const button = buttonByText(testid, text);
    expect(button).not.toBeNull();
    button!.click();
    fixture.detectChanges();
  }

  function saveButton(): HTMLButtonElement {
    return fixture.nativeElement.querySelector(
      '[data-testid="ai-provider-save"]',
    ) as HTMLButtonElement;
  }

  function clickSave(): void {
    saveButton().click();
    fixture.detectChanges();
  }

  function statusElement(testid: string): HTMLElement {
    return cardSection(testid).querySelector('.provider-status') as HTMLElement;
  }

  function llmStatusText(): string {
    return (statusElement('ai-provider-llm').textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function saveStatusText(): string {
    const element = fixture.nativeElement.querySelector(
      '.provider-save-status',
    ) as HTMLElement | null;
    return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function cardHeaders(): string[] {
    return fixture.debugElement
      .queryAll(By.css('.card-header h2'))
      .map((h) => h.nativeElement.textContent.trim());
  }

  function cardHeading(title: string): string {
    return cardHeaders().find((h) => h === title) ?? '';
  }

  function assistantCard(): HTMLElement | null {
    return fixture.nativeElement.querySelector(
      '[data-testid="assistant-settings-card"]',
    ) as HTMLElement | null;
  }

  function assistantToggle(): HTMLInputElement {
    return fixture.nativeElement.querySelector(
      '[data-testid="assistant-enabled-toggle"]',
    ) as HTMLInputElement;
  }

  function captureModeDropdown(): HTMLElement | null {
    return fixture.nativeElement.querySelector(
      '[data-testid="capture-processing-mode"]',
    ) as HTMLElement | null;
  }

  function erCard(): HTMLElement | null {
    const cards = Array.from(
      fixture.nativeElement.querySelectorAll('.settings-card'),
    ) as HTMLElement[];
    return (
      cards.find((c) => c.querySelector('h2')?.textContent?.trim() === 'E-reader access') ?? null
    );
  }

  function cardBodyText(): string {
    const card = erCard();
    if (!card) return '';
    const clone = card.cloneNode(true) as HTMLElement;
    clone.querySelector('h2')?.remove();
    return (clone.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function catalogUrlText(): string {
    return (erCard()?.querySelector('.catalog-url')?.textContent ?? '').trim();
  }

  function copyButton(): HTMLButtonElement | null {
    const buttons = Array.from(erCard()?.querySelectorAll('button') ?? []) as HTMLButtonElement[];
    return (
      buttons.find((b) => /Copy URL|Copied/u.test(b.textContent ?? '')) ?? null
    );
  }

  function installClipboard(writeText: (text: string) => Promise<void>): void {
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    });
  }

  function flush(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
  }

  /** Re-renders the card with a different server answer. */
  function renderWith(info: OpdsInfo): void {
    opdsServiceMock.getInfo.mockReturnValue(of(info));
    render();
  }

  /** Re-renders with whatever the mock currently answers. */
  function render(): void {
    fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
  }
});

describe('SettingsComponent shared library transfer host', () => {
  const CHUNK = 4 * 1024 * 1024;
  let fixture: ComponentFixture<SettingsComponent>;
  let mock: MockLibraryTransferTransport;

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

  /** Rejects the first status read with a 401, then behaves normally. */
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

  async function configure(
    capabilities: DeploymentCapabilities,
    options: ConstructorParameters<typeof MockLibraryTransferTransport>[0] = {},
    wrap?: (inner: MockLibraryTransferTransport) => DelegatingTransport,
  ): Promise<void> {
    TestBed.resetTestingModule();
    localStorage.clear();
    capabilitiesServiceMock.get.mockClear();
    capabilitiesServiceMock.get.mockReturnValue(of(capabilities));
    portableLibraryServiceMock.exportArchive.mockClear();
    toastMock.success.mockClear();
    toastMock.error.mockClear();

    mock = new MockLibraryTransferTransport({ chunkSizeBytes: CHUNK, ...options });
    const transport: LibraryTransferTransport = wrap ? wrap(mock) : mock;

    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { data: { manageLibraryPage: true } } },
        },
        { provide: BackupService, useValue: backupServiceMock },
        { provide: OpdsService, useValue: opdsServiceMock },
        { provide: ToastService, useValue: toastMock },
        { provide: AssistantStatusService, useValue: assistantStatusMock },
        { provide: AssistantSettingsService, useValue: assistantSettingsMock },
        { provide: AiProviderService, useValue: aiProviderServiceMock },
        { provide: ProviderSettingsService, useValue: providerSettingsServiceMock },
        { provide: DeploymentCapabilitiesService, useValue: capabilitiesServiceMock },
        { provide: CloudAiRefillService, useValue: cloudAiRefillServiceMock },
        { provide: CloudAuthService, useValue: cloudAuthServiceMock },
        { provide: PortableLibraryService, useValue: portableLibraryServiceMock },
        { provide: LIBRARY_TRANSFER_TRANSPORT, useValue: transport },
        { provide: HASH_WORKER_FACTORY, useValue: () => null },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
  }

  function coordinator(): LibraryTransferCoordinator {
    return TestBed.inject(LibraryTransferCoordinator);
  }

  function importFlow(): LibraryImportFlowComponent {
    return fixture.debugElement.query(By.directive(LibraryImportFlowComponent))
      .componentInstance;
  }

  function testId(id: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  }

  async function portableFile(): Promise<File> {
    return createFile(await portableArchiveFixture(portableManifest()));
  }

  function selectFile(file: File): void {
    importFlow().onFileSelected({
      target: { files: [file], value: 'picked' },
    } as unknown as Event);
    fixture.detectChanges();
  }

  async function waitForKind(kind: string): Promise<void> {
    await vi.waitFor(() => expect(coordinator().state().kind).toBe(kind), { timeout: 5_000 });
    fixture.detectChanges();
  }

  beforeEach(() => {
    localStorage.clear();
  });

  afterEach(() => {
    fixture?.destroy();
    localStorage.removeItem(TRANSFER_RESUME_STORAGE_KEY);
    localStorage.removeItem(TRANSFER_TAB_LEASE_KEY);
    vi.useRealTimers();
  });

  it('renders the card only when migration is advertised, in both modes', async () => {
    // Capability off (every real deployment today): exactly main's surface.
    await configure(selfHostedCapabilities);
    expect(testId('library-transfer-card')).toBeNull();
    expect(testId('library-transfer-host')).toBeNull();
    expect(testId('cloud-portable-export-card')).toBeNull();
    expect(testId('cloud-portable-export-action')).toBeNull();
    expect(mock.calls.preflight).toBe(0);
    expect(mock.calls.createJob).toBe(0);
    expect(mock.calls.getJob).toBe(0);
    expect(mock.calls.uploadChunk).toBe(0);

    await configure(cloudCapabilities);
    expect(testId('library-transfer-card')).toBeNull();
    expect(testId('library-transfer-host')).toBeNull();
    expect(testId('cloud-portable-export-card')).toBeTruthy();
    expect(testId('cloud-portable-export-action')).toBeTruthy();

    // Capability on: the shared host replaces the Cloud-only card in both modes.
    await configure({
      ...selfHostedCapabilities,
      supportsLibraryMigration: true,
      supportsSafeActivation: true,
    });

    const card = testId('library-transfer-card') as HTMLElement;
    expect(card).toBeTruthy();
    expect(card.getAttribute('aria-labelledby')).toBe('library-transfer-heading');
    expect(card.querySelector('#library-transfer-heading')?.textContent).toContain(
      'Move your library',
    );

    const host = testId('library-transfer-host') as HTMLElement;
    expect(host).toBeTruthy();
    expect(testId('library-export-flow')).toBeTruthy();
    expect(testId('library-import-flow')).toBeTruthy();
    expect(testId('cloud-portable-export-card')).toBeNull();
    expect(testId('cloud-portable-export-action')).toBeNull();

    // Entries are real buttons; the file input is triggered from the picker.
    expect((testId('export-start') as HTMLButtonElement).tagName).toBe('BUTTON');
    expect((testId('import-choose-file') as HTMLButtonElement).tagName).toBe('BUTTON');
    expect(
      fixture.nativeElement.querySelector('input[type="file"][data-testid="library-import-file-input"]'),
    ).toBeTruthy();
    const groups = Array.from(host.querySelectorAll('[role="group"]')).map((group) =>
      group.getAttribute('aria-label'),
    );
    expect(groups).toEqual(['Export library', 'Import library']);

    // Slice B8: the host forwards the server capability instead of hard-coding
    // false (the old gated assertion is intentionally replaced).
    expect(importFlow().supportsSafeActivation()).toBe(true);

    await configure({ ...cloudCapabilities, supportsLibraryMigration: true });
    expect(testId('library-transfer-host')).toBeTruthy();
    expect(testId('library-export-flow')).toBeTruthy();
    expect(testId('library-import-flow')).toBeTruthy();
    expect(testId('cloud-portable-export-card')).toBeNull();
  });

  it('separates portable library transfer from the local operational Backup when migration is on', async () => {
    await configure({ ...selfHostedCapabilities, supportsLibraryMigration: true });

    const pageText = (fixture.nativeElement.textContent ?? '').replace(/\s+/g, ' ');
    expect(pageText).toContain(
      'Portable library archives are for moving your library between Nostos installations.',
    );
    expect(pageText).toContain(
      'Backups are for recovering this SelfHosted installation. They are not portable library exports.',
    );

    const transferCard = testId('library-transfer-card') as HTMLElement;
    const backupPurpose = testId('backup-purpose') as HTMLElement;
    expect(backupPurpose).toBeTruthy();
    expect(transferCard.contains(backupPurpose)).toBe(false);

    const backupButtons = Array.from(
      fixture.nativeElement.querySelectorAll('button'),
    ).map((button: any) => (button.textContent ?? '').trim());
    expect(backupButtons).toContain('Back up now');
    expect(backupButtons).toContain('Scan for Backups');
    expect(transferCard.textContent).not.toContain('Back up now');
    expect(transferCard.textContent).not.toContain('Scan for Backups');
  });

  it('runs the import happy path through the Settings host with the mock transport', async () => {
    await configure({
      ...selfHostedCapabilities,
      supportsLibraryMigration: true,
      supportsSafeActivation: true,
    });
    const completed = vi.spyOn(fixture.componentInstance, 'onLibraryTransferCompleted');

    const file = await portableFile();
    selectFile(file);
    await waitForKind('ready-empty');

    // Slice B8: with safe activation advertised the empty path hands off to
    // the real controller instead of showing the gated explanation.
    expect(testId('import-activation-unavailable')).toBeNull();
    expect(mock.calls.createJob).toBe(1);
    expect(mock.uploadedChunks.length).toBeGreaterThan(0);

    await vi.waitFor(() => expect(mock.calls.activateJob).toBe(1), { timeout: 5_000 });
    const state = coordinator().state();
    const jobId = state.kind === 'ready-empty' ? state.jobId : '';
    mock.completeActivation(jobId);

    await vi.waitFor(() => expect(testId('import-completed')).toBeTruthy(), { timeout: 5_000 });
    expect(completed).toHaveBeenCalledTimes(1);
    fixture.detectChanges();
  });

  it('shows the server current counts before a confirmed replacement', async () => {
    await configure(
      {
        ...selfHostedCapabilities,
        supportsLibraryMigration: true,
        supportsSafeActivation: true,
      },
      { destinationStatus: 'Populated', existingCounts: { books: 11, notes: 4 } },
    );

    const file = await portableFile();
    selectFile(file);
    await waitForKind('replacement-confirmation');

    // The host fetches fresh destination facts before the user confirms, so
    // the dialog shows the server's counts rather than the preflight estimate.
    await vi.waitFor(() => expect(testId('replacement-conflict')).toBeTruthy(), {
      timeout: 5_000,
    });
    expect(testId('replacement-existing')?.textContent).toContain('11 books');
    expect(mock.calls.activateJob).toBe(1);
    expect(mock.activationRequests[0]).toEqual({
      destinationRevision: 'rev-1',
      confirmReplacement: false,
    });

    (fixture.nativeElement.querySelector('.replacement-confirm') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(mock.calls.activateJob).toBe(2), { timeout: 5_000 });
    expect(mock.activationRequests[1]).toEqual({
      destinationRevision: 'rev-1',
      confirmReplacement: true,
    });

    const state = coordinator().state();
    const jobId = state.kind === 'replacement-confirmation' ? state.jobId : '';
    mock.completeActivation(jobId);
    await vi.waitFor(() => expect(testId('import-completed')).toBeTruthy(), { timeout: 5_000 });
  });

  it('gates replacement confirmation when safe activation is unavailable', async () => {
    await configure(
      {
        ...selfHostedCapabilities,
        supportsLibraryMigration: true,
        supportsSafeActivation: false,
      },
      { destinationStatus: 'Populated', existingCounts: { books: 1 } },
    );

    const file = await portableFile();
    selectFile(file);
    await waitForKind('replacement-confirmation');

    expect(testId('replacement-blocked')).toBeTruthy();
    const confirm = fixture.nativeElement.querySelector(
      '.replacement-confirm',
    ) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);
    confirm.click();
    fixture.detectChanges();

    expect(coordinator().state().kind).toBe('replacement-confirmation');
    expect(mock.calls.cancelJob).toBe(0);
    expect(mock.calls.activateJob).toBe(0);
  });

  it('adopts the tab lease after leaving and re-entering Settings and releases it on completion', async () => {
    await configure(
      { ...selfHostedCapabilities, supportsLibraryMigration: true },
      {},
      (inner) => new HeldUploadTransport(inner),
    );
    const transport = TestBed.inject(LIBRARY_TRANSFER_TRANSPORT) as HeldUploadTransport;
    const lease = TestBed.inject(TransferTabLease);

    const file = await portableFile();
    selectFile(file);
    await waitForKind('uploading');
    await vi.waitFor(() => expect(transport.pending.length).toBeGreaterThan(0));
    expect(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)).not.toBeNull();
    expect(lease.heartbeatActive).toBe(true);

    fixture.destroy();
    fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();

    // The root-scoped coordinator still owns the transfer; the new host adopts
    // the same-tab lease and shows the running state.
    expect(coordinator().state().kind).toBe('uploading');
    expect(testId('import-uploading')).toBeTruthy();

    await transport.releaseAll();
    await waitForKind('ready-empty');

    const state = coordinator().state();
    const jobId = state.kind === 'ready-empty' ? state.jobId : '';
    mock.setJobState(jobId, 'Completed');
    await coordinator().refreshStatus();
    await waitForKind('completed');

    expect(localStorage.getItem(TRANSFER_TAB_LEASE_KEY)).toBeNull();
    expect(lease.heartbeatActive).toBe(false);
  });

  it('offers sign-in recovery after a 401 and resumes after re-authentication', async () => {
    await configure(
      { ...selfHostedCapabilities, supportsLibraryMigration: true },
      {},
      (inner) => new UnauthorizedOnceTransport(inner),
    );
    const transport = TestBed.inject(LIBRARY_TRANSFER_TRANSPORT) as UnauthorizedOnceTransport;

    const file = await portableFile();
    selectFile(file);
    await waitForKind('failed');

    expect(testId('import-failed')?.textContent).toContain('Sign in again');
    const action = testId('import-failure-action') as HTMLButtonElement;
    expect(action.textContent).toContain('Try again');

    transport.unauthorized = false;
    action.click();
    await waitForKind('ready-empty');

    expect(testId('import-ready-empty')).toBeTruthy();
    expect(testId('import-activation-unavailable')).toBeTruthy();
  });
});
