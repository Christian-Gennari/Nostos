import { Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { SwUpdate } from '@angular/service-worker';
import { of } from 'rxjs';
import { App } from './app.component';
import { WorkspaceLayout } from './layout/workspace-layout/workspace-layout.component';
import { AssistantStatusService } from './ui/assistant/assistant-status.service';
import { CloudEntryService } from './core/services/cloud-entry.service';
import { HostedBrowserIntegrationService } from './core/services/hosted-browser-integration.service';
import { DeploymentCapabilitiesService } from './core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from './core/dtos/deployment-capabilities.dtos';
import { LibraryPreferencesService } from './core/services/library-preferences.service';
import { LibraryActivationController } from './library-transfer/services/library-activation-controller.service';
import { HostActivationState } from './library-transfer/models/library-transfer.models';
import { MigrationActivationPhase } from './library-transfer/models/migration-http.dtos';

@Component({ standalone: true, template: '' })
class BlankComponent {}

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

describe('App', () => {
  const productReady = signal(true);

  beforeEach(async () => {
    localStorage.clear();
    productReady.set(true);

    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        { provide: HostedBrowserIntegrationService, useValue: { start: vi.fn() } },
        provideHttpClient(),
        {
          provide: CloudEntryService,
          useValue: {
            productReady,
            initialize: () => Promise.resolve(),
            view: signal({ kind: 'product' }),
            actionPending: signal(false),
            actionError: signal(null),
            checkoutRedirect: signal(null),
            supportsLibraryMigration: signal(false),
            supportsSafeActivation: signal(false),
            firstRunImportPending: signal(false),
            openManageLibraryFromFirstRun: vi.fn(),
          },
        },
        {
          provide: AssistantStatusService,
          useValue: { available: signal(true), ensureLoaded: () => {}, refresh: () => {} },
        },
        {
          provide: SwUpdate,
          useValue: { isEnabled: false, checkForUpdate: () => Promise.resolve(false) },
        },
      ],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('starts optional host integration while the product entitlement gate is closed', () => {
    productReady.set(false);
    TestBed.createComponent(App);
    expect(TestBed.inject(HostedBrowserIntegrationService).start).toHaveBeenCalledTimes(1);
  });

  it('renders the normal product shell only after Cloud entry is ready', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('router-outlet')).not.toBeNull();
    expect(compiled.querySelector('app-toast-container')).not.toBeNull();

    productReady.set(false);
    fixture.detectChanges();

    expect(compiled.querySelector('router-outlet')).toBeNull();
    expect(compiled.querySelector('app-cloud-entry')).not.toBeNull();
    expect(compiled.querySelector('app-assistant')).toBeNull();
  });
});

describe('App activation boundary', () => {
  const activationState = signal<HostActivationState>('idle');
  const activationPhase = signal<MigrationActivationPhase | null>(null);
  const supportsSafeActivation = signal(false);
  const reattach = vi.fn();

  beforeEach(async () => {
    localStorage.clear();
    activationState.set('idle');
    activationPhase.set(null);
    supportsSafeActivation.set(false);
    reattach.mockClear();

    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        {
          provide: CloudEntryService,
          useValue: {
            productReady: signal(true),
            initialize: () => Promise.resolve(),
            view: signal({ kind: 'product' }),
            actionPending: signal(false),
            actionError: signal(null),
            checkoutRedirect: signal(null),
            supportsLibraryMigration: signal(false),
            supportsSafeActivation,
            firstRunImportPending: signal(false),
            openManageLibraryFromFirstRun: vi.fn(),
          },
        },
        {
          provide: AssistantStatusService,
          useValue: { available: signal(true), ensureLoaded: () => {}, refresh: () => {} },
        },
        {
          provide: SwUpdate,
          useValue: { isEnabled: false, checkForUpdate: () => Promise.resolve(false) },
        },
        { provide: DeploymentCapabilitiesService, useValue: { get: () => of(cloudCapabilities) } },
        {
          provide: LibraryActivationController,
          useValue: { state: activationState, phase: activationPhase, reattach },
        },
      ],
    }).compileComponents();
  });

  it('covers the whole app while the server replaces the library', () => {
    activationState.set('in-progress');
    activationPhase.set('Activating');

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    const overlay = fixture.nativeElement.querySelector(
      '[data-testid="library-activation-overlay"]',
    ) as HTMLElement;
    expect(overlay).toBeTruthy();
    expect(overlay.textContent).toContain('Switching libraries…');
    expect(overlay.querySelectorAll('button')).toHaveLength(0);
  });

  it('leaves the app interactive when no activation is running', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-testid="library-activation-overlay"]'),
    ).toBeNull();
  });

  it('reattaches persisted activation only when the server advertises safe activation', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(reattach).not.toHaveBeenCalled();

    supportsSafeActivation.set(true);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(reattach).toHaveBeenCalledTimes(1);
  });
});

