/**
 * Assistant conversation/session state (issue #261 §3, §5, §6, §7).
 *
 * The backend bridge (`POST /api/assistant/turn/stream`) owns the LLM and the tool
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
import { HttpClient, HttpEventType } from '@angular/common/http';
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
  suggestions?: AssistantSuggestionDto[];
  /** Inert historical truth owned by this exact TurnId. */
  artifacts?: AssistantTurnArtifact[];
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

/** Exact #562 identity for canonical evidence; never mutation authority. */
export interface AssistantEvidenceHandleDto {
  kind: 'note' | 'concept' | 'book_text' | string;
  noteId?: string | null;
  conceptId?: string | null;
  bookId?: string | null;
  sourceSha256?: string | null;
  extractorVersion?: string | null;
  ordinal?: number | null;
}

/** Bounded display data paired with the canonical handle. */
export interface AssistantEvidenceReferenceDto {
  handle: AssistantEvidenceHandleDto;
  label: string;
  excerpt?: string | null;
  bookTitle?: string | null;
  bookAuthor?: string | null;
  format?: string | null;
  locators?: AssistantSourceLocatorDto[] | null;
}

/**
 * Historical turn outcomes. These values are inert: restoring them never
 * executes a capability, replays a capture, or approves a plan.
 */
export type AssistantTurnArtifact =
  | { kind: 'evidence'; evidence: AssistantEvidenceReferenceDto }
  | { kind: 'capture'; noteId: string; acknowledgement: string | null; state: 'saved' }
  | { kind: 'action'; capability: string; state: 'completed' }
  | { kind: 'failure'; code: string; message: string; retryable: boolean; state: 'failed' | 'cancelled' }
  | { kind: 'proposal'; proposal: AssistantSuggestionDto }
  | {
      kind: 'destructive-result';
      planId: string;
      summary: string;
      outcome: 'applied' | 'refused' | 'failed' | 'superseded';
      capabilities: string[];
    };

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
  /** Exact canonical handles for #565 turns; excerpts stay out of history. */
  evidenceHandles?: AssistantEvidenceHandleDto[];
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
  noteId?: string | null;
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

export interface AssistantTurnActivityDto {
  code: string;
  message: string;
}

export interface AssistantTurnFailureDto {
  code: string;
  message: string;
  retryable: boolean;
}

export interface AssistantTurnEventDto {
  turnId: string;
  sequence: number;
  kind: 'started' | 'activity' | 'completed' | 'failed' | 'cancelled' | string;
  activity?: AssistantTurnActivityDto | null;
  failure?: AssistantTurnFailureDto | null;
  response?: AssistantTurnResponse | null;
}

