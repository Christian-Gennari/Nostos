import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse, HttpEventType, HttpResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';

import { BackupService } from '../core/services/backup.service';
import { OpdsService } from '../core/services/opds.service';
import { ToastService } from '../core/services/toast.service';
import { ThemeService, Theme } from '../core/services/theme.service';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import {
  BackupStatus,
  BackupSettings,
  BackupHistoryItem,
  BackupProgress,
} from '../core/dtos/backup.dtos';
import { ManagedOpdsAccess, OpdsInfo } from '../core/dtos/opds.dtos';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { ButtonComponent } from '../ui/button/button.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { SwitchComponent } from '../ui/switch/switch.component';
import { BadgeComponent } from '../ui/badge/badge.component';
import { InputDirective } from '../ui/form-control/form-control.directive';
import { DropdownComponent, type DropdownOption } from '../ui/dropdown/dropdown.component';
import { LibraryPreferencesService } from '../core/services/library-preferences.service';
import { AssistantStatusService } from '../ui/assistant/assistant-status.service';
import {
  AssistantSettingsService,
  PROCESSING_MODES,
  ProcessingMode,
} from '../ui/assistant/assistant-settings.service';
import { AiProviderService } from '../core/services/ai-provider.service';
import { DeploymentCapabilitiesService } from '../core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from '../core/dtos/deployment-capabilities.dtos';
import { CloudAiRefillService } from '../core/services/cloud-ai-refill.service';
import { CloudAuthService } from '../core/services/cloud-auth.service';
import { CloudSession } from '../core/dtos/cloud-auth.dtos';
import { PortableLibraryService } from '../core/services/portable-library.service';
import { CloudManagedAiUsage } from '../core/dtos/cloud-ai-refill.dtos';
import {
  AiProviderKind,
  AiProviderSection,
  AiProviderSectionUpdate,
  AiProviderUpdate,
} from '../core/dtos/ai-provider.dtos';

const SLOW_STEP_THRESHOLD_MS = 30_000;

/** How long the copy button stays on "Copied" before it offers to copy again. */
const COPIED_FEEDBACK_MS = 2_500;
const PORTABLE_EXPORT_URL_LIFETIME_MS = 60_000;

/**
 * Every user-visible string for the AI provider feature, in one place: the card's
 * own copy plus the locked-state sentence the older Reading assistant card shows
 * when no provider is configured. Keeping them together means a wording change is
 * a single edit.
 */
const AI_PROVIDER_COPY = {
  title: 'AI provider',
  intro:
    'Notes and voice recordings are sent directly to the OpenAI-compatible endpoints configured below.',
  readingAssistant: 'Reading assistant',
  voiceTranscription: 'Voice transcription',
  voiceToggle: 'Enable voice transcription',
  voiceToggleHelp: 'Send voice recordings to the transcription endpoint.',
  endpoint: 'Endpoint',
  endpointPlaceholder: 'https://api.openai.com/v1',
  endpointHelp: 'Base URL, including /v1.',
  model: 'Model',
  modelPlaceholderLlm: 'e.g. gpt-4o-mini',
  modelPlaceholderStt: 'e.g. whisper-1',
  modelHelp: 'Exact model name expected by the endpoint.',
  apiKey: 'API key',
  apiKeyPlaceholderUnset: 'Leave empty if unauthenticated',
  apiKeyPlaceholderSet: 'Configured on server (leave blank to keep)',
  apiKeyHelp: 'Stored on your server. Never returned to the browser.',
  loadModels: 'Load models',
  testConnection: 'Test connection',
  clear: 'Clear key',
  save: 'Save',
  testing: 'Testing connection…',
  loading: 'Loading models…',
  saved: 'Saved.',
  modelsLoaded: (count: number) => `Loaded ${count} models.`,
  modelsEmpty: 'No models returned by endpoint.',
  modelsError: (message: string) => `Could not load models: ${message}`,
  connectionError: (message: string) => `Connection failed: ${message}`,
  keyCleared: 'Key removed. Falling back to environment variable if present.',
  oldCardEmptyState: 'Set up an AI provider in Settings to enable this.',
  // Capture processing (issue #262): the stored, global choice that used to be a
  // per-capture select in the widget. The description shown is the one belonging
  // to the currently selected option, followed by `captureScope`.
  //
  // This global preference applies only to Ask Nostos captures. It never
  // silently processes direct note creation, imports, edits or existing notes.
  captureLabel: 'Saved thought wording',
  captureScope: 'For thoughts saved through Ask Nostos, typed or spoken. Quotes stay exact. Manual notes, imports, edits and existing notes are unchanged. The original wording is kept when AI rephrases a thought.',
  captureDescriptions: {
    verbatim: 'Save your exact wording, without AI rephrasing.',
    light_polish: 'Ask AI to tidy grammar and filler while preserving your meaning and voice.',
    clarify: 'Ask AI to reorganize and rephrase your thought for clarity, without adding ideas.',
  } as Record<ProcessingMode, string>,
  captureLoadFailed: 'Could not read this setting',
  captureLoadFailedHelp:
    'The server did not answer the request for how your captures are saved. Reload the page to try again.',
  captureSaveFailed: 'Could not save this setting. Your previous choice is still in effect.',
  // The reviewed set covers the four card actions but not a failed GET/PUT or
  // the configured-key signals, so these keep their earlier wording.
  configured: 'Configured',
  configuredFromEnv: 'Configured — using the server environment variable.',
  loadFailed: 'Could not load the AI provider settings.',
  couldNotSave: (message: string) => `Could not save: ${message}`,
} as const;

