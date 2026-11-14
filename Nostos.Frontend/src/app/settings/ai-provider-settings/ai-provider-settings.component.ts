import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';

import {
  AiProviderKind,
  AiProviderSection,
  AiProviderSectionUpdate,
  AiProviderSettings,
  AiProviderUpdate,
  AlignmentTranscriptionApiStyle,
  AlignmentTranscriptionSection,
  AlignmentTranscriptionSectionUpdate,
} from '../../core/dtos/ai-provider.dtos';
import { AiProviderService } from '../../core/services/ai-provider.service';
import { READ_ALOUD_GUIDE_URL } from '../read-aloud-guide';
import { ButtonComponent } from '../../ui/button/button.component';
import { DropdownComponent } from '../../ui/dropdown/dropdown.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { SwitchComponent } from '../../ui/switch/switch.component';

/**
 * Every user-visible string for the AI provider feature, in one place: the
 * card's own copy plus the locked-state sentence the older Reading assistant
 * card shows when no provider is configured. Keeping them together means a
 * wording change is a single edit.
 */
const AI_PROVIDER_COPY = {
  title: 'AI provider',
  intro:
    'Notes, voice recordings and book passages are sent directly to the OpenAI-compatible endpoints configured below.',
  readingAssistant: 'Ask Nostos',
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

/**
 * Settings → AI connections: the endpoints this server calls for the assistant,
 * transcription, read-aloud and search.
 *
 * Four calls, one dedicated service. The key is write-only: `toAiForm` never
 * seeds `key`, and Save includes `apiKey` only when the user typed one or
 * pressed Clear. A failed read is shown in place rather than guessed at, so the
 * fields are never presented as fact.
 */
@Component({
  selector: 'app-ai-provider-settings',
  standalone: true,
  imports: [
    ButtonComponent,
    DropdownComponent,
    EmptyStateComponent,
    InputDirective,
    NostosIconComponent,
    SwitchComponent,
  ],
  templateUrl: './ai-provider-settings.component.html',
  styleUrls: ['../settings-section.host.css', '../settings-section.shared.css', './ai-provider-settings.component.css', '../settings-section.responsive.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AiProviderSettingsComponent implements OnInit {
  private readonly aiProvider = inject(AiProviderService);

  /** The AI provider card's copy, exposed so the template reads one source. */
  readonly copy = AI_PROVIDER_COPY;

  readonly readAloudGuideUrl = READ_ALOUD_GUIDE_URL;

  // --- AI provider card state ------------------------------------------
  aiLoadFailed = signal(false);
  aiLlm = signal<AiProviderForm>(emptyAiProviderForm());
  aiStt = signal<AiProviderForm>(emptyAiProviderForm());
  aiEmbedding = signal<AiProviderForm>(emptyAiProviderForm());
  aiAlignment = signal<AlignmentAiProviderForm>(emptyAlignmentAiProviderForm());

  /** False when the host reports no embedding section (it manages embeddings itself). */
  aiEmbeddingAvailable = signal(false);
  aiTts = signal<AiProviderForm>(emptyAiProviderForm());
  aiTtsAvailable = signal(false);

  /** False when the host does not report alignment transcription settings. */
  aiAlignmentAvailable = signal(false);

  /** Model ids offered as suggestions for each sub-section (free text stays editable). */
  aiLlmModels = signal<string[]>([]);
  aiSttModels = signal<string[]>([]);
  aiEmbeddingModels = signal<string[]>([]);
  aiAlignmentModels = signal<string[]>([]);
  aiTtsModels = signal<string[]>([]);

  /** Inline outcome lines: `Testing…` / test result / load result / clear notice. */
  aiLlmStatus = signal<AiProviderStatus | null>(null);
  aiSttStatus = signal<AiProviderStatus | null>(null);
  aiEmbeddingStatus = signal<AiProviderStatus | null>(null);
  aiAlignmentStatus = signal<AiProviderStatus | null>(null);
  aiTtsStatus = signal<AiProviderStatus | null>(null);
  aiSaveStatus = signal<AiProviderStatus | null>(null);

  aiSaving = signal(false);

  /** Which section, if any, is running a Load models request. One at a time. */
  aiLoadingKind = signal<AiProviderKind | null>(null);

  /** Which section, if any, is running a Test connection request. One at a time. */
  aiTestingKind = signal<AiProviderKind | null>(null);

  aiBusy = computed(
    () => this.aiSaving() || this.aiLoadingKind() !== null || this.aiTestingKind() !== null,
  );

  readonly alignmentApiStyleOptions = [
    { value: 'openai-multipart', label: AI_PROVIDER_COPY.alignmentApiStyleMultipart },
    { value: 'json-base64', label: AI_PROVIDER_COPY.alignmentApiStyleJson },
  ];

  ngOnInit(): void {
    this.loadAiProvider();
  }

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
    const useVoice = kind === 'alignmentStt' && this.aiAlignment().useVoiceTranscription;
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
    const useVoice = kind === 'alignmentStt' && this.aiAlignment().useVoiceTranscription;
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
}
