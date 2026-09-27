/**
 * Assistant conversation/session state (issue #261 §3, §5, §6, §7).
 *
 * The backend bridge (`POST /api/assistant/turn`) owns the LLM and the tool
 * loop; this service owns the surface's state: the editorial transcript, the
 * deterministic source-location follow-up, the non-mutating suggestions, and
 * destructive confirmations. Ordinary safe actions execute inline.
 *
 * Trust is structural, not cosmetic:
 *   - Suggestions never mutate merely by being shown.
 *   - Choosing a normal reversible action (for example an existing concept link)
 *     is sufficient authorization for the backend's immediate Act path.
 *   - Only destructive/high-impact pending plans use `approvePlan` with the
 *     exact plan id and approval token.
 *
 * `TRANSCRIPT_SEND_POLICY` is unchanged: a voice transcript enters the composer
 * and is dispatched after the grace window, with a pre-dispatch Undo.
 */
import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Subject } from 'rxjs';

import {
  AssistantAnchor,
  AssistantContext,
  AssistantContextService,
} from './assistant-context.service';

export interface AssistantEntry {
  /** UI/event identity. Deliberately distinct from the logical TurnId. */
  id: string;
  /** The logical user turn this visible event belongs to. */
  turnId: string;
  /**
   * Speaker-explicit (issue #286): the user's own words, the assistant's words,
   * or a delivery failure. A capture's acknowledgement and the deterministic
   * anchor question are assistant entries, distinguished by their meta/anchor.
   */
  kind: 'user' | 'assistant' | 'error';
  text: string;
  anchorLabel: string | null;
  meta: string | null;
  sources?: AssistantSourceReferenceDto[];
}

export interface AssistantSourceLocatorDto {
  type: 'pdf' | 'epub' | 'audio' | string;
  pdfPageIndex?: number | null;
  pdfPageLabel?: string | null;
  epubSpineIndex?: number | null;
  epubResourceHref?: string | null;
  epubCfi?: string | null;
  startTextOffset?: number | null;
  endTextOffset?: number | null;
}

export interface AssistantSourceReferenceDto {
  bookId: string;
  bookTitle: string;
  bookAuthor: string | null;
  format: string;
  sourceSha256: string;
  excerpt: string;
  locators: AssistantSourceLocatorDto[];
}

/** Compact application snapshot from when an older user turn occurred. */
export interface AssistantHistoricalContextDto {
  surface: string | null;
  bookId: string | null;
  bookTitle: string | null;
  brainReviewNoteId: string | null;
  concept: string | null;
  collectionId: string | null;
}

/** Compact identity for source evidence surfaced during an older turn. */
export interface AssistantHistoricalEvidenceDto {
  bookId: string;
  bookTitle: string;
  sourceSha256: string;
  locators: AssistantSourceLocatorDto[];
}

/**
 * One remembered conversational message. The browser sends the complete
 * session ledger; the server decides what fits into model context.
 */
export interface AssistantHistoryMessage {
  role: 'user' | 'assistant';
  text: string;
  context?: AssistantHistoricalContextDto | null;
  evidence?: AssistantHistoricalEvidenceDto[];
  actions?: string[];
  capturedNoteId?: string | null;
}

export interface AssistantAnchorPrompt {
  /**
   * What the capture needs before it can be saved. `book` is the app asking
   * which book a thought belongs to, because none is open and the app will not
   * guess; the other two are a source location the format cannot supply.
   */
  kind: 'physical_page' | 'external_audio_timestamp' | 'book';
  question: string;
  /** Server-authoritative deterministic capture continuation. */
  continuationId: string;
}

/** A non-mutating proposal returned by the bridge. */
export interface AssistantSuggestionDto {
  kind: string;
  label: string;
  reason: string;
  value: string | null;
}

/** One ordered step of a plan awaiting approval. */
export interface AssistantPlanStepDto {
  capability: string;
  summary: string;
  argumentsJson: string;
}

/** A plan the assistant will not run without an explicit approval. */
export interface AssistantPendingPlanDto {
  planId: string;
  summary: string;
  steps: AssistantPlanStepDto[];
  approvalToken: string;
}

export interface AssistantPlanStepOutcomeDto {
  capability: string;
  success: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  data: unknown;
}

/** The result of executing an approved plan. Failures are data. */
export interface AssistantPlanApproveResponse {
  success: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  steps: AssistantPlanStepOutcomeDto[];
}

export interface AssistantAnchorPromptDto {
  kind: string;
  question: string;
  continuationId?: string | null;
}

export interface AssistantTurnErrorDto {
  code: string;
  message: string;
}

