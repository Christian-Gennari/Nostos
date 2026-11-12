import { failureAction, type AssistantFailureAction } from './assistant-failure';

interface Case {
  code: string;
  retryable: boolean;
  action: AssistantFailureAction['kind'];
}

/**
 * Every code the assistant backend can put in a failure event, plus the
 * client's own transport codes. `Retryable` is the flag the backend sends:
 * `AssistantEndpoints.Retryable`, `AiUsageException`, and the client transport
 * paths. These are also listed in the PR body.
 */
const CASES: Case[] = [
  // LlmErrorCodes (`Nostos.Product/Services/Ai/ILlmProvider.cs`).
  { code: 'assistant_disabled', retryable: false, action: 'settings' },
  { code: 'assistant_not_configured', retryable: false, action: 'connections' },
  { code: 'assistant_access_denied', retryable: false, action: 'allowance' },
  { code: 'assistant_permission_denied', retryable: false, action: 'connections' },
  { code: 'assistant_rate_limited', retryable: true, action: 'retry' },
  { code: 'assistant_provider_timeout', retryable: true, action: 'retry' },
  { code: 'assistant_provider_error', retryable: true, action: 'retry' },
  { code: 'assistant_response_invalid', retryable: true, action: 'retry' },

  // Managed accounting (`AiUsageException`).
  { code: 'ai_access_denied', retryable: false, action: 'allowance' },
  { code: 'ai_rate_limited', retryable: true, action: 'retry' },
  { code: 'ai_usage_limit_reached', retryable: false, action: 'allowance' },
  { code: 'ai_disabled', retryable: true, action: 'retry' },
  { code: 'ai_temporarily_unavailable', retryable: true, action: 'retry' },

  // AssistantErrorCodes (`AssistantCapability.cs`), as terminal turn errors.
  { code: 'assistant_unknown_capability', retryable: false, action: 'book' },
  { code: 'assistant_changeset_unavailable', retryable: false, action: 'book' },
  { code: 'assistant_changeset_not_found', retryable: false, action: 'book' },
  { code: 'assistant_changeset_token_mismatch', retryable: false, action: 'book' },
  { code: 'assistant_changeset_not_reviewable', retryable: false, action: 'book' },
  { code: 'assistant_changeset_not_undoable', retryable: false, action: 'book' },
  { code: 'assistant_changeset_busy', retryable: false, action: 'book' },
  { code: 'assistant_not_permitted', retryable: false, action: 'book' },
  { code: 'assistant_invalid_arguments', retryable: false, action: 'book' },
  { code: 'assistant_not_found', retryable: false, action: 'book' },
  { code: 'assistant_continuation_not_found', retryable: false, action: 'book' },
  { code: 'assistant_continuation_mismatch', retryable: false, action: 'book' },
  { code: 'assistant_continuation_answer_required', retryable: false, action: 'book' },
  { code: 'invalid_processing_mode', retryable: false, action: 'book' },
  { code: 'assistant_turn_already_running', retryable: false, action: 'book' },
  // Cancelled and partially-completed turns are not rendered as failures, but
  // if one ever reaches this table it still has a concrete destination.
  { code: 'assistant_turn_cancelled', retryable: false, action: 'book' },
  { code: 'assistant_execution_budget_exhausted', retryable: false, action: 'book' },
  { code: 'assistant_turn_partially_completed', retryable: false, action: 'book' },
  { code: 'assistant_unverified_completion_claim', retryable: false, action: 'book' },
  { code: 'assistant_no_evidence', retryable: false, action: 'book' },
  { code: 'assistant_source_indexing_pending', retryable: false, action: 'book' },
  { code: 'assistant_source_indexing_failed', retryable: false, action: 'book' },
  { code: 'assistant_source_indexing_unsupported', retryable: false, action: 'book' },

  // Client transport codes (`assistant.service.ts`).
  { code: 'assistant_network_error', retryable: true, action: 'retry' },
  { code: 'assistant_offline', retryable: true, action: 'retry' },
  { code: 'assistant_stream_stalled', retryable: true, action: 'retry' },
  { code: 'assistant_turn_failed', retryable: true, action: 'retry' },
];

function context(overrides: Partial<Parameters<typeof failureAction>[1]> = {}) {
  return {
    retryable: false,
    canConfigureAiProvider: true,
    accountManagementUrl: 'https://nostos.page/account',
    bookId: 'book-1',
    ...overrides,
  };
}

describe('failureAction (#1556)', () => {
  it('gives every backend and transport code exactly one fitting action', () => {
    for (const item of CASES) {
      const action = failureAction(item.code, context({ retryable: item.retryable }));
      expect(action.kind, `${item.code} should ${item.action}`).toBe(item.action);
      expect(action.label.trim(), item.code).not.toBe('');
    }
  });

  it('keeps the account link for allowance and account failures', () => {
    expect(failureAction('ai_usage_limit_reached', context())).toEqual({
      kind: 'allowance',
      label: 'Manage allowance',
      href: 'https://nostos.page/account',
    });
    expect(failureAction('ai_access_denied', context())).toEqual({
      kind: 'allowance',
      label: 'Manage allowance',
      href: 'https://nostos.page/account',
    });
  });

  it('falls back to Settings when there is no account link', () => {
    expect(failureAction('ai_usage_limit_reached', context({ accountManagementUrl: null })))
      .toEqual({ kind: 'settings', label: 'Open Settings' });
  });

  it('does not send a managed host to an AI connections page it does not have', () => {
    expect(failureAction('assistant_not_configured', context({ canConfigureAiProvider: false })))
      .toEqual({ kind: 'settings', label: 'Open Settings' });
    expect(failureAction('assistant_permission_denied', context({
      canConfigureAiProvider: false,
      retryable: true,
    }))).toEqual({ kind: 'retry', label: 'Try again' });
  });

  it('opens the book for indexing and evidence failures, and Settings without one', () => {
    expect(failureAction('assistant_source_indexing_pending', context())).toEqual({
      kind: 'book',
      label: 'Open the book',
      bookId: 'book-1',
    });
    expect(failureAction('assistant_no_evidence', context({ bookId: null })))
      .toEqual({ kind: 'settings', label: 'Open Settings' });
  });

  it('trusts the backend retryable flag over the static table', () => {
    expect(failureAction('assistant_execution_budget_exhausted', context({ retryable: true })))
      .toEqual({ kind: 'retry', label: 'Try again' });
    expect(failureAction('assistant_provider_timeout', context({ retryable: false })))
      .toEqual({ kind: 'retry', label: 'Try again' });
  });

  it('defaults an unknown non-retryable code to somewhere concrete', () => {
    expect(failureAction('assistant_future_code', context())).toEqual({
      kind: 'book',
      label: 'Open the book',
      bookId: 'book-1',
    });
    expect(failureAction('assistant_future_code', context({ bookId: null })))
      .toEqual({ kind: 'settings', label: 'Open Settings' });
  });
});