export const ASSISTANT_PENDING_DELAY_MS = 350;

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
  /** Canonical material found/used by this turn, with exact #562 handles. */
  evidence?: AssistantEvidenceReferenceDto[];
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
   * The completed session ledger from BEFORE this logical turn. The browser
   * sends it intact; the server owns the budgeted selection that reaches the
   * model, so this field is not itself a provider-context contract.
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
  /** Captured note identity when this turn created one (legacy v1 restore fallback). */
  historyCapturedNoteId: string | null;
  /** Durable, inert results for this exact TurnId. */
  artifacts: AssistantTurnArtifact[];
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
  /** Turn that proposed the currently active destructive plan. */
  pendingPlanTurnId: string | null;
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
  /** Exact logical turn currently executing through the streamed transport. */
  readonly activeTurnId = signal<string | null>(null);
  /** Product-owned activity only; never model reasoning or raw tool data. */
  readonly turnActivity = signal<AssistantTurnActivityDto | null>(null);
  /** Delayed so fast/simple turns complete without flashing progress chrome. */
  readonly pendingVisible = signal(false);
  /** Transport-local turn id; Stop is exposed only after server 'started'. */
  private inFlightTurnId: string | null = null;
  private pendingTimer: ReturnType<typeof setTimeout> | null = null;

  /** The most recent turn, for the transcript and live verification. */
  readonly lastTurn = signal<AssistantTurnResponse | null>(null);
  /** Non-mutating concept suggestions for the current turn. */
  readonly suggestions = signal<AssistantSuggestionDto[]>([]);
  /** The destructive plan (if any) waiting for explicit approval. */
  readonly pendingPlan = signal<AssistantPendingPlanDto | null>(null);
  /** Exact TurnId that produced the active server-held destructive proposal. */
  readonly pendingPlanTurnId = signal<string | null>(null);
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
  /** A terminal result keyed to the originating turn, for focused surfaces such as Brain. */
  readonly turnFinished = new Subject<{
    turnId: string;
    response: AssistantTurnResponse | null;
    error: string | null;
  }>();

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
      })),
  );

  /** Enriched wire history; historical artifacts are reduced to inert reference metadata. */
  private readonly contextualHistory = computed<AssistantHistoryMessage[]>(() => {
    const ledger = this.eventLedger();
    return ledger
      .filter((event) => event.remember && event.delivery !== 'retryable')
      .map((event) => {
        const turnArtifacts = ledger
          .filter((candidate) => candidate.turnId === event.turnId)
          .flatMap((candidate) => candidate.artifacts ?? []);
        const facts = historyFactsFromArtifacts(turnArtifacts);
        return {
          role: event.kind === 'user' ? 'user' as const : 'assistant' as const,
          text: event.text,
          ...(event.kind === 'user' && event.historyContext
            ? { context: event.historyContext }
            : {}),
          // Legacy restored sessions may still carry #561 book-only evidence.
          ...(event.historyEvidence.length > 0 ? { evidence: event.historyEvidence } : {}),
          ...(facts.evidenceHandles.length > 0
            ? { evidenceHandles: facts.evidenceHandles }
            : {}),
          ...((facts.actions.length > 0 ? facts.actions : event.historyActions).length > 0
            ? { actions: facts.actions.length > 0 ? facts.actions : event.historyActions }
            : {}),
          ...(facts.capturedNoteId ?? event.historyCapturedNoteId
            ? { capturedNoteId: facts.capturedNoteId ?? event.historyCapturedNoteId }
            : {}),
        };
      });
  });

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

  private restoreSession(): void {
    const storage = sessionStorageOrNull();
    if (!storage) return;

    try {
      const raw = storage.getItem(ASSISTANT_SESSION_STORAGE_KEY);
      if (!raw) return;

      const parsed = JSON.parse(raw) as Partial<PersistedAssistantSession>;
      if (
        parsed.version !== 1 ||
        typeof parsed.conversationId !== 'string' ||
        !parsed.conversationId.trim() ||
        !Array.isArray(parsed.eventLedger)
      ) {
        storage.removeItem(ASSISTANT_SESSION_STORAGE_KEY);
        return;
      }

      this.conversationId.set(parsed.conversationId);
      const restoredEvents = parsed.eventLedger
        .filter(isRestorableConversationEvent)
        .map((event) => ({
          ...event,
          // Artifacts are inert data. Proposal buttons are active UI state and
          // intentionally do not survive a page reload.
          artifacts: Array.isArray(event.artifacts)
            ? event.artifacts.filter(isAssistantTurnArtifact)
            : [],
          suggestions: [],
          ...(event.delivery === 'sending'
            ? { delivery: 'retryable' as const, meta: 'Delivery uncertain' }
            : {}),
        }));
      this.eventLedger.set(restoredEvents);

      this.pendingAnchor.set(isAnchorPrompt(parsed.pendingAnchor) ? parsed.pendingAnchor : null);
      this.pendingContinuationContext.set(
        isAssistantContext(parsed.pendingContinuationContext)
          ? parsed.pendingContinuationContext
          : null,
      );
      this.pendingPlan.set(isPendingPlan(parsed.pendingPlan) ? parsed.pendingPlan : null);
      this.pendingPlanTurnId.set(
        this.pendingPlan() && typeof parsed.pendingPlanTurnId === 'string'
          ? parsed.pendingPlanTurnId
          : null,
      );
      this.suggestions.set([]);
      this.capturedNoteId.set(
        typeof parsed.capturedNoteId === 'string' ? parsed.capturedNoteId : null,
      );

      const restoredRetry = isPreparedTurn(parsed.retryableTurn)
        ? parsed.retryableTurn
        : null;
      this.retryableTurn.set(restoredRetry);

      if (restoredRetry) {
        // A reload can interrupt an in-flight request after the server committed
        // but before the browser received the response. Treat it as uncertain:
        // restore the exact request/TurnId and let canonical receipts decide.
        this.draft.set(restoredRetry.text);
        this.updateUserDelivery(
          restoredRetry.userEntryId,
          'retryable',
          'Delivery uncertain',
        );
        if (restoredRetry.continuationPrompt) {
          this.pendingAnchor.set(restoredRetry.continuationPrompt);
          this.pendingContinuationContext.set(restoredRetry.context);
        }
      } else {
        this.draft.set(typeof parsed.draft === 'string' ? parsed.draft : '');
      }

      // Natural-language destructive approval is intentionally NOT restored as
      // armed. The explicit confirmation button can still submit the exact
      // server-bound plan/token, while a generic "yes" after refresh cannot.
      this.directPlanApprovalArmed.set(false);
    } catch {
      storage.removeItem(ASSISTANT_SESSION_STORAGE_KEY);
    }
  }

  private persistSession(): void {
    const storage = sessionStorageOrNull();
    if (!storage) return;

    const state: PersistedAssistantSession = {
      version: 1,
      conversationId: this.conversationId(),
      eventLedger: this.eventLedger(),
      draft: this.draft(),
      pendingAnchor: this.pendingAnchor(),
      pendingContinuationContext: this.pendingContinuationContext(),
      pendingPlan: this.pendingPlan(),
      pendingPlanTurnId: this.pendingPlanTurnId(),
      // Proposal artifacts persist; executable chips do not.
      suggestions: [],
      capturedNoteId: this.capturedNoteId(),
      retryableTurn: this.retryableTurn(),
    };

    try {
      storage.setItem(ASSISTANT_SESSION_STORAGE_KEY, JSON.stringify(state));
    } catch {
      // Storage can be unavailable or full (privacy mode / browser quota).
      // Conversation still works in memory; persistence is a convenience, not
      // an execution/safety dependency.
    }
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
   * Explicit #561 conversation boundary. The header action clears the current
   * tab-scoped ledger and starts a fresh ConversationId; it never creates a
   * named, durable or server-persisted chat.
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
    this.pendingPlanTurnId.set(null);
    this.directPlanApprovalArmed.set(false);
    this.suggestions.set([]);
    this.lastTurn.set(null);
    this.lastError.set(null);
    this.draft.set('');
    this.capturedNoteId.set(null);
    this.finishTurnUi(null);
    this.rawOpen.set(false);
    this.rawTranscript.set(null);
    this.rawLoading.set(false);
    this.persistSession();
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
      this.pushEntry(
        turnId,
        'user',
        text,
        null,
        null,
        [],
        true,
        'complete',
        toHistoricalContext(this.context()),
      );
      const planTurnId = this.pendingPlanTurnId() ?? turnId;
      this.pendingPlan.set(null);
      this.pendingPlanTurnId.set(null);
      this.directPlanApprovalArmed.set(false);
      const reply = 'Okay. I won\'t make that change.';
      this.pushEntry(planTurnId, 'assistant', reply, null, 'Cancelled');
      this.upsertTurnArtifact(planTurnId, {
        kind: 'destructive-result',
        planId: plan.planId,
        summary: plan.summary,
        outcome: 'refused',
        capabilities: plan.steps.map((step) => step.capability),
      });
      this.persistSession();
      return;
    }

    if (plan && this.directPlanApprovalArmed() && isExplicitPlanApproval(text)) {
      // A short, unambiguous confirmation on the immediate confirmation turn
      // executes only the exact server-held plan id + token.
      this.draft.set('');
      const turnId = createId();
      this.pushEntry(
        turnId,
        'user',
        text,
        null,
        null,
        [],
        true,
        'complete',
        toHistoricalContext(this.context()),
      );
      this.directPlanApprovalArmed.set(false);
      this.persistSession();
      this.approvePlan(
        plan.planId,
        plan.approvalToken,
        this.pendingPlanTurnId() ?? turnId,
      );
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

  /** Request the same assistant proposal artifact while keeping the Brain note in place. */
  requestConceptProposals(noteId: string): string | null {
    const context = this.context();
    if (this.sending() || !noteId || context.brainReviewNoteId !== noteId) return null;

    return this.startPreparedTurn({
      text: `Suggest up to three existing concepts for note ${noteId}. Read the note and existing concept evidence, then explicitly propose only grounded links. If none fit, return no proposals. Do not link anything.`,
      context,
      requestAnchor: this.effectiveAnchor(context),
      captureBookTitle: null,
      continuationPrompt: null,
      continuationSkipped: false,
      displayAnchor: this.effectiveAnchor(context),
    });
  }

  /**
   * Choose a non-mutating suggestion. The click is the user's explicit choice,
   * so the resulting existing-concept link may execute through the normal Act
   * path without asking for a second approval.
   */
  applySuggestion(suggestion: AssistantSuggestionDto): string | null {
    if (this.sending() || suggestion.kind !== 'concept' || !suggestion.value) return null;

    const context = this.context();
    if (!suggestion.noteId || context.brainReviewNoteId !== suggestion.noteId) {
      this.lastError.set('Open the suggested note in Brain before linking it.');
      return null;
    }

    return this.dispatchTurn(
      `Link note ${suggestion.noteId} to the existing concept “${suggestion.label}” (ID ${suggestion.value}).`,
      this.effectiveAnchor(context),
    );
  }

  /** "None of these": leave the note unlinked, with no error. */
  dismissSuggestions(turnId?: string): void {
    if (turnId) this.eventLedger.update((events) => events.map((event) =>
      event.turnId === turnId ? { ...event, suggestions: [] } : event));
    this.suggestions.set([]);
    this.persistSession();
  }

  /** A note changed or left focus; old turn chips must not outlive that snapshot. */
  dismissSuggestionsForNote(noteId: string): void {
    this.eventLedger.update((events) => events.map((event) => ({
      ...event,
      suggestions: event.suggestions?.filter((item) => item.noteId !== noteId),
    })));
    this.suggestions.update((items) => items.filter((item) => item.noteId !== noteId));
    this.persistSession();
  }

  /**
   * Execute exactly one pending destructive plan. The server keeps a bounded
   * terminal receipt, so retrying the exact plan id + token after a lost HTTP
   * response reports the original result without executing it twice.
   */
  approvePlan(
    planId: string,
    approvalToken: string,
    turnId = this.pendingPlanTurnId() ?? createId(),
  ): void {
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
          const capabilities = (plan?.steps ?? response.steps).map((step) => step.capability);
          if (response.success) {
            this.pendingPlan.set(null);
            this.pendingPlanTurnId.set(null);
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
            this.upsertTurnArtifact(turnId, {
              kind: 'destructive-result',
              planId,
              summary: plan?.summary ?? executionReply,
              outcome: 'applied',
              capabilities,
            });
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
            this.upsertTurnArtifact(turnId, {
              kind: 'destructive-result',
              planId,
              summary: plan?.summary ?? failureReply,
              outcome: 'failed',
              capabilities,
            });
            this.upsertTurnArtifact(turnId, {
              kind: 'failure',
              code: response.errorCode ?? 'assistant_plan_refused',
              message: failureReply,
              retryable: false,
              state: 'failed',
            });
          }
          this.persistSession();
        },
        error: () => {
          this.sending.set(false);
          const message = 'The plan could not be applied. It is still waiting for approval.';
          this.lastError.set(message);
          this.upsertTurnArtifact(turnId, {
            kind: 'failure',
            code: 'assistant_network_error',
            message,
            retryable: true,
            state: 'failed',
          });
          this.persistSession();
        },
      });
  }

  private dispatchTurn(
    text: string,
    anchor: AssistantAnchor | null,
    captureBookTitle: string | null = null,
  ): string {
    const retry = this.retryableTurn();
    if (retry && retry.text === text && retry.request.continuationId === null) {
      this.draft.set('');
      this.sendPreparedTurn(retry);
      return retry.turnId;
    }

    this.retryableTurn.set(null);
    const context = this.context();
    return this.startPreparedTurn({
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
  }): string {
    const turnId = createId();
    const conversationId = this.conversationId();
    const history = this.contextualHistory();
    const userEntryId = this.pushEntry(
      turnId,
      'user',
      options.text,
      null,
      null,
      [],
      true,
      'sending',
      toHistoricalContext(options.context),
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
    return turnId;
  }

  private sendPreparedTurn(turn: PreparedAssistantTurn): void {
    this.updateUserDelivery(turn.userEntryId, 'sending', null);
    this.retryableTurn.set(turn);
    this.sending.set(true);
    this.beginTurnUi(turn.turnId);

    // Persist before transport begins. A reload while the HTTP result is
    // ambiguous can then retry this exact logical TurnId instead of inventing a
    // new turn and defeating #560's mutation receipts.
    this.persistSession();

    let receivedCharacters = 0;
    let pendingText = '';
    let lastSequence = 0;
    let terminalHandled = false;

    const handleValue = (value: unknown): void => {
      if (terminalHandled) return;

      // Unit/backward compatibility: a complete response object is treated as
      // one completed terminal event. Production uses the event envelope.
      if (isAssistantTurnResponse(value)) {
        terminalHandled = true;
        this.applyTurnResponse(turn, value);
        this.finishTurnUi(turn.turnId);
        this.turnFinished.next({ turnId: turn.turnId, response: value, error: value.error?.message ?? null });
        return;
      }

      if (!isAssistantTurnEvent(value)
          || value.turnId !== turn.turnId
          || value.sequence <= lastSequence) {
        return;
      }

      lastSequence = value.sequence;

      if (value.kind === 'started') {
        this.acknowledgeTurnStarted(turn.turnId);
        return;
      }

      if (value.kind === 'activity' && value.activity) {
        this.showTurnActivity(turn.turnId, value.activity);
        return;
      }

      if (value.kind === 'completed' && value.response) {
        terminalHandled = true;
        this.applyTurnResponse(turn, value.response);
        this.finishTurnUi(turn.turnId);
        this.turnFinished.next({ turnId: turn.turnId, response: value.response, error: value.response.error?.message ?? null });
        return;
      }

      if (value.kind === 'failed') {
        terminalHandled = true;
        this.handleTerminalFailure(turn, value.failure, value.response ?? null);
        return;
      }

      if (value.kind === 'cancelled') {
        terminalHandled = true;
        this.handleTerminalCancellation(turn, value.failure, value.response ?? null);
      }
    };

    const consumeCumulative = (text: string, final: boolean): void => {
      const delta = text.slice(receivedCharacters);
      receivedCharacters = text.length;
      pendingText += delta;

      const lines = pendingText.split('\n');
      pendingText = lines.pop() ?? '';
      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed) continue;
        try {
          handleValue(JSON.parse(trimmed));
        } catch {
          // A malformed product-event line is a transport failure, not a reason
          // to expose its raw contents in the transcript.
        }
      }

      if (final && pendingText.trim()) {
        try {
          handleValue(JSON.parse(pendingText.trim()));
        } catch {
          // Handled below as an incomplete/invalid transport.
        }
        pendingText = '';
      }
    };

    this.http.post('/api/assistant/turn/stream', turn.request, {
      observe: 'events',
      reportProgress: true,
      responseType: 'text',
    }).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.DownloadProgress) {
          consumeCumulative(event.partialText ?? '', false);
          return;
        }

        if (event.type === HttpEventType.Response) {
          const body = typeof event.body === 'string'
            ? event.body
            : JSON.stringify(event.body ?? '');
          consumeCumulative(body, true);

          if (!terminalHandled) {
            this.handleTransportFailure(
              turn,
              'assistant_network_error',
              'The Ask Nostos response ended unexpectedly. Your message is ready to retry.',
            );
          }
        }
      },
      error: () => {
        if (terminalHandled) return;
        const offline =
          typeof globalThis.navigator !== 'undefined'
          && globalThis.navigator.onLine === false;
        this.handleTransportFailure(
          turn,
          offline ? 'assistant_offline' : 'assistant_network_error',
          offline
            ? 'You are offline. Reconnect and retry this message.'
            : 'The connection to Ask Nostos was interrupted. Your message is ready to retry.',
        );
      },
    });
  }

  /** Stop only the currently visible logical turn. No new turn is created. */
  stopActiveTurn(): void {
    const turnId = this.activeTurnId();
    if (!turnId || !this.sending()) return;

    this.http.post('/api/assistant/turn/cancel', {
      conversationId: this.conversationId(),
      turnId,
    }).subscribe({
      error: () => {
        this.lastError.set('Ask Nostos could not send the Stop request.');
      },
    });
  }

  private beginTurnUi(turnId: string): void {
    this.clearPendingTimer();
    this.inFlightTurnId = turnId;
    this.activeTurnId.set(null);
    this.turnActivity.set(null);
    this.pendingVisible.set(false);
    this.pendingTimer = setTimeout(() => {
      this.pendingTimer = null;
      if (this.sending() && this.inFlightTurnId === turnId) {
        this.pendingVisible.set(true);
      }
    }, ASSISTANT_PENDING_DELAY_MS);
  }

  private acknowledgeTurnStarted(turnId: string): void {
    if (this.inFlightTurnId !== turnId || !this.sending()) return;
    this.activeTurnId.set(turnId);
  }

  private showTurnActivity(turnId: string, activity: AssistantTurnActivityDto): void {
    if (this.inFlightTurnId !== turnId) return;
    this.clearPendingTimer();
    this.turnActivity.set(activity);
    this.pendingVisible.set(true);
  }

  private finishTurnUi(turnId: string | null): void {
    if (turnId !== null && this.inFlightTurnId !== turnId) return;
    this.clearPendingTimer();
    this.inFlightTurnId = null;
    this.activeTurnId.set(null);
    this.turnActivity.set(null);
    this.pendingVisible.set(false);
    this.sending.set(false);
  }

  private clearPendingTimer(): void {
    if (this.pendingTimer !== null) {
      clearTimeout(this.pendingTimer);
      this.pendingTimer = null;
    }
  }

  private handleTerminalFailure(
    turn: PreparedAssistantTurn,
    failure: AssistantTurnFailureDto | null | undefined,
    response: AssistantTurnResponse | null,
  ): void {
    if (response) {
      this.applyTurnResponse(turn, response);
    }

    const message =
      failure?.message
      ?? response?.error?.message
      ?? 'Ask Nostos could not complete this turn.';
    const code =
      failure?.code
      ?? response?.error?.code
      ?? 'assistant_turn_failed';

    this.lastError.set(message);

    if (!response?.reply && !response?.acknowledgement) {
      this.pushEntry(turn.turnId, 'error', message, null, code, [], false);
    }

    if (failure?.retryable && !response) {
      this.retryableTurn.set(turn);
      this.draft.set(turn.text);
      this.updateUserDelivery(turn.userEntryId, 'retryable', 'Ready to retry');
      if (turn.continuationPrompt) {
        this.pendingAnchor.set(turn.continuationPrompt);
        this.pendingContinuationContext.set(turn.context);
      }
    } else {
      if (this.retryableTurn()?.turnId === turn.turnId) this.retryableTurn.set(null);
      this.updateUserDelivery(turn.userEntryId, 'complete', null);
    }

    this.replaceTurnArtifacts(
      turn.turnId,
      artifactsFromTurnResponse(response, {
        code,
        message,
        retryable: failure?.retryable ?? false,
      }),
    );
    this.finishTurnUi(turn.turnId);
    this.persistSession();
    this.turnFinished.next({ turnId: turn.turnId, response, error: message });
  }

  private handleTerminalCancellation(
    turn: PreparedAssistantTurn,
    failure: AssistantTurnFailureDto | null | undefined,
    response: AssistantTurnResponse | null,
  ): void {
    if (response) this.applyTurnResponse(turn, response);

    if (this.retryableTurn()?.turnId === turn.turnId) this.retryableTurn.set(null);
    this.updateUserDelivery(turn.userEntryId, 'complete', null);
    this.lastError.set(null);

    const stoppedMessage =
      response?.error?.message
      ?? failure?.message
      ?? 'Stopped.';
    this.pushEntry(
      turn.turnId,
      'assistant',
      stoppedMessage,
      null,
      'Stopped',
      [],
      false,
    );

    this.replaceTurnArtifacts(
      turn.turnId,
      artifactsFromTurnResponse(response, {
        code: failure?.code ?? response?.error?.code ?? 'assistant_turn_cancelled',
        message: stoppedMessage,
        retryable: false,
      }, 'cancelled'),
    );
    this.finishTurnUi(turn.turnId);
    this.persistSession();
    this.turnFinished.next({ turnId: turn.turnId, response, error: stoppedMessage });
  }

  private handleTransportFailure(
    turn: PreparedAssistantTurn,
    code: string,
    message: string,
  ): void {
    this.finishTurnUi(turn.turnId);
    this.retryableTurn.set(turn);
    this.lastError.set(message);
    this.draft.set(turn.text);
    this.updateUserDelivery(
      turn.userEntryId,
      'retryable',
      code === 'assistant_offline' ? 'Offline' : 'Delivery uncertain',
    );
    this.pushEntry(turn.turnId, 'error', message, null, code, [], false);

    if (turn.continuationPrompt) {
      this.pendingAnchor.set(turn.continuationPrompt);
      this.pendingContinuationContext.set(turn.context);
    }
    this.replaceTurnArtifacts(turn.turnId, [{
      kind: 'failure',
      code,
      message,
      retryable: true,
      state: 'failed',
    }]);
    this.persistSession();
    this.turnFinished.next({ turnId: turn.turnId, response: null, error: message });
  }

  private applyTurnResponse(
    turn: PreparedAssistantTurn,
    response: AssistantTurnResponse,
  ): void {
    if (this.retryableTurn()?.turnId === turn.turnId) this.retryableTurn.set(null);
    this.updateUserDelivery(turn.userEntryId, 'complete', null);
    this.lastError.set(response.error?.message ?? null);
    this.lastTurn.set(response);
    this.suggestions.set(response.suggestions ?? []);

    if (response.pendingPlan) {
      const previous = this.pendingPlan();
      const previousTurnId = this.pendingPlanTurnId();
      if (previous && previous.planId !== response.pendingPlan.planId && previousTurnId) {
        this.upsertTurnArtifact(previousTurnId, {
          kind: 'destructive-result',
          planId: previous.planId,
          summary: previous.summary,
          outcome: 'superseded',
          capabilities: previous.steps.map((step) => step.capability),
        });
      }
      this.pendingPlan.set(response.pendingPlan);
      this.pendingPlanTurnId.set(turn.turnId);
      this.directPlanApprovalArmed.set(true);
    }

    for (const capability of response.executedCapabilities ?? []) {
      this.actionExecuted.next({ capability, context: turn.context });
      if (capability === 'notes_link_existing_concept' && turn.context.brainReviewNoteId) {
        const linkedNoteId = turn.context.brainReviewNoteId;
        this.eventLedger.update((events) => events.map((event) => ({
          ...event,
          suggestions: event.suggestions?.filter((suggestion) => suggestion.noteId !== linkedNoteId),
        })));
        this.suggestions.update((items) => items.filter((item) => item.noteId !== linkedNoteId));
      }
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
        continuationId
        && (kind === 'physical_page'
          || kind === 'external_audio_timestamp'
          || kind === 'book')
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
          response.error?.message
            ?? 'The assistant requested follow-up input without a valid continuation.',
        );
      }
    } else {
      this.pendingAnchor.set(null);
      this.pendingContinuationContext.set(null);
    }

    if (
      response.reply
      && (!promptText || response.reply.trim() !== promptText.trim())
    ) {
      this.pushEntry(
        turn.turnId,
        response.error ? 'error' : 'assistant',
        response.reply,
        null,
        response.error?.code ?? null,
        response.sources ?? [],
        true,
        'complete',
        null,
        response.suggestions ?? [],
      );
    } else if (response.suggestions?.length) {
      this.pushEntry(turn.turnId, 'assistant', 'Here are possible connections.', null, null,
        response.sources ?? [], true, 'complete', null, response.suggestions);
    }

    this.replaceTurnArtifacts(turn.turnId, artifactsFromTurnResponse(response));
    this.persistSession();
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
    historyContext: AssistantHistoricalContextDto | null = null,
    suggestions: AssistantSuggestionDto[] = [],
  ): string {
    const id = createId();
    this.eventLedger.update((events) => [
      ...events,
      {
        id,
        turnId,
        kind,
        text,
        anchorLabel,
        meta,
        sources,
        suggestions,
        remember,
        delivery,
        historyContext,
        historyEvidence: [],
        historyActions: [],
        historyCapturedNoteId: null,
        artifacts: [],
      },
    ]);
    return id;
  }

  private replaceTurnArtifacts(turnId: string, artifacts: AssistantTurnArtifact[]): void {
    this.eventLedger.update((events) => {
      let targetIndex = -1;
      for (let index = 0; index < events.length; index++) {
        if (events[index].turnId === turnId) targetIndex = index;
      }
      if (targetIndex < 0) return events;

      return events.map((event, index) =>
        event.turnId !== turnId
          ? event
          : { ...event, artifacts: index === targetIndex ? [...artifacts] : [] },
      );
    });
  }

  private upsertTurnArtifact(turnId: string, artifact: AssistantTurnArtifact): void {
    const existing = this.eventLedger()
      .filter((event) => event.turnId === turnId)
      .flatMap((event) => event.artifacts ?? [])
      .filter((item) => artifactKey(item) !== artifactKey(artifact));
    this.replaceTurnArtifacts(turnId, [...existing, artifact]);
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

function toHistoricalContext(context: AssistantContext): AssistantHistoricalContextDto {
  return {
    surface: context.surface,
    bookId: context.bookId,
    bookTitle: context.bookTitle,
    brainReviewNoteId: context.brainReviewNoteId,
    concept: context.concept,
    collectionId: context.collectionId,
  };
}


function artifactsFromTurnResponse(
  response: AssistantTurnResponse | null,
  failure?: { code: string; message: string; retryable: boolean } | null,
  failureState: 'failed' | 'cancelled' = 'failed',
): AssistantTurnArtifact[] {
  if (!response && !failure) return [];

  const artifacts: AssistantTurnArtifact[] = [];
  for (const evidence of response?.evidence ?? []) {
    artifacts.push({ kind: 'evidence', evidence: cloneEvidence(evidence) });
  }

  // Backward-compatible bridge responses may not yet carry #565 evidence.
  if (!(response?.evidence?.length) && response?.sources?.length) {
    for (const source of response.sources) {
      artifacts.push({
        kind: 'evidence',
        evidence: {
          handle: {
            kind: 'book_text',
            bookId: source.bookId,
            sourceSha256: source.sourceSha256,
          },
          label: source.bookTitle,
          excerpt: source.excerpt.slice(0, 320),
          bookTitle: source.bookTitle,
          bookAuthor: source.bookAuthor,
          format: source.format,
          locators: source.locators.map((locator) => ({ ...locator })),
        },
      });
    }
  }

  if (response?.capturedNoteId) {
    artifacts.push({
      kind: 'capture',
      noteId: response.capturedNoteId,
      acknowledgement: response.acknowledgement,
      state: 'saved',
    });
  }

  for (const capability of response?.executedCapabilities ?? []) {
    artifacts.push({ kind: 'action', capability, state: 'completed' });
  }

  for (const proposal of response?.suggestions ?? []) {
    artifacts.push({ kind: 'proposal', proposal: { ...proposal } });
  }

  const terminalFailure = failure ?? (response?.error
    ? { code: response.error.code, message: response.error.message, retryable: false }
    : null);
  if (terminalFailure) {
    artifacts.push({
      kind: 'failure',
      code: terminalFailure.code,
      message: terminalFailure.message,
      retryable: terminalFailure.retryable,
      state: failureState === 'cancelled' || /cancelled/i.test(terminalFailure.code)
        ? 'cancelled'
        : 'failed',
    });
  }

  return dedupeArtifacts(artifacts);
}

function cloneEvidence(evidence: AssistantEvidenceReferenceDto): AssistantEvidenceReferenceDto {
  return {
    ...evidence,
    handle: { ...evidence.handle },
    locators: evidence.locators?.map((locator) => ({ ...locator })) ?? [],
  };
}

function historyFactsFromArtifacts(artifacts: readonly AssistantTurnArtifact[]): {
  evidenceHandles: AssistantEvidenceHandleDto[];
  actions: string[];
  capturedNoteId: string | null;
} {
  const evidenceHandles = artifacts
    .filter((artifact): artifact is Extract<AssistantTurnArtifact, { kind: 'evidence' }> =>
      artifact.kind === 'evidence')
    .map((artifact) => ({ ...artifact.evidence.handle }));
  const actions = artifacts
    .flatMap((artifact) => {
      if (artifact.kind === 'action') return [artifact.capability];
      if (artifact.kind === 'destructive-result' && artifact.outcome === 'applied')
        return artifact.capabilities;
      return [];
    });
  const capturedNoteId = artifacts.find(
    (artifact): artifact is Extract<AssistantTurnArtifact, { kind: 'capture' }> =>
      artifact.kind === 'capture',
  )?.noteId ?? null;

  return {
    evidenceHandles: uniqueBy(evidenceHandles, evidenceHandleKey),
    actions: [...new Set(actions)],
    capturedNoteId,
  };
}

function artifactKey(artifact: AssistantTurnArtifact): string {
  switch (artifact.kind) {
    case 'evidence':
      return `evidence:${evidenceHandleKey(artifact.evidence.handle)}`;
    case 'capture':
      return `capture:${artifact.noteId}`;
    case 'action':
      return `action:${artifact.capability}`;
    case 'failure':
      return `failure:${artifact.code}:${artifact.message}`;
    case 'proposal':
      return `proposal:${artifact.proposal.noteId ?? ''}:${artifact.proposal.value ?? artifact.proposal.label}`;
    case 'destructive-result':
      return `destructive:${artifact.planId}`;
  }
}

function dedupeArtifacts(artifacts: readonly AssistantTurnArtifact[]): AssistantTurnArtifact[] {
  return uniqueBy(artifacts, artifactKey);
}

function evidenceHandleKey(handle: AssistantEvidenceHandleDto): string {
  return [
    handle.kind,
    handle.noteId ?? '',
    handle.conceptId ?? '',
    handle.bookId ?? '',
    handle.sourceSha256 ?? '',
    handle.extractorVersion ?? '',
    handle.ordinal ?? '',
  ].join('|');
}

function uniqueBy<T>(values: readonly T[], key: (value: T) => string): T[] {
  const seen = new Set<string>();
  return values.filter((value) => {
    const id = key(value);
    if (seen.has(id)) return false;
    seen.add(id);
    return true;
  });
}

function isAssistantTurnArtifact(value: unknown): value is AssistantTurnArtifact {
  if (!value || typeof value !== 'object') return false;
  const artifact = value as Partial<AssistantTurnArtifact>;
  switch (artifact.kind) {
    case 'evidence':
      return !!(artifact as { evidence?: unknown }).evidence;
    case 'capture':
      return typeof (artifact as { noteId?: unknown }).noteId === 'string';
    case 'action':
      return typeof (artifact as { capability?: unknown }).capability === 'string';
    case 'failure':
      return typeof (artifact as { code?: unknown }).code === 'string'
        && typeof (artifact as { message?: unknown }).message === 'string';
    case 'proposal':
      return !!(artifact as { proposal?: unknown }).proposal;
    case 'destructive-result':
      return typeof (artifact as { planId?: unknown }).planId === 'string';
    default:
      return false;
  }
}

function sessionStorageOrNull(): Storage | null {
  try {
    return typeof globalThis.sessionStorage === 'undefined'
      ? null
      : globalThis.sessionStorage;
  } catch {
    return null;
  }
}

function isAnchorPrompt(value: unknown): value is AssistantAnchorPrompt {
  if (!value || typeof value !== 'object') return false;
  const prompt = value as Partial<AssistantAnchorPrompt>;
  return (
    (prompt.kind === 'physical_page' ||
      prompt.kind === 'external_audio_timestamp' ||
      prompt.kind === 'book') &&
    typeof prompt.question === 'string' &&
    typeof prompt.continuationId === 'string' &&
    prompt.continuationId.length > 0
  );
}

function isAssistantContext(value: unknown): value is AssistantContext {
  if (!value || typeof value !== 'object') return false;
  const context = value as Partial<AssistantContext>;
  return typeof context.surface === 'string' && typeof context.route === 'string';
}

function isPendingPlan(value: unknown): value is AssistantPendingPlanDto {
  if (!value || typeof value !== 'object') return false;
  const plan = value as Partial<AssistantPendingPlanDto>;
  return (
    typeof plan.planId === 'string' &&
    typeof plan.summary === 'string' &&
    typeof plan.approvalToken === 'string' &&
    Array.isArray(plan.steps)
  );
}

function isPreparedTurn(value: unknown): value is PreparedAssistantTurn {
  if (!value || typeof value !== 'object') return false;
  const turn = value as Partial<PreparedAssistantTurn>;
  const request = turn.request as Partial<AssistantTurnRequestDto> | undefined;
  return (
    typeof turn.turnId === 'string' &&
    typeof turn.text === 'string' &&
    typeof turn.userEntryId === 'string' &&
    isAssistantContext(turn.context) &&
    !!request &&
    typeof request.turnId === 'string' &&
    request.turnId === turn.turnId &&
    typeof request.conversationId === 'string' &&
    typeof request.message === 'string' &&
    (turn.continuationPrompt === null || isAnchorPrompt(turn.continuationPrompt))
  );
}

function isRestorableConversationEvent(value: unknown): value is AssistantConversationEvent {
  if (!value || typeof value !== 'object') return false;
  const event = value as Partial<AssistantConversationEvent>;
  return (
    typeof event.id === 'string' &&
    typeof event.turnId === 'string' &&
    (event.kind === 'user' || event.kind === 'assistant' || event.kind === 'error') &&
    typeof event.text === 'string' &&
    typeof event.remember === 'boolean' &&
    (event.delivery === 'sending' ||
      event.delivery === 'complete' ||
      event.delivery === 'retryable') &&
    Array.isArray(event.sources) &&
    Array.isArray(event.historyEvidence) &&
    Array.isArray(event.historyActions) &&
    (event.artifacts === undefined || Array.isArray(event.artifacts))
  );
}

function isAssistantTurnEvent(value: unknown): value is AssistantTurnEventDto {
  if (!value || typeof value !== 'object') return false;
  const event = value as Partial<AssistantTurnEventDto>;
  return (
    typeof event.turnId === 'string'
    && typeof event.sequence === 'number'
    && typeof event.kind === 'string'
  );
}

function isAssistantTurnResponse(value: unknown): value is AssistantTurnResponse {
  if (!value || typeof value !== 'object') return false;
  const response = value as Partial<AssistantTurnResponse>;
  return (
    typeof response.reply === 'string'
    && Array.isArray(response.suggestions)
    && ('pendingPlan' in response)
    && ('anchorPrompt' in response)
  );
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
