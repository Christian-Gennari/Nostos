/**
 * Assistant conversation/session state (issue #261 §3, §5, §6, §7).
 *
 * The backend bridge (`POST /api/assistant/turn/stream`) owns the LLM and the tool
 * loop; this service owns the surface's state: the editorial transcript, the
 * deterministic source-location follow-up, the non-mutating suggestions, and
 * library changes, which are applied with undo or held for review.
 *
 * Trust is structural, not cosmetic:
 *   - Suggestions never mutate merely by being shown.
 *   - Choosing a normal reversible action (for example an existing topic link)
 *     is sufficient authorization for the backend's immediate Act path.
 *   - Library changes arrive as one changeset per turn. Everyday changes are
 *     already applied and carry Undo; larger ones wait as a review that only
 *     `applyChangeset` (with the changeset's own token) can apply.
 *
 * `TRANSCRIPT_SEND_POLICY` is unchanged: a voice transcript enters the composer
 * and is dispatched after the grace window, with a pre-dispatch Undo.
 */
import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { HttpClient, HttpEventType } from '@angular/common/http';
import { Subject } from 'rxjs';

import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import { formatAudioTime } from '../../core/format';
import { AssistantStatusService } from './assistant-status.service';

import {
  AssistantAnchor,
  AssistantContext,
  AssistantContextService,
} from './assistant-context.service';
import {
  evidenceHandleKey,
  projectContextualModelHistory,
  projectPlainModelHistory,
  projectVisibleTranscript,
  uniqueBy,
  type AssistantConversationEvent,
  type AssistantEventDelivery,
} from './assistant-history';

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
  kind: 'note' | 'topic' | 'book_text' | string;
  noteId?: string | null;
  topicId?: string | null;
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

export interface AssistantResolvedBookDto {
  bookId: string;
  bookTitle: string;
}

/**
 * Historical turn outcomes. These values are inert: restoring them never
 * executes a capability, replays a capture, or approves a plan.
 */
export type AssistantTurnArtifact =
  | { kind: 'evidence'; evidence: AssistantEvidenceReferenceDto }
  | { kind: 'capture'; noteId: string; acknowledgement: string | null; state: 'saved' }
  | { kind: 'action'; capability: string; state: 'completed' }
  | { kind: 'failure'; code: string; message: string; retryable: boolean; state: 'failed' | 'cancelled' | 'incomplete' }
  | { kind: 'proposal'; proposal: AssistantSuggestionDto }
  | { kind: 'changeset'; changeset: AssistantChangesetDto; conflicts?: string[] };

/** Compact application snapshot from when an older user turn occurred. */
export interface AssistantHistoricalContextDto {
  surface: string | null;
  bookId: string | null;
  bookTitle: string | null;
  brainReviewNoteId: string | null;
  topic: string | null;
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

/** Totals of what a changeset does (mirrors `LibraryChangesetCountsDto`). */
export interface LibraryChangesetCountsDto {
  collectionsCreated: number;
  collectionsDeleted: number;
  collectionsUpdated: number;
  booksTouched: number;
  membershipsAdded: number;
  membershipsRemoved: number;
  booksLosingMembership: number;
  bookEdits: number;
}

export interface LibraryCollectionChangeDto {
  id: string;
  kind: 'created' | 'deleted' | 'updated' | string;
  name: string;
  previousName: string | null;
  parentName: string | null;
  previousParentName: string | null;
  parentChanged: boolean;
  bookCount: number;
}

export interface LibraryMembershipChangeDto {
  bookId: string;
  title: string;
  author: string | null;
  added: string[];
  removed: string[];
}

export interface LibraryFieldChangeDto {
  field: string;
  before: string | null;
  after: string | null;
}

export interface LibraryBookEditDto {
  bookId: string;
  title: string;
  fields: LibraryFieldChangeDto[];
}

/** Exactly what a changeset does, as the server previewed or applied it. */
export interface LibraryChangesetDiffDto {
  collections: LibraryCollectionChangeDto[];
  memberships: LibraryMembershipChangeDto[];
  bookEdits: LibraryBookEditDto[];
  counts: LibraryChangesetCountsDto;
}

/** One step of a changeset, which the user can leave out while it is in review. */
export interface AssistantChangeStepDto {
  index: number;
  summary: string;
  included: boolean;
  dependsOn: number[];
}

export type AssistantChangesetStatus = 'review' | 'applied' | 'undone' | 'discarded';

/**
 * A turn's library changes. `review` changes nothing until applied; `applied`
 * can be undone while `canUndo`. `actionToken` is required by every action on it.
 */
export interface AssistantChangesetDto {
  changesetId: string;
  status: AssistantChangesetStatus | string;
  steps: AssistantChangeStepDto[];
  diff: LibraryChangesetDiffDto;
  diffHash: string;
  actionToken: string;
  reviewReasons: string[];
  canUndo: boolean;
}

/** The outcome of a changeset action. Failures are data. */
export interface AssistantChangesetActionResponse {
  success: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  changeset: AssistantChangesetDto | null;
  conflicts: string[];
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
  /** The turn's library changes: applied with undo, or waiting for review. */
  changeset?: AssistantChangesetDto | null;
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
  /** One unambiguous canonical book read/resolved/searched by this turn. */
  resolvedBook?: AssistantResolvedBookDto | null;
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
  topic: string | null;
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
  pendingReview: AssistantChangesetDto | null;
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
  private readonly preferences = inject(LibraryPreferencesService);
  private readonly status = inject(AssistantStatusService);

