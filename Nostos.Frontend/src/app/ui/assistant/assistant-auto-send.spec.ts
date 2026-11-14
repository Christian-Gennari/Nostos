import { AssistantAutoSend, TRANSCRIPT_AUTO_SEND_DELAY_MS } from './assistant-auto-send';
import { AssistantRetryState, retryMatchesTurn } from './assistant-retry';
import type { AssistantContext } from './assistant-context.service';
import type { PreparedAssistantTurn } from './assistant-session';

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

function prepared(overrides: Partial<PreparedAssistantTurn> = {}): PreparedAssistantTurn {
  return {
    turnId: 'turn-1',
    text: 'Where is the harbour?',
    context: CONTEXT,
    displayAnchor: null,
    userEntryId: 'entry-1',
    continuationPrompt: null,
    request: {
      clientId: 'conversation-1',
      idempotencyKey: 'turn-1',
      conversationId: 'conversation-1',
      turnId: 'turn-1',
      message: 'Where is the harbour?',
      context: { ...CONTEXT, anchor: null, captureBookTitle: null },
      continuationId: null,
      continuationSkipped: false,
      history: [],
    },
    ...overrides,
  };
}

describe('AssistantAutoSend', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('dispatches once after the grace window and clears its pending flag', () => {
    const autoSend = new AssistantAutoSend();
    const dispatch = vi.fn();

    autoSend.schedule(TRANSCRIPT_AUTO_SEND_DELAY_MS, dispatch);
    expect(autoSend.pending()).toBe(true);

    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS - 1);
    expect(dispatch).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect(dispatch).toHaveBeenCalledTimes(1);
    expect(autoSend.pending()).toBe(false);
  });

  it('cancels the dispatch before anything is sent', () => {
    const autoSend = new AssistantAutoSend();
    const dispatch = vi.fn();

    autoSend.schedule(TRANSCRIPT_AUTO_SEND_DELAY_MS, dispatch);
    autoSend.cancel();
    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);

    expect(dispatch).not.toHaveBeenCalled();
    expect(autoSend.pending()).toBe(false);
  });

  it('clears the timer on destroy', () => {
    const autoSend = new AssistantAutoSend();
    const dispatch = vi.fn();

    autoSend.schedule(TRANSCRIPT_AUTO_SEND_DELAY_MS, dispatch);
    autoSend.destroy();
    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);

    expect(dispatch).not.toHaveBeenCalled();
  });
});

describe('AssistantRetryState', () => {
  it('matches only the exact text and continuation the turn was prepared with', () => {
    const turn = prepared();

    expect(retryMatchesTurn(turn, 'Where is the harbour?', null, false)).toBe(true);
    expect(retryMatchesTurn(turn, 'Where is the harbour? ', null, false)).toBe(false);
    expect(retryMatchesTurn(turn, 'Where is the harbour?', 'cont-1', false)).toBe(false);
    expect(retryMatchesTurn(null, 'Where is the harbour?', null, false)).toBe(false);
  });

  it('arms and consumes a continuation with its own context', () => {
    const retry = new AssistantRetryState();
    retry.armContinuation(
      { kind: 'physical_page', question: 'Which page?', continuationId: 'cont-1' },
      CONTEXT,
    );

    expect(retry.pendingAnchor()?.continuationId).toBe('cont-1');
    const taken = retry.takeContinuation({ ...CONTEXT, surface: 'library' });
    expect(taken?.prompt.continuationId).toBe('cont-1');
    expect(taken?.context.surface).toBe('reader');
    expect(retry.pendingAnchor()).toBeNull();
  });

  it('clears the retry on reset', () => {
    const retry = new AssistantRetryState();
    retry.remember(prepared());
    retry.armContinuation(
      { kind: 'book', question: 'Which book?', continuationId: 'cont-2' },
      CONTEXT,
    );

    retry.reset();

    expect(retry.retryableTurn()).toBeNull();
    expect(retry.pendingAnchor()).toBeNull();
    expect(retry.retryableTurnId()).toBeNull();
  });
});
