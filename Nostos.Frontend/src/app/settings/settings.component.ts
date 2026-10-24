import {
  Component,
  computed,
  ElementRef,
  effect,
  inject,
  OnDestroy,
  OnInit,
  signal,
  untracked,
  ViewChild,
  ChangeDetectionStrategy,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { NostosDatePipe } from '../ui/pipes/nostos-date.pipe';
import { HttpErrorResponse, HttpEventType, HttpResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, convertToParamMap, Router } from '@angular/router';
import { of } from 'rxjs';

import { BackupService } from '../core/services/backup.service';
import { OpdsService } from '../core/services/opds.service';
import { ToastService } from '../core/services/toast.service';
import { DAY_THEMES, NIGHT_THEMES, THEMES, ThemeChoice, ThemeName, ThemeService, themeOption } from '../core/services/theme.service';
import { SegmentedComponent, SegmentedOption } from '../ui/segmented/segmented.component';
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
import { AiProviderService } from '../core/services/ai-provider.service';
import { ProviderSettingsService } from '../core/services/provider-settings.service';
import { ProviderSettingsItem } from '../core/dtos/provider.dtos';
import { DeploymentCapabilitiesService } from '../core/services/deployment-capabilities.service';
import { DeploymentCapabilities } from '../core/dtos/deployment-capabilities.dtos';
import { CloudAiRefillService } from '../core/services/cloud-ai-refill.service';
import { CloudAuthService } from '../core/services/cloud-auth.service';
import { CloudEntryService } from '../core/services/cloud-entry.service';
import { CloudSession } from '../core/dtos/cloud-auth.dtos';
import { HighlightImportService } from '../core/services/highlight-import.service';
import { PortableLibraryService } from '../core/services/portable-library.service';
import { ManagedBackupsService } from '../core/services/managed-backups.service';
import { ManagedBackupListing } from '../core/dtos/managed-backups.dtos';
import { CloudAccountDeletionStatus } from '../core/dtos/cloud-account-deletion.dtos';
import { CloudAccountDeletionSettingsComponent } from './cloud-account-deletion-settings.component';
import { ExternalMediaSettingsComponent } from './external-media-settings.component';
import { READ_ALOUD_GUIDE_URL } from './read-aloud-guide';
import { LibraryTransferHostComponent } from '../library-transfer/components/library-transfer-host.component';
import { CloudManagedAiUsage } from '../core/dtos/cloud-ai-refill.dtos';
import {
  AiProviderKind,
  AiProviderSection,
  AiProviderSectionUpdate,
  AiProviderSettings,
  AiProviderUpdate,
  AlignmentTranscriptionApiStyle,
  AlignmentTranscriptionSection,
  AlignmentTranscriptionSectionUpdate,
} from '../core/dtos/ai-provider.dtos';
import { NoticeComponent } from '../ui/notice/notice.component';
import { ProgressComponent } from '../ui/progress/progress.component';

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
    'Notes, voice recordings and book passages are sent directly to the OpenAI-compatible endpoints configured below.',
  readingAssistant: 'Reading assistant',
  voiceTranscription: 'Voice transcription',
  voiceToggle: 'Enable voice transcription',
  voiceToggleHelp: 'Send voice recordings to the transcription endpoint.',
  alignmentTranscription: 'Alignment transcription',
  alignmentDescription:
    'Find the same sentence in the ebook when jumping from an audiobook of the same work.',
  alignmentToggle: 'Enable alignment transcription',
  alignmentToggleHelp: 'Allow audiobook passages to be transcribed for ebook alignment.',
  alignmentUseVoice: 'Use my voice transcription setup',
  alignmentUseVoiceHelp: 'Alignment uses your voice transcription provider.',
  alignmentApiStyle: 'API style',
  alignmentApiStyleMultipart: 'OpenAI-compatible (multipart upload)',
  alignmentApiStyleJson: 'JSON (base64 audio)',
  alignmentApiStyleHelp: 'Most transcription providers use the multipart upload format.',
  readAloud: 'Read aloud voice',
  readAloudDescription:
    'Read books aloud with a natural voice from a speech server, on every device. Without one, each device reads with its own voice.',
  readAloudToggle: 'Enable the natural voice',
  readAloudToggleHelp: 'Send the sentence being read to the speech server below.',
  endpointPlaceholderTts: 'http://kokoro:8880',
  endpointHelpTts: 'Address of a speech server with the OpenAI speech route, e.g. Kokoro.',
  modelPlaceholderTts: 'kokoro',
  apiKeyHelpTts: 'Leave empty for a speech server on your own network.',
  readAloudGuide: 'How to set up a speech server',
  embeddings: 'Embeddings',
  embeddingToggle: 'Enable passage embeddings',
  embeddingToggleHelp:
    'Send book passages to the embedding endpoint to build a semantic index of your library. Ask Nostos keeps using keyword search whenever this is off or the endpoint is unavailable.',
  endpointPlaceholderEmbedding: 'https://api.openai.com/v1',
  modelPlaceholderEmbedding: 'e.g. text-embedding-3-small',
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
  oldCardEmptyState: 'Connect an AI provider in Settings to turn this on.',
  // The reviewed set covers the four card actions but not a failed GET/PUT or
  // the configured-key signals, so these keep their earlier wording.
  configured: 'Configured',
  configuredFromEnv: 'Configured — using the server environment variable.',
  loadFailed: 'Could not load the AI provider settings.',
  couldNotSave: (message: string) => `Could not save: ${message}`,
} as const;

