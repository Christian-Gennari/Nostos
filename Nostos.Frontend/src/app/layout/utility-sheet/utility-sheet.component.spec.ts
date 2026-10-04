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

    await TestBed.inject(Router).navigateByUrl('/library');
    sheet = TestBed.inject(UtilitySheetService);
    fixture = TestBed.createComponent(UtilitySheetComponent);
    fixture.detectChanges();
  });

  function open(): void {
    sheet.open.set(true);
    fixture.detectChanges();
  }

  function clickWithoutNavigating(element: HTMLElement): void {
    element.addEventListener('click', (event) => event.preventDefault());
    element.click();
  }

  it('renders nothing until the dock trigger opens it', () => {
    expect(fixture.nativeElement.querySelector('[data-testid="utility-sheet"]')).toBeNull();

    open();
    expect(fixture.nativeElement.querySelector('[data-testid="utility-sheet"]')).toBeTruthy();
  });

  it('exposes Feedback with the current origin and canonical external-link behavior', () => {
    open();

    const feedback = fixture.nativeElement.querySelector(
      '[data-testid="utility-sheet-feedback"]',
    ) as HTMLAnchorElement;
    expect(feedback.textContent).toContain('Send feedback');
    expect(feedback.href).toBe('https://nostos.page/feedback?from=library');
    expect(feedback.target).toBe('_blank');
    expect(feedback.rel).toBe('noopener noreferrer');

    const settings = fixture.nativeElement.querySelector(
      '[data-testid="utility-sheet-settings"]',
    ) as HTMLAnchorElement;
    expect(settings.textContent).toContain('Settings');
    expect(settings.getAttribute('href')).toBe('/settings');
  });

  it('closes the sheet, with no lingering overlay, when Feedback opens', () => {
    open();
    clickWithoutNavigating(
      fixture.nativeElement.querySelector('[data-testid="utility-sheet-feedback"]'),
    );
    fixture.detectChanges();

    expect(sheet.open()).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="utility-sheet"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('.utility-sheet-scrim')).toBeNull();
  });

  it('closes the sheet when Settings is chosen', () => {
    open();
    clickWithoutNavigating(
      fixture.nativeElement.querySelector('[data-testid="utility-sheet-settings"]'),
    );
    fixture.detectChanges();

    expect(sheet.open()).toBe(false);
  });

  it('closes on a backdrop tap', () => {
    open();
    (fixture.nativeElement.querySelector('.utility-sheet-scrim') as HTMLElement).click();
    fixture.detectChanges();

    expect(sheet.open()).toBe(false);
  });

  it('closes on Escape', () => {
    open();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(sheet.open()).toBe(false);
  });

  it('closes when the route changes underneath it', async () => {
    open();
    await TestBed.inject(Router).navigateByUrl('/settings');
    fixture.detectChanges();

    expect(sheet.open()).toBe(false);
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