/** How a section's inline status line is coloured. */
type SettingsSection = 'library' | 'account' | 'assistant' | 'appearance';

type AiProviderStatusTone = 'neutral' | 'ok' | 'error';

interface AiProviderStatus {
  text: string;
  tone: AiProviderStatusTone;
}

/**
 * One provider's editable state. The `saved*` fields are the effective values
 * the server last reported, so Save can send only what actually changed; the
 * typed `key` is never seeded from a response (the API does not return one).
 */
interface AiProviderForm {
  enabled: boolean;
  baseUrl: string;
  model: string;
  key: string;
  keyCleared: boolean;
  hasKey: boolean;
  keyFromServerEnv: boolean;
  savedBaseUrl: string;
  savedModel: string;
  savedEnabled: boolean;
}

function emptyAiProviderForm(): AiProviderForm {
  return {
    enabled: false,
    baseUrl: '',
    model: '',
    key: '',
    keyCleared: false,
    hasKey: false,
    keyFromServerEnv: false,
    savedBaseUrl: '',
    savedModel: '',
    savedEnabled: false,
  };
}

const defaultProgress: BackupProgress = {
  isRunning: false,
  currentStep: null,
  percentComplete: 0,
  startedAt: null,
  stepNumber: 0,
  totalSteps: 0,
};

@Component({
  standalone: true,
  selector: 'app-settings',
  imports: [
    CommonModule,
    FormsModule,
    NostosIconComponent,
    ButtonComponent,
    IconButtonComponent,
    SwitchComponent,
    BadgeComponent,
    InputDirective,
    DropdownComponent,
    ConfirmModal,
  ],
  templateUrl: './settings.component.html',
  styleUrls: ['./settings.component.css'],
})
export class SettingsComponent implements OnInit, OnDestroy {
  private backupService = inject(BackupService);
  private opdsService = inject(OpdsService);
  private toast = inject(ToastService);
  private themeService = inject(ThemeService);
  private assistantStatus = inject(AssistantStatusService);
  private assistantSettings = inject(AssistantSettingsService);
  private preferences = inject(LibraryPreferencesService);
  private aiProvider = inject(AiProviderService);
  private deploymentCapabilitiesService = inject(DeploymentCapabilitiesService);
  private cloudAiRefills = inject(CloudAiRefillService);
  private portableLibrary = inject(PortableLibraryService);
  private cloudAuth = inject(CloudAuthService);

  /** Which settings surface is visible. This is local UI state, not a route. */
  readonly activeSettingsSection = signal<SettingsSection>('library');

  /** Server-authoritative deployment capabilities. Null means not loaded yet. */
  readonly deploymentCapabilities = signal<DeploymentCapabilities | null>(null);
  readonly capabilitiesFailed = signal(false);
  readonly capabilitiesLoading = computed(
    () => this.deploymentCapabilities() === null && !this.capabilitiesFailed(),
  );
  readonly supportsLocalBackupConfiguration = computed(
    () => this.deploymentCapabilities()?.supportsLocalBackupConfiguration === true,
  );
  readonly supportsPrivateNetworkAccess = computed(
    () => this.deploymentCapabilities()?.supportsPrivateNetworkAccess === true,
  );
  readonly supportsEreaderAccess = computed(
    () => this.deploymentCapabilities()?.supportsEreaderAccess === true,
  );
  readonly isCloud = computed(() => this.deploymentCapabilities()?.deploymentMode === 'Cloud');
  readonly cloudAccountManagementUrl = computed(() =>
    this.isCloud() ? (this.deploymentCapabilities()?.accountManagementUrl ?? null) : null,
  );
  readonly cloudFeedbackUrl = computed(() =>
    this.isCloud() ? (this.deploymentCapabilities()?.feedbackUrl ?? null) : null,
  );
  readonly supportsCloudPortableExport = computed(() => this.isCloud());
  readonly cloudSession = signal<CloudSession | null>(null);
  readonly managedEreaderAccess = computed(
    () =>
      this.supportsEreaderAccess() &&
      this.deploymentCapabilities()?.deploymentMode === 'Cloud',
  );
  readonly canConfigureAiProvider = computed(
    () => this.deploymentCapabilities()?.canConfigureAiProvider === true,
  );
  readonly managedAi = computed(
    () => this.deploymentCapabilities()?.managedAi === true,
  );
  readonly managedVoiceTranscription = computed(
    () => this.deploymentCapabilities()?.managedVoiceTranscription === true,
  );
  readonly managedAiUsageAvailable = computed(
    () =>
      this.deploymentCapabilities()?.deploymentMode === 'Cloud' &&
      this.deploymentCapabilities()?.managedAi === true &&
      this.deploymentCapabilities()?.usageMeteringAvailable === true,
  );
  readonly hasLibrarySettings = computed(
    () =>
      this.supportsLocalBackupConfiguration() ||
      this.supportsEreaderAccess() ||
      this.supportsCloudPortableExport(),
  );