/**
 * Every user-visible string for the "Book providers" card (issue #774), in one
 * place for the same reason as AI_PROVIDER_COPY: one edit changes the wording.
 */
const BOOK_PROVIDER_COPY = {
  intro:
    'Choose which free book and audiobook sources Nostos searches when you add a book. Sources you turn off stay out of Add Book and cannot be used until you turn them back on.',
  loading: 'Loading book sources…',
  loadFailed: 'Could not load the book sources.',
  loadFailedHelp:
    'The server did not answer the request for the provider list. Switch away and back to try again.',
  empty: 'No book sources are available on this server.',
  saveFailed: 'Could not save this source. Your previous choice is still in effect.',
} as const;

/** How a section's inline status line is coloured. */
type SettingsSection =
  | 'general'
  | 'library'
  | 'assistant'
  | 'connections'
  | 'external'
  | 'data'
  | 'account';

/** Sections a `?section=` link may open. `data` is its own route, `/settings/library`. */
const LINKABLE_SECTIONS: readonly SettingsSection[] = [
  'general',
  'library',
  'assistant',
  'connections',
  'external',
  'account',
];

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

interface AlignmentAiProviderForm extends AiProviderForm {
  useVoiceTranscription: boolean;
  apiStyle: AlignmentTranscriptionApiStyle;
  savedUseVoiceTranscription: boolean;
  savedApiStyle: AlignmentTranscriptionApiStyle;
}