describe('App shell utility area', () => {
  const assistantAvailable = signal(false);

  beforeEach(async () => {
    localStorage.clear();
    assistantAvailable.set(false);

    // CDK's interactivity checker needs real geometry; jsdom reports 0x0.
    Object.defineProperty(HTMLElement.prototype, 'offsetWidth', {
      configurable: true,
      get: () => 1,
    });
    Object.defineProperty(HTMLElement.prototype, 'offsetHeight', {
      configurable: true,
      get: () => 1,
    });

    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([
          {
            path: '',
            component: WorkspaceLayout,
            children: [
              { path: 'library', component: BlankComponent },
              { path: 'settings', component: BlankComponent },
            ],
          },
        ]),
        {
          provide: CloudEntryService,
          useValue: {
            productReady: signal(true),
            initialize: () => Promise.resolve(),
            view: signal({ kind: 'product' }),
            actionPending: signal(false),
            actionError: signal(null),
            checkoutRedirect: signal(null),
            supportsLibraryMigration: signal(false),
            supportsSafeActivation: signal(false),
            firstRunImportPending: signal(false),
            openManageLibraryFromFirstRun: vi.fn(),
          },
        },
        {
          provide: AssistantStatusService,
          useValue: { available: assistantAvailable, ensureLoaded: () => {}, refresh: () => {} },
        },
        {
          provide: SwUpdate,
          useValue: { isEnabled: false, checkForUpdate: () => Promise.resolve(false) },
        },
        {
          provide: DeploymentCapabilitiesService,
          useValue: { get: () => of(cloudCapabilities) },
        },
      ],
    }).compileComponents();
  });

  afterEach(() => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1024 });
  });

  async function renderNarrow(): Promise<ComponentFixture<App>> {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 390 });
    const fixture = TestBed.createComponent(App);
    await TestBed.inject(Router).navigateByUrl('/library');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  async function openMore(fixture: ComponentFixture<App>): Promise<HTMLButtonElement> {
    const more = fixture.nativeElement.querySelector(
      '[data-testid="dock-more"]',
    ) as HTMLButtonElement;
    // Keyboard activation: the trigger owns focus before the sheet opens, which
    // is exactly the state CDK captures for restoration.
    more.focus();
    more.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return more;
  }

  const menu = (): HTMLElement | null => document.querySelector('.nostos-menu');
  const dock = (fixture: ComponentFixture<App>): HTMLElement =>
    fixture.nativeElement.querySelector('app-app-dock');

  it('keeps Send feedback in the shell with Ask Nostos disabled and enabled', async () => {
    const fixture = TestBed.createComponent(App);
    await TestBed.inject(Router).navigateByUrl('/library');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const feedback = fixture.nativeElement.querySelector('[data-testid="dock-feedback"]');
    expect(feedback).toBeTruthy();
    expect(feedback.href).toBe('https://nostos.page/feedback?from=library');

    // Disabled: the assistant shell renders nothing, and the utility area has no
    // slot reserved for it, so there is no gap or placeholder to collapse.
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeNull();

    // Enabled and available: the dock carries the one launcher, the assistant's
    // floating capsule stands down, and Feedback stays a dock utility.
    TestBed.inject(LibraryPreferencesService).assistantEnabled.set(true);
    assistantAvailable.set(true);
    fixture.detectChanges();

    const launchers = fixture.nativeElement.querySelectorAll('[data-testid="assistant-trigger"]');
    expect(launchers.length).toBe(1);
    expect(dock(fixture).contains(launchers[0])).toBe(true);
    expect(fixture.nativeElement.querySelector('[data-testid="dock-feedback"]')).toBeTruthy();
  });

  it('opens More as a bottom sheet holding Feedback, Search and Settings, and gives focus back on Escape', async () => {
    const fixture = await renderNarrow();
    const more = await openMore(fixture);

    expect(menu()).toBeTruthy();
    expect(menu()!.closest('.nostos-sheet-pane')).toBeTruthy();
    expect(more.getAttribute('aria-haspopup')).toBe('menu');
    expect(more.getAttribute('aria-expanded')).toBe('true');
    expect(
      Array.from(menu()!.querySelectorAll('[nostosMenuItem], .nostos-menu-item')).map((item) =>
        item.textContent?.trim(),
      ),
    ).toEqual(['Send feedback', 'Search everything', 'Settings']);
    expect(document.activeElement).toBe(
      document.querySelector('[data-testid="utility-sheet-feedback"]'),
    );

    (document.activeElement as HTMLElement).dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    fixture.detectChanges();

    expect(menu()).toBeNull();
    expect(document.activeElement).toBe(more);
  });

  it('closes the menu and goes to Settings when Settings is chosen', async () => {
    const fixture = await renderNarrow();
    await openMore(fixture);

    (document.querySelector('[data-testid="utility-sheet-settings"]') as HTMLElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(TestBed.inject(Router).url).toBe('/settings');
    expect(menu()).toBeNull();
  });

  it('leaves Ask Nostos usable after the menu closes', async () => {
    TestBed.inject(LibraryPreferencesService).assistantEnabled.set(true);
    assistantAvailable.set(true);
    const fixture = await renderNarrow();

    await openMore(fixture);
    (document.activeElement as HTMLElement).dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }),
    );
    fixture.detectChanges();

    const trigger = fixture.nativeElement.querySelector(
      '[data-testid="assistant-trigger"]',
    ) as HTMLButtonElement;
    expect(trigger).toBeTruthy();
    expect(trigger.closest('[inert]')).toBeNull();

    trigger.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-panel"]')).toBeTruthy();
  });

  it('removes the More menu when the viewport crosses to wide', async () => {
    const fixture = await renderNarrow();
    await openMore(fixture);

    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 });
    window.dispatchEvent(new Event('resize'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="dock-feedback"]')).toBeTruthy();
    expect(menu()).toBeNull();
    expect(dock(fixture).hasAttribute('inert')).toBe(false);
  });
});