  /** Same availability contract as the app-wide AssistantComponent capsule. */
  readonly surfaceAvailable = computed(
    () => this.preferences.assistantEnabled() && this.status.available(),
  );

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
    projectVisibleTranscript(this.eventLedger()),
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
  /** Non-mutating topic suggestions for the current turn. */
  readonly suggestions = signal<AssistantSuggestionDto[]>([]);
  /**
   * The changeset waiting for review, if any: the latest one in the
   * conversation. The server keeps at most one review per conversation and
   * sets older ones aside, so the client mirrors exactly that.
   */
  readonly pendingReview = computed<{ turnId: string; changeset: AssistantChangesetDto } | null>(() => {
    const events = this.eventLedger();
    for (let index = events.length - 1; index >= 0; index--) {
      for (const artifact of events[index].artifacts ?? []) {
        if (artifact.kind === 'changeset' && artifact.changeset.status === 'review') {
          return { turnId: events[index].turnId, changeset: artifact.changeset };
        }
      }
    }
    return null;
  });
  /** The changeset an action is running on, so its card can show progress. */
  readonly changesetBusy = signal<string | null>(null);
  /** The last action failure per changeset, shown on its card. */
  readonly changesetErrors = signal<Partial<Record<string, string>>>({});
  /**
   * Natural "yes / go ahead" applies a review only on the turn right after it
   * appeared. Any intervening ordinary message disarms this shortcut so a
   * later, unrelated "yes" cannot apply stale changes. The card's own Apply
   * button stays available while the review is open.
   */
  private readonly directReviewApprovalArmed = signal(false);
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
    projectPlainModelHistory(this.eventLedger()),
  );

  /** Enriched wire history; historical artifacts are reduced to inert reference metadata. */
  private readonly contextualHistory = computed<AssistantHistoryMessage[]>(() =>
    projectContextualModelHistory(this.eventLedger()),
  );

  /** The note the last turn captured, if any. */
  readonly capturedNoteId = signal<string | null>(null);

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
        pendingReview: this.pendingReview()?.changeset ?? null,
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
        const inlineTurn = restoredEvents.some((event) =>
          event.turnId === restoredRetry.turnId && event.hiddenFromTranscript);
        // Never place an internal Brain operation (including topic IDs) in the composer.
        this.draft.set(inlineTurn ? '' : restoredRetry.text);
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

      // Natural-language approval is intentionally NOT restored as armed. The
      // card's Apply button still applies the exact server-held changeset,
      // while a generic "yes" after refresh cannot.
      this.directReviewApprovalArmed.set(false);
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

  /**
   * UI-level request to open the assistant through AssistantComponent's normal
   * lifecycle. Feature surfaces use this instead of calling open() directly so
   * availability, focus restoration and mobile visual-viewport tracking remain
   * owned by the one app-wide assistant shell.
   */
  readonly surfaceOpenRequested = new Subject<void>();

  /**
   * The assistant shell is loaded lazily (app.component.html): true once
   * something asked for it, which loads it at once instead of when the browser
   * is idle. A request made before it exists is kept and handed over when it
   * attaches (`takePendingSurfaceOpen`).
   */
  readonly surfaceWanted = signal(false);
  private pendingSurfaceOpen = false;

  requestSurfaceOpen(): void {
    if (!this.surfaceAvailable()) return;
    if (this.surfaceOpenRequested.observed) {
      this.surfaceOpenRequested.next();
      return;
    }
    this.pendingSurfaceOpen = true;
    this.surfaceWanted.set(true);
  }

