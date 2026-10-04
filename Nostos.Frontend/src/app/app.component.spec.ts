import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { SwUpdate } from '@angular/service-worker';
import { of } from 'rxjs';
import { App } from './app.component';
import { WorkspaceLayout } from './layout/workspace-layout/workspace-layout.component';
import { AssistantStatusService } from './ui/assistant/assistant-status.service';
import { CloudEntryService } from './core/services/cloud-entry.service';
import { DeploymentCapabilitiesService } from './core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from './core/dtos/deployment-capabilities.dtos';
import { LibraryPreferencesService } from './core/services/library-preferences.service';

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
        {
          provide: CloudEntryService,
          useValue: {
            productReady,
            initialize: () => Promise.resolve(),
            view: signal({ kind: 'product' }),
            actionPending: signal(false),
            actionError: signal(null),
            checkoutRedirect: signal(null),
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

  const background = (fixture: ComponentFixture<App>): HTMLElement =>
    fixture.nativeElement.querySelector('.workspace-content');
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

    // Enabled and available: the assistant appears as its own floating control,
    // while Feedback remains the quiet dock sibling of Settings.
    TestBed.inject(LibraryPreferencesService).assistantEnabled.set(true);
    assistantAvailable.set(true);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="dock-feedback"]')).toBeTruthy();
  });

  it('makes the shell inert while the More sheet is open and lifts it on Escape', async () => {
    const fixture = await renderNarrow();
    const more = await openMore(fixture);

    expect(background(fixture).hasAttribute('inert')).toBe(true);
    expect(dock(fixture).hasAttribute('inert')).toBe(true);
    expect(
      (fixture.nativeElement.querySelector('app-utility-sheet') as HTMLElement).hasAttribute(
        'inert',
      ),
    ).toBe(false);
    expect(document.activeElement).toBe(
      fixture.nativeElement.querySelector('[data-testid="utility-sheet-feedback"]'),
    );

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="utility-sheet"]')).toBeNull();
    expect(background(fixture).hasAttribute('inert')).toBe(false);
    expect(dock(fixture).hasAttribute('inert')).toBe(false);
    expect(document.activeElement).toBe(more);
  });

  it('lifts the inert shell and restores focus when the scrim closes the sheet', async () => {
    const fixture = await renderNarrow();
    const more = await openMore(fixture);

    (fixture.nativeElement.querySelector('.utility-sheet-scrim') as HTMLElement).click();
    fixture.detectChanges();

    expect(background(fixture).hasAttribute('inert')).toBe(false);
    expect(dock(fixture).hasAttribute('inert')).toBe(false);
    expect(document.activeElement).toBe(more);
  });

  it('lifts the inert shell when a route change closes the sheet', async () => {
    const fixture = await renderNarrow();
    await openMore(fixture);

    await TestBed.inject(Router).navigateByUrl('/settings');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="utility-sheet"]')).toBeNull();
    expect(background(fixture).hasAttribute('inert')).toBe(false);
    expect(dock(fixture).hasAttribute('inert')).toBe(false);
  });

  it('leaves Ask Nostos usable after the sheet closes', async () => {
    TestBed.inject(LibraryPreferencesService).assistantEnabled.set(true);
    assistantAvailable.set(true);
    const fixture = await renderNarrow();

    await openMore(fixture);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    const trigger = fixture.nativeElement.querySelector(
      '[data-testid="assistant-trigger"]',
    ) as HTMLButtonElement;
    expect(trigger).toBeTruthy();
    expect(trigger.closest('[inert]')).toBeNull();

    trigger.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="assistant-panel"]')).toBeTruthy();
  });

  it('closes the More sheet, its scrim and the inert shell when the viewport crosses to wide', async () => {
    const fixture = await renderNarrow();
    await openMore(fixture);
    expect(background(fixture).hasAttribute('inert')).toBe(true);

    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 });
    window.dispatchEvent(new Event('resize'));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="utility-sheet"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('.utility-sheet-scrim')).toBeNull();
    expect(background(fixture).hasAttribute('inert')).toBe(false);
    expect(dock(fixture).hasAttribute('inert')).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="dock-feedback"]')).toBeTruthy();
  });
});
