import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Subject, of } from 'rxjs';

import { AppDockComponent } from './app-dock.component';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from '../../core/dtos/deployment-capabilities.dtos';
import { UtilitySheetService } from '../utility-sheet/utility-sheet.service';
import { AssistantLauncherService } from '../../ui/assistant/assistant-launcher.service';
import { AssistantService } from '../../ui/assistant/assistant.service';
import { AssistantStatusService } from '../../ui/assistant/assistant-status.service';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import { NOSTOS_ICONS } from '../../ui/icon/nostos-icons';

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
        provideHttpClient(),
        provideHttpClientTesting(),
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
    // Settings alone is the utility group: one hairline, one icon button, and
    // nothing reserved for the absent Cloud action.
    expect(fixture.nativeElement.querySelectorAll('.dock-divider').length).toBe(1);
    const labels = Array.from<Element>(
      fixture.nativeElement.querySelectorAll('.dock-item .label'),
    ).map((label) => label.textContent?.trim());
    expect(labels).toEqual(['Library', 'Brain', 'Studio', 'Settings']);
    const utilities = fixture.nativeElement.querySelectorAll('.dock-item-utility');
    expect(utilities.length).toBe(1);
    expect(utilities[0].getAttribute('title')).toBe('Settings');
  });

  describe('Ask Nostos launcher', () => {
    function enableAssistant(): void {
      TestBed.inject(LibraryPreferencesService).assistantEnabled.set(true);
      TestBed.inject(AssistantStatusService).available.set(true);
    }

    it('is absent while Ask Nostos is off, with no divider reserved for it', () => {
      const fixture = render(1280);

      expect(fixture.nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeNull();
      // Only the utility divider: Feedback and Settings.
      expect(fixture.nativeElement.querySelectorAll('.dock-divider').length).toBe(1);
      expect(render(390).nativeElement.querySelector('[data-testid="assistant-trigger"]')).toBeNull();
    });

    it('sits between the destinations and the utilities on a wide shell', () => {
      enableAssistant();
      const fixture = render(1280);
      const ask = fixture.nativeElement.querySelector(
        '[data-testid="assistant-trigger"]',
      ) as HTMLButtonElement;

      expect(ask).toBeTruthy();
      expect(ask.classList).toContain('dock-ask');
      expect(ask.hasAttribute('data-assistant-launcher')).toBe(true);
      expect(ask.getAttribute('aria-label')).toBe('Ask Nostos');
      expect(ask.querySelector('.dock-ask-key')?.textContent?.trim()).toMatch(/^(⌘ J|Ctrl J)$/);
      // Destinations, divider, Ask, divider, utilities.
      const order = Array.from<Element>(fixture.nativeElement.querySelector('.dock-bar').children)
        .filter((el) => !el.classList.contains('dock-pill'))
        .map((el) =>
          el.classList.contains('dock-divider')
            ? '|'
            : el.classList.contains('dock-ask')
              ? 'Ask'
              : el.querySelector('.label')?.textContent?.trim(),
        );
      expect(order).toEqual(['Library', 'Brain', 'Studio', '|', 'Ask', '|', 'Feedback', 'Settings']);
    });

    it('takes the centre slot of the phone rail', () => {
      enableAssistant();
      const fixture = render(390);
      const labels = Array.from<Element>(
        fixture.nativeElement.querySelectorAll('.dock-item .label'),
      ).map((label) => label.textContent?.trim());

      expect(labels).toEqual(['Library', 'Brain', 'Ask', 'Studio', 'More']);
      const ask = fixture.nativeElement.querySelector(
        '[data-testid="assistant-trigger"]',
      ) as HTMLButtonElement;
      expect(ask.classList).toContain('dock-item-ask');
      expect(ask.hasAttribute('data-assistant-launcher')).toBe(true);
    });

    it('toggles the assistant and mirrors whether it is open', () => {
      enableAssistant();
      const fixture = render(1280);
      const launcher = TestBed.inject(AssistantLauncherService);
      const assistant = TestBed.inject(AssistantService);
      const toggles: number[] = [];
      launcher.toggleRequested.subscribe(() => toggles.push(1));
      const ask = fixture.nativeElement.querySelector(
        '[data-testid="assistant-trigger"]',
      ) as HTMLButtonElement;

      expect(ask.getAttribute('aria-expanded')).toBe('false');
      ask.click();
      expect(toggles.length).toBe(1);

      assistant.open();
      fixture.detectChanges();
      expect(ask.getAttribute('aria-expanded')).toBe('true');
    });

    it('hosts the launcher only while the dock is on screen', () => {
      const launcher = TestBed.inject(AssistantLauncherService);
      expect(launcher.dockHosted()).toBe(false);

      const fixture = render(1280);
      expect(launcher.dockHosted()).toBe(true);

      fixture.destroy();
      expect(launcher.dockHosted()).toBe(false);
    });
  });

  it('fills the selected destination icon and leaves the others regular', async () => {
    await TestBed.inject(Router).navigateByUrl('/studio');
    const fixture = render(1280);
    await fixture.whenStable();
    fixture.detectChanges();

    const firstPath = (svg: string) => /\sd="([^"]+)"/.exec(svg)?.[1] ?? '';
    const iconOf = (title: string) =>
      (fixture.nativeElement.querySelector(`.dock-item[title="${title}"] nostos-icon`) as HTMLElement)
        .innerHTML;
    expect(iconOf('Studio')).toContain(firstPath(NOSTOS_ICONS['pen-nib'].fill));
    expect(iconOf('Library')).toContain(firstPath(NOSTOS_ICONS.books.regular));
  });

  it('puts More in the Settings slot on a narrow Cloud shell', () => {
    const fixture = render(390);

    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="dock-feedback"]')).toBeNull();
    const labels = Array.from<Element>(
      fixture.nativeElement.querySelectorAll('.dock-item .label'),
    ).map((label) => label.textContent?.trim());
    expect(labels).toEqual(['Library', 'Brain', 'Studio', 'More']);
    const more = fixture.nativeElement.querySelector('[data-testid="dock-more"]') as HTMLButtonElement;
    expect(more.tagName).toBe('BUTTON');
    expect(more.classList).toContain('dock-item');
    expect(more.classList).not.toContain('dock-item-open');
  });

  it('gives a narrow SelfHosted shell the same More slot, with no feedback link', () => {
    capabilities = selfHostedCapabilities;
    const fixture = render(390);

    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeTruthy();
    const labels = Array.from<Element>(
      fixture.nativeElement.querySelectorAll('.dock-item .label'),
    ).map((label) => label.textContent?.trim());
    expect(labels).toEqual(['Library', 'Brain', 'Studio', 'More']);
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

  it('shows More before capabilities resolve and keeps it after', () => {
    const pending = new Subject<DeploymentCapabilities>();
    TestBed.overrideProvider(DeploymentCapabilitiesService, {
      useValue: { get: () => pending.asObservable() },
    });

    const fixture = render(390);

    // More does not wait on the answer: the dock does not shift when it lands.
    expect(fixture.nativeElement.querySelector('[data-testid="dock-more"]')).toBeTruthy();
    const labels = () =>
      Array.from<Element>(fixture.nativeElement.querySelectorAll('.dock-item .label')).map(
        (label) => label.textContent?.trim(),
      );
    expect(labels()).toEqual(['Library', 'Brain', 'Studio', 'More']);

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