  /** The AI provider card's copy, exposed so the template reads one source. */
  readonly copy = AI_PROVIDER_COPY;

  /** The active theme, exposed for the Appearance card. */
  readonly theme = this.themeService.theme;

  /** Whether the server can run the assistant, for the Reading assistant card. */
  readonly assistantAvailable = this.assistantStatus.available;

  /** The persisted user intent for the Reading assistant toggle. */
  readonly assistantEnabled = this.preferences.assistantEnabled;

  /** Product-level voice intent, separate from provider configuration. */
  readonly assistantVoiceEnabled = this.preferences.assistantVoiceEnabled;

  /** Cloud failures should never direct a customer to provider plumbing. */
  readonly assistantUnavailableCopy = computed(() =>
    this.managedAi()
      ? 'Ask Nostos is temporarily unavailable.'
      : this.copy.oldCardEmptyState,
  );

  /** The stored capture-processing choice, exposed to the Reading assistant card. */
  readonly captureProcessingMode = this.assistantSettings.captureProcessingMode;

  /** The three modes in presentation order, with the labels the dropdown shows. */
  readonly processingModes = PROCESSING_MODES;

  readonly backupIntervalOptions = [
    { value: '6', label: 'Every 6 hours' },
    { value: '12', label: 'Every 12 hours' },
    { value: '24', label: 'Daily' },
    { value: '168', label: 'Weekly' },
  ] satisfies readonly DropdownOption[];

  readonly maxBackupOptions = [
    { value: '3', label: '3' },
    { value: '5', label: '5' },
    { value: '10', label: '10' },
    { value: '20', label: '20' },
  ] satisfies readonly DropdownOption[];

  /** True when the server did not answer the capture setting GET. */
  readonly assistantSettingsFailed = this.assistantSettings.loadFailed;

  /** True when the capture setting PUT failed; the previous choice stays in force. */
  readonly assistantSettingsSaveFailed = this.assistantSettings.saveFailed;

  /** The description of the selected option; the card appends the fixed scope note. */
  readonly captureModeDescription = computed(
    () => this.copy.captureDescriptions[this.captureProcessingMode()],
  );