/** One normal turn result, mirroring `AssistantTurnResponse`. */
export interface AssistantTurnResponse {
  reply: string;
  acknowledgement: string | null;
  anchorPrompt: AssistantAnchorPromptDto | null;
  suggestions: AssistantSuggestionDto[];
  pendingPlan: AssistantPendingPlanDto | null;
  /** The note a capture created this turn; null when nothing was captured. */
  capturedNoteId?: string | null;
  /** Immediate Act capabilities that actually completed successfully. */
  executedCapabilities?: string[];
  /** Server-grounded passages with exact source locators. */
  sources?: AssistantSourceReferenceDto[];
  /** Server-known deterministic refusal, e.g. stale continuation. */
  error?: AssistantTurnErrorDto | null;
}

/** The turn request the bridge accepts. */
interface AssistantTurnRequestDto {
  /** Compatibility aliases: equal to conversationId / turnId for new clients. */
  clientId: string;
  idempotencyKey: string;
  /** Stable for the current working Ask Nostos conversation. */
  conversationId: string;
  /** Stable identity for this logical user turn, reused on delivery retry. */
  turnId: string;
  message: string;
  context: AssistantContextDto;
  /** Server-held deterministic continuation answered by this real user turn. */
  continuationId: string | null;
  continuationSkipped: boolean;
  /**
   * The recent turns the client remembers (issue #286). The server appends the
   * current `message` itself, so this is the completed log from BEFORE this
   * logical turn — never the message being sent or a transport retry copy.
   */
  history: AssistantHistoryMessage[];
}

/**
 * The raw transcript of one note and the mode its current text reflects
 * (mirrors the backend `NoteRawTranscriptDto`).
 */
export interface NoteRawTranscriptDto {
  id: string;
  rawContent: string | null;
  content: string;
  processingMode: string;
}

/** Mirrors the backend `AssistantContextDto` field-for-field. */
interface AssistantContextDto {
  surface: string;
  route: string;
  bookId: string | null;
  bookTitle: string | null;
  bookFormat: string | null;
  readerType: string | null;
  epubCfi: string | null;
  pdfPage: number | null;
  audioTimestamp: number | null;
  audioChapter: string | null;
  selectedText: string | null;
  brainReviewNoteId: string | null;
  concept: string | null;
  collectionId: string | null;
  anchor: { kind: string; value: string | null; verified: boolean } | null;
  /**
   * Legacy wire compatibility only. V3 continuation answers travel as the real
   * message; this stays null on new-client follow-ups and the server resolves
   * the book from its continuation state.
   */
  captureBookTitle: string | null;
}

/**
 * What happens to a voice transcript (issue #262 §6).
 *
 * `auto` (the decision): the transcript enters the composer and is dispatched
 * after {@link TRANSCRIPT_AUTO_SEND_DELAY_MS}. While that grace window is open
 * an Undo cancels the dispatch BEFORE anything is sent. Pre-dispatch on purpose:
 * the app has no delete capability, so a capture must never be created wrongly
 * in the first place.
 *
 * `review`: the transcript waits in the composer until the user sends it.
 *
 * ONE SWITCH, deliberately. Both branches converge on `submit()` — the exact
 * entry point a typed message uses — so there is no second send path and no
 * mode asymmetry between an ordinary capture and a follow-up answer.
 */
export const TRANSCRIPT_SEND_POLICY: 'review' | 'auto' = 'auto';

/** The grace window before an auto-sent transcript is dispatched. */
export const TRANSCRIPT_AUTO_SEND_DELAY_MS = 2000;

/** Session-scoped persistence only; closing the browser tab ends the conversation. */
export const ASSISTANT_SESSION_STORAGE_KEY = 'nostos.ask-nostos.session.v1';

/** Shape exposed on `globalThis.__nostosAssistant` for live verification. */
export interface NostosAssistantDiagnostics {
  context: AssistantContext;
  conversationId: string;
  lastTurn: AssistantTurnResponse | null;
  suggestions: AssistantSuggestionDto[];
  pendingPlan: AssistantPendingPlanDto | null;
  /** Complete ephemeral session history; the server owns model-context packing. */
  history: AssistantHistoryMessage[];
  capturedNoteId: string | null;
}

declare global {
  // eslint-disable-next-line no-var
  var __nostosAssistant: NostosAssistantDiagnostics | undefined;
}