  /** For the shell when it attaches: whether an open was requested before it existed. */
  takePendingSurfaceOpen(): boolean {
    const pending = this.pendingSurfaceOpen;
    this.pendingSurfaceOpen = false;
    return pending;
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
    this.directReviewApprovalArmed.set(false);
    this.changesetBusy.set(null);
    this.changesetErrors.set({});
    this.suggestions.set([]);
    this.lastTurn.set(null);
    this.lastError.set(null);
    this.draft.set('');
    this.capturedNoteId.set(null);
    this.finishTurnUi(null);
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

    const review = this.pendingReview();
    const reviewAnswer = review
      ? isExplicitPlanRejection(text)
        ? 'discard'
        : this.directReviewApprovalArmed() && isExplicitPlanApproval(text)
          ? 'apply'
          : null
      : null;
    if (review && reviewAnswer) {
      // A short, unambiguous answer to the review right after it appeared acts
      // on exactly that server-held changeset. Anything else is a new turn.
      this.draft.set('');
      this.pushEntry(
        createId(),
        'user',
        text,
        null,
        null,
        [],
        true,
        'complete',
        toHistoricalContext(this.context()),
      );
      this.directReviewApprovalArmed.set(false);
      this.persistSession();
      // A spoken or typed answer gets a spoken answer back once it landed;
      // the card itself shows exactly what changed.
      const replyTurnId = review.turnId;
      const answered = (reply: string) => this.pushEntry(replyTurnId, 'assistant', reply, null, null);
      if (reviewAnswer === 'apply') {
        this.applyChangeset(review.changeset.changesetId, () => answered('Done.'));
      } else {
        this.discardChangeset(review.changeset.changesetId, () => answered('Okay. Nothing was changed.'));
      }
      return;
    }

    if (review) this.directReviewApprovalArmed.set(false);

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
  requestTopicProposals(noteId: string): string | null {
    const context = this.context();
    if (this.sending() || !noteId || context.brainReviewNoteId !== noteId) return null;

    return this.startPreparedTurn({
      text: 'Suggest up to three additional existing topics for this note. Read the note and existing topic evidence, then explicitly propose only grounded links that are not already present. If none fit, return no proposals. Do not link anything.',
      context,
      requestAnchor: this.effectiveAnchor(context),
      captureBookTitle: null,
      continuationPrompt: null,
      continuationSkipped: false,
      displayAnchor: this.effectiveAnchor(context),
      hiddenFromTranscript: true,
    });
  }

  /**
   * Choose a non-mutating suggestion. The click is the user's explicit choice,
   * so the resulting existing-topic link may execute through the normal Act
   * path without asking for a second approval.
   */
  applySuggestion(suggestion: AssistantSuggestionDto, inline = false): string | null {
    if (this.sending() || suggestion.kind !== 'topic' || !suggestion.value) return null;

    const context = this.context();
    if (!suggestion.noteId || context.brainReviewNoteId !== suggestion.noteId) {
      this.lastError.set('Open the suggested note in Brain before linking it.');
      return null;
    }

    const text = `Link note ${suggestion.noteId} to the existing topic “${suggestion.label}” (ID ${suggestion.value}).`;
    if (inline) {
      return this.startPreparedTurn({
        text,
        context,
        requestAnchor: this.effectiveAnchor(context),
        captureBookTitle: null,
        continuationPrompt: null,
        continuationSkipped: false,
        displayAnchor: this.effectiveAnchor(context),
        hiddenFromTranscript: true,
      });
    }
    return this.dispatchTurn(text, this.effectiveAnchor(context));
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
   * Apply the changes in review, leaving out the steps the user unticked. The
   * server applies only its own stored steps, and only if they still do what
   * the card shows; otherwise the card is refreshed with what they would do now.
   */
  applyChangeset(changesetId: string, onDone?: () => void): void {
    const found = this.findChangeset(changesetId);
    if (!found || found.changeset.status !== 'review') return;
    const { changeset } = found;
    this.directReviewApprovalArmed.set(false);
    this.runChangesetAction(changesetId, 'apply', {
      actionToken: changeset.actionToken,
      excluded: excludedSteps(changeset),
      diffHash: changeset.diffHash,
    }, onDone);
  }

  /** Set the changes in review aside. Nothing was applied, so nothing changes. */
  discardChangeset(changesetId: string, onDone?: () => void): void {
    const found = this.findChangeset(changesetId);
    if (!found || found.changeset.status !== 'review') return;
    this.directReviewApprovalArmed.set(false);
    this.runChangesetAction(changesetId, 'discard', { actionToken: found.changeset.actionToken }, onDone);
  }

  /** Take back applied changes. Anything edited since is kept and listed. */
  undoChangeset(changesetId: string): void {
    const found = this.findChangeset(changesetId);
    if (!found || found.changeset.status !== 'applied' || !found.changeset.canUndo) return;
    this.runChangesetAction(changesetId, 'undo', { actionToken: found.changeset.actionToken });
  }

  /**
   * Include or leave out one step of a review. Leaving out a step also leaves
   * out the steps that depend on it; the server recomputes exactly what the
   * remaining steps would do.
   */
  setChangesetStepIncluded(changesetId: string, index: number, included: boolean): void {
    const found = this.findChangeset(changesetId);
    if (!found || found.changeset.status !== 'review') return;
    const excluded = new Set(excludedSteps(found.changeset));
    if (included) excluded.delete(index);
    else excluded.add(index);
    this.runChangesetAction(changesetId, 'preview', {
      actionToken: found.changeset.actionToken,
      excluded: [...excluded].sort((a, b) => a - b),
    });
  }

  private runChangesetAction(
    changesetId: string,
    action: 'preview' | 'apply' | 'discard' | 'undo',
    body: Record<string, unknown>,
    onDone?: () => void,
  ): void {
    if (this.changesetBusy()) return;
    this.changesetBusy.set(changesetId);
    this.setChangesetError(changesetId, null);

    const settle = (response: AssistantChangesetActionResponse | null) => {
      this.changesetBusy.set(null);
      if (response?.changeset) this.replaceChangeset(response.changeset, response.conflicts);
      if (!response) {
        this.setChangesetError(changesetId, 'Nostos could not be reached. Nothing changed; try again.');
      } else if (!response.success) {
        this.setChangesetError(changesetId, response.errorMessage ?? 'The changes could not be made.');
      } else {
        onDone?.();
      }
      this.persistSession();
    };

    this.http
      .post<AssistantChangesetActionResponse>(
        `/api/assistant/changesets/${encodeURIComponent(changesetId)}/${action}`,
        body,
      )
      .subscribe({
        next: (response) => settle(response),
        error: (error: unknown) => settle(changesetActionFromError(error)),
      });
  }

  private findChangeset(changesetId: string): { turnId: string; changeset: AssistantChangesetDto } | null {
    for (const event of this.eventLedger()) {
      for (const artifact of event.artifacts ?? []) {
        if (artifact.kind === 'changeset' && artifact.changeset.changesetId === changesetId) {
          return { turnId: event.turnId, changeset: artifact.changeset };
        }
      }
    }
    return null;
  }

  private replaceChangeset(changeset: AssistantChangesetDto, conflicts?: string[]): void {
    const found = this.findChangeset(changeset.changesetId);
    if (!found) return;
    this.upsertTurnArtifact(found.turnId, {
      kind: 'changeset',
      changeset,
      ...(conflicts?.length ? { conflicts: [...conflicts] } : {}),
    });
  }

  private setChangesetError(changesetId: string, message: string | null): void {
    this.changesetErrors.update((errors) => {
      const next = { ...errors };
      if (message) next[changesetId] = message;
      else delete next[changesetId];
      return next;
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
    hiddenFromTranscript?: boolean;
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
      [],
      options.hiddenFromTranscript ?? false,
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

    this.lastError.set(code === PARTIALLY_COMPLETED_CODE ? null : message);

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

    // The message is the one visible cancellation indicator: it carries the
    // server's truthful copy ("…changes that already completed remain
    // applied"). The cancelled failure artifact below stays attached to the
    // turn as inert state and is not rendered as a second "Stopped" label.
    const stoppedMessage =
      response?.error?.message
      ?? failure?.message
      ?? 'Stopped.';
    this.pushEntry(
      turn.turnId,
      'assistant',
      stoppedMessage,
      null,
      null,
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

    if (response.resolvedBook) {
      this.promoteResolvedBookContext(turn.userEntryId, response.resolvedBook);
    }

    if (response.changeset?.status === 'review') {
      // The server set any older review in this conversation aside.
      const previous = this.pendingReview();
      if (previous && previous.changeset.changesetId !== response.changeset.changesetId) {
        this.replaceChangeset({ ...previous.changeset, status: 'discarded' });
      }
      this.directReviewApprovalArmed.set(true);
    }

    for (const capability of response.executedCapabilities ?? []) {
      this.actionExecuted.next({ capability, context: turn.context });
      if (capability === 'notes_link_existing_topic' && turn.context.brainReviewNoteId) {
        const linkedNoteId = turn.context.brainReviewNoteId;
        this.eventLedger.update((events) => events.map((event) => ({
          ...event,
          suggestions: event.suggestions?.filter((suggestion) => suggestion.noteId !== linkedNoteId),
        })));
        this.suggestions.update((items) => items.filter((item) => item.noteId !== linkedNoteId));
      }
    }

    this.capturedNoteId.set(response.capturedNoteId ?? null);

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
      // A partially completed turn is still the assistant speaking about work
      // that did happen; only a real failure is rendered as an error entry.
      const failed = !!response.error && response.error.code !== PARTIALLY_COMPLETED_CODE;
      this.pushEntry(
        turn.turnId,
        failed ? 'error' : 'assistant',
        response.reply,
        null,
        failed ? response.error!.code : null,
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
    hiddenFromTranscript = false,
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
        hiddenFromTranscript,
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

  private promoteResolvedBookContext(
    userEntryId: string,
    book: AssistantResolvedBookDto,
  ): void {
    this.eventLedger.update((events) => events.map((event) => {
      if (event.id !== userEntryId || event.kind !== 'user' || !event.historyContext) {
        return event;
      }

      return {
        ...event,
        historyContext: {
          ...event.historyContext,
          bookId: book.bookId,
          bookTitle: book.bookTitle,
        },
      };
    }));
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
 * True only for a complete, short approval utterance while a review of
 * library changes is visibly pending. Deliberately exact rather than fuzzy:
 * "yes, but explain first" is discussion, not permission to change anything.
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

/** Exact rejection vocabulary for the visible review of library changes. */
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
    topic: context.topic,
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
    topic: context.topic,
    collectionId: context.collectionId,
  };
}


/**
 * The turn committed canonical changes but stopped before finishing the rest
 * (#647). It is an honest incomplete state, never a failure or a rollback.
 */
const PARTIALLY_COMPLETED_CODE = 'assistant_turn_partially_completed';

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
    const passageOrdinals = new Map<string, number>();
    for (const source of response.sources) {
      const passageKey =
        `${source.bookId}|${source.sourceSha256}|${JSON.stringify(source.locators ?? [])}`;
      let ordinal = passageOrdinals.get(passageKey);
      if (ordinal === undefined) {
        ordinal = passageOrdinals.size;
        passageOrdinals.set(passageKey, ordinal);
      }

      artifacts.push({
        kind: 'evidence',
        evidence: {
          handle: {
            kind: 'book_text',
            bookId: source.bookId,
            sourceSha256: source.sourceSha256,
            // Synthesized passage identity keeps distinct passages through dedupe/grouping.
            ordinal,
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

  if (response?.changeset) {
    artifacts.push({ kind: 'changeset', changeset: response.changeset });
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
        : terminalFailure.code === PARTIALLY_COMPLETED_CODE
          ? 'incomplete'
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
    case 'changeset':
      return `changeset:${artifact.changeset.changesetId}`;
  }
}

function dedupeArtifacts(artifacts: readonly AssistantTurnArtifact[]): AssistantTurnArtifact[] {
  return uniqueBy(artifacts, artifactKey);
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
    case 'changeset': {
      const changeset = (artifact as { changeset?: Partial<AssistantChangesetDto> }).changeset;
      return !!changeset
        && typeof changeset.changesetId === 'string'
        && typeof changeset.status === 'string'
        && Array.isArray(changeset.steps)
        && !!changeset.diff;
    }
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

/** The steps of a changeset the user has left out. */
function excludedSteps(changeset: AssistantChangesetDto): number[] {
  return changeset.steps.filter((step) => !step.included).map((step) => step.index);
}

/**
 * A changeset route answers 404/403 with the same body as a refusal, so the
 * card can say what happened instead of a generic network failure.
 */
function changesetActionFromError(error: unknown): AssistantChangesetActionResponse | null {
  const body = (error as { error?: unknown } | null)?.error;
  if (body && typeof body === 'object' && typeof (body as { errorCode?: unknown }).errorCode === 'string') {
    const refusal = body as Partial<AssistantChangesetActionResponse>;
    return {
      success: false,
      errorCode: refusal.errorCode ?? null,
      errorMessage: refusal.errorMessage ?? null,
      changeset: refusal.changeset ?? null,
      conflicts: refusal.conflicts ?? [],
    };
  }
  return null;
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
  return formatAudioTime(Number(value ?? '0'));
}
