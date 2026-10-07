import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';

import { AppDockComponent } from './app-dock.component';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from '../../core/dtos/deployment-capabilities.dtos';
import { UtilitySheetService } from '../utility-sheet/utility-sheet.service';

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
  requiresAuthentication: false,
  managedAi: false,
  usesCloudStorage: false,
  supportsLocalBackupConfiguration: true,
  supportsPrivateNetworkAccess: true,
  usageMeteringAvailable: false,
  accountManagementUrl: null,
  feedbackUrl: null,
};

describe('AppDockComponent', () => {
  let capabilities: DeploymentCapabilities;

  beforeEach(() => {
    capabilities = cloudCapabilities;
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', component: BlankComponent }]),
        {
          provide: DeploymentCapabilitiesService,
          useValue: { get: () => of(capabilities) },
        },
      ],
    });
  });

  afterEach(() => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1024 });
  });

  function render(width: number): ComponentFixture<AppDockComponent> {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: width });
    const fixture = TestBed.createComponent(AppDockComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('keeps the three primary destinations in both layouts', () => {
    const labels = (fixture: ComponentFixture<AppDockComponent>) =>
      Array.from<Element>(
        fixture.nativeElement.querySelectorAll('.dock-item .label'),
      ).map((label) => label.textContent?.trim());

    expect(labels(render(1280))).toEqual(
      expect.arrayContaining(['Library', 'Brain', 'Studio']),
    );
    expect(labels(render(390))).toEqual(
      expect.arrayContaining(['Library', 'Brain', 'Studio']),
    );
  });

  it('shows Feedback as a quiet Settings sibling on a wide Cloud shell', async () => {
    await TestBed.inject(Router).navigateByUrl('/library');
    const fixture = render(1280);
    const feedback = fixture.nativeElement.querySelector(
      '[data-testid="dock-feedback"]',
    ) as HTMLAnchorElement;

    expect(feedback).toBeTruthy();
    expect(feedback.textContent).toContain('Feedback');
    expect(feedback.href).toBe('https://nostos.page/feedback?from=library');
    expect(feedback.target).toBe('_blank');
    expect(feedback.rel).toBe('noopener noreferrer');
    expect(feedback.classList).toContain('dock-item-utility');

    const divider = fixture.nativeElement.querySelector('.dock-divider');
    expect(divider).toBeTruthy();

    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeNull();
  });

  it('omits Feedback for SelfHosted with no empty utility placeholder', () => {
    capabilities = selfHostedCapabilities;
    const fixture = render(1280);

    expect(fixture.nativeElement.querySelector('[data-testid="dock-feedback"]')).toBeNull();
    // No divider and no utility restyle: the SelfHosted dock is exactly the
    // dock it shipped with, with nothing reserved for the absent Cloud action.
    expect(fixture.nativeElement.querySelector('.dock-divider')).toBeNull();
    const labels = Array.from<Element>(
      fixture.nativeElement.querySelectorAll('.dock-item .label'),
    ).map((label) => label.textContent?.trim());
    expect(labels).toEqual(['Library', 'Brain', 'Studio', 'Settings']);
    expect(fixture.nativeElement.querySelectorAll('.dock-item-utility').length).toBe(0);
  });

  it('replaces the Settings slot with More on a narrow Cloud shell', () => {
    const fixture = render(390);

    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="dock-feedback"]')).toBeNull();
    const labels = Array.from<Element>(
      fixture.nativeElement.querySelectorAll('.dock-item .label'),
    ).map((label) => label.textContent?.trim());
    expect(labels).toEqual(['Library', 'Brain', 'Studio', 'More']);
  });

  it('keeps Settings as a direct dock destination on a narrow SelfHosted shell', () => {
    capabilities = selfHostedCapabilities;
    const fixture = render(390);

    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeNull();
    const labels = Array.from<Element>(
      fixture.nativeElement.querySelectorAll('.dock-item .label'),
    ).map((label) => label.textContent?.trim());
    expect(labels).toEqual(['Library', 'Brain', 'Studio', 'Settings']);
  });

  it('opens and closes the More sheet from the dock trigger', () => {
    const fixture = render(390);
    const sheet = TestBed.inject(UtilitySheetService);
    const more = fixture.nativeElement.querySelector(
      '[data-testid="dock-more"]',
    ) as HTMLButtonElement;

    expect(sheet.open()).toBe(false);
    expect(more.getAttribute('aria-expanded')).toBe('false');
    expect(more.getAttribute('aria-controls')).toBe('mobile-more-sheet');
    expect(more.getAttribute('aria-haspopup')).toBe('dialog');

    more.click();
    fixture.detectChanges();
    expect(sheet.open()).toBe(true);
    expect(more.getAttribute('aria-expanded')).toBe('true');
    expect(more.classList).toContain('dock-item-open');

    more.click();
    fixture.detectChanges();
    expect(sheet.open()).toBe(false);
  });

  it('adopts the Cloud utility when capabilities resolve late', () => {
    const pending = new Subject<DeploymentCapabilities>();
    TestBed.overrideProvider(DeploymentCapabilitiesService, {
      useValue: { get: () => pending.asObservable() },
    });

    const fixture = render(390);

    // Before the answer, the shipped SelfHosted shape stands (no guessed URL).
    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeNull();
    const labels = () =>
      Array.from<Element>(fixture.nativeElement.querySelectorAll('.dock-item .label')).map(
        (label) => label.textContent?.trim(),
      );
    expect(labels()).toEqual(['Library', 'Brain', 'Studio', 'Settings']);

    pending.next(cloudCapabilities);
    fixture.detectChanges();

    expect(labels()).toEqual(['Library', 'Brain', 'Studio', 'More']);
    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeTruthy();
  });

  it('closes the More sheet when the viewport crosses to the wide shell', () => {
    const fixture = render(390);
    const sheet = TestBed.inject(UtilitySheetService);

    (fixture.nativeElement.querySelector('[data-testid="dock-more"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(sheet.open()).toBe(true);

    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1280 });
    window.dispatchEvent(new Event('resize'));
    fixture.detectChanges();

    expect(sheet.open()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="dock-feedback"]')).toBeTruthy();
  });
});
