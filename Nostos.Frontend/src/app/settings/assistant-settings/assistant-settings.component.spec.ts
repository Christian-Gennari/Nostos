import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { of } from 'rxjs';

import { CloudManagedAiUsage } from '../../core/dtos/cloud-ai-refill.dtos';
import { CloudAiRefillService } from '../../core/services/cloud-ai-refill.service';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import { AssistantStatusService } from '../../ui/assistant/assistant-status.service';
import { AssistantSettingsComponent } from './assistant-settings.component';

const assistantStatusMock = {
  available: signal(true),
  refresh: vi.fn(),
};

const managedAiUsage: CloudManagedAiUsage = {
  state: 'near_limit',
  renewsAtUtc: '2026-10-01T00:00:00Z',
  refill: { available: false, state: 'empty' },
};

const cloudAiRefillServiceMock = {
  getUsage: vi.fn(() => of(managedAiUsage)),
};

describe('AssistantSettingsComponent', () => {
  let fixture: ComponentFixture<AssistantSettingsComponent>;
  let component: AssistantSettingsComponent;

  async function configure(inputs: {
    available?: boolean;
    managedAi?: boolean;
    canConfigureAiProvider?: boolean;
    managedVoiceTranscription?: boolean;
    managedUsageAvailable?: boolean;
    accountManagementUrl?: string | null;
  } = {}): Promise<void> {
    TestBed.resetTestingModule();
localStorage.clear();
    assistantStatusMock.available.set(inputs.available ?? true);
    assistantStatusMock.refresh.mockClear();
    cloudAiRefillServiceMock.getUsage.mockClear();
    cloudAiRefillServiceMock.getUsage.mockReturnValue(of(managedAiUsage));

    await TestBed.configureTestingModule({
      imports: [AssistantSettingsComponent],
      providers: [
        { provide: AssistantStatusService, useValue: assistantStatusMock },
        { provide: CloudAiRefillService, useValue: cloudAiRefillServiceMock },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(AssistantSettingsComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('managedAi', inputs.managedAi ?? false);
    fixture.componentRef.setInput(
      'canConfigureAiProvider',
      inputs.canConfigureAiProvider ?? true,
    );
    fixture.componentRef.setInput(
      'managedVoiceTranscription',
      inputs.managedVoiceTranscription ?? false,
    );
    fixture.componentRef.setInput('managedUsageAvailable', inputs.managedUsageAvailable ?? false);
    fixture.componentRef.setInput('accountManagementUrl', inputs.accountManagementUrl ?? null);
    fixture.detectChanges();
  }

  afterEach(() => localStorage.clear());

  it('asks the server for its status once, rather than trusting a stored guess', async () => {
    await configure();

    expect(assistantStatusMock.refresh).toHaveBeenCalledTimes(1);
  });

  it('records the switch through the preferences service', async () => {
    await configure();
    const preferences = TestBed.inject(LibraryPreferencesService);

    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="assistant-enabled-toggle"]',
    ) as HTMLInputElement;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));

    expect(preferences.assistantEnabled()).toBe(true);
  });

  it('renders the switch off and inert when the server cannot run the assistant', async () => {
    await configure({ available: false });

    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="assistant-enabled-toggle"]',
    ) as HTMLInputElement;
    expect(toggle.disabled).toBe(true);
    expect(toggle.checked).toBe(false);

    // Belt-and-braces: a synthetic change never records intent either.
    component.setAssistantEnabled({ target: { checked: true } } as unknown as Event);
    expect(TestBed.inject(LibraryPreferencesService).assistantEnabled()).toBe(false);
  });

  it('links the setup hint to AI connections through the page', async () => {
    await configure({ available: false });
    const emitted: string[] = [];
    component.openSection.subscribe((section) => emitted.push(section));

    (
      fixture.nativeElement.querySelector('[data-testid="assistant-setup-hint"] a') as HTMLAnchorElement
    ).click();

    expect(emitted).toEqual(['connections']);
  });

  it('keeps the managed voice switch as product intent', async () => {
    await configure({ managedVoiceTranscription: true });

    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="managed-voice-transcription-toggle"]',
    ) as HTMLInputElement;
    toggle.checked = true;
    toggle.dispatchEvent(new Event('change'));

    expect(TestBed.inject(LibraryPreferencesService).assistantVoiceEnabled()).toBe(true);
  });

  it('shows the included allowance only when the host meters managed usage', async () => {
    await configure({ managedUsageAvailable: false });
    expect(fixture.nativeElement.querySelector('[data-testid="managed-ai-usage-card"]')).toBeNull();

    fixture.destroy();
    await configure({ managedUsageAvailable: true });

    expect(cloudAiRefillServiceMock.getUsage).toHaveBeenCalledTimes(1);
    expect(fixture.nativeElement.querySelector('[data-testid="managed-ai-usage-card"]')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('nearly used');
  });

  it('links refill management to the account site when the host offers one', async () => {
    await configure({
      managedUsageAvailable: true,
      accountManagementUrl: 'https://nostos.page/account',
    });

    const link = fixture.nativeElement.querySelector(
      '[data-testid="managed-ai-refill-link"]',
    ) as HTMLAnchorElement;
    expect(link.href).toBe('https://nostos.page/account');
    expect(link.target).toBe('_blank');
  });
});
