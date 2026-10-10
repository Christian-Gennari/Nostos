import {
  projectContextualModelHistory,
  projectPlainModelHistory,
  projectVisibleTranscript,
  type AssistantConversationEvent,
} from './assistant-history';

function event(
  overrides: Partial<AssistantConversationEvent> = {},
): AssistantConversationEvent {
  return {
    id: 'event-1',
    turnId: 'turn-1',
    kind: 'user',
    text: 'Question',
    anchorLabel: null,
    meta: null,
    sources: [],
    suggestions: [],
    remember: true,
    delivery: 'complete',
    historyContext: null,
    historyEvidence: [],
    historyActions: [],
    historyCapturedNoteId: null,
    artifacts: [],
    ...overrides,
  };
}

describe('assistant history projections', () => {
  it('keeps uncertain delivery visible with all visible fields but excludes it from model history', () => {
    const uncertain = event({
      id: 'uncertain-event',
      text: 'Maybe delivered',
      anchorLabel: 'Book A · p. 42',
      meta: 'Delivery uncertain',
      sources: [
        {
          bookId: 'book-1',
          bookTitle: 'Book A',
          bookAuthor: 'Author',
          format: 'pdf',
          sourceSha256: 'sha-1',
          excerpt: 'Visible excerpt.',
          locators: [{ type: 'pdf', pdfPageIndex: 41, pdfPageLabel: '42' }],
        },
      ],
      suggestions: [
        {
          kind: 'topic',
          label: 'Homecoming',
          reason: 'Shared theme.',
          value: 'topic-1',
          noteId: 'note-1',
        },
      ],
      delivery: 'retryable',
      artifacts: [
        { kind: 'failure', code: 'network', message: 'Uncertain.', retryable: true, state: 'failed' },
      ],
    });
    const completed = event({
      id: 'assistant-event',
      turnId: 'turn-2',
      kind: 'assistant',
      text: 'Known answer',
    });
    const ledger = [uncertain, completed];

    expect(projectVisibleTranscript(ledger)).toEqual([
      {
        id: 'uncertain-event',
        turnId: 'turn-1',
        kind: 'user',
        text: 'Maybe delivered',
        anchorLabel: 'Book A · p. 42',
        meta: 'Delivery uncertain',
        sources: uncertain.sources,
        suggestions: uncertain.suggestions,
        artifacts: uncertain.artifacts,
      },
      {
        id: 'assistant-event',
        turnId: 'turn-2',
        kind: 'assistant',
        text: 'Known answer',
        anchorLabel: null,
        meta: null,
        sources: [],
        suggestions: [],
        artifacts: [],
      },
    ]);
    expect(projectPlainModelHistory(ledger)).toEqual([
      { role: 'assistant', text: 'Known answer' },
    ]);
    expect(projectContextualModelHistory(ledger)).toEqual([
      { role: 'assistant', text: 'Known answer' },
    ]);
  });

  it('keeps Brain-initiated operations out of chat and future conversation history', () => {
    const inlineUser = event({
      id: 'inline-user', turnId: 'inline-1',
      text: 'Internal link command with topic IDs',
      hiddenFromTranscript: true,
    });
    const inlineReply = event({
      id: 'inline-reply', turnId: 'inline-1',
      kind: 'assistant', text: 'Possible connections.',
      suggestions: [{
        kind: 'topic', label: 'Justice', reason: 'A link.', value: 'topic-1', noteId: 'note-1',
      }],
    });
    const conversation = event({
      id: 'regular-user', turnId: 'turn-2', text: 'What am I reading?',
    });

    expect(projectVisibleTranscript([inlineUser, inlineReply, conversation]))
      .toEqual([expect.objectContaining({ id: 'regular-user', text: 'What am I reading?' })]);
    expect(projectPlainModelHistory([inlineUser, inlineReply, conversation]))
      .toEqual([{ role: 'user', text: 'What am I reading?' }]);
    expect(projectContextualModelHistory([inlineUser, inlineReply, conversation]))
      .toEqual([{ role: 'user', text: 'What am I reading?' }]);
  });

  it('attaches deduplicated artifact facts once to the historical user root', () => {
    const handle = {
      kind: 'book_text' as const,
      bookId: 'book-1',
      sourceSha256: 'sha-1',
      extractorVersion: 'nostos-book-text-v2',
      ordinal: 7,
    };
    const user = event({
      historyContext: {
        surface: 'reader',
        bookId: 'book-1',
        bookTitle: 'Book A',
        brainReviewNoteId: null,
        topic: null,
        collectionId: null,
      },
      historyEvidence: [
        {
          bookId: 'book-1',
          bookTitle: 'Book A',
          sourceSha256: 'sha-1',
          locators: [{ type: 'pdf', pdfPageIndex: 41, pdfPageLabel: '42' }],
        },
      ],
    });
    const assistant = event({
      id: 'event-2',
      kind: 'assistant',
      text: 'Done.',
      artifacts: [
        {
          kind: 'evidence',
          evidence: { handle, label: 'Book A', excerpt: 'First copy.' },
        },
        {
          kind: 'evidence',
          evidence: { handle: { ...handle }, label: 'Book A', excerpt: 'Duplicate copy.' },
        },
        { kind: 'action', capability: 'library_update_book', state: 'completed' },
        { kind: 'action', capability: 'library_update_book', state: 'completed' },
        {
          kind: 'destructive-result',
          planId: 'plan-1',
          summary: 'Apply changes',
          outcome: 'applied',
          capabilities: ['library_update_book', 'library_delete_collection'],
        },
        { kind: 'capture', noteId: 'note-42', acknowledgement: 'Saved.', state: 'saved' },
      ],
    });

    expect(projectContextualModelHistory([user, assistant])).toEqual([
      {
        role: 'user',
        text: 'Question',
        context: user.historyContext,
        evidence: user.historyEvidence,
        evidenceHandles: [handle],
        actions: ['library_update_book', 'library_delete_collection'],
        capturedNoteId: 'note-42',
      },
      {
        role: 'assistant',
        text: 'Done.',
      },
    ]);
  });

  it('preserves legacy root facts when a restored turn has no replacement artifact facts', () => {
    const restored = event({
      text: 'Older question',
      historyEvidence: [
        {
          bookId: 'book-old',
          bookTitle: 'Older Book',
          sourceSha256: 'sha-old',
          locators: [],
        },
      ],
      historyActions: ['library_update_book'],
      historyCapturedNoteId: 'note-old',
      artifacts: [],
    });

    expect(projectContextualModelHistory([restored])).toEqual([
      {
        role: 'user',
        text: 'Older question',
        evidence: restored.historyEvidence,
        actions: ['library_update_book'],
        capturedNoteId: 'note-old',
      },
    ]);
  });
});