function emptyAlignmentAiProviderForm(): AlignmentAiProviderForm {
  return {
    ...emptyAiProviderForm(),
    useVoiceTranscription: true,
    apiStyle: 'openai-multipart',
    savedUseVoiceTranscription: true,
    savedApiStyle: 'openai-multipart',
  };
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
    ProgressComponent,
    NoticeComponent,
    CommonModule,
    NostosDatePipe,
    FormsModule,
    NostosIconComponent,
    ButtonComponent,
    IconButtonComponent,
    SwitchComponent,
    BadgeComponent,
    InputDirective,
    DropdownComponent,
    ConfirmModal,
    CloudAccountDeletionSettingsComponent,
    ExternalMediaSettingsComponent,
    LibraryTransferHostComponent,
    SegmentedComponent,
  ],
  templateUrl: './settings.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrls: ['./settings.component.css'],
})
export class SettingsComponent implements OnInit, OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly route = inject(ActivatedRoute, { optional: true });
  private readonly router = inject(Router, { optional: true });
  private readonly queryParamMap = toSignal(
    this.route?.queryParamMap ?? of(this.route?.snapshot.queryParamMap ?? convertToParamMap({})),
    { initialValue: this.route?.snapshot.queryParamMap ?? convertToParamMap({}) },
  );
  private backupService = inject(BackupService);
  private opdsService = inject(OpdsService);
  private toast = inject(ToastService);
  private themeService = inject(ThemeService);
  private assistantStatus = inject(AssistantStatusService);
  private preferences = inject(LibraryPreferencesService);
  private aiProvider = inject(AiProviderService);
  private providerSettingsService = inject(ProviderSettingsService);
  private deploymentCapabilitiesService = inject(DeploymentCapabilitiesService);
  private cloudAiRefills = inject(CloudAiRefillService);
  private portableLibrary = inject(PortableLibraryService);
  private managedBackupsService = inject(ManagedBackupsService);
  private cloudAuth = inject(CloudAuthService);
  readonly cloudEntry = inject(CloudEntryService);
  private highlightImport = inject(HighlightImportService);

  @ViewChild('firstRunImportInput')
  private firstRunImportInput?: ElementRef<HTMLInputElement>;

  private readonly firstRunCloudImportElement = signal<ElementRef<HTMLElement> | null>(null);

  @ViewChild('firstRunCloudImport')
  set firstRunCloudImportSection(section: ElementRef<HTMLElement> | undefined) {
    this.firstRunCloudImportElement.set(section ?? null);
  }

  /** `/settings/library`: the Backups & data section, kept as its own route. */
  readonly isManageLibraryPage = this.route?.snapshot.data['manageLibraryPage'] === true;
  /**
   * Which settings section is visible. Mirrored to `?section=` so a section
   * survives a reload and can be linked to.
   */
  readonly activeSettingsSection = signal<SettingsSection>(
    this.isManageLibraryPage ? 'data' : 'general',
  );
  readonly isImportSelected = computed(() => this.queryParamMap().get('action') === 'import');
  readonly isFirstRunImport = computed(() => this.queryParamMap().get('source') === 'first-run');

  constructor() {
    effect(() => {
      const requested = this.queryParamMap().get('section') as SettingsSection | null;
      if (this.isManageLibraryPage || !requested || !LINKABLE_SECTIONS.includes(requested)) return;
      // A link to a section this deployment does not have leaves the default open.
      if (requested === 'connections' && !this.canConfigureAiProvider()) return;
      if (requested === 'external' && !this.supportsExternalMediaLibraries()) return;
      if (requested === 'account' && !this.isCloud()) return;
      untracked(() => this.showSection(requested));
    });

    effect(() => {
      this.deploymentCapabilities();
      this.activeSettingsSection();
      // The narrow layout scrolls its sections sideways; keep the open one in view.
      setTimeout(() =>
        this.host.nativeElement
          .querySelector('.settings-nav-item.is-active')
          ?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' }),
      );
    });

    effect(() => {
      const section = this.firstRunCloudImportElement();
      if (!section || !this.isImportSelected() || !this.isFirstRunImport()) return;

      section.nativeElement.scrollIntoView?.({ block: 'center' });
      section.nativeElement.focus({ preventScroll: true });
    });
  }

  /** Server-authoritative deployment capabilities. Null means not loaded yet. */
  readonly deploymentCapabilities = signal<DeploymentCapabilities | null>(null);
  readonly capabilitiesFailed = signal(false);
  readonly capabilitiesLoading = computed(
    () => this.deploymentCapabilities() === null && !this.capabilitiesFailed(),
  );
  readonly supportsLocalBackupConfiguration = computed(
    () => this.deploymentCapabilities()?.supportsLocalBackupConfiguration === true,
  );
  readonly supportsManagedRestore = computed(
    () =>
      this.deploymentCapabilities()?.supportsManagedBackups === true &&
      this.deploymentCapabilities()?.supportsManagedBackupRestore === true,
  );
  readonly supportsManagedBackups = computed(
    () => this.deploymentCapabilities()?.supportsManagedBackups === true,
  );
  readonly managedBackups = signal<ManagedBackupListing | null>(null);
  /** Newest backup: the only one shown until the restore list is opened. */
  readonly latestManagedBackup = computed(() => {
    const backups = this.managedBackups()?.backups ?? [];
    return backups.reduce<(typeof backups)[number] | null>(
      (latest, backup) =>
        !latest || backup.createdAtUtc > latest.createdAtUtc ? backup : latest,
      null,
    );
  });
  readonly managedBackupsExpanded = signal(false);
  readonly managedBackupsLoading = signal(false);
  readonly managedBackupsFailed = signal(false);

  readonly pendingManagedRestore = signal<string | null>(null);
  readonly managedRestoreBusy = signal(false);
  readonly managedRestoreError = signal<string | null>(null);
  readonly managedRestoreUncertain = signal(false);
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
  readonly supportsAccountDeletion = computed(
    () => this.isCloud() && this.deploymentCapabilities()?.supportsAccountDeletion === true,
  );
  readonly supportsCloudPortableExport = computed(() => this.isCloud());
  readonly showFirstRunCloudImport = computed(
    () =>
      this.isManageLibraryPage &&
      (this.isFirstRunImport() || this.cloudEntry.firstRunImportPending()) &&
      this.supportsCloudPortableExport() &&
      !this.supportsLibraryMigration(),
  );
  /**
   * Server-authoritative migration capability (#680 plan §4). Only a true
   * value renders the shared "Move your library" card; false or absent keeps
   * this surface exactly as it is on main. Never inferred from
   * `deploymentMode`.
   */
  readonly supportsLibraryMigration = computed(
    () => this.deploymentCapabilities()?.supportsLibraryMigration === true,
  );
  /**
   * Server-authoritative safe activation (#681). The shared import flow only
   * starts activation when this is true; false keeps the gated explanation.
   */
  readonly supportsSafeActivation = computed(
    () => this.deploymentCapabilities()?.supportsSafeActivation === true,
  );
  /**
   * A SelfHosted server with an external media folder configured (#843).
   * Server-authoritative: never inferred from `deploymentMode`.
   */
  readonly supportsExternalMediaLibraries = computed(
    () => this.deploymentCapabilities()?.supportsExternalMediaLibraries === true,
  );
  readonly hasManageLibraryActions = computed(
    () =>
      this.supportsLocalBackupConfiguration() ||
      this.supportsLibraryMigration() ||
      this.supportsCloudPortableExport(),
  );
  readonly manageLibrarySummary = computed(() => {
    const hasBackups = this.supportsLocalBackupConfiguration();
    const hasMigration = this.supportsLibraryMigration();

    if (hasBackups && hasMigration) {
      return 'Back up this installation or move your library to another Nostos.';
    }
    if (hasBackups) {
      return 'Backups help you recover this SelfHosted installation.';
    }
    if (hasMigration) {
      return 'Move your library to another Nostos installation.';
    }
    if (this.supportsManagedBackups()) {
      return 'Nostos backs up your library every night. You can also download a portable archive of it.';
    }
    if (this.supportsCloudPortableExport()) {
      return 'Download a portable archive of your Nostos library.';
    }

    return 'Manage the data for your Nostos library.';
  });
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
  /** Backups & data is offered when this deployment has anything to put in it. */
  readonly hasDataSection = computed(
    () => this.isManageLibraryPage || this.hasManageLibraryActions(),
  );
  /** Whether the nav's second group ("This server" / "Your account") has entries. */
  readonly hasInfrastructureSections = computed(
    () =>
      this.canConfigureAiProvider() ||
      this.supportsExternalMediaLibraries() ||
      this.hasDataSection() ||
      this.isCloud(),
  );

  openFirstRunImportPicker(): void {
    this.firstRunImportInput?.nativeElement.click();
  }

  async importFirstRunArchive(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    await this.cloudEntry.importPortableArchive(file);
    input.value = '';
  }

  /** The AI provider card's copy, exposed so the template reads one source. */
  readonly copy = AI_PROVIDER_COPY;

  /** The Book providers card's copy, same single-source rule. */
  readonly copyProviders = BOOK_PROVIDER_COPY;

  /** The theme choice and its options, for the Colour theme card (issue #1074). */
  readonly themeChoice = this.themeService.choice;
  readonly themeCards: readonly { value: ThemeChoice; label: string }[] = [
    { value: 'system', label: 'Follow system' },
    ...THEMES.map((t) => ({ value: t.value, label: t.label })),
  ];
  readonly dayThemeOptions: readonly SegmentedOption<ThemeName>[] = DAY_THEMES.map((t) => ({ value: t.value, label: t.label }));
  readonly nightThemeOptions: readonly SegmentedOption<ThemeName>[] = NIGHT_THEMES.map((t) => ({ value: t.value, label: t.label }));
  readonly dayTheme = this.themeService.dayTheme;
  readonly nightTheme = this.themeService.nightTheme;
  readonly dayThemeOption = computed(() => themeOption(this.dayTheme()));
  readonly nightThemeOption = computed(() => themeOption(this.nightTheme()));

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

  openHighlightImport(): void {
    this.highlightImport.open();
  }

  setSettingsSection(section: SettingsSection): void {
    if (section === 'data') {
      if (!this.isManageLibraryPage) void this.router?.navigate(['/settings/library']);
      return;
    }
    if (this.isManageLibraryPage) {
      void this.router?.navigate(['/settings'], { queryParams: { section } });
      return;
    }

    this.showSection(section);
    void this.router?.navigate([], {
      relativeTo: this.route,
      queryParams: { section },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  private showSection(section: SettingsSection): void {
    this.activeSettingsSection.set(section);
    if (section === 'library') this.loadProviderSettings();
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

  // --- Book providers card (issue #774) --------------------------------
  // Loaded lazily the first time the tab opens; a failed load retries on the
  // next open. A toggle is applied optimistically and reverted on failure,
  // because a native checkbox stays where the click put it unless the bound
  // value actually changes.

  /** Every registered source with its effective choice, disabled ones included. */
  readonly providerSettings = signal<ProviderSettingsItem[]>([]);
  readonly providerSettingsLoading = signal(false);
  readonly providerSettingsError = signal(false);
  readonly providerSettingsSaveFailed = signal(false);
  readonly providerSavingIds = signal<ReadonlySet<string>>(new Set<string>());
  private providerSettingsLoaded = false;

  loadProviderSettings(): void {
    if (this.providerSettingsLoading()) return;
    if (this.providerSettingsLoaded && !this.providerSettingsError()) return;

    this.providerSettingsLoading.set(true);
    this.providerSettingsError.set(false);
    this.providerSettingsService.list().subscribe({
      next: (response) => {
        this.providerSettings.set(response.providers);
        this.providerSettingsLoaded = true;
        this.providerSettingsLoading.set(false);
      },
      error: () => {
        this.providerSettingsError.set(true);
        this.providerSettingsLoading.set(false);
      },
    });
  }

  setProviderEnabled(provider: ProviderSettingsItem, event: Event): void {
    if (this.providerSavingIds().has(provider.id)) return;

    const previousEnabled = provider.enabled;
    const checked = (event.target as HTMLInputElement).checked;

    this.providerSettings.set(
      this.withProviderEnabled(this.providerSettings(), provider.id, checked),
    );
    this.setProviderSaving(provider.id, true);
    this.providerSettingsSaveFailed.set(false);

    this.providerSettingsService.setEnabled(provider.id, checked).subscribe({
      next: (updated) => {
        this.setProviderSaving(provider.id, false);
        // The server's item is the truth; replacing it also undoes any drift.
        this.providerSettings.update((list) =>
          list.map((item) => (item.id === updated.id ? updated : item)),
        );
      },
      error: () => {
        this.setProviderSaving(provider.id, false);
        // Revert only this row: another provider's concurrent save may have
        // succeeded since this one started.
        this.providerSettings.update((list) =>
          this.withProviderEnabled(list, provider.id, previousEnabled),
        );
        this.providerSettingsSaveFailed.set(true);
        this.toast.error(BOOK_PROVIDER_COPY.saveFailed);
      },
    });
  }

  private withProviderEnabled(
    list: readonly ProviderSettingsItem[],
    providerId: string,
    enabled: boolean,
  ): ProviderSettingsItem[] {
    return list.map((item) => (item.id === providerId ? { ...item, enabled } : item));
  }

  private setProviderSaving(providerId: string, saving: boolean): void {
    const next = new Set(this.providerSavingIds());
    if (saving) next.add(providerId);
    else next.delete(providerId);
    this.providerSavingIds.set(next);
  }

  setTheme(choice: ThemeChoice): void {
    this.themeService.setChoice(choice);
  }

  setDayTheme(name: ThemeName): void {
    this.themeService.setDayTheme(name);
  }

  setNightTheme(name: ThemeName): void {
    this.themeService.setNightTheme(name);
  }

  /** A theme card's swatch colours (not for Follow system, which shows day and night). */
  swatchOf(choice: ThemeChoice) {
    return themeOption(choice as ThemeName).swatch;
  }

  /** Arrow keys move between the theme cards and pick, as in any radio group. */
  themeKey(event: KeyboardEvent, index: number): void {
    const step =
      event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const next = (index + step + this.themeCards.length) % this.themeCards.length;
    this.setTheme(this.themeCards[next].value);
    (this.host.nativeElement.querySelectorAll('.theme-card')[next] as HTMLElement | undefined)?.focus();
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
  aiEmbedding = signal<AiProviderForm>(emptyAiProviderForm());
  aiAlignment = signal<AlignmentAiProviderForm>(emptyAlignmentAiProviderForm());

  /** False when the host reports no embedding section (it manages embeddings itself). */
  aiEmbeddingAvailable = signal(false);
  aiTts = signal<AiProviderForm>(emptyAiProviderForm());
  aiTtsAvailable = signal(false);
  aiTtsModels = signal<string[]>([]);
  aiTtsStatus = signal<AiProviderStatus | null>(null);
  readonly readAloudGuideUrl = READ_ALOUD_GUIDE_URL;

  /** False when the host does not report alignment transcription settings. */
  aiAlignmentAvailable = signal(false);

  /** Model ids offered as suggestions for each sub-section (free text stays editable). */
  aiLlmModels = signal<string[]>([]);
  aiSttModels = signal<string[]>([]);
  aiEmbeddingModels = signal<string[]>([]);
  aiAlignmentModels = signal<string[]>([]);

  /** Inline outcome lines: `Testing…` / test result / load result / clear notice. */
  aiLlmStatus = signal<AiProviderStatus | null>(null);
  aiSttStatus = signal<AiProviderStatus | null>(null);
  aiEmbeddingStatus = signal<AiProviderStatus | null>(null);
  aiAlignmentStatus = signal<AiProviderStatus | null>(null);
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
  /** The history list stays collapsed until asked for; it grows with every backup. */
  historyExpanded = signal(false);
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

        // Do not touch owner/infrastructure APIs before the server says this
        // deployment exposes them. This also prevents forbidden controls from
        // flashing while the capability request is in flight.
        if (capabilities.supportsLocalBackupConfiguration) this.loadData();
        if (this.isManageLibraryPage && capabilities.supportsManagedBackups === true) {
          this.loadManagedBackups();
        }
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
      },
      error: () => {
        this.deploymentCapabilities.set(null);
        this.capabilitiesFailed.set(true);
      },
    });
  }

  private loadManagedBackups(): void {
    this.managedBackupsLoading.set(true);
    this.managedBackupsService.getBackups().subscribe({
      next: (listing) => {
        this.managedBackups.set(listing);
        this.managedBackupsFailed.set(false);
        this.managedBackupsLoading.set(false);
      },
      error: () => {
        this.managedBackups.set(null);
        this.managedBackupsFailed.set(true);
        this.managedBackupsLoading.set(false);
      },
    });
  }

  requestManagedRestore(id: string): void {
    if (
      !this.isManageLibraryPage ||
      !this.supportsManagedRestore() ||
      this.managedBackupsLoading() ||
      this.managedRestoreBusy() ||
      this.managedRestoreUncertain() ||
      !this.managedBackups()?.backups.some(
        backup => backup.id === id && backup.state === 'completed',
      )
    ) return;

    this.managedRestoreError.set(null);
    this.pendingManagedRestore.set(id);
  }

  cancelManagedRestore(): void {
    if (this.managedRestoreBusy()) return;
    this.pendingManagedRestore.set(null);
  }

  confirmManagedRestore(): void {
    const id = this.pendingManagedRestore();

    if (
      !id ||
      this.managedRestoreBusy() ||
      this.managedRestoreUncertain() ||
      !this.isManageLibraryPage ||
      !this.supportsManagedRestore() ||
      !this.managedBackups()?.backups.some(
        backup => backup.id === id && backup.state === 'completed',
      )
    ) return;

    this.managedRestoreBusy.set(true);
    this.managedRestoreError.set(null);

    this.managedBackupsService.restore(id, { confirm: true }).subscribe({
      next: result => {
        if (result.backupId !== id || !result.restoredAtUtc) {
          this.managedRestoreBusy.set(false);
          this.pendingManagedRestore.set(null);
          this.managedRestoreUncertain.set(true);
          this.managedRestoreError.set(
            'The restore response was unexpected. Your library may have ' +
            'been replaced. Reload Nostos, inspect your library, and contact ' +
            'support before attempting another restore.',
          );
          return;
        }

        // Activation has been explicitly confirmed by the server.
        // Reinitialize the application against the newly active library.
        // This discards in-memory library and managed-backup list state.
        window.location.reload();
      },
      error: (error: unknown) => {
        this.managedRestoreBusy.set(false);
        this.pendingManagedRestore.set(null);

        if (error instanceof HttpErrorResponse) {
          if (error.status === 404) {
            this.managedRestoreError.set(
              'This backup is no longer available for your account. ' +
              'Reload the backup list before selecting another.',
            );
            return;
          }

          if (error.status === 409) {
            this.managedRestoreError.set(
              'The restore was rejected because the library mapping changed. ' +
              'Reload Nostos before selecting another backup.',
            );
            return;
          }

          if (error.status === 422) {
            this.managedRestoreError.set(
              'The backup failed staged restore verification and was not activated.',
            );
            return;
          }

          if (error.status === 501) {
            this.managedRestoreError.set(
              'Managed backup restore is not supported by this host.',
            );
            return;
          }

          if (error.status === 401 || error.status === 403) {
            this.managedRestoreError.set(
              'Your session does not permit this restore. ' +
              'Sign in again and inspect your library before retrying.',
            );
            return;
          }

          if (error.status === 400 || error.status === 429) {
            this.managedRestoreError.set(
              'The restore request was rejected. ' +
              'Review the request or wait for the rate limit to clear.',
            );
            return;
          }
        }

        // A timeout, lost connection, or 5xx may occur after activation.
        // Do not assert that the original library is still active.
        this.managedRestoreUncertain.set(true);
        this.managedRestoreError.set(
          'Nostos could not confirm the restore outcome. Your library may ' +
          'already have been replaced. Reload Nostos and inspect the library ' +
          'before doing anything else. Contact support if the outcome is unclear.',
        );
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

  showAccountDeletionPending(status: CloudAccountDeletionStatus): void {
    this.cloudEntry.showAccountDeletionPending(status);
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
        this.toast.error('Could not enable e-reader access');
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
        this.toast.error('Could not regenerate the e-reader password');
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
        this.toast.success('E-reader access disabled');
      },
      error: () => {
        this.managedOpdsBusy.set(null);
        this.toast.error('Could not disable e-reader access');
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
        this.applyAiSettings(settings);
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

  setAiAlignmentEnabled(event: Event): void {
    this.patchAiForm('alignmentStt', { enabled: (event.target as HTMLInputElement).checked });
  }

  setAiAlignmentUseVoice(event: Event): void {
    const useVoiceTranscription = (event.target as HTMLInputElement).checked;
    this.aiAlignment.update((form) => ({ ...form, useVoiceTranscription }));
  }

  readonly alignmentApiStyleOptions = [
    { value: 'openai-multipart', label: AI_PROVIDER_COPY.alignmentApiStyleMultipart },
    { value: 'json-base64', label: AI_PROVIDER_COPY.alignmentApiStyleJson },
  ];

  setAiAlignmentApiStyle(value: string): void {
    if (value !== 'openai-multipart' && value !== 'json-base64') return;
    this.aiAlignment.update((form) => ({ ...form, apiStyle: value }));
  }

  setAiEmbeddingEnabled(event: Event): void {
    this.patchAiForm('embedding', { enabled: (event.target as HTMLInputElement).checked });
  }

  setAiTtsEnabled(event: Event): void {
    this.patchAiForm('tts', { enabled: (event.target as HTMLInputElement).checked });
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
    const useVoice =
      kind === 'alignmentStt' && this.aiAlignment().useVoiceTranscription;
    const request = useVoice
      ? { kind, baseUrl: undefined, apiKey: undefined }
      : { kind, baseUrl: form.baseUrl, apiKey: form.key || undefined };

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
    const useVoice =
      kind === 'alignmentStt' && this.aiAlignment().useVoiceTranscription;
    const request = {
      kind,
      baseUrl: useVoice ? undefined : form.baseUrl,
      model: useVoice ? undefined : form.model,
      apiKey: useVoice ? undefined : form.key || undefined,
      // Test what is selected now, not what was last saved.
      ...(kind === 'alignmentStt'
        ? {
            useVoiceTranscription: useVoice,
            apiStyle: useVoice ? undefined : this.aiAlignment().apiStyle,
          }
        : {}),
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
    if (this.aiEmbeddingAvailable()) {
      const embedding = this.buildAiSectionUpdate(this.aiEmbedding());
      if (embedding) update.embedding = embedding;
    }
    if (this.aiAlignmentAvailable()) {
      const alignment = this.buildAiAlignmentUpdate(this.aiAlignment());
      if (alignment) update.alignmentTranscription = alignment;
    }
    if (this.aiTtsAvailable()) {
      const tts = this.buildAiSectionUpdate(this.aiTts());
      if (tts) update.tts = tts;
    }

    this.aiSaving.set(true);
    this.aiSaveStatus.set(null);
    this.aiProvider.update(update).subscribe({
      next: (settings) => {
        this.aiSaving.set(false);
        this.applyAiSettings(settings);
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

  private buildAiAlignmentUpdate(
    form: AlignmentAiProviderForm,
  ): AlignmentTranscriptionSectionUpdate | null {
    const update: AlignmentTranscriptionSectionUpdate = {
      ...this.buildAiSectionUpdate(form),
    };

    if (form.useVoiceTranscription !== form.savedUseVoiceTranscription) {
      update.useVoiceTranscription = form.useVoiceTranscription;
      if (!form.useVoiceTranscription) {
        // Until now the fields showed the inherited voice setup. Save what the
        // user sees, or the dedicated setup would start out disabled and empty.
        update.enabled = form.enabled;
        update.baseUrl = form.baseUrl;
        update.model = form.model;
        update.apiStyle = form.apiStyle;
      }
    }
    if (form.apiStyle !== form.savedApiStyle) {
      update.apiStyle = form.apiStyle;
    }

    return Object.keys(update).length > 0 ? update : null;
  }

  private toAlignmentAiForm(section: AlignmentTranscriptionSection): AlignmentAiProviderForm {
    return {
      ...this.toAiForm(section),
      useVoiceTranscription: section.useVoiceTranscription ?? true,
      apiStyle: section.apiStyle ?? 'openai-multipart',
      savedUseVoiceTranscription: section.useVoiceTranscription ?? true,
      savedApiStyle: section.apiStyle ?? 'openai-multipart',
    };
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

  /** Seeds every section from a GET/PUT response. */
  private applyAiSettings(settings: AiProviderSettings): void {
    this.aiLlm.set(this.toAiForm(settings.llm));
    this.aiStt.set(this.toAiForm(settings.stt));
    this.aiEmbeddingAvailable.set(!!settings.embedding);
    this.aiEmbedding.set(
      settings.embedding ? this.toAiForm(settings.embedding) : emptyAiProviderForm(),
    );
    this.aiTtsAvailable.set(!!settings.tts);
    this.aiTts.set(settings.tts ? this.toAiForm(settings.tts) : emptyAiProviderForm());
    this.aiAlignmentAvailable.set(!!settings.alignmentTranscription);
    this.aiAlignment.set(
      settings.alignmentTranscription
        ? this.toAlignmentAiForm(settings.alignmentTranscription)
        : emptyAlignmentAiProviderForm(),
    );
  }

  private aiFormSignal(kind: Exclude<AiProviderKind, 'alignmentStt'>) {
    switch (kind) {
      case 'llm':
        return this.aiLlm;
      case 'stt':
        return this.aiStt;
      case 'embedding':
        return this.aiEmbedding;
      case 'tts':
        return this.aiTts;
    }
  }

  private aiFormFor(kind: AiProviderKind): AiProviderForm {
    return kind === 'alignmentStt' ? this.aiAlignment() : this.aiFormSignal(kind)();
  }

  private patchAiForm(kind: AiProviderKind, patch: Partial<AiProviderForm>): void {
    if (kind === 'alignmentStt') {
      this.aiAlignment.update((form) => ({ ...form, ...patch }));
      return;
    }
    this.aiFormSignal(kind).update((form) => ({ ...form, ...patch }));
  }

  private setAiModels(kind: AiProviderKind, models: string[]): void {
    switch (kind) {
      case 'llm':
        this.aiLlmModels.set(models);
        break;
      case 'stt':
        this.aiSttModels.set(models);
        break;
      case 'embedding':
        this.aiEmbeddingModels.set(models);
        break;
      case 'tts':
        this.aiTtsModels.set(models);
        break;
      case 'alignmentStt':
        this.aiAlignmentModels.set(models);
        break;
    }
  }

  private setAiStatus(kind: AiProviderKind, status: AiProviderStatus | null): void {
    switch (kind) {
      case 'llm':
        this.aiLlmStatus.set(status);
        break;
      case 'stt':
        this.aiSttStatus.set(status);
        break;
      case 'embedding':
        this.aiEmbeddingStatus.set(status);
        break;
      case 'tts':
        this.aiTtsStatus.set(status);
        break;
      case 'alignmentStt':
        this.aiAlignmentStatus.set(status);
        break;
    }
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
        this.toast.success('Catalog address copied');
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
      this.toast.error('Connection details are not available to copy');
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
        this.toast.success('E-reader connection details copied');
        if (this.copiedTimeout !== null) clearTimeout(this.copiedTimeout);
        this.copiedTimeout = setTimeout(() => {
          this.copied.set(false);
          this.copiedTimeout = null;
        }, COPIED_FEEDBACK_MS);
      },
      () => this.toast.error('Could not copy the connection details automatically'),
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
            this.toast.success('Your Nostos export is ready');
          }
        }
      },
      error: (error) => {
        this.portableExportBusy.set(false);
        this.portableExportProgress.set(null);
        this.portableExportError.set(this.portableExportFailureMessage(error));
        this.toast.error('Could not export your Nostos data');
      },
    });
  }

  private savePortableArchive(blob: Blob | null, contentDisposition: string | null): void {
    if (!blob) {
      this.portableExportBusy.set(false);
      this.portableExportProgress.set(null);
      this.portableExportError.set('Nostos returned an empty export. Try again.');
      this.toast.error('Could not export your Nostos data');
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

  /**
   * Capability-on only: a completed shared import replaced the server library,
   * so refresh what Settings reads from the server rather than leaving stale
   * data on screen.
   */
  onLibraryTransferCompleted(): void {
    if (this.isFirstRunImport() || this.cloudEntry.firstRunImportPending()) {
      this.cloudEntry.finishFirstRunAfterImport();
    }
    if (this.supportsLocalBackupConfiguration()) this.loadData();
  }

  loadData(): void {
    this.backupService.getStatus().subscribe({
      next: (s) => this.status.set(s),
      error: () => this.toast.error('Could not load the backup status'),
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
      error: () => this.toast.error('Could not update the settings'),
    });
  }

  toggleBookFiles(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.backupService.updateSettings({ includeBookFiles: checked }).subscribe({
      next: (s) => {
        this.settings.set(s);
        this.refreshStatus();
      },
      error: () => this.toast.error('Could not update the settings'),
    });
  }

  changeInterval(selected: string): void {
    const value = parseInt(selected, 10);
    this.backupService.updateSettings({ intervalHours: value }).subscribe({
      next: (s) => this.settings.set(s),
      error: () => this.toast.error('Could not update the settings'),
    });
  }

  changeMaxBackups(selected: string): void {
    const value = parseInt(selected, 10);
    this.backupService.updateSettings({ maxBackups: value }).subscribe({
      next: (s) => this.settings.set(s),
      error: () => this.toast.error('Could not update the settings'),
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
          this.toast.success(`Backup created (${this.formatSize(result.sizeBytes)})`);
        } else {
          this.toast.error('Could not create the backup. Check the logs for details.');
        }
        this.loadData();
      },
      error: () => {
        this.backingUp.set(false);
        this.stopProgressPolling();
        this.progress.set(defaultProgress); this.showSlowNotice.set(false);
        this.toast.error('Could not create the backup. Check the logs for details.');
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
          this.toast.info('No new backups found on disk');
        }
        this.loadHistory();
      },
      error: () => {
        this.importing.set(false);
        this.toast.error('Could not scan for backups');
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
        this.toast.error('Could not restore the backup. Check the logs for details.');
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
        this.toast.success('Backup deleted');
        this.loadHistory();
      },
      error: () => this.toast.error('Could not delete the backup'),
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
