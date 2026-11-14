/**
 * Tab-scoped conversation persistence for Ask Nostos.
 *
 * The transcript is convenient, never authoritative: a browser with no
 * `sessionStorage`, a full quota or a stale schema still runs the conversation
 * in memory. This module owns that boundary — the storage key, the persisted
 * schema, the validation that keeps a malformed or older ledger out, and the
 * serialization — so `AssistantService` only applies a restored state.
 *
 * A reload can interrupt a request after the server committed but before the
 * answer arrived. The persisted turn is therefore kept, and restored as
 * delivery-uncertain with its exact TurnId, so a retry reuses the server's
 * mutation receipts instead of writing twice.
 */
import type { AssistantAnchor, AssistantContext } from './assistant-context.service';
import type { AssistantConversationEvent, AssistantEventDelivery } from './assistant-history';
import type {
  AssistantAnchorPrompt,
  AssistantSuggestionDto,
  AssistantTurnArtifact,
} from './assistant.service';
import type { AssistantTurnRequestDto } from './assistant-wire';

/** Session-scoped persistence only; closing the browser tab ends the conversation. */
export const ASSISTANT_SESSION_STORAGE_KEY = 'nostos.ask-nostos.session.v1';

/** A prepared logical turn, persisted before transport begins. */
export interface PreparedAssistantTurn {
  turnId: string;
  text: string;
  request: AssistantTurnRequestDto;
  context: AssistantContext;
  displayAnchor: AssistantAnchor | null;
  userEntryId: string;
  /** Restored if delivery fails while answering a deterministic continuation. */
  continuationPrompt: AssistantAnchorPrompt | null;
}

/** The exact session shape that may be written to storage. */
export interface PersistedAssistantSession {
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

/** A validated session, ready for the service's signals. */
export interface RestoredAssistantSession {
  conversationId: string;
  eventLedger: AssistantConversationEvent[];
  draft: string;
  pendingAnchor: AssistantAnchorPrompt | null;
  pendingContinuationContext: AssistantContext | null;
  capturedNoteId: string | null;
  retryableTurn: PreparedAssistantTurn | null;
}

/** `sessionStorage`, or null where the browser refuses it (privacy mode). */
export function sessionStorageOrNull(): Storage | null {
  try {
    return typeof globalThis.sessionStorage === 'undefined'
      ? null
      : globalThis.sessionStorage;
  } catch {
    return null;
  }
}

/**
 * Reads and validates the persisted session. A schema mismatch or unreadable
 * value is discarded at once: a half-applied ledger would be worse than a
 * fresh conversation.
 */
export function readAssistantSession(
  storage: Storage | null = sessionStorageOrNull(),
): RestoredAssistantSession | null {
  if (!storage) return null;

  let raw: string | null = null;
  try {
    raw = storage.getItem(ASSISTANT_SESSION_STORAGE_KEY);
  } catch {
    return null;
  }
  if (!raw) return null;

  try {
    const parsed = JSON.parse(raw) as Partial<PersistedAssistantSession>;
    if (
      parsed.version !== 1 ||
      typeof parsed.conversationId !== 'string' ||
      !parsed.conversationId.trim() ||
      !Array.isArray(parsed.eventLedger)
    ) {
      storage.removeItem(ASSISTANT_SESSION_STORAGE_KEY);
      return null;
    }

    const eventLedger = parsed.eventLedger
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

    const restoredRetry = isPreparedTurn(parsed.retryableTurn)
      ? parsed.retryableTurn
      : null;

    let draft = typeof parsed.draft === 'string' ? parsed.draft : '';
    if (restoredRetry) {
      // Never place an internal Brain operation (including topic IDs) in the
      // composer.
      const inlineTurn = eventLedger.some(
        (event) => event.turnId === restoredRetry.turnId && event.hiddenFromTranscript,
      );
      draft = inlineTurn ? '' : restoredRetry.text;
      markDelivery(eventLedger, restoredRetry.userEntryId, 'retryable', 'Delivery uncertain');
    }

    const pendingAnchor = restoredRetry?.continuationPrompt ?? parsed.pendingAnchor;
    const pendingContinuationContext = restoredRetry?.continuationPrompt
      ? restoredRetry.context
      : parsed.pendingContinuationContext;

    return {
      conversationId: parsed.conversationId,
      eventLedger,
      draft,
      pendingAnchor: isAnchorPrompt(pendingAnchor) ? pendingAnchor : null,
      pendingContinuationContext: isAssistantContext(pendingContinuationContext)
        ? pendingContinuationContext
        : null,
      capturedNoteId: typeof parsed.capturedNoteId === 'string' ? parsed.capturedNoteId : null,
      retryableTurn: restoredRetry,
    };
  } catch {
    try {
      storage.removeItem(ASSISTANT_SESSION_STORAGE_KEY);
    } catch {
      // Storage is already unavailable; nothing else to clean up.
    }
    return null;
  }
}

/** Serializes the bounded session shape; validation happens on the way back in. */
export function serializeAssistantSession(state: PersistedAssistantSession): string {
  return JSON.stringify(state);
}

/**
 * Writes the session, swallowing quota/privacy failures. Persistence is a
 * convenience, not an execution or safety dependency.
 */
export function writeAssistantSession(
  state: PersistedAssistantSession,
  storage: Storage | null = sessionStorageOrNull(),
): boolean {
  if (!storage) return false;
  try {
    storage.setItem(ASSISTANT_SESSION_STORAGE_KEY, serializeAssistantSession(state));
    return true;
  } catch {
    return false;
  }
}

function markDelivery(
  events: AssistantConversationEvent[],
  entryId: string,
  delivery: AssistantEventDelivery,
  meta: string | null,
): void {
  const index = events.findIndex((event) => event.id === entryId);
  if (index >= 0) events[index] = { ...events[index], delivery, meta };
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
    (request.history === undefined || Array.isArray(request.history)) &&
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
      const changeset = (artifact as { changeset?: { changesetId?: unknown; status?: unknown; steps?: unknown; diff?: unknown } }).changeset;
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
