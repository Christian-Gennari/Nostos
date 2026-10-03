import type {
  AssistantEntry,
  AssistantEvidenceHandleDto,
  AssistantHistoricalContextDto,
  AssistantHistoricalEvidenceDto,
  AssistantHistoryMessage,
  AssistantTurnArtifact,
} from './assistant.service';

export type AssistantEventDelivery = 'sending' | 'complete' | 'retryable';

/**
 * Canonical in-memory/session event shape. The service owns all mutations;
 * this module only projects the ledger into transcript and model-facing views.
 */
export interface AssistantConversationEvent extends AssistantEntry {
  remember: boolean;
  delivery: AssistantEventDelivery;
  historyContext: AssistantHistoricalContextDto | null;
  historyEvidence: AssistantHistoricalEvidenceDto[];
  historyActions: string[];
  historyCapturedNoteId: string | null;
  artifacts: AssistantTurnArtifact[];
}

/** Preserve every visible event field while removing ledger-only projection metadata. */
export function projectVisibleTranscript(
  ledger: readonly AssistantConversationEvent[],
): AssistantEntry[] {
  return ledger.map(
    ({
      remember: _remember,
      delivery: _delivery,
      historyContext: _historyContext,
      historyEvidence: _historyEvidence,
      historyActions: _historyActions,
      historyCapturedNoteId: _historyCapturedNoteId,
      ...entry
    }) => entry,
  );
}

/** Plain conversational history exposed to diagnostics and existing UI consumers. */
export function projectPlainModelHistory(
  ledger: readonly AssistantConversationEvent[],
): AssistantHistoryMessage[] {
  return ledger
    .filter((event) => event.remember && event.delivery !== 'retryable')
    .map((event) => ({
      role: event.kind === 'user' ? 'user' : 'assistant',
      text: event.text,
    }));
}

/**
 * Enriched wire history. Turn artifacts reduce to inert facts on the historical
 * user root; assistant prose remains text-only.
 */
export function projectContextualModelHistory(
  ledger: readonly AssistantConversationEvent[],
): AssistantHistoryMessage[] {
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
        ...(event.kind === 'user' && event.historyEvidence.length > 0
          ? { evidence: event.historyEvidence }
          : {}),
        ...(event.kind === 'user' && facts.evidenceHandles.length > 0
          ? { evidenceHandles: facts.evidenceHandles }
          : {}),
        ...(event.kind === 'user'
          && (facts.actions.length > 0 ? facts.actions : event.historyActions).length > 0
          ? { actions: facts.actions.length > 0 ? facts.actions : event.historyActions }
          : {}),
        ...(event.kind === 'user' && (facts.capturedNoteId ?? event.historyCapturedNoteId)
          ? { capturedNoteId: facts.capturedNoteId ?? event.historyCapturedNoteId }
          : {}),
      };
    });
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

function evidenceHandleKey(handle: AssistantEvidenceHandleDto): string {
  return [
    handle.kind,
    handle.noteId ?? '',
    handle.topicId ?? '',
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
