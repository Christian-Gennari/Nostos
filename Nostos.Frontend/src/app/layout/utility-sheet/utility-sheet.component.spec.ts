import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { UtilitySheetComponent } from './utility-sheet.component';
import { UtilitySheetService } from './utility-sheet.service';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from '../../core/dtos/deployment-capabilities.dtos';

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

const selfHostedCapabilities: DeploymentCapabilities = {
  ...cloudCapabilities,
  deploymentMode: 'SelfHosted',
  managedAi: false,
  usesCloudStorage: false,
  supportsLocalBackupConfiguration: true,
  supportsPrivateNetworkAccess: true,
  usageMeteringAvailable: false,
  accountManagementUrl: null,
  feedbackUrl: null,
};

describe('UtilitySheetComponent', () => {
  let capabilities: DeploymentCapabilities;
  let sheet: UtilitySheetService;
  let fixture: ComponentFixture<UtilitySheetComponent>;
  let trigger: HTMLButtonElement;

  beforeEach(async () => {
    capabilities = cloudCapabilities;
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', component: BlankComponent }]),
        {
          provide: DeploymentCapabilitiesService,
          useValue: { get: () => of(capabilities) },
        },
      ],
    }).compileComponents();

    // CDK's interactivity checker needs real geometry; jsdom reports 0x0.
    Object.defineProperty(HTMLElement.prototype, 'offsetWidth', {
      configurable: true,
      get: () => 1,
    });
    Object.defineProperty(HTMLElement.prototype, 'offsetHeight', {
      configurable: true,
      get: () => 1,
    });

    await TestBed.inject(Router).navigateByUrl('/library');
    sheet = TestBed.inject(UtilitySheetService);
    fixture = TestBed.createComponent(UtilitySheetComponent);
    fixture.detectChanges();

    trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.setAttribute('data-testid', 'more-trigger');
    trigger.setAttribute('aria-controls', 'mobile-more-sheet');
    document.body.appendChild(trigger);
  });

  afterEach(() => {
    sheet?.close();
    trigger.remove();
  });

  async function open(): Promise<void> {
    trigger.focus();
    sheet.open.set(true);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function finishClose(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 240));
    fixture.detectChanges();
  }

  function clickWithoutNavigating(element: HTMLElement): void {
    element.addEventListener('click', (event) => event.preventDefault());
    element.click();
  }

  function query<T extends Element>(selector: string): T {
    return fixture.nativeElement.querySelector(selector) as T;
  }

  it('renders nothing until the dock trigger opens it', () => {
    expect(query('[data-testid="utility-sheet"]')).toBeNull();

    sheet.open.set(true);
    fixture.detectChanges();
    expect(query('[data-testid="utility-sheet"]')).toBeTruthy();
  });

  it('connects the trigger to an accessible dialog and disables it during its exit', async () => {
    await open();

    const panel = query<HTMLElement>('[data-testid="utility-sheet"]');
    expect(trigger.getAttribute('aria-controls')).toBe(panel.id);
    expect(panel.id).toBe('mobile-more-sheet');
    expect(panel.getAttribute('aria-modal')).toBe('true');
    expect(panel.hasAttribute('inert')).toBe(false);

    query<HTMLElement>('.utility-sheet-scrim').click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(sheet.open()).toBe(false);
    expect(panel.getAttribute('aria-hidden')).toBe('true');
    expect(panel.hasAttribute('aria-modal')).toBe(false);
    expect(panel.hasAttribute('inert')).toBe(true);
    expect(document.activeElement).toBe(trigger);

    await finishClose();
    expect(query('[data-testid="utility-sheet"]')).toBeNull();
  });

  it('exposes Feedback with the current origin and canonical external-link behavior', async () => {
    await open();

    const feedback = query<HTMLAnchorElement>('[data-testid="utility-sheet-feedback"]');
    expect(feedback.textContent).toContain('Send feedback');
    expect(feedback.href).toBe('https://nostos.page/feedback?from=library');
    expect(feedback.target).toBe('_blank');
    expect(feedback.rel).toBe('noopener noreferrer');

    const settings = query<HTMLAnchorElement>('[data-testid="utility-sheet-settings"]');
    expect(settings.textContent).toContain('Settings');
    expect(settings.getAttribute('href')).toBe('/settings');
  });

  it('moves focus to the first sheet item on open', async () => {
    await open();

    expect(document.activeElement).toBe(
      query('[data-testid="utility-sheet-feedback"]'),
    );
  });

  it('wraps Tab forward and Shift+Tab backward inside the sheet', async () => {
    await open();

    const anchors = fixture.nativeElement.querySelectorAll(
      '.cdk-focus-trap-anchor',
    ) as NodeListOf<HTMLElement>;
    expect(anchors.length).toBe(2);

    // Tabbing off the last item lands on the end anchor, which redirects to the
    // first item; Shift+Tab off the first lands on the start anchor and wraps
    // back to the last.
    anchors[1].focus();
    expect(document.activeElement).toBe(
      query('[data-testid="utility-sheet-feedback"]'),
    );

    anchors[0].focus();
    expect(document.activeElement).toBe(
      query('[data-testid="utility-sheet-settings"]'),
    );
  });

  it('closes the sheet, with no lingering overlay, when Feedback opens', async () => {
    await open();
    clickWithoutNavigating(query('[data-testid="utility-sheet-feedback"]'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(sheet.open()).toBe(false);
    expect(document.activeElement).toBe(trigger);

    await finishClose();
    expect(query('[data-testid="utility-sheet"]')).toBeNull();
    expect(query('.utility-sheet-scrim')).toBeNull();
  });

  it('closes the sheet when Settings is chosen and restores the trigger focus', async () => {
    await open();
    clickWithoutNavigating(query('[data-testid="utility-sheet-settings"]'));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(sheet.open()).toBe(false);
    expect(document.activeElement).toBe(trigger);
    await finishClose();
    expect(query('[data-testid="utility-sheet"]')).toBeNull();
  });

  it('closes on a backdrop tap and restores the trigger focus', async () => {
    await open();
    query<HTMLElement>('.utility-sheet-scrim').click();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(sheet.open()).toBe(false);
    expect(document.activeElement).toBe(trigger);
    await finishClose();
    expect(query('[data-testid="utility-sheet"]')).toBeNull();
  });

  it('closes on Escape and restores the trigger focus', async () => {
    await open();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(sheet.open()).toBe(false);
    expect(document.activeElement).toBe(trigger);
    await finishClose();
    expect(query('[data-testid="utility-sheet"]')).toBeNull();
  });

  it('closes when the route changes underneath it and restores the trigger focus', async () => {
    await open();
    await TestBed.inject(Router).navigateByUrl('/settings');
    fixture.detectChanges();
    await fixture.whenStable();

    expect(sheet.open()).toBe(false);
    expect(document.activeElement).toBe(trigger);
    await finishClose();
    expect(query('[data-testid="utility-sheet"]')).toBeNull();
  });

  it('shows only Settings when the deployment has no feedback destination', async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', component: BlankComponent }]),
        {
          provide: DeploymentCapabilitiesService,
          useValue: { get: () => of(selfHostedCapabilities) },
        },
      ],
    }).compileComponents();

    await TestBed.inject(Router).navigateByUrl('/library');
    const selfHostedSheet = TestBed.inject(UtilitySheetService);
    const selfHostedFixture = TestBed.createComponent(UtilitySheetComponent);
    selfHostedSheet.open.set(true);
    selfHostedFixture.detectChanges();

    expect(
      selfHostedFixture.nativeElement.querySelector('[data-testid="utility-sheet-feedback"]'),
    ).toBeNull();
    expect(
      selfHostedFixture.nativeElement.querySelector('[data-testid="utility-sheet-settings"]'),
    ).toBeTruthy();
  });
});
