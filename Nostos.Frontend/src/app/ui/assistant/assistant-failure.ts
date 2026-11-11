/**
 * One failure, one next step (#1556).
 *
 * The backend owns the sentence and the typed code; this module owns the one
 * action that fits that code. Keeping the table here (rather than in the
 * template) means every code the backend can send has exactly one decision,
 * and `assistant-failure.spec.ts` can pin all of them.
 */

export type AssistantFailureAction =
  | { kind: 'retry'; label: 'Try again' }
  | { kind: 'connections'; label: 'Open AI connections' }
  | { kind: 'settings'; label: 'Open Settings' }
  | { kind: 'allowance'; label: 'Manage allowance'; href: string }
  | { kind: 'book'; label: 'Open the book'; bookId: string };

export interface AssistantFailureContext {
  /** The backend's own retryable flag; it outranks the static table. */
  retryable: boolean;
  /** SelfHosted can point at Settings → AI connections; a managed host cannot. */
  canConfigureAiProvider: boolean;
  /** The existing Cloud account link (Settings → Ask Nostos usage). */
  accountManagementUrl: string | null;
  /** The book this turn was about, when the turn carried one. */
  bookId: string | null;
}

/**
 * Provider or transport trouble that can succeed on a second delivery.
 *
 * The backend's `retryable` flag is the authority; this table is the fallback
 * for a response that omits it (and for the client's own transport codes).
 */
const RETRY_CODES = new Set([
  'assistant_provider_timeout',
  'assistant_provider_error',
  'assistant_rate_limited',
  'assistant_response_invalid',
  // Client-side transport states, so a dropped stream gets the same control.
  'assistant_network_error',
  'assistant_offline',
  'assistant_stream_stalled',
  'assistant_turn_failed',
  // Managed accounting: the host can recover on its own.
  'ai_rate_limited',
  'ai_temporarily_unavailable',
  'ai_disabled',
]);

/** The user or the host owner must change the AI provider configuration. */
const CONNECTION_CODES = new Set([
  'assistant_not_configured',
  'assistant_permission_denied',
]);

/** The managed allowance, or the account it belongs to, must be managed. */
const ALLOWANCE_CODES = new Set(['ai_usage_limit_reached']);
const ACCOUNT_CODES = new Set(['assistant_access_denied', 'ai_access_denied']);

/** The failure is about one book's searchable text, so open that book. */
const BOOK_CODES = new Set([
  'assistant_no_evidence',
  'assistant_source_indexing_pending',
  'assistant_source_indexing_failed',
  'assistant_source_indexing_unsupported',
]);

/** Only the host owner can change these; Settings is the honest destination. */
const SETTINGS_CODES = new Set(['assistant_disabled']);

const SETTINGS: AssistantFailureAction = { kind: 'settings', label: 'Open Settings' };
const RETRY: AssistantFailureAction = { kind: 'retry', label: 'Try again' };
const CONNECTIONS: AssistantFailureAction = {
  kind: 'connections',
  label: 'Open AI connections',
};

function allowanceAction(accountManagementUrl: string | null): AssistantFailureAction {
  return accountManagementUrl
    ? { kind: 'allowance', label: 'Manage allowance', href: accountManagementUrl }
    : SETTINGS;
}

function bookOrSettings(bookId: string | null): AssistantFailureAction {
  return bookId
    ? { kind: 'book', label: 'Open the book', bookId }
    : SETTINGS;
}

/**
 * The one next step for a failure code.
 *
 * Precedence is deliberate: the backend's `retryable` flag is the truth about
 * delivery, then the codes that need a specific surface (allowance, provider,
 * book), then restartable domain refusals. A domain refusal that cannot be
 * retried still gets somewhere concrete: the book the turn was about, or
 * Settings. Cancelled and partially-completed turns never reach this function:
 * they are not failures and are not rendered as such.
 */
export function failureAction(
  code: string,
  context: AssistantFailureContext,
): AssistantFailureAction {
  const normalized = code.trim();

  if (ALLOWANCE_CODES.has(normalized)) {
    return allowanceAction(context.accountManagementUrl);
  }

  if (ACCOUNT_CODES.has(normalized)) {
    return allowanceAction(context.accountManagementUrl);
  }

  if (CONNECTION_CODES.has(normalized)) {
    if (context.canConfigureAiProvider) return CONNECTIONS;
    // On a managed host there is no AI connections page. "Not configured" is
    // reported there as a temporary host problem, so a retry is honest; a
    // non-retryable one at least leads to the assistant's own Settings card.
    return context.retryable ? RETRY : SETTINGS;
  }

  if (BOOK_CODES.has(normalized)) {
    return bookOrSettings(context.bookId);
  }

  if (SETTINGS_CODES.has(normalized)) return SETTINGS;

  if (context.retryable || RETRY_CODES.has(normalized)) return RETRY;

  // Everything else is a domain refusal: a stale continuation, a refused
  // step, a busy changeset, a ceiling stop. None is a configuration problem,
  // so open the book the turn was about, or Settings when it had none.
  return bookOrSettings(context.bookId);
}
