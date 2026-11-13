import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import { CloudManagedAiUsage } from '../../core/dtos/cloud-ai-refill.dtos';
import { AssistantStatusService } from '../../ui/assistant/assistant-status.service';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import { CloudAiRefillService } from '../../core/services/cloud-ai-refill.service';
import { ButtonComponent } from '../../ui/button/button.component';
import { LoadingIndicatorComponent } from '../../ui/loading-indicator/loading-indicator.component';
import { NostosDatePipe } from '../../ui/pipes/nostos-date.pipe';
import { SwitchComponent } from '../../ui/switch/switch.component';
import { SettingsSection } from '../settings-sections';

/**
 * Settings → Assistant: the Ask Nostos switch and, on Cloud, the included
 * managed allowance.
 *
 * Availability is the server's to decide; this toggle is user intent only. When
 * unavailable it renders off and non-interactive, and the supporting sentence
 * explains why rather than the card hiding or adding a warning.
 */
@Component({
  selector: 'app-assistant-settings',
  standalone: true,
  imports: [ButtonComponent, LoadingIndicatorComponent, NostosDatePipe, SwitchComponent],
  templateUrl: './assistant-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AssistantSettingsComponent implements OnInit {
  private readonly assistantStatus = inject(AssistantStatusService);
  private readonly preferences = inject(LibraryPreferencesService);
  private readonly cloudAiRefills = inject(CloudAiRefillService);

  /** The host manages the model endpoint for this account. */
  readonly managedAi = input(false);
  /** The deployment exposes provider configuration, so the setup hint can link to it. */
  readonly canConfigureAiProvider = input(false);
  /** The host manages voice transcription, so the voice intent is a product switch. */
  readonly managedVoiceTranscription = input(false);
  /** The Cloud host advertises managed usage metering. */
  readonly managedUsageAvailable = input(false);
  /** The account site, or null when this host has none. */
  readonly accountManagementUrl = input<string | null>(null);

  /** Ask the page to open another settings section. */
  readonly openSection = output<SettingsSection>();

  /** Whether the server can run the assistant, for the Ask Nostos card. */
  readonly assistantAvailable = this.assistantStatus.available;

  /** The persisted user intent for the Ask Nostos toggle. */
  readonly assistantEnabled = this.preferences.assistantEnabled;

  /** Product-level voice intent, separate from provider configuration. */
  readonly assistantVoiceEnabled = this.preferences.assistantVoiceEnabled;

  /** Cloud failures should never direct a customer to provider plumbing. */
  readonly assistantUnavailableCopy = computed(() =>
    this.managedAi()
      ? 'Ask Nostos is temporarily unavailable.'
      : 'Connect an AI provider in Settings to turn this on.',
  );

  readonly managedAiUsage = signal<CloudManagedAiUsage | null>(null);
  readonly managedAiUsageFailed = signal(false);

  readonly managedAiUsageCopy = computed(() => {
    switch (this.managedAiUsage()?.state) {
      case 'normal':
        return 'Your included Ask Nostos allowance is available.';
      case 'near_limit':
        return 'Your included Ask Nostos allowance is nearly used.';
      case 'using_refill':
        return 'Your included allowance is used. Ask Nostos is using purchased refill capacity.';
      case 'exhausted':
        return 'Your included allowance is used. Add an AI refill to continue, or wait for your monthly allowance to renew.';
      case 'not_included':
        return 'Managed Ask Nostos usage is not included with your account.';
      case 'temporarily_unavailable':
        return 'Managed Ask Nostos usage is temporarily unavailable.';
      default:
        return 'Ask Nostos usage is managed with your Nostos Cloud plan.';
    }
  });

  constructor() {
    this.assistantStatus.refresh();
  }

  ngOnInit(): void {
    if (this.managedUsageAvailable()) this.loadManagedAiUsage();
  }

  setAssistantEnabled(event: Event): void {
    // The control is disabled while unavailable, so this is belt-and-braces:
    // never record intent the server cannot yet honour.
    if (!this.assistantAvailable()) return;
    const checked = (event.target as HTMLInputElement).checked;
    this.preferences.setAssistantEnabled(checked);
  }

  setAssistantVoiceEnabled(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.preferences.setAssistantVoiceEnabled(checked);
  }

  loadManagedAiUsage(): void {
    this.cloudAiRefills.getUsage().subscribe({
      next: (usage) => {
        this.managedAiUsage.set(usage);
        this.managedAiUsageFailed.set(false);
      },
      error: () => {
        this.managedAiUsage.set(null);
        this.managedAiUsageFailed.set(true);
      },
    });
  }
}