/** A stable per-session id, with a fallback for environments without `crypto.randomUUID`. */
function createId(): string {
  const cryptoObj = globalThis.crypto as Crypto | undefined;
  if (cryptoObj && typeof cryptoObj.randomUUID === 'function') {
    return cryptoObj.randomUUID();
  }
  return `id-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

type AssistantEventDelivery = 'sending' | 'complete' | 'retryable';

interface AssistantConversationEvent extends AssistantEntry {
  /** Whether this event belongs in the model-facing history. */
  remember: boolean;
  /** Transport state for user turns; assistant/server events are complete. */
  delivery: AssistantEventDelivery;
  /** Compact app snapshot owned by the user-turn root of this event group. */
  historyContext: AssistantHistoricalContextDto | null;
  /** Compact source handles surfaced by the completed turn. */
  historyEvidence: AssistantHistoricalEvidenceDto[];
  /** Canonical immediate actions reported as completed by the backend. */
  historyActions: string[];
  /** Captured note identity when this turn created one. */
  historyCapturedNoteId: string | null;
}

interface PreparedAssistantTurn {
  turnId: string;
  text: string;
  request: AssistantTurnRequestDto;
  context: AssistantContext;
  displayAnchor: AssistantAnchor | null;
  userEntryId: string;
  /** Restored if delivery fails while answering a deterministic continuation. */
  continuationPrompt: AssistantAnchorPrompt | null;
}

interface PersistedAssistantSession {
  version: 1;
  conversationId: string;
  eventLedger: AssistantConversationEvent[];
  draft: string;
  pendingAnchor: AssistantAnchorPrompt | null;
  pendingContinuationContext: AssistantContext | null;
  pendingPlan: AssistantPendingPlanDto | null;
  suggestions: AssistantSuggestionDto[];
  capturedNoteId: string | null;
  /**
   * A request persisted before transport begins. On reload it is treated as
   * delivery-uncertain and can be retried with the exact same TurnId.
   */
  retryableTurn: PreparedAssistantTurn | null;
}

@Injectable({ providedIn: 'root' })
export class AssistantService {
  private readonly contextService = inject(AssistantContextService);
  private readonly http = inject(HttpClient);

  /** Stable working-conversation identity. Close/reopen never changes it. */
  readonly conversationId = signal(createId());

  readonly isOpen = signal(false);
  readonly draft = signal('');
  private readonly eventLedger = signal<AssistantConversationEvent[]>([]);
  /**
   * Visible transcript projected from the same canonical event ledger that
   * supplies model history. UI event ids remain distinct from TurnIds.
   */
  readonly entries = computed<AssistantEntry[]>(() =>
    this.eventLedger().map(
      ({
        remember: _remember,
        delivery: _delivery,
        historyContext: _historyContext,
        historyEvidence: _historyEvidence,
        historyActions: _historyActions,
        historyCapturedNoteId: _historyCapturedNoteId,
        ...entry
      }) => entry,
    ),
  );
  readonly sending = signal(false);
  readonly lastError = signal<string | null>(null);

  /** The most recent turn, for the transcript and live verification. */
  readonly lastTurn = signal<AssistantTurnResponse | null>(null);
  /** Non-mutating concept suggestions for the current turn. */
  readonly suggestions = signal<AssistantSuggestionDto[]>([]);
  /** The destructive plan (if any) waiting for explicit approval. */
  readonly pendingPlan = signal<AssistantPendingPlanDto | null>(null);
  /**
   * Natural "yes / go ahead" is accepted only on the immediate confirmation
   * turn. Any intervening ordinary message disarms this shortcut so a later,
   * unrelated "yes" cannot approve stale destructive work. The explicit
   * confirmation button remains available while the plan itself is pending.
   */
  private readonly directPlanApprovalArmed = signal(false);
  /**
   * Emits only from backend-reported successful immediate Act calls. Consumers
   * can update their local surface state from execution truth without parsing
   * the assistant's prose.
   */
  readonly actionExecuted = new Subject<{ capability: string; context: AssistantContext }>();

  /** True while an auto transcript is waiting out its Undo window. */
  readonly autoSendPending = signal(false);
  private autoSendTimer: ReturnType<typeof setTimeout> | null = null;

  /** A deterministic follow-up awaiting the user's next real turn. */
  readonly pendingAnchor = signal<AssistantAnchorPrompt | null>(null);
  private readonly pendingContinuationContext = signal<AssistantContext | null>(null);

  /**
   * The one logical turn whose HTTP result is uncertain. Retrying the unchanged
   * draft resends this exact request and TurnId; editing starts a new turn.
   */
  private readonly retryableTurn = signal<PreparedAssistantTurn | null>(null);

  /**
   * Model history is a projection of the canonical event ledger. A transport-
   * uncertain user event stays visible but is excluded until that same TurnId
   * completes, so the next genuinely new turn never pretends delivery was known.
   */
  readonly history = computed<AssistantHistoryMessage[]>(() =>
    this.eventLedger()
      .filter((event) => event.remember && event.delivery !== 'retryable')
      .map((event) => ({
        role: event.kind === 'user' ? 'user' : 'assistant',
        text: event.text,
        context: event.kind === 'user' ? event.historyContext : null,
        evidence: event.historyEvidence.length > 0 ? event.historyEvidence : undefined,
        actions: event.historyActions.length > 0 ? event.historyActions : undefined,
        capturedNoteId: event.historyCapturedNoteId,
      })),
  );

  /**
   * The note the last turn captured, if any (issue #262 §8). Its raw transcript
   * is what the surface can show and restore; nothing is shown when it is null.
   */
  readonly capturedNoteId = signal<string | null>(null);
  /** True while the captured note's raw-transcript view is open. */
  readonly rawOpen = signal(false);
  /** The fetched raw transcript, once loaded. */
  readonly rawTranscript = signal<NoteRawTranscriptDto | null>(null);
  readonly rawLoading = signal(false);

  /** The user removed the ambient anchor for this session (saves as unknown). */
  private readonly anchorDismissed = signal(false);

  /** The live snapshot; a provider registry resolves the first non-null value. */
  readonly context = computed<AssistantContext>(() => this.contextService.context());

  /** The known-location chip, hidden once dismissed. */
  readonly anchorChip = computed<{ label: string; anchor: AssistantAnchor } | null>(() => {
    if (this.anchorDismissed()) return null;
    const context = this.context();
    if (!context.anchor) return null;
    const label = this.anchorLabel(context);
    if (!label) return null;
    return { label, anchor: context.anchor };
  });

  constructor() {
    this.restoreSession();

    // The repo verifies UI by reading handles in a live browser; expose the
    // resolved context, the last turn, the suggestions, the pending plan and the
    // complete ephemeral history. The same reactive read also persists the
    // current tab-scoped conversation to sessionStorage.
    effect(() => {
      globalThis.__nostosAssistant = {
        context: this.context(),
        conversationId: this.conversationId(),
        lastTurn: this.lastTurn(),
        suggestions: this.suggestions(),
        pendingPlan: this.pendingPlan(),
        history: this.history(),
        capturedNoteId: this.capturedNoteId(),
      };
      this.persistSession();
    });
  }

  open(): void {
    this.anchorDismissed.set(false);
    this.lastError.set(null);
    this.isOpen.set(true);
  }

  close(): void {
    // Closing abandons a pending auto-send as well as a live recording: nothing
    // is dispatched behind a surface the user can no longer Undo from. The
    // transcript and a pending follow-up are the conversation, not the panel, so
    // they survive: the user can step away to find the page and answer without
    // losing the thought that is waiting on it.
    this.cancelAutoSend();
    this.isOpen.set(false);
  }

  toggle(): void {
    if (this.isOpen()) this.close();
    else this.open();
  }

  /**
   * Deterministic conversation reset foundation for #560. The explicit UI action
   * can be added later; this method already defines the state boundary. It does
   * not persist or create a named/durable chat.
   */
  newConversation(): void {
    if (this.sending()) return;

    this.cancelAutoSend();
    this.anchorDismissed.set(false);
    this.conversationId.set(createId());
    this.eventLedger.set([]);
    this.retryableTurn.set(null);
    this.pendingAnchor.set(null);
    this.pendingContinuationContext.set(null);
    this.pendingPlan.set(null);
    this.directPlanApprovalArmed.set(false);
    this.suggestions.set([]);
    this.lastTurn.set(null);
    this.lastError.set(null);
    this.draft.set('');
    this.capturedNoteId.set(null);
    this.rawOpen.set(false);
    this.rawTranscript.set(null);
    this.rawLoading.set(false);
  }

  updateDraft(value: string): void {
    const retry = this.retryableTurn();
    if (retry && value.trim() !== retry.text) {
      // Editing an uncertain delivery is a new logical turn. The old visible
      // event remains marked uncertain and is never promoted into model history.
      this.retryableTurn.set(null);
    }
    this.draft.set(value);
  }

  /**
   * A finished voice transcript enters here and nowhere else. It lands in the
   * composer exactly as if it had been typed; under `auto` it is then dispatched
   * after the grace window, through the one shared `submit()`.
   */
  insertTranscript(text: string): void {
    const transcript = text.trim();
    if (!transcript) return;

    const current = this.draft().trim();
    this.updateDraft(current ? `${current} ${transcript}` : transcript);

    if (TRANSCRIPT_SEND_POLICY === 'auto') this.scheduleAutoSend();
  }

  /**
   * Pre-dispatch Undo: stop the pending auto-send before anything is sent. The
   * transcript stays in the composer, editable, exactly where the user can fix
   * it — which is the point, because a created capture cannot be deleted.
   */
  undoTranscript(): void {
    this.cancelAutoSend();
  }

  private scheduleAutoSend(): void {
    this.cancelAutoSend();
    this.autoSendPending.set(true);
    this.autoSendTimer = setTimeout(() => {
      this.autoSendTimer = null;
      this.autoSendPending.set(false);
      this.submit();
    }, TRANSCRIPT_AUTO_SEND_DELAY_MS);
  }

  private cancelAutoSend(): void {
    if (this.autoSendTimer !== null) {
      clearTimeout(this.autoSendTimer);
      this.autoSendTimer = null;
    }
    this.autoSendPending.set(false);
  }

  /**
   * Enter submits. A deterministic follow-up answer is a genuine new user turn:
   * its own Message + TurnId reference the server-held continuation instead of
   * replaying the original capture text in a mutated context.
   */
  submit(): void {
    this.cancelAutoSend();
    const text = this.draft().trim();
    if (!text || this.sending()) return;

    const pending = this.pendingAnchor();
    if (pending) {
      const context = this.pendingContinuationContext() ?? this.context();
      this.pendingAnchor.set(null);
      this.pendingContinuationContext.set(null);
      this.draft.set('');
      this.dispatchContinuation(text, pending, context, false);
      return;
    }

    const plan = this.pendingPlan();
    if (plan && isExplicitPlanRejection(text)) {
      // A clear rejection is terminal in the client: discard the token and plan
      // so nothing in a later conversation can accidentally approve it.
      this.draft.set('');
      const turnId = createId();
      this.pushEntry(turnId, 'user', text, null, null);
      this.pendingPlan.set(null);
      this.directPlanApprovalArmed.set(false);
      const reply = 'Okay. I won\'t make that change.';
      this.pushEntry(turnId, 'assistant', reply, null, 'Cancelled');
      return;
    }

    if (plan && this.directPlanApprovalArmed() && isExplicitPlanApproval(text)) {
      // A short, unambiguous confirmation on the immediate confirmation turn
      // executes only the exact server-held plan id + token.
      this.draft.set('');
      const turnId = createId();
      this.pushEntry(turnId, 'user', text, null, null);
      this.directPlanApprovalArmed.set(false);
      this.approvePlan(plan.planId, plan.approvalToken, turnId);
      return;
    }

    if (plan) this.directPlanApprovalArmed.set(false);

    const context = this.context();
    const anchor = this.effectiveAnchor(context);
    this.draft.set('');

    // Same unchanged draft after an uncertain delivery is the same logical turn.
    // dispatchTurn recognizes it and reuses the prepared request + TurnId.
    this.dispatchTurn(text, anchor);
  }

  /**
   * "I don't know" is an explicit deterministic continuation turn for page /
   * timestamp prompts. Book questions remain deliberately non-skippable.
   */
  skipAnchor(): void {
    const pending = this.pendingAnchor();
    if (!pending || pending.kind === 'book' || this.sending()) return;

    const context = this.pendingContinuationContext() ?? this.context();
    this.pendingAnchor.set(null);
    this.pendingContinuationContext.set(null);
    this.draft.set('');
    this.dispatchContinuation("I don't know", pending, context, true);
  }

  /** Remove a wrong ambient anchor for this session. */
  dismissAnchor(): void {
    this.anchorDismissed.set(true);
  }

  /**
   * Open (and load) or close the raw-transcript view for the note the last turn
   * captured (issue #262 §8). The raw words are kept server-side, so the original
   * stays readable after any mode processed it.
   */
  toggleRawTranscript(): void {
    const noteId = this.capturedNoteId();
    if (!noteId) return;

    if (this.rawOpen()) {
      this.rawOpen.set(false);
      return;
    }

    this.rawOpen.set(true);
    this.loadRawTranscript(noteId);
  }

  /** Fetch one note's raw transcript and the mode its text currently reflects. */
  loadRawTranscript(noteId: string): void {
    this.rawLoading.set(true);
    this.http.get<NoteRawTranscriptDto>(`/api/notes/${noteId}/raw`).subscribe({
      next: (raw) => {
        this.rawLoading.set(false);
        this.rawTranscript.set(raw);
      },
      error: () => {
        this.rawLoading.set(false);
        this.rawOpen.set(false);
        this.lastError.set('The original text could not be loaded.');
      },
    });
  }

  /**
   * Restore the captured note's text from its raw transcript. The transcript
   * itself is never erased: restoring is not a way to lose the capture.
   */
  restoreRawTranscript(): void {
    const noteId = this.capturedNoteId();
    if (!noteId || this.sending()) return;

    this.sending.set(true);
    this.http.post<NoteRawTranscriptDto>(`/api/notes/${noteId}/raw/restore`, {}).subscribe({
      next: (restored) => {
        this.sending.set(false);
        this.lastError.set(null);
        this.rawTranscript.set(restored);
        this.pushEntry(createId(), 'assistant', 'Original text restored.', null, 'Restored');
      },
      error: () => {
        this.sending.set(false);
        this.lastError.set('The original text could not be restored.');
      },
    });
  }

  /**
   * The Brain review affordance: open the assistant and ask it where the
   * reviewed note belongs. The review-note context is supplied by the Second
   * Brain's provider, so the turn knows which note is under review.
   */
  requestSuggestions(prompt = 'Where do you think this belongs?'): void {
    this.open();
    this.updateDraft(prompt);
    this.submit();
  }

  /**
   * Choose a non-mutating suggestion. The click is the user's explicit choice,
   * so the resulting existing-concept link may execute through the normal Act
   * path without asking for a second approval.
   */
  applySuggestion(suggestion: AssistantSuggestionDto): void {
    if (suggestion.kind !== 'concept' || !suggestion.value) return;

    if (!this.context().brainReviewNoteId) {
      this.lastError.set('Open the note in the Second Brain so the link has a target.');
      return;
    }

    const context = this.context();
    this.dispatchTurn(
      `Link the note I am reviewing to the existing concept “${suggestion.label}”.`,
      this.effectiveAnchor(context),
    );
  }

  /** "None of these": leave the note unlinked, with no error. */
  dismissSuggestions(): void {
    this.suggestions.set([]);
  }

  /**
   * Execute exactly one pending destructive plan. The server keeps a bounded
   * terminal receipt, so retrying the exact plan id + token after a lost HTTP
   * response reports the original result without executing it twice.
   */
  approvePlan(planId: string, approvalToken: string, turnId = createId()): void {
    if (!planId || !approvalToken || this.sending()) return;

    const plan = this.pendingPlan();
    this.directPlanApprovalArmed.set(false);
    this.sending.set(true);
    this.http
      .post<AssistantPlanApproveResponse>('/api/assistant/plan/approve', { planId, approvalToken })
      .subscribe({
        next: (response) => {
          this.sending.set(false);
          this.lastError.set(null);
          if (response.success) {
            this.pendingPlan.set(null);
            const replies = response.steps
              .map((step) => {
                if (!step.data || typeof step.data !== 'object') return null;
                const reply = (step.data as { reply?: unknown }).reply;
                return typeof reply === 'string' && reply.trim() ? reply.trim() : null;
              })
              .filter((reply): reply is string => reply !== null);
            const executionReply =
              replies.length > 0 ? replies.join(' ') : (plan?.summary ?? 'Plan applied.');
            this.pushEntry(turnId, 'assistant', executionReply, null, 'Applied');
          } else {
            const failureReply =
              response.errorMessage ?? plan?.summary ?? 'The plan could not be applied.';
            this.lastError.set(failureReply);
            this.pushEntry(
              turnId,
              'error',
              failureReply,
              null,
              response.errorCode ?? 'Refused',
            );
          }
        },
        error: () => {
          this.sending.set(false);
          this.lastError.set('The plan could not be applied. It is still waiting for approval.');
        },
      });
  }

  private dispatchTurn(
    text: string,
    anchor: AssistantAnchor | null,
    captureBookTitle: string | null = null,
  ): void {
    const retry = this.retryableTurn();
    if (retry && retry.text === text && retry.request.continuationId === null) {
      this.draft.set('');
      this.sendPreparedTurn(retry);
      return;
    }

    this.retryableTurn.set(null);
    const context = this.context();
    this.startPreparedTurn({
      text,
      context,
      requestAnchor: anchor,
      captureBookTitle,
      continuationPrompt: null,
      continuationSkipped: false,
      displayAnchor: anchor,
    });
  }

  private dispatchContinuation(
    text: string,
    prompt: AssistantAnchorPrompt,
    context: AssistantContext,
    skipped: boolean,
  ): void {
    const retry = this.retryableTurn();
    if (
      retry &&
      retry.text === text &&
      retry.request.continuationId === prompt.continuationId &&
      retry.request.continuationSkipped === skipped
    ) {
      this.sendPreparedTurn(retry);
      return;
    }

    this.retryableTurn.set(null);
    const displayAnchor: AssistantAnchor | null =
      prompt.kind === 'book'
        ? null
        : skipped
          ? { kind: 'unknown', value: null, verified: false }
          : this.anchorFromAnswer(prompt.kind, text);

    // The answer itself is NOT encoded in context. The stored continuation is
    // authoritative; context here remains the user's application snapshot.
    this.startPreparedTurn({
      text,
      context,
      requestAnchor: this.effectiveAnchor(context),
      captureBookTitle: null,
      continuationPrompt: prompt,
      continuationSkipped: skipped,
      displayAnchor,
    });
  }

  private startPreparedTurn(options: {
    text: string;
    context: AssistantContext;
    requestAnchor: AssistantAnchor | null;
    captureBookTitle: string | null;
    continuationPrompt: AssistantAnchorPrompt | null;
    continuationSkipped: boolean;
    displayAnchor: AssistantAnchor | null;
  }): void {
    const turnId = createId();
    const conversationId = this.conversationId();
    const history = this.history();
    const userEntryId = this.pushEntry(
      turnId,
      'user',
      options.text,
      null,
      null,
      [],
      true,
      'sending',
    );

    const request: AssistantTurnRequestDto = {
      clientId: conversationId,
      idempotencyKey: turnId,
      conversationId,
      turnId,
      message: options.text,
      context: toContextDto(
        options.context,
        options.requestAnchor,
        options.captureBookTitle,
      ),
      continuationId: options.continuationPrompt?.continuationId ?? null,
      continuationSkipped: options.continuationSkipped,
      history,
    };

    this.sendPreparedTurn({
      turnId,
      text: options.text,
      request,
      context: options.context,
      displayAnchor: options.displayAnchor,
      userEntryId,
      continuationPrompt: options.continuationPrompt,
    });
  }

  private sendPreparedTurn(turn: PreparedAssistantTurn): void {
    this.updateUserDelivery(turn.userEntryId, 'sending', null);
    this.sending.set(true);

    this.http.post<AssistantTurnResponse>('/api/assistant/turn', turn.request).subscribe({
      next: (response) => {
        this.sending.set(false);
        if (this.retryableTurn()?.turnId === turn.turnId) this.retryableTurn.set(null);
        this.updateUserDelivery(turn.userEntryId, 'complete', null);
        this.lastError.set(response.error?.message ?? null);
        this.lastTurn.set(response);
        this.suggestions.set(response.suggestions ?? []);

        if (response.pendingPlan) {
          this.pendingPlan.set(response.pendingPlan);
          this.directPlanApprovalArmed.set(true);
        }

        for (const capability of response.executedCapabilities ?? []) {
          this.actionExecuted.next({ capability, context: turn.context });
        }

        this.capturedNoteId.set(response.capturedNoteId ?? null);
        this.rawOpen.set(false);
        this.rawTranscript.set(null);
        this.rawLoading.set(false);

        if (response.acknowledgement) {
          this.pushEntry(
            turn.turnId,
            'assistant',
            response.acknowledgement,
            this.anchorLabel({ ...turn.context, anchor: turn.displayAnchor }),
            'Saved',
          );
        }

        let promptText: string | null = null;
        if (response.anchorPrompt) {
          const kind = response.anchorPrompt.kind;
          const continuationId = response.anchorPrompt.continuationId?.trim();
          if (
            continuationId &&
            (kind === 'physical_page' ||
              kind === 'external_audio_timestamp' ||
              kind === 'book')
          ) {
            const prompt: AssistantAnchorPrompt = {
              kind,
              question: response.anchorPrompt.question,
              continuationId,
            };
            this.pendingAnchor.set(prompt);
            this.pendingContinuationContext.set(turn.context);
            promptText = prompt.question;
            this.pushEntry(turn.turnId, 'assistant', prompt.question, null, null);
          } else {
            this.pendingAnchor.set(null);
            this.pendingContinuationContext.set(null);
            this.lastError.set(
              response.error?.message ??
                'The assistant requested follow-up input without a valid continuation.',
            );
          }
        } else {
          this.pendingAnchor.set(null);
          this.pendingContinuationContext.set(null);
        }

        if (
          response.reply &&
          (!promptText || response.reply.trim() !== promptText.trim())
        ) {
          this.pushEntry(
            turn.turnId,
            response.error ? 'error' : 'assistant',
            response.reply,
            null,
            response.error?.code ?? null,
            response.sources ?? [],
          );
        }
      },
      error: () => {
        this.sending.set(false);
        this.retryableTurn.set(turn);
        this.lastError.set(
          'The assistant could not be reached. Your message is still in the composer to retry.',
        );
        this.draft.set(turn.text);
        this.updateUserDelivery(turn.userEntryId, 'retryable', 'Delivery uncertain');

        if (turn.continuationPrompt) {
          this.pendingAnchor.set(turn.continuationPrompt);
          this.pendingContinuationContext.set(turn.context);
        }
      },
    });
  }

  private effectiveAnchor(context: AssistantContext): AssistantAnchor | null {
    if (this.anchorDismissed()) return null;
    return context.anchor;
  }

  private anchorFromAnswer(
    kind: 'physical_page' | 'external_audio_timestamp',
    answer: string,
  ): AssistantAnchor {
    return { kind, value: normalizeAnchorAnswer(kind, answer), verified: false };
  }

  private pushEntry(
    turnId: string,
    kind: AssistantEntry['kind'],
    text: string,
    anchorLabel: string | null,
    meta: string | null,
    sources: AssistantSourceReferenceDto[] = [],
    remember = true,
    delivery: AssistantEventDelivery = 'complete',
  ): string {
    const id = createId();
    this.eventLedger.update((events) => [
      ...events,
      { id, turnId, kind, text, anchorLabel, meta, sources, remember, delivery },
    ]);
    return id;
  }

  private updateUserDelivery(
    entryId: string,
    delivery: AssistantEventDelivery,
    meta: string | null,
  ): void {
    this.eventLedger.update((events) =>
      events.map((event) =>
        event.id === entryId ? { ...event, delivery, meta } : event,
      ),
    );
  }

  /** "The Magic Mountain · p. 183", from the resolved context or the answer. */
  private anchorLabel(context: AssistantContext): string | null {
    const anchor = context.anchor;
    if (!anchor || anchor.kind === 'unknown') return null;
    const title = context.bookTitle ?? 'This book';
    switch (anchor.kind) {
      case 'pdf_page':
      case 'physical_page':
        return `${title} · p. ${anchor.value}`;
      case 'epub_cfi':
        return `${title} · reading position`;
      case 'audio_timestamp':
      case 'external_audio_timestamp':
        return `${title} · ${formatTimestamp(anchor.value)}`;
      default:
        return title;
    }
  }
}

/**
 * True only for a complete, short approval utterance while one destructive
 * plan is visibly pending. Deliberately exact rather than fuzzy: "yes, but
 * explain first" is discussion, not permission to delete anything.
 *
 * Voice transcripts use the same submit path, so "go ahead" spoken aloud has
 * the same semantics as typing it.
 */
export function isExplicitPlanApproval(value: string): boolean {
  const normalized = value
    .trim()
    .toLowerCase()
    .replace(/[.!]+$/g, '')
    .replace(/\s+/g, ' ');

  return new Set([
    'yes',
    'yes please',
    'yes, please',
    'yes do it',
    'yes, do it',
    'yes go ahead',
    'yes, go ahead',
    'go ahead',
    'do it',
    'confirm',
  ]).has(normalized);
}

/** Exact rejection vocabulary for the visible pending destructive change. */
export function isExplicitPlanRejection(value: string): boolean {
  const normalized = value
    .trim()
    .toLowerCase()
    .replace(/[.!]+$/g, '')
    .replace(/\s+/g, ' ');

  return new Set([
    'no',
    'no thanks',
    'no, thanks',
    'cancel',
    'cancel it',
    'don\'t',
    'do not',
    'never mind',
    'nevermind',
  ]).has(normalized);
}

/**
 * Builds the wire context for an ordinary turn. Deterministic follow-up answers
 * no longer travel here: they are real Message values tied to a continuation.
 */
function toContextDto(
  context: AssistantContext,
  anchor: AssistantAnchor | null,
  captureBookTitle: string | null = null,
): AssistantContextDto {
  return {
    surface: context.surface,
    route: context.route,
    bookId: context.bookId,
    bookTitle: context.bookTitle,
    bookFormat: context.bookFormat,
    readerType: context.readerType,
    epubCfi: context.epubCfi,
    pdfPage: context.pdfPage,
    audioTimestamp: context.audioTimestamp,
    audioChapter: context.audioChapter,
    selectedText: context.selectedText,
    brainReviewNoteId: context.brainReviewNoteId,
    concept: context.concept,
    collectionId: context.collectionId,
    anchor: anchor ? { kind: anchor.kind, value: anchor.value, verified: anchor.verified } : null,
    captureBookTitle,
  };
}

/**
 * Normalize a page/timestamp for local acknowledgement display. The server
 * independently performs the same deterministic normalization and remains the
 * authority for what is stored; this helper never chooses the capture target.
 */
export function normalizeAnchorAnswer(
  kind: 'physical_page' | 'external_audio_timestamp',
  answer: string,
): string {
  const trimmed = answer.trim();
  if (kind === 'physical_page') {
    // "Page 247." / "p. 247" / "247" -> "247". Anything else is left as the
    // user said it rather than guessing which number they meant.
    const cleaned = trimmed.replace(/[.\s]+$/, '');
    const match = /^(?:page|p\.?)\s*(\d+)$/i.exec(cleaned);
    return match ? match[1] : cleaned;
  }
  return parseTimestamp(trimmed) ?? trimmed;
}

/** `1:23:45` / `1:23` / `83` (seconds) to whole seconds; null when unparseable. */
export function parseTimestamp(value: string): string | null {
  const parts = value.trim().split(':');
  if (parts.length === 0 || parts.length > 3) return null;
  if (parts.some((part) => !/^\d+$/.test(part.trim()))) return null;
  const seconds = parts
    .map((part) => Number(part.trim()))
    .reduce((total, part) => total * 60 + part, 0);
  return String(seconds);
}

/**
 * The history cap (issue #286). Keeps the last {@link HISTORY_MAX_EXCHANGES}
 * user turns and everything from the earliest of those onward, so each kept
 * user turn brings the assistant turn that answered it. Every message is
 * truncated to {@link HISTORY_MAX_CHARS} rather than dropped.
 */
export function capHistory(
  log: readonly AssistantHistoryMessage[],
): AssistantHistoryMessage[] {
  const userTurns = log
    .map((message, index) => (message.role === 'user' ? index : -1))
    .filter((index) => index >= 0);
  const start =
    userTurns.length > HISTORY_MAX_EXCHANGES
      ? userTurns[userTurns.length - HISTORY_MAX_EXCHANGES]
      : 0;

  return log.slice(start).map((message) => ({
    role: message.role,
    text: message.text.slice(0, HISTORY_MAX_CHARS),
  }));
}

/** Seconds (as a string) to `m:ss` / `h:mm:ss`. Null-safe and never NaN-y. */
export function formatTimestamp(value: string | null): string {
  const seconds = Math.max(0, Math.floor(Number(value ?? '0')));
  if (!Number.isFinite(seconds)) return '0:00';
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = seconds % 60;
  if (hours > 0) return `${hours}:${String(minutes).padStart(2, '0')}:${String(secs).padStart(2, '0')}`;
  return `${minutes}:${String(secs).padStart(2, '0')}`;
}
