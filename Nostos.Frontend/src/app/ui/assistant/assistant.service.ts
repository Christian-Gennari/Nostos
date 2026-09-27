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

/** One remembered turn; the ordered log the model is told on the next request. */
export interface AssistantHistoryMessage {
  role: 'user' | 'assistant';
  text: string;
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
 * History caps (issue #286). {@link HISTORY_MAX_EXCHANGES} counts EXCHANGES, not
 * messages: an exchange is one user turn plus the assistant turn that followed
 * it, so the cap keeps the last ten user turns and everything from the earliest
 * of those onward. {@link HISTORY_MAX_CHARS} bounds each message's length.
 *
 * Exported so the spec asserts the shipped numbers rather than restating them.
 */
export const HISTORY_MAX_EXCHANGES = 10;
export const HISTORY_MAX_CHARS = 2000;

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
   * The book title the user gave when a capture asked which book it belongs to,
   * for a turn that is the answer to that question. Null on every other turn;
   * the server resolves it and never lets the model choose a book.
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

/** Shape exposed on `globalThis.__nostosAssistant` for live verification. */
export interface NostosAssistantDiagnostics {
  context: AssistantContext;
  conversationId: string;
  lastTurn: AssistantTurnResponse | null;
  suggestions: AssistantSuggestionDto[];
  pendingPlan: AssistantPendingPlanDto | null;
  /** The capped turn log the next request will carry (issue #286). */
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

@Injectable({ providedIn: 'root' })
export class AssistantService {
  private readonly contextService = inject(AssistantContextService);
  private readonly http = inject(HttpClient);

  /** Stable for the life of the surface; keys the server-held pending plan. */
  private readonly clientId = createId();

  readonly isOpen = signal(false);
  readonly draft = signal('');
  readonly entries = signal<AssistantEntry[]>([]);
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

  /** A deterministic follow-up awaiting an anchor answer. */
  readonly pendingAnchor = signal<AssistantAnchorPrompt | null>(null);
  private readonly pendingText = signal('');

  /**
   * The remembered turns, in order (issue #286). Separate from the display
   * entries: this is exactly what the model is told on the next request, so an
   * entry is appended the moment a turn is dispatched and never for a turn that
   * failed. It lives only in memory and dies on refresh, by design.
   */
  private readonly turnLog = signal<AssistantHistoryMessage[]>([]);

  /** The capped, truncated history sent with the next turn (and in diagnostics). */
  readonly history = computed<AssistantHistoryMessage[]>(() => capHistory(this.turnLog()));

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
    // The repo verifies UI by reading handles in a live browser; expose the
    // resolved context, the last turn, the suggestions, the pending plan and the
    // conversation history the next turn will carry.
    effect(() => {
      globalThis.__nostosAssistant = {
        context: this.context(),
        lastTurn: this.lastTurn(),
        suggestions: this.suggestions(),
        pendingPlan: this.pendingPlan(),
        history: this.history(),
        capturedNoteId: this.capturedNoteId(),
      };
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

  updateDraft(value: string): void {
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
    this.draft.set(current ? `${current} ${transcript}` : transcript);

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
   * Enter submits. If a follow-up is pending, this is its answer: a page or
   * timestamp for a location question, or the book's title when the capture
   * asked which book it belongs to (there is no book open, so the app cannot
   * know it and will not guess).
   */
  submit(): void {
    // A manual send consumes the pending window; the same call the timer makes
    // is a no-op here because it already cleared its own timer.
    this.cancelAutoSend();
    const text = this.draft().trim();
    if (!text || this.sending()) return;

    const pending = this.pendingAnchor();
    if (pending) {
      const original = this.pendingText();
      this.pendingAnchor.set(null);
      this.pendingText.set('');
      this.draft.set('');

      if (pending.kind === 'book') {
        this.dispatchTurn(original, this.effectiveAnchor(this.context()), text);
        return;
      }

      this.dispatchTurn(original, this.anchorFromAnswer(pending.kind, text));
      return;
    }

    const plan = this.pendingPlan();
    if (plan && isExplicitPlanRejection(text)) {
      // A clear rejection is terminal in the client: discard the token and plan
      // so nothing in a later conversation can accidentally approve it. No
      // mutation has occurred.
      this.draft.set('');
      this.turnLog.update((log) => [...log, { role: 'user', text }]);
      this.pushEntry('user', text, null, null);
      this.pendingPlan.set(null);
      this.directPlanApprovalArmed.set(false);
      const reply = 'Okay. I won\'t make that change.';
      this.turnLog.update((log) => [...log, { role: 'assistant', text: reply }]);
      this.pushEntry('assistant', reply, null, 'Cancelled');
      return;
    }

    if (plan && this.directPlanApprovalArmed() && isExplicitPlanApproval(text)) {
      // A short, unambiguous confirmation on the immediate confirmation turn
      // is itself the approval gesture. Execute the exact server-held plan id +
      // token rather than asking the model to reinterpret destructive work.
      this.draft.set('');
      this.turnLog.update((log) => [...log, { role: 'user', text }]);
      this.pushEntry('user', text, null, null);
      this.directPlanApprovalArmed.set(false);
      this.approvePlan(plan.planId, plan.approvalToken);
      return;
    }

    // Discussion or an unrelated turn may keep the destructive plan visible,
    // but it disarms generic natural-language approval. This prevents a later
    // unrelated "yes" from authorizing an old delete.
    if (plan) this.directPlanApprovalArmed.set(false);

    const context = this.context();
    const anchor = this.effectiveAnchor(context);
    this.draft.set('');

    // Every other message dispatches normally. A question or qualification
    // while a destructive plan is pending is discussion, not approval, so the
    // plan survives and the assistant can answer without accidentally acting.
    this.dispatchTurn(text, anchor);
  }

  /**
   * "I don't know" — never lose the capture to a missing anchor. A book
   * question is deliberately not skippable: skipping it would save nothing and
   * ask again, so the surface does not offer it (and this refuses it).
   */
  skipAnchor(): void {
    const pending = this.pendingAnchor();
    if (!pending || pending.kind === 'book') return;
    const text = this.pendingText();
    this.pendingAnchor.set(null);
    this.pendingText.set('');
    this.draft.set('');
    this.dispatchTurn(text, { kind: 'unknown', value: null, verified: false });
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
        this.pushEntry('assistant', 'Original text restored.', null, 'Restored');
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
   * Execute exactly one pending plan through
   * `POST /api/assistant/plan/approve`. The plan id and its approval token are
   * both required; the server refuses a mismatch and mutates nothing.
   */
  approvePlan(planId: string, approvalToken: string): void {
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
            this.turnLog.update((log) => [...log, { role: 'assistant', text: executionReply }]);
            this.pushEntry('assistant', executionReply, null, 'Applied');
          } else {
            const failureReply =
              response.errorMessage ?? plan?.summary ?? 'The plan could not be applied.';
            this.lastError.set(failureReply);
            this.turnLog.update((log) => [...log, { role: 'assistant', text: failureReply }]);
            this.pushEntry('error', failureReply, null, response.errorCode ?? 'Refused');
          }

        },
        error: () => {
          this.sending.set(false);
          this.lastError.set('The plan could not be applied. It is still waiting for approval.');
        },
      });
  }

  private dispatchTurn(text: string, anchor: AssistantAnchor | null, captureBookTitle: string | null = null): void {
    const context = this.context();

    // Read the history BEFORE this turn's user entry joins the log: the server
    // receives `message` separately, so repeating it here would say it twice.
    const history = this.history();

    // The turn is being dispatched, so it is remembered and the user's own words
    // appear in the transcript at once — a plain question used to leave no trace
    // of what was asked. Typed and voice both arrive here through `submit()`.
    this.turnLog.update((log) => [...log, { role: 'user', text }]);
    this.pushEntry('user', text, null, null);

    const request: AssistantTurnRequestDto = {
      clientId: this.clientId,
      idempotencyKey: createId(),
      message: text,
      context: toContextDto(context, anchor, captureBookTitle),
      history,
    };

    this.sending.set(true);
    this.http.post<AssistantTurnResponse>('/api/assistant/turn', request).subscribe({
      next: (response) => {
        this.sending.set(false);
        this.lastError.set(null);
        this.lastTurn.set(response);
        this.suggestions.set(response.suggestions ?? []);
        // A no-plan response does not cancel a destructive plan that is still
        // pending server-side. Only a newly proposed plan replaces it, and a
        // successful approval clears it below.
        if (response.pendingPlan) {
          this.pendingPlan.set(response.pendingPlan);
          this.directPlanApprovalArmed.set(true);
        }

        for (const capability of response.executedCapabilities ?? []) {
          this.actionExecuted.next({ capability, context });
        }

        // The raw-transcript view belongs to one captured note; a new turn
        // replaces it, so stale raw words are never shown against a new note.
        this.capturedNoteId.set(response.capturedNoteId ?? null);
        this.rawOpen.set(false);
        this.rawTranscript.set(null);
        this.rawLoading.set(false);

        if (response.acknowledgement) {
          // A capture's acknowledgement is an assistant entry: it keeps its
          // "Saved" status and the anchor label naming where the thought landed.
          this.pushEntry(
            'assistant',
            response.acknowledgement,
            this.anchorLabel({ ...context, anchor }),
            'Saved',
          );
        }

        if (response.reply) {
          this.turnLog.update((log) => [...log, { role: 'assistant', text: response.reply }]);
          this.pushEntry('assistant', response.reply, null, null, response.sources ?? []);
        }

        // A backend-requested follow-up arrives as the same deterministic prompt
        // the surface already knows how to ask: a page or timestamp, or the book
        // when no book is open and the app cannot know which one it is.
        if (response.anchorPrompt) {
          const kind = response.anchorPrompt.kind;
          if (kind === 'physical_page' || kind === 'external_audio_timestamp' || kind === 'book') {
            this.pendingText.set(text);
            this.pendingAnchor.set({ kind, question: response.anchorPrompt.question });
          }
          this.pushEntry('assistant', response.anchorPrompt.question, null, null);
        }
      },
      error: () => {
        this.sending.set(false);
        this.lastError.set('The assistant could not be reached. Your message is still in the composer to retry.');
        this.draft.set(text);
        // The turn never ran: forget it, so the history does not claim a turn
        // that the user is about to retry.
        this.forgetLastUserTurn(text);
        this.pushEntry('error', text, null, 'Not sent');
      },
    });
  }

  /** Undo a user turn that was logged but never reached the assistant. */
  private forgetLastUserTurn(text: string): void {
    this.turnLog.update((log) => {
      const last = log[log.length - 1];
      if (last && last.role === 'user' && last.text === text) return log.slice(0, -1);
      return log;
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
    kind: AssistantEntry['kind'],
    text: string,
    anchorLabel: string | null,
    meta: string | null,
    sources: AssistantSourceReferenceDto[] = [],
  ): void {
    entrySeq += 1;
    this.entries.update((entries) => [
      ...entries,
      { id: `a${entrySeq}`, kind, text, anchorLabel, meta, sources },
    ]);
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
 * Builds the wire context, applying the turn's anchor (the composer may have
 * answered a follow-up) over the ambient one.
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
 * Turn a follow-up answer into the anchor value a note stores. The answer
 * arrives on the one send path — often as a voice transcript, so as prose
 * ("Page 247.") rather than a bare number — and the note's `SourceAnchorValue`
 * is a page/timestamp, not a sentence. Cleaning it here keeps a second "answer
 * mode" from being needed anywhere else (issue #262 §6).
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
