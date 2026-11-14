import {
  ASSISTANT_SESSION_STORAGE_KEY,
  readAssistantSession,
  serializeAssistantSession,
  writeAssistantSession,
  type PersistedAssistantSession,
} from './assistant-session';
import type { AssistantContext } from './assistant-context.service';
import type { AssistantConversationEvent } from './assistant-history';

class MemoryStorage implements Storage {
  private readonly values = new Map<string, string>();

  get length(): number {
    return this.values.size;
  }

  clear(): void {
    this.values.clear();
  }

  getItem(key: string): string | null {
    return this.values.get(key) ?? null;
  }

  key(index: number): string | null {
    return [...this.values.keys()][index] ?? null;
  }

  removeItem(key: string): void {
    this.values.delete(key);
  }

  setItem(key: string, value: string): void {
    this.values.set(key, value);
  }
}

const CONTEXT: AssistantContext = {
  surface: 'reader',
  route: '/read/b1',
  bookId: 'b1',
  bookTitle: 'The Magic Mountain',
  bookFormat: null,
  readerType: null,
  epubCfi: null,
  pdfPage: null,
  audioTimestamp: null,
  audioChapter: null,
  selectedText: null,
  readingTarget: 'b1',
  brainReviewNoteId: null,
  topic: null,
  collectionId: null,
  anchor: null,
};

function event(overrides: Partial<AssistantConversationEvent> = {}): AssistantConversationEvent {
  return {
    id: 'entry-1',
    turnId: 'turn-1',
    kind: 'user',
    text: 'Where is the harbour?',
    anchorLabel: null,
    meta: null,
    sources: [],
    suggestions: [],
    artifacts: [],
    remember: true,
    delivery: 'complete',
    historyContext: null,
    historyEvidence: [],
    historyActions: [],
    historyCapturedNoteId: null,
    ...overrides,
  };
}

function session(overrides: Partial<PersistedAssistantSession> = {}): PersistedAssistantSession {
  return {
    version: 1,
    conversationId: 'conversation-1',
    eventLedger: [event()],
    draft: 'a draft',
    pendingAnchor: null,
    pendingContinuationContext: null,
    suggestions: [],
    capturedNoteId: null,
    retryableTurn: null,
    ...overrides,
  };
}

describe('assistant session persistence', () => {
  let storage: MemoryStorage;

  beforeEach(() => {
    storage = new MemoryStorage();
  });

  it('round-trips the bounded session shape', () => {
    expect(writeAssistantSession(session(), storage)).toBe(true);

    const stored = storage.getItem(ASSISTANT_SESSION_STORAGE_KEY);
    expect(stored).toBe(serializeAssistantSession(session()));

    const restored = readAssistantSession(storage);
    expect(restored?.conversationId).toBe('conversation-1');
    expect(restored?.eventLedger.map((item) => item.text)).toEqual(['Where is the harbour?']);
    expect(restored?.draft).toBe('a draft');
  });

  it('discards a schema mismatch instead of applying half of it', () => {
    storage.setItem(ASSISTANT_SESSION_STORAGE_KEY, JSON.stringify({ version: 2 }));

    expect(readAssistantSession(storage)).toBeNull();
    expect(storage.getItem(ASSISTANT_SESSION_STORAGE_KEY)).toBeNull();
  });

  it('keeps the valid ledger and drops events that do not match the schema', () => {
    storage.setItem(ASSISTANT_SESSION_STORAGE_KEY, JSON.stringify(session({
      eventLedger: [
        event(),
        { id: 'broken' } as unknown as AssistantConversationEvent,
      ],
    })));

    expect(readAssistantSession(storage)?.eventLedger).toHaveLength(1);
  });

  it('treats an interrupted send as delivery-uncertain on restore', () => {
    storage.setItem(ASSISTANT_SESSION_STORAGE_KEY, JSON.stringify(session({
      eventLedger: [event({ delivery: 'sending' })],
    })));

    const restored = readAssistantSession(storage);
    expect(restored?.eventLedger[0].delivery).toBe('retryable');
    expect(restored?.eventLedger[0].meta).toBe('Delivery uncertain');
  });

  it('restores a prepared turn and its continuation instead of a fresh draft', () => {
    const prepared = {
      turnId: 'turn-9',
      text: 'Page 42',
      userEntryId: 'entry-9',
      context: CONTEXT,
      displayAnchor: null,
      continuationPrompt: {
        kind: 'physical_page' as const,
        question: 'Which page?',
        continuationId: 'cont-9',
      },
      request: {
        clientId: 'conversation-1',
        idempotencyKey: 'turn-9',
        conversationId: 'conversation-1',
        turnId: 'turn-9',
        message: 'Page 42',
        context: { ...CONTEXT, anchor: null, captureBookTitle: null },
        continuationId: 'cont-9',
        continuationSkipped: false,
        history: [],
      },
    };

    storage.setItem(ASSISTANT_SESSION_STORAGE_KEY, JSON.stringify(session({
      eventLedger: [
        event({ id: 'entry-9', turnId: 'turn-9', kind: 'user', text: 'Save this quote' }),
      ],
      draft: 'stale',
      retryableTurn: prepared,
    })));

    const restored = readAssistantSession(storage);
    expect(restored?.retryableTurn?.turnId).toBe('turn-9');
    expect(restored?.draft).toBe('Page 42');
    expect(restored?.pendingAnchor?.continuationId).toBe('cont-9');
    expect(restored?.eventLedger[0].delivery).toBe('retryable');
  });

  it('reports failure without throwing when storage refuses the write', () => {
    const refusing = {
      getItem: () => null,
      setItem: () => { throw new Error('quota'); },
      removeItem: () => undefined,
    } as unknown as Storage;

    expect(writeAssistantSession(session(), refusing)).toBe(false);
  });

  it('returns null with no storage at all', () => {
    expect(readAssistantSession(null)).toBeNull();
    expect(writeAssistantSession(session(), null)).toBe(false);
  });
});