  setSettingsSection(section: SettingsSection): void {
    this.activeSettingsSection.set(section);
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

  changeCaptureProcessingMode(mode: string): void {
    this.assistantSettings.setCaptureProcessingMode(mode as ProcessingMode);
  }

  setTheme(theme: Theme): void {
    this.themeService.setTheme(theme);
  }
  /** E-reader access (issue #187): null until the server has answered. */
  opds = signal<OpdsInfo | null>(null);

  /** True when the info request failed, so the card never presents a guess as fact. */
  opdsFailed = signal(false);

  /** Hosted credential state. Plaintext password, when present, is one-time response material only. */
  managedOpds = signal<ManagedOpdsAccess | null>(null);
  managedOpdsFailed = signal(false);
  managedOpdsBusy = signal<'enable' | 'rotate' | 'revoke' | null>(null);
  pendingOpdsRevoke = signal(false);

  copied = signal(false);
  private copiedTimeout: ReturnType<typeof setTimeout> | null = null;

  // --- AI provider card state ------------------------------------------
  // The card loads its own effective settings; a failure is shown in place
  // rather than guessed at, so the fields are never presented as fact.
  aiLoadFailed = signal(false);
  aiLlm = signal<AiProviderForm>(emptyAiProviderForm());
  aiStt = signal<AiProviderForm>(emptyAiProviderForm());

  /** Model ids offered as suggestions for each sub-section (free text stays editable). */
  aiLlmModels = signal<string[]>([]);
  aiSttModels = signal<string[]>([]);

  /** Inline outcome lines: `Testing…` / test result / load result / clear notice. */
  aiLlmStatus = signal<AiProviderStatus | null>(null);
  aiSttStatus = signal<AiProviderStatus | null>(null);
  aiSaveStatus = signal<AiProviderStatus | null>(null);

  aiSaving = signal(false);

  /** Which section, if any, is running a Load models request. One at a time. */
  aiLoadingKind = signal<AiProviderKind | null>(null);

  /** Which section, if any, is running a Test connection request. One at a time. */
  aiTestingKind = signal<AiProviderKind | null>(null);

  aiBusy = computed(
    () => this.aiSaving() || this.aiLoadingKind() !== null || this.aiTestingKind() !== null,
  );

  // --- Managed Cloud AI allowance / refills ----------------------------
  // These endpoints exist only in the official Cloud host. They are never
  // touched until the runtime capability response explicitly advertises them.
  readonly portableExportBusy = signal(false);
  readonly portableExportProgress = signal<number | null>(null);
  readonly portableExportError = signal<string | null>(null);

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
        return 'Managed Ask Nostos usage is not included with this account.';
      case 'temporarily_unavailable':
        return 'Managed Ask Nostos usage is temporarily unavailable.';
      default:
        return 'Ask Nostos usage is managed with your Cloud plan.';
    }
  });

  readonly managedAiRefillCopy = computed(() => {
    switch (this.managedAiUsage()?.refill.state) {
      case 'active':
        return 'Purchased AI refill capacity is available after your included allowance is used.';
      case 'low':
        return 'Your purchased AI refill capacity is running low.';
      case 'empty':
        return 'You do not currently have purchased AI refill capacity.';
      default:
        return 'AI refills are not currently offered for this account.';
    }
  });

  status = signal<BackupStatus>({
    isEnabled: false,
    provider: 'Local',
    lastBackupAt: null,
    lastBackupStatus: null,
    includeBookFiles: true,
    intervalHours: 168,
    maxBackups: 3,
  });

  settings = signal<BackupSettings>({
    isEnabled: false,
    provider: 'Local',
    includeBookFiles: true,
    intervalHours: 168,
    maxBackups: 3,
  });

  history = signal<BackupHistoryItem[]>([]);
  backingUp = signal(false);
  restoring = signal(false);
  importing = signal(false);

  /** Backup id awaiting restore confirmation (asked through ConfirmModal). */
  pendingRestore = signal<string | null>(null);

  /** Backup id awaiting archive-delete confirmation (asked through ConfirmModal). */
  pendingBackupDelete = signal<string | null>(null);
  progress = signal<BackupProgress>(defaultProgress);
  showSlowNotice = signal(false);
  private stepChangedAt = 0;
  private slowNoticeInterval: ReturnType<typeof setInterval> | null = null;

  private progressInterval: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.loadCapabilities();
  }

  private loadCapabilities(): void {
    this.deploymentCapabilitiesService.get().subscribe({
      next: (capabilities) => {
        this.deploymentCapabilities.set(capabilities);
        this.capabilitiesFailed.set(false);

        // Keep Library & data when any library-facing capability exists.
        if (
          !capabilities.supportsLocalBackupConfiguration &&
          !capabilities.supportsEreaderAccess &&
          capabilities.deploymentMode !== 'Cloud'
        ) {
          this.activeSettingsSection.set('assistant');
        }

        // Do not touch owner/infrastructure APIs before the server says this
        // deployment exposes them. This also prevents forbidden controls from
        // flashing while the capability request is in flight.
        if (capabilities.supportsLocalBackupConfiguration) this.loadData();
        if (capabilities.supportsEreaderAccess) {
          this.loadOpdsInfo();
          if (capabilities.deploymentMode === 'Cloud') this.loadManagedOpdsAccess();
        }
        if (capabilities.deploymentMode === 'Cloud') this.loadCloudSession();
        if (capabilities.canConfigureAiProvider) this.loadAiProvider();
        if (
          capabilities.deploymentMode === 'Cloud' &&
          capabilities.managedAi &&
          capabilities.usageMeteringAvailable
        ) {
          this.loadManagedAiUsage();
        }

        this.assistantStatus.refresh();
        this.assistantSettings.refresh();
      },
      error: () => {
        this.deploymentCapabilities.set(null);
        this.capabilitiesFailed.set(true);
      },
    });
  }

  private loadCloudSession(): void {
    this.cloudAuth.getSession().subscribe({
      next: (session) => this.cloudSession.set(session),
      error: () => this.cloudSession.set(null),
    });
  }

  signOut(): void {
    this.cloudAuth.logout();
  }

  ngOnDestroy(): void {
    this.stopProgressPolling();
    if (this.copiedTimeout !== null) {
      clearTimeout(this.copiedTimeout);
      this.copiedTimeout = null;
    }
  }

  loadOpdsInfo(): void {
    this.opdsService.getInfo().subscribe({
      next: (info) => {
        this.opds.set(info);
        this.opdsFailed.set(false);
      },
      error: () => {
        this.opds.set(null);
        this.opdsFailed.set(true);
      },
    });
  }

  loadManagedOpdsAccess(): void {
    this.opdsService.getManagedAccess().subscribe({
      next: (access) => {
        this.managedOpds.set(access);
        this.managedOpdsFailed.set(false);
      },
      error: () => {
        this.managedOpds.set(null);
        this.managedOpdsFailed.set(true);
      },
    });
  }

  enableManagedOpds(): void {
    if (this.managedOpdsBusy() !== null) return;
    this.managedOpdsBusy.set('enable');
    this.opdsService.enableManagedAccess().subscribe({
      next: (access) => {
        this.managedOpdsBusy.set(null);
        this.managedOpds.set(access);
        this.managedOpdsFailed.set(false);
        this.toast.success('E-reader access enabled. Save the generated password now.');
      },
      error: () => {
        this.managedOpdsBusy.set(null);
        this.toast.error('Could not enable e-reader access.');
      },
    });
  }

  rotateManagedOpdsPassword(): void {
    if (this.managedOpdsBusy() !== null) return;
    this.managedOpdsBusy.set('rotate');
    this.opdsService.rotateManagedPassword().subscribe({
      next: (access) => {
        this.managedOpdsBusy.set(null);
        this.managedOpds.set(access);
        this.managedOpdsFailed.set(false);
        this.toast.success('E-reader password regenerated. The previous password no longer works.');
      },
      error: () => {
        this.managedOpdsBusy.set(null);
        this.toast.error('Could not regenerate the e-reader password.');
      },
    });
  }

  requestManagedOpdsRevoke(): void {
    if (this.managedOpdsBusy() !== null) return;
    this.pendingOpdsRevoke.set(true);
  }

  cancelManagedOpdsRevoke(): void {
    if (this.managedOpdsBusy() === 'revoke') return;
    this.pendingOpdsRevoke.set(false);
  }

  confirmManagedOpdsRevoke(): void {
    if (!this.pendingOpdsRevoke() || this.managedOpdsBusy() !== null) return;
    this.managedOpdsBusy.set('revoke');
    this.opdsService.revokeManagedAccess().subscribe({
      next: (access) => {
        this.managedOpdsBusy.set(null);
        this.pendingOpdsRevoke.set(false);
        this.managedOpds.set(access);
        this.managedOpdsFailed.set(false);
        this.toast.success('E-reader access disabled.');
      },
      error: () => {
        this.managedOpdsBusy.set(null);
        this.toast.error('Could not disable e-reader access.');
      },
    });
  }

  // --- Managed Cloud AI allowance / refills ----------------------------

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

  // --- AI provider card -------------------------------------------------
  // Four calls, one dedicated service. The key is write-only: `toForm` never
  // seeds `key`, and Save includes `apiKey` only when the user typed one or
  // pressed Clear.

  loadAiProvider(): void {
    this.aiProvider.get().subscribe({
      next: (settings) => {
        this.aiLlm.set(this.toAiForm(settings.llm));
        this.aiStt.set(this.toAiForm(settings.stt));
        this.aiLoadFailed.set(false);
      },
      error: () => {
        this.aiLoadFailed.set(true);
      },
    });
  }

  setAiBaseUrl(kind: AiProviderKind, event: Event): void {
    this.patchAiForm(kind, { baseUrl: (event.target as HTMLInputElement).value });
  }

  setAiModel(kind: AiProviderKind, event: Event): void {
    this.patchAiForm(kind, { model: (event.target as HTMLInputElement).value });
  }

  setAiKey(kind: AiProviderKind, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    // Typing revokes a pending Clear; emptying the field again does NOT clear the
    // stored key — the brief makes Clear the only explicit clear signal.
    this.patchAiForm(kind, {
      key: value,
      keyCleared: value.length > 0 ? false : this.aiFormFor(kind).keyCleared,
    });
  }

  setAiVoiceEnabled(event: Event): void {
    this.patchAiForm('stt', { enabled: (event.target as HTMLInputElement).checked });
  }

  clearAiKey(kind: AiProviderKind): void {
    this.patchAiForm(kind, { key: '', keyCleared: true });
    this.setAiStatus(kind, { text: AI_PROVIDER_COPY.keyCleared, tone: 'neutral' });
  }

  /**
   * Asks the endpoint what models it advertises. The unsaved endpoint/key are
   * sent along so the lookup matches what the user is about to save; the stored
   * key is used when the field is empty.
   */
  loadAiModels(kind: AiProviderKind): void {
    if (this.aiBusy()) return;
    const form = this.aiFormFor(kind);
    const request = { kind, baseUrl: form.baseUrl, apiKey: form.key || undefined };

    this.aiLoadingKind.set(kind);
    this.setAiStatus(kind, { text: AI_PROVIDER_COPY.loading, tone: 'neutral' });
    this.aiProvider.loadModels(request).subscribe({
      next: (response) => {
        this.aiLoadingKind.set(null);
        const models = response.models ?? [];
        this.setAiModels(kind, models);
        this.setAiStatus(
          kind,
          models.length === 0
            ? { text: AI_PROVIDER_COPY.modelsEmpty, tone: 'neutral' }
            : { text: AI_PROVIDER_COPY.modelsLoaded(models.length), tone: 'ok' },
        );
      },
      error: (error) => {
        this.aiLoadingKind.set(null);
        this.setAiStatus(kind, {
          text: AI_PROVIDER_COPY.modelsError(this.errorMessage(error)),
          tone: 'error',
        });
      },
    });
  }

  /**
   * One real round-trip against the provider. The route always answers HTTP 200
   * so a failure can carry a message, so this branches on `ok`, never the status.
   */
  testAiConnection(kind: AiProviderKind): void {
    if (this.aiBusy()) return;
    const form = this.aiFormFor(kind);
    const request = {
      kind,
      baseUrl: form.baseUrl,
      model: form.model,
      apiKey: form.key || undefined,
    };

    this.aiTestingKind.set(kind);
    this.setAiStatus(kind, { text: AI_PROVIDER_COPY.testing, tone: 'neutral' });
    this.aiProvider.test(request).subscribe({
      next: (result) => {
        this.aiTestingKind.set(null);
        this.setAiStatus(
          kind,
          result.ok
            ? { text: result.detail, tone: 'ok' }
            : { text: result.error, tone: 'error' },
        );
      },
      error: (error) => {
        this.aiTestingKind.set(null);
        this.setAiStatus(kind, {
          text: AI_PROVIDER_COPY.connectionError(this.errorMessage(error)),
          tone: 'error',
        });
      },
    });
  }

  /** Sends only the sections that changed; re-seeds from the response on success. */
  saveAiProvider(): void {
    if (this.aiBusy()) return;

    const update: AiProviderUpdate = {};
    const llm = this.buildAiSectionUpdate(this.aiLlm());
    if (llm) update.llm = llm;
    const stt = this.buildAiSectionUpdate(this.aiStt());
    if (stt) update.stt = stt;

    this.aiSaving.set(true);
    this.aiSaveStatus.set(null);
    this.aiProvider.update(update).subscribe({
      next: (settings) => {
        this.aiSaving.set(false);
        this.aiLlm.set(this.toAiForm(settings.llm));
        this.aiStt.set(this.toAiForm(settings.stt));
        this.aiSaveStatus.set({ text: AI_PROVIDER_COPY.saved, tone: 'ok' });
      },
      error: (error) => {
        this.aiSaving.set(false);
        this.aiSaveStatus.set({
          text: AI_PROVIDER_COPY.couldNotSave(this.errorMessage(error)),
          tone: 'error',
        });
      },
    });
  }

  /**
   * A section's PUT body, or null when nothing in it changed. `apiKey` is present
   * only for a typed value or a Clear — otherwise the key is left untouched.
   */
  private buildAiSectionUpdate(form: AiProviderForm): AiProviderSectionUpdate | null {
    const update: AiProviderSectionUpdate = {};
    let changed = false;

    if (form.baseUrl !== form.savedBaseUrl) {
      update.baseUrl = form.baseUrl;
      changed = true;
    }
    if (form.model !== form.savedModel) {
      update.model = form.model;
      changed = true;
    }
    if (form.enabled !== form.savedEnabled) {
      update.enabled = form.enabled;
      changed = true;
    }
    if (form.key.length > 0) {
      update.apiKey = form.key;
      changed = true;
    } else if (form.keyCleared) {
      update.apiKey = '';
      changed = true;
    }

    return changed ? update : null;
  }

  private toAiForm(section: AiProviderSection): AiProviderForm {
    return {
      enabled: section.enabled,
      baseUrl: section.baseUrl,
      model: section.model,
      // Always empty: the API never returns the key, so there is nothing to seed.
      key: '',
      keyCleared: false,
      hasKey: section.hasKey,
      keyFromServerEnv: section.keyFromServerEnv,
      savedBaseUrl: section.baseUrl,
      savedModel: section.model,
      savedEnabled: section.enabled,
    };
  }

  private aiFormFor(kind: AiProviderKind): AiProviderForm {
    return kind === 'llm' ? this.aiLlm() : this.aiStt();
  }

  private patchAiForm(kind: AiProviderKind, patch: Partial<AiProviderForm>): void {
    const target = kind === 'llm' ? this.aiLlm : this.aiStt;
    target.update((form) => ({ ...form, ...patch }));
  }

  private setAiModels(kind: AiProviderKind, models: string[]): void {
    const target = kind === 'llm' ? this.aiLlmModels : this.aiSttModels;
    target.set(models);
  }

  private setAiStatus(kind: AiProviderKind, status: AiProviderStatus | null): void {
    const target = kind === 'llm' ? this.aiLlmStatus : this.aiSttStatus;
    target.set(status);
  }

  /** The most useful message from an HttpErrorResponse, or a plain fallback. */
  private errorMessage(error: unknown): string {
    const body = (error as { error?: unknown } | null)?.error;
    if (body && typeof body === 'object') {
      const message = (body as { error?: unknown }).error;
      if (typeof message === 'string' && message) return message;
    }
    const message = (error as { message?: unknown } | null)?.message;
    if (typeof message === 'string' && message) return message;
    return 'Unknown error';
  }

  /**
   * Copies the catalog address. The clipboard API is only available in a
   * secure context (https, or localhost), so a failure is reported rather than
   * swallowed — the address on screen stays selectable as the fallback.
   */
  copyCatalogUrl(): void {
    const url = this.opds()?.catalogUrl;
    if (!url || !navigator.clipboard) {
      this.toast.error('Copying is not available here — select the address and copy it.');
      return;
    }

    navigator.clipboard.writeText(url).then(
      () => {
        this.copied.set(true);
        this.toast.success('Catalog address copied.');
        if (this.copiedTimeout !== null) clearTimeout(this.copiedTimeout);
        this.copiedTimeout = setTimeout(() => {
          this.copied.set(false);
          this.copiedTimeout = null;
        }, COPIED_FEEDBACK_MS);
      },
      () => {
        this.toast.error('Could not copy automatically — select the address and copy it.');
      },
    );
  }

  copyManagedOpdsConnectionDetails(): void {
    const info = this.opds();
    const access = this.managedOpds();
    if (
      !info?.catalogUrl ||
      !access?.enabled ||
      !access.username ||
      !access.password ||
      !navigator.clipboard
    ) {
      this.toast.error('Connection details are not available to copy.');
      return;
    }

    const details = [
      `Catalog: ${info.catalogUrl}`,
      `Username: ${access.username}`,
      `Password: ${access.password}`,
    ].join('\n');

    navigator.clipboard.writeText(details).then(
      () => {
        this.copied.set(true);
        this.toast.success('E-reader connection details copied.');
        if (this.copiedTimeout !== null) clearTimeout(this.copiedTimeout);
        this.copiedTimeout = setTimeout(() => {
          this.copied.set(false);
          this.copiedTimeout = null;
        }, COPIED_FEEDBACK_MS);
      },
      () => this.toast.error('Could not copy the connection details automatically.'),
    );
  }

  exportAllNostosData(): void {
    if (this.portableExportBusy() || !this.supportsCloudPortableExport()) return;

    this.portableExportBusy.set(true);
    this.portableExportProgress.set(null);
    this.portableExportError.set(null);

    this.portableLibrary.exportArchive().subscribe({
      next: (event) => {
        if (event.type === HttpEventType.DownloadProgress) {
          this.portableExportProgress.set(
            event.total && event.total > 0
              ? Math.min(100, Math.round((event.loaded / event.total) * 100))
              : null,
          );
          return;
        }

        if (event instanceof HttpResponse) {
          this.savePortableArchive(event.body, event.headers.get('content-disposition'));
          if (this.portableExportError() === null) {
            this.portableExportBusy.set(false);
            this.portableExportProgress.set(null);
            this.toast.success('Your Nostos export is ready.');
          }
        }
      },
      error: (error) => {
        this.portableExportBusy.set(false);
        this.portableExportProgress.set(null);
        this.portableExportError.set(this.portableExportFailureMessage(error));
        this.toast.error('Could not export your Nostos data.');
      },
    });
  }

  private savePortableArchive(blob: Blob | null, contentDisposition: string | null): void {
    if (!blob) {
      this.portableExportBusy.set(false);
      this.portableExportProgress.set(null);
      this.portableExportError.set('Nostos returned an empty export. Try again.');
      this.toast.error('Could not export your Nostos data.');
      return;
    }

    const objectUrl = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = objectUrl;
    link.download = this.portableExportFileName(contentDisposition);
    link.style.display = 'none';
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(objectUrl), PORTABLE_EXPORT_URL_LIFETIME_MS);
  }

  private portableExportFileName(contentDisposition: string | null): string {
    const match = contentDisposition?.match(/filename="?([^";]+)"?/iu);
    const fileName = match?.[1]?.trim();
    return fileName?.toLowerCase().endsWith('.nostos')
      ? fileName
      : 'nostos-export.nostos';
  }

  private portableExportFailureMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      if (error.status === 401 || error.status === 403) {
        return 'Your session no longer allows this export. Sign in again, then retry.';
      }
      if (error.status === 409) {
        return 'This export is no longer available for the current account state.';
      }
    }

    return 'Nostos could not create the export. Your data was not changed. Try again.';
  }

  loadData(): void {
    this.backupService.getStatus().subscribe({
      next: (s) => this.status.set(s),
      error: () => this.toast.error('Failed to load backup status.'),
    });

    this.backupService.getSettings().subscribe({
      next: (s) => this.settings.set(s),
      error: () => {},
    });

    this.loadHistory();
  }

  loadHistory(): void {
    this.backupService.getHistory().subscribe({
      next: (h) => this.history.set(h),
      error: () => {},
    });
  }

  toggleEnabled(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.backupService.updateSettings({ isEnabled: checked }).subscribe({
      next: (s) => {
        this.settings.set(s);
        this.refreshStatus();
      },
      error: () => this.toast.error('Failed to update settings.'),
    });
  }

  toggleBookFiles(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.backupService.updateSettings({ includeBookFiles: checked }).subscribe({
      next: (s) => {
        this.settings.set(s);
        this.refreshStatus();
      },
      error: () => this.toast.error('Failed to update settings.'),
    });
  }

  changeInterval(selected: string): void {
    const value = parseInt(selected, 10);
    this.backupService.updateSettings({ intervalHours: value }).subscribe({
      next: (s) => this.settings.set(s),
      error: () => this.toast.error('Failed to update settings.'),
    });
  }

  changeMaxBackups(selected: string): void {
    const value = parseInt(selected, 10);
    this.backupService.updateSettings({ maxBackups: value }).subscribe({
      next: (s) => this.settings.set(s),
      error: () => this.toast.error('Failed to update settings.'),
    });
  }

  triggerBackup(): void {
    this.backingUp.set(true);
    this.startProgressPolling();
    this.backupService.triggerBackup().subscribe({
      next: (result) => {
        this.backingUp.set(false);
        this.stopProgressPolling();
        this.progress.set(defaultProgress); this.showSlowNotice.set(false);
        if (result.status === 'Completed') {
          this.toast.success(`Backup created successfully (${this.formatSize(result.sizeBytes)}).`);
        } else {
          this.toast.error('Backup failed. Check the logs for details.');
        }
        this.loadData();
      },
      error: () => {
        this.backingUp.set(false);
        this.stopProgressPolling();
        this.progress.set(defaultProgress); this.showSlowNotice.set(false);
        this.toast.error('Backup failed. Check the logs for details.');
      },
    });
  }

  downloadBackup(id: string): void {
    window.open(this.backupService.getDownloadUrl(id), '_blank');
  }

  scanForBackups(): void {
    this.importing.set(true);
    this.backupService.importExisting().subscribe({
      next: (imported) => {
        this.importing.set(false);
        if (imported.length > 0) {
          this.toast.success(`Found ${imported.length} backup(s) on disk.`);
        } else {
          this.toast.info('No new backups found on disk.');
        }
        this.loadHistory();
      },
      error: () => {
        this.importing.set(false);
        this.toast.error('Failed to scan for backups.');
      },
    });
  }

  restoreBackup(id: string): void {
    this.pendingRestore.set(id);
  }

  cancelRestore(): void {
    if (this.restoring()) return;
    this.pendingRestore.set(null);
  }

  confirmRestore(): void {
    const id = this.pendingRestore();
    if (!id || this.restoring()) return;
    this.pendingRestore.set(null);

    this.restoring.set(true);
    this.startProgressPolling();
    this.backupService.restore(id).subscribe({
      next: (result) => {
        this.restoring.set(false);
        this.stopProgressPolling();
        this.progress.set(defaultProgress); this.showSlowNotice.set(false);
        if (result.success) {
          this.toast.success(result.message);
        } else {
          this.toast.error(result.message);
        }
        this.loadHistory();
      },
      error: () => {
        this.restoring.set(false);
        this.stopProgressPolling();
        this.progress.set(defaultProgress); this.showSlowNotice.set(false);
        this.toast.error('Restore failed. Check the logs for details.');
      },
    });
  }

  deleteBackup(id: string): void {
    this.pendingBackupDelete.set(id);
  }

  cancelBackupDelete(): void {
    this.pendingBackupDelete.set(null);
  }

  confirmBackupDelete(): void {
    const id = this.pendingBackupDelete();
    if (!id) return;
    this.pendingBackupDelete.set(null);

    this.backupService.deleteBackup(id).subscribe({
      next: () => {
        this.toast.success('Backup deleted.');
        this.loadHistory();
      },
      error: () => this.toast.error('Failed to delete backup.'),
    });
  }

  private startProgressPolling(): void {
    this.stopProgressPolling();
    this.stepChangedAt = Date.now();
    this.slowNoticeInterval = setInterval(() => {
      this.showSlowNotice.set(Date.now() - this.stepChangedAt > SLOW_STEP_THRESHOLD_MS);
    }, 1000);
    this.progressInterval = setInterval(() => {
      this.backupService.getProgress().subscribe({
        next: (p) => {
          const prev = this.progress();
          if (p.currentStep !== prev.currentStep) {
            this.stepChangedAt = Date.now();
            this.showSlowNotice.set(false);
          }
          this.progress.set(p);
        },
        error: () => {},
      });
    }, 1000);
  }

  private stopProgressPolling(): void {
    if (this.progressInterval) {
      clearInterval(this.progressInterval);
      this.progressInterval = null;
    }
    if (this.slowNoticeInterval) {
      clearInterval(this.slowNoticeInterval);
      this.slowNoticeInterval = null;
    }
  }

  private refreshStatus(): void {
    this.backupService.getStatus().subscribe({
      next: (s) => this.status.set(s),
      error: () => {},
    });
  }

  formatSize(bytes: number): string {
    if (bytes === 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB'];
    const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
    return `${(bytes / Math.pow(1024, i)).toFixed(1)} ${units[i]}`;
  }

  formatInterval(hours: number): string {
    if (hours < 24) return `${hours} hours`;
    if (hours === 24) return 'day';
    if (hours === 168) return 'week';
    return `${hours} hours`;
  }
}
