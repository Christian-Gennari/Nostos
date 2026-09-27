import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import {
  AssistantHistoryMessage,
  AssistantService,
  AssistantTurnResponse,
  HISTORY_MAX_CHARS,
  HISTORY_MAX_EXCHANGES,
  TRANSCRIPT_AUTO_SEND_DELAY_MS,
  TRANSCRIPT_SEND_POLICY,
  isExplicitPlanApproval,
  isExplicitPlanRejection,
} from './assistant.service';
import { AssistantContext, AssistantContextService } from './assistant-context.service';

/**
 * The conversation now flows through the assistant bridge
 * (`POST /api/assistant/turn`), so "typed" and "transcribed" are compared on the
 * wire, not on a mock's arguments.
 */

function context(overrides: Partial<AssistantContext> = {}): AssistantContext {
  return {
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
    concept: null,
    collectionId: null,
    anchor: null,
    ...overrides,
  };
}

function fakeContextService(initial: Partial<AssistantContext>) {
  const state = signal<AssistantContext>(context(initial));
  return {
    context: state.asReadonly(),
    set: (overrides: Partial<AssistantContext>) => state.set(context(overrides)),
  };
}

/** A minimal successful turn: a short reply and nothing else. */
function turn(overrides: Partial<AssistantTurnResponse> = {}): AssistantTurnResponse {
  return {
    reply: 'Noted.',
    acknowledgement: null,
    anchorPrompt: null,
    suggestions: [],
    pendingPlan: null,
    capturedNoteId: null,
    ...overrides,
  };
}

describe('AssistantService voice transcript alignment', () => {
  let service: AssistantService;
  let http: HttpTestingController;
  let fake: ReturnType<typeof fakeContextService>;

  beforeEach(() => {
    fake = fakeContextService({});
    TestBed.configureTestingModule({
      providers: [
        { provide: AssistantContextService, useValue: fake },
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(AssistantService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  it('defaults to auto-send', () => {
    expect(TRANSCRIPT_SEND_POLICY).toBe('auto');
  });

  it('auto-sends a transcript after the grace window with no user action', () => {
    vi.useFakeTimers();
    service.open();

    service.insertTranscript('The Magic Mountain');

    // While the window is open the text is in the composer and nothing is sent.
    expect(service.draft()).toBe('The Magic Mountain');
    expect(service.autoSendPending()).toBe(true);
    http.expectNone('/api/assistant/turn');

    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);

    expect(service.autoSendPending()).toBe(false);
    const request = http.expectOne('/api/assistant/turn');
    expect(request.request.method).toBe('POST');
    expect(request.request.body.message).toBe('The Magic Mountain');
    request.flush(turn());
  });

  it('Undo inside the window results in NO send at all, text still editable', () => {
    vi.useFakeTimers();
    service.open();
    service.insertTranscript('The Magic Mountain');
    expect(service.autoSendPending()).toBe(true);

    service.undoTranscript();

    expect(service.autoSendPending()).toBe(false);
    // Well past the window: the cancelled dispatch must never fire.
    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS * 4);
    http.expectNone('/api/assistant/turn');
    expect(service.draft()).toBe('The Magic Mountain');
  });

  it('sends a transcribed message through the IDENTICAL request as a typed one', () => {
    const text = 'The Magic Mountain';
    vi.useFakeTimers();

    service.open();
    service.updateDraft(text);
    service.submit();
    const typed = http.expectOne('/api/assistant/turn');
    const typedRequest = typed.request;
    typed.flush(turn());

    service.insertTranscript(text);
    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);
    const transcribed = http.expectOne('/api/assistant/turn');

    // The idempotency key is unique per turn by design; everything that carries
    // meaning — the endpoint, the message and the resolved context — is identical.
    expect(transcribed.request.method).toBe(typedRequest.method);
    expect(transcribed.request.url).toBe(typedRequest.url);
    expect(transcribed.request.body.message).toBe(typedRequest.body.message);
    expect(transcribed.request.body.context).toEqual(typedRequest.body.context);
    expect(transcribed.request.body.clientId).toBe(typedRequest.body.clientId);
    transcribed.flush(turn());
  });

  it('auto-sends a voice answer to a pending follow-up in the same conversation', () => {
    vi.useFakeTimers();
    fake.set({ bookFormat: 'physical' });
    service.open();
    service.updateDraft('A thought I cannot place');
    service.submit();

    // A capture that cannot know a page is dispatched first; the backend is what
    // decides to ask for one, and the prompt arrives on the turn response.
    http.expectOne('/api/assistant/turn').flush(
      turn({ anchorPrompt: { kind: 'physical_page', question: 'What page are you on?', continuationId: 'cont-page' } }),
    );
    expect(service.pendingAnchor()?.question).toBe('What page are you on?');

    service.insertTranscript('247');
    expect(service.draft()).toBe('247');
    expect(service.autoSendPending()).toBe(true);
    http.expectNone('/api/assistant/turn');

    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);

    const request = http.expectOne('/api/assistant/turn');
    expect(request.request.body.message).toBe('247');
    expect(request.request.body.continuationId).toBe('cont-page');
    expect(request.request.body.context.anchor).toBeNull();
    request.flush(turn());

    expect(
      service.history().filter((message) => message.role === 'user').map((message) => message.text),
    ).toEqual(['A thought I cannot place', '247']);
  });

  it('retries a continuation answer with the same TurnId and no duplicate user event', () => {
    fake.set({ bookFormat: 'physical' });
    service.updateDraft('A thought I cannot place');
    service.submit();

    http.expectOne('/api/assistant/turn').flush(
      turn({
        reply: 'What page are you on?',
        anchorPrompt: {
          kind: 'physical_page',
          question: 'What page are you on?',
          continuationId: 'cont-retry',
        },
      }),
    );

    service.updateDraft('247');
    service.submit();
    const firstAnswer = http.expectOne('/api/assistant/turn');
    const answerTurnId = firstAnswer.request.body.turnId;
    firstAnswer.error(new ProgressEvent('error'));

    expect(service.pendingAnchor()?.continuationId).toBe('cont-retry');
    expect(service.draft()).toBe('247');
    expect(
      service.entries().filter((entry) => entry.kind === 'user' && entry.text === '247'),
    ).toHaveLength(1);

    service.submit();
    const retry = http.expectOne('/api/assistant/turn');
    expect(retry.request.body.turnId).toBe(answerTurnId);
    expect(retry.request.body.idempotencyKey).toBe(answerTurnId);
    expect(retry.request.body.message).toBe('247');
    expect(retry.request.body.continuationId).toBe('cont-retry');
    expect(
      service.entries().filter((entry) => entry.kind === 'user' && entry.text === '247'),
    ).toHaveLength(1);

    retry.flush(turn({ reply: '', acknowledgement: 'Saved to The Magic Mountain.' }));
  });

  it('cleans a spoken page answer and names book and page in the acknowledgement', () => {
    vi.useFakeTimers();
    fake.set({ bookFormat: 'physical' });
    service.open();
    service.updateDraft('A thought I cannot place');
    service.submit();

    http.expectOne('/api/assistant/turn').flush(
      turn({ anchorPrompt: { kind: 'physical_page', question: 'What page are you on?', continuationId: 'cont-page' } }),
    );

    service.insertTranscript('Page 247.');
    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);

    const request = http.expectOne('/api/assistant/turn');
    expect(request.request.body.message).toBe('Page 247.');
    expect(request.request.body.continuationId).toBe('cont-page');
    expect(request.request.body.context.anchor).toBeNull();
    request.flush(
      turn({ acknowledgement: 'Saved to The Magic Mountain.', capturedNoteId: 'note-1' }),
    );

    const capture = service.entries().find((entry) => entry.meta === 'Saved');
    expect(capture?.anchorLabel).toBe('The Magic Mountain · p. 247');
  });

  it('parses a spoken timestamp into seconds for an external audiobook follow-up', () => {
    vi.useFakeTimers();
    fake.set({ surface: 'book-detail', route: '/library/b1', bookFormat: 'audiobook' });
    service.open();
    service.updateDraft('A thought');
    service.submit();

    http.expectOne('/api/assistant/turn').flush(
      turn({
        anchorPrompt: {
          kind: 'external_audio_timestamp',
          question: "What's the current timestamp?",
          continuationId: 'cont-audio',
        },
      }),
    );
    expect(service.pendingAnchor()?.question).toBe("What's the current timestamp?");

    service.insertTranscript('1:23');
    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);

    const request = http.expectOne('/api/assistant/turn');
    expect(request.request.body.message).toBe('1:23');
    expect(request.request.body.continuationId).toBe('cont-audio');
    expect(request.request.body.context.anchor).toBeNull();
    request.flush(turn({ acknowledgement: 'Saved to The Magic Mountain.', capturedNoteId: 'n1' }));

    const capture = service.entries().find((entry) => entry.meta === 'Saved');
    expect(capture?.anchorLabel).toBe('The Magic Mountain · 1:23');
  });

  it('answers a book question with the title as a real continuation turn', () => {
    // No book is open, so the app asks which one and refuses to guess: the note
    // is filed silently-wrong otherwise, and a wrong note is invisible.
    fake.set({ surface: 'library', route: '/library', bookId: null, bookTitle: null, readingTarget: null });
    service.open();
    service.updateDraft('A thought with no book behind it');
    service.submit();

    http
      .expectOne('/api/assistant/turn')
      .flush(turn({ anchorPrompt: { kind: 'book', question: 'Which book is this for?', continuationId: 'cont-book' } }));

    expect(service.pendingAnchor()?.question).toBe('Which book is this for?');

    service.updateDraft('Vita Contemplativa');
    service.submit();

    const request = http.expectOne('/api/assistant/turn');
    expect(request.request.body.message).toBe('Vita Contemplativa');
    expect(request.request.body.continuationId).toBe('cont-book');
    expect(request.request.body.context.captureBookTitle).toBeNull();
    expect(request.request.body.context.anchor).toBeNull();
    request.flush(
      turn({ acknowledgement: 'Saved to Vita Contemplativa.', capturedNoteId: 'note-2' }),
    );

    expect(service.pendingAnchor()).toBeNull();
    const capture = service.entries().find(
      (entry) => entry.text === 'Saved to Vita Contemplativa.',
    );
    expect(capture?.meta).toBe('Saved');
  });

  it('refuses to skip a book question, because there is nothing to save without it', () => {
    fake.set({ surface: 'library', route: '/library', bookId: null, bookTitle: null, readingTarget: null });
    service.open();
    service.updateDraft('A thought with no book behind it');
    service.submit();

    http
      .expectOne('/api/assistant/turn')
      .flush(turn({ anchorPrompt: { kind: 'book', question: 'Which book is this for?', continuationId: 'cont-book' } }));

    service.skipAnchor();

    // Still waiting: no second turn was dispatched, and the question stands.
    http.expectNone('/api/assistant/turn');
    expect(service.pendingAnchor()?.question).toBe('Which book is this for?');
  });

  it('keeps a pending follow-up across a close and reopen', () => {
    vi.useFakeTimers();
    fake.set({ bookFormat: 'physical' });
    service.open();
    service.updateDraft('A thought I cannot place');
    service.submit();

    http.expectOne('/api/assistant/turn').flush(
      turn({ anchorPrompt: { kind: 'physical_page', question: 'What page are you on?', continuationId: 'cont-page' } }),
    );

    service.close();
    service.open();

    // The question is the conversation, not the panel: it is still waiting.
    expect(service.pendingAnchor()?.question).toBe('What page are you on?');
    expect(
      service.entries().filter((entry) => entry.text === 'What page are you on?'),
    ).toHaveLength(1);
    expect(service.draft()).toBe('');

    service.updateDraft('247');
    service.submit();

    const request = http.expectOne('/api/assistant/turn');
    expect(request.request.body.message).toBe('247');
    expect(request.request.body.continuationId).toBe('cont-page');
    request.flush(turn());
  });

  it('still saves with an unknown anchor when a follow-up is skipped', () => {
    fake.set({ bookFormat: 'physical' });
    service.open();
    service.updateDraft('A thought with no page');
    service.submit();

    http.expectOne('/api/assistant/turn').flush(
      turn({ anchorPrompt: { kind: 'physical_page', question: 'What page are you on?', continuationId: 'cont-page' } }),
    );
    expect(service.pendingAnchor()).not.toBeNull();

    service.skipAnchor();

    const request = http.expectOne('/api/assistant/turn');
    expect(request.request.body.message).toBe("I don't know");
    expect(request.request.body.continuationId).toBe('cont-page');
    expect(request.request.body.continuationSkipped).toBe(true);
    request.flush(turn());
    expect(service.pendingAnchor()).toBeNull();
  });

  it('keeps suggestions and a pending plan from the turn', () => {
    service.open();
    service.updateDraft('Where does this go?');
    service.submit();

    const request = http.expectOne('/api/assistant/turn');
    request.flush(
      turn({
        reply: 'Mountains looks right.',
        suggestions: [
          { kind: 'concept', label: 'Mountains', reason: 'Existing concept.', value: 'c-alpha' },
        ],
        pendingPlan: {
          planId: 'plan-1',
          summary: 'Link the note to Mountains',
          steps: [
            {
              capability: 'notes_link_existing_concept',
              summary: 'Link the note to Mountains',
              argumentsJson: '{}',
            },
          ],
          approvalToken: 'token-1',
        },
      }),
    );

    expect(service.suggestions().map((suggestion) => suggestion.label)).toEqual(['Mountains']);
    expect(service.pendingPlan()?.planId).toBe('plan-1');
    expect(service.lastTurn()?.reply).toBe('Mountains looks right.');
  });

  it('keeps a destructive pending plan across an ordinary follow-up turn', () => {
    service.open();
    service.updateDraft('Delete the obsolete collection');
    service.submit();

    http.expectOne('/api/assistant/turn').flush(
      turn({
        pendingPlan: {
          planId: 'plan-delete',
          summary: 'Delete Obsolete',
          steps: [
            {
              capability: 'library_delete_collection',
              summary: 'Delete Obsolete',
              argumentsJson: '{}',
            },
          ],
          approvalToken: 'token-delete',
        },
      }),
    );
    expect(service.pendingPlan()?.planId).toBe('plan-delete');

    service.updateDraft('What books are in it?');
    service.submit();
    http.expectOne('/api/assistant/turn').flush(turn({ reply: 'Three books.' }));

    // The backend still holds the destructive plan; a normal answer must not
    // make the confirmation disappear only on the client.
    expect(service.pendingPlan()?.planId).toBe('plan-delete');
  });

  it('reuses ConversationId and TurnId when an uncertain delivery is retried', () => {
    service.open();
    service.updateDraft('Capture this once');
    service.submit();

    const first = http.expectOne('/api/assistant/turn');
    const firstBody = first.request.body;
    expect(firstBody.clientId).toBe(firstBody.conversationId);
    expect(firstBody.idempotencyKey).toBe(firstBody.turnId);
    expect(service.entries()[0].turnId).toBe(firstBody.turnId);
    expect(service.entries()[0].id).not.toBe(firstBody.turnId);
    first.error(new ProgressEvent('error'));

    expect(service.draft()).toBe('Capture this once');
    expect(service.entries().filter((entry) => entry.kind === 'user')).toHaveLength(1);
    expect(service.history()).toEqual([]);

    service.submit();
    const retry = http.expectOne('/api/assistant/turn');
    expect(retry.request.body.turnId).toBe(firstBody.turnId);
    expect(retry.request.body.idempotencyKey).toBe(firstBody.idempotencyKey);
    expect(retry.request.body.conversationId).toBe(firstBody.conversationId);
    expect(retry.request.body.history).toEqual(firstBody.history);
    expect(service.entries().filter((entry) => entry.kind === 'user')).toHaveLength(1);
    retry.flush(turn({ reply: 'Captured once.' }));

    expect(service.history()).toEqual([
      { role: 'user', text: 'Capture this once' },
      { role: 'assistant', text: 'Captured once.' },
    ]);
  });

  it('retries an uncertain continuation answer with the same TurnId and one visible user turn', () => {
    fake.set({ bookFormat: 'physical' });
    service.open();
    service.updateDraft('A thought I cannot place');
    service.submit();

    http.expectOne('/api/assistant/turn').flush(
      turn({
        anchorPrompt: {
          kind: 'physical_page',
          question: 'What page are you on?',
          continuationId: 'cont-page-retry',
        },
      }),
    );

    service.updateDraft('247');
    service.submit();
    const firstAnswer = http.expectOne('/api/assistant/turn');
    const firstBody = firstAnswer.request.body;
    expect(firstBody.message).toBe('247');
    expect(firstBody.continuationId).toBe('cont-page-retry');
    firstAnswer.error(new ProgressEvent('error'));

    expect(service.draft()).toBe('247');
    expect(
      service.entries().filter((entry) => entry.kind === 'user' && entry.text === '247'),
    ).toHaveLength(1);

    service.submit();
    const retry = http.expectOne('/api/assistant/turn');
    expect(retry.request.body.turnId).toBe(firstBody.turnId);
    expect(retry.request.body.idempotencyKey).toBe(firstBody.idempotencyKey);
    expect(retry.request.body.continuationId).toBe(firstBody.continuationId);
    expect(retry.request.body.history).toEqual(firstBody.history);
    expect(
      service.entries().filter((entry) => entry.kind === 'user' && entry.text === '247'),
    ).toHaveLength(1);

    retry.flush(turn({ acknowledgement: 'Saved to The Magic Mountain.', capturedNoteId: 'n1' }));
    expect(service.pendingAnchor()).toBeNull();
    expect(service.capturedNoteId()).toBe('n1');
  });

  it('allocates a new TurnId when the user changes an uncertain draft', () => {
    service.updateDraft('First delivery');
    service.submit();
    const first = http.expectOne('/api/assistant/turn');
    const firstTurnId = first.request.body.turnId;
    first.error(new ProgressEvent('error'));

    service.updateDraft('This is a genuinely new turn');
    service.submit();
    const second = http.expectOne('/api/assistant/turn');
    expect(second.request.body.turnId).not.toBe(firstTurnId);
    expect(second.request.body.conversationId).toBe(service.conversationId());
    expect(second.request.body.history).toEqual([]);
    second.flush(turn());
  });

  it('allocates a new TurnId after a normal successful turn', () => {
    service.updateDraft('First');
    service.submit();
    const first = http.expectOne('/api/assistant/turn');
    const conversationId = first.request.body.conversationId;
    const firstTurnId = first.request.body.turnId;
    first.flush(turn());

    service.updateDraft('Second');
    service.submit();
    const second = http.expectOne('/api/assistant/turn');
    expect(second.request.body.conversationId).toBe(conversationId);
    expect(second.request.body.turnId).not.toBe(firstTurnId);
    second.flush(turn());
  });

  it('keeps ConversationId across close/reopen and resets it only for a new conversation', () => {
    const conversationId = service.conversationId();

    service.open();
    service.close();
    service.open();
    expect(service.conversationId()).toBe(conversationId);

    service.updateDraft('Keep me in the old ledger');
    service.submit();
    http.expectOne('/api/assistant/turn').flush(turn());
    expect(service.entries().length).toBeGreaterThan(0);

    service.newConversation();
    expect(service.conversationId()).not.toBe(conversationId);
    expect(service.entries()).toEqual([]);
    expect(service.history()).toEqual([]);
    expect(service.pendingAnchor()).toBeNull();
  });

  it('sends no per-turn processing mode', () => {
    service.open();
    service.updateDraft('A thought');
    service.submit();

    const request = http.expectOne('/api/assistant/turn');
    expect('processingMode' in request.request.body).toBe(false);
    request.flush(turn({ capturedNoteId: 'note-1' }));

    expect(service.capturedNoteId()).toBe('note-1');
  });

  describe('conversation memory (issue #286)', () => {
    it('remembers the user turn on dispatch and the assistant turn on success', () => {
      service.open();
      service.updateDraft('What did I just read?');
      service.submit();

      // Dispatch is synchronous, so the turn is remembered before the reply.
      expect(service.history()).toEqual([{ role: 'user', text: 'What did I just read?' }]);

      http.expectOne('/api/assistant/turn').flush(turn({ reply: 'The snow chapter.' }));

      expect(service.history()).toEqual([
        { role: 'user', text: 'What did I just read?' },
        { role: 'assistant', text: 'The snow chapter.' },
      ]);
    });

    it('keeps an uncertain turn visible but excludes it from remembered history', () => {
      service.open();
      service.updateDraft('A question that fails');
      service.submit();

      http.expectOne('/api/assistant/turn').error(new ProgressEvent('error'));

      expect(service.history()).toEqual([]);
      expect(service.draft()).toBe('A question that fails');
    });

    it('sends the remembered turns, in order, on the next turn', () => {
      service.open();
      service.updateDraft('First');
      service.submit();
      http.expectOne('/api/assistant/turn').flush(turn({ reply: 'First reply' }));

      service.updateDraft('Second');
      service.submit();
      const request = http.expectOne('/api/assistant/turn');

      expect(request.request.body.history).toEqual([
        { role: 'user', text: 'First' },
        { role: 'assistant', text: 'First reply' },
      ]);
      request.flush(turn({ reply: 'Second reply' }));
    });

    it('caps the history at the last 10 exchanges', () => {
      expect(HISTORY_MAX_EXCHANGES).toBe(10);

      service.open();
      for (let i = 1; i <= 11; i += 1) {
        service.updateDraft(`Thought ${i}`);
        service.submit();
        http.expectOne('/api/assistant/turn').flush(turn({ reply: `Reply ${i}` }));
      }

      service.updateDraft('Twelfth');
      service.submit();
      const request = http.expectOne('/api/assistant/turn');
      const history = request.request.body.history as AssistantHistoryMessage[];

      // Eleven exchanges became ten: the first exchange fell off the front, and
      // every kept user turn still carries the answer that followed it.
      expect(history).toHaveLength(20);
      expect(history[0]).toEqual({ role: 'user', text: 'Thought 2' });
      expect(history[19]).toEqual({ role: 'assistant', text: 'Reply 11' });
      expect(history.some((entry) => entry.text === 'Thought 1')).toBe(false);
      request.flush(turn());
    });

    it('truncates a remembered message to HISTORY_MAX_CHARS, never drops it', () => {
      expect(HISTORY_MAX_CHARS).toBe(2000);

      service.open();
      service.updateDraft('x'.repeat(3000));
      service.submit();
      http.expectOne('/api/assistant/turn').flush(turn({ reply: 'Noted.' }));

      service.updateDraft('Next');
      service.submit();
      const request = http.expectOne('/api/assistant/turn');
      const history = request.request.body.history as AssistantHistoryMessage[];

      expect(history[0].role).toBe('user');
      expect(history[0].text).toHaveLength(HISTORY_MAX_CHARS);
      request.flush(turn());
    });
  });

  describe('a two-sided transcript (issue #286)', () => {
    it('shows a typed question as a user entry followed by the assistant reply', () => {
      service.open();
      service.updateDraft('What did I just read?');
      service.submit();
      http.expectOne('/api/assistant/turn').flush(turn({ reply: 'The snow chapter.' }));

      expect(service.entries().map((entry) => entry.kind)).toEqual(['user', 'assistant']);
      expect(service.entries()[0]).toMatchObject({
        text: 'What did I just read?',
        anchorLabel: null,
        meta: null,
      });
      expect(service.entries()[1].text).toBe('The snow chapter.');
    });

    it('records a voice transcript as the same user entry as typing', () => {
      vi.useFakeTimers();
      service.open();

      service.updateDraft('The Magic Mountain');
      service.submit();
      http.expectOne('/api/assistant/turn').flush(turn({ reply: 'Noted.' }));
      const typed = service.entries().find((entry) => entry.kind === 'user');

      service.insertTranscript('The Magic Mountain');
      vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);
      http.expectOne('/api/assistant/turn').flush(turn({ reply: 'Noted.' }));
      const voiced = service.entries().filter((entry) => entry.kind === 'user')[1];

      expect(typed).toBeTruthy();
      expect(voiced.kind).toBe(typed?.kind);
      expect(voiced.text).toBe(typed?.text);
      expect(voiced.anchorLabel).toBe(typed?.anchorLabel);
      expect(voiced.meta).toBe(typed?.meta);
    });
  });

  it('clears the captured note and its raw view on a later turn', () => {
    service.open();
    service.updateDraft('First');
    service.submit();
    http.expectOne('/api/assistant/turn').flush(turn({ capturedNoteId: 'note-1' }));
    expect(service.capturedNoteId()).toBe('note-1');

    service.updateDraft('Second');
    service.submit();
    http.expectOne('/api/assistant/turn').flush(turn());
    expect(service.capturedNoteId()).toBeNull();
    expect(service.rawOpen()).toBe(false);
  });

  it('loads the raw transcript and restores it without erasing it', () => {
    service.open();
    service.capturedNoteId.set('note-1');

    service.toggleRawTranscript();
    expect(service.rawOpen()).toBe(true);
    http.expectOne('/api/notes/note-1/raw').flush({
      id: 'note-1',
      rawContent: 'raw words',
      content: 'Polished.',
      processingMode: 'light_polish',
    });
    expect(service.rawTranscript()?.rawContent).toBe('raw words');

    service.restoreRawTranscript();
    http.expectOne('/api/notes/note-1/raw/restore').flush({
      id: 'note-1',
      rawContent: 'raw words',
      content: 'raw words',
      processingMode: 'verbatim',
    });
    expect(service.rawTranscript()?.content).toBe('raw words');
    expect(service.rawTranscript()?.processingMode).toBe('verbatim');
  });

  it('ignores a suggestion that is not a concept and does nothing on the wire', () => {
    service.applySuggestion({ kind: 'collection', label: 'Essays', reason: 'A collection.', value: 'col-1' });

    expect(service.lastError()).toBeNull();
    http.expectNone('/api/assistant/turn');
  });

  it('needs a review target before it will request a concept link', () => {
    service.applySuggestion({
      kind: 'concept',
      label: 'Mountains',
      reason: 'Existing concept.',
      value: 'c-alpha',
    });
    expect(service.lastError()).toContain('Second Brain');
    http.expectNone('/api/assistant/turn');
  });

  it('requests an immediate concept link and emits only a backend-confirmed action receipt', () => {
    fake.set({ brainReviewNoteId: 'note-1' });
    const receipts: Array<{ capability: string; noteId: string | null | undefined }> = [];
    const subscription = service.actionExecuted.subscribe((event) =>
      receipts.push({
        capability: event.capability,
        noteId: event.context.brainReviewNoteId,
      }),
    );

    service.applySuggestion({
      kind: 'concept',
      label: 'Mountains',
      reason: 'Existing concept.',
      value: 'c-alpha',
    });

    const request = http.expectOne('/api/assistant/turn');
    expect(request.request.body.context.brainReviewNoteId).toBe('note-1');
    expect(request.request.body.message).toContain('Mountains');
    request.flush(turn({ executedCapabilities: ['notes_link_existing_concept'] }));

    expect(receipts).toEqual([
      { capability: 'notes_link_existing_concept', noteId: 'note-1' },
    ]);
    subscription.unsubscribe();
  });

  it('approves exactly one destructive plan and reports the canonical execution result', () => {
    service.pendingPlan.set({
      planId: 'plan-1',
      summary: 'Delete the obsolete collection',
      steps: [],
      approvalToken: 'token-1',
    });

    service.approvePlan('plan-1', 'token-1');

    const request = http.expectOne('/api/assistant/plan/approve');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ planId: 'plan-1', approvalToken: 'token-1' });
    request.flush({
      success: true,
      errorCode: null,
      errorMessage: null,
      steps: [
        {
          capability: 'library_delete_collection',
          success: true,
          errorCode: null,
          errorMessage: null,
          data: { reply: 'Deleted the obsolete collection.' },
        },
      ],
    });

    expect(service.pendingPlan()).toBeNull();
    expect(service.entries().at(-1)?.text).toBe('Deleted the obsolete collection.');
    expect(service.entries().at(-1)?.meta).toBe('Applied');
  });

  it('treats an immediate conversational yes as approval of the pending destructive plan', () => {
    const plan = {
      planId: 'plan-1',
      summary: 'Delete the obsolete collection',
      steps: [],
      approvalToken: 'token-1',
    };

    // The destructive plan arrived from the immediately preceding assistant
    // turn, which is what arms natural-language confirmation.
    service.updateDraft('Remove the obsolete collection.');
    service.submit();
    http.expectOne('/api/assistant/turn').flush(turn({ pendingPlan: plan }));

    service.updateDraft('Go ahead.');
    service.submit();

    http.expectNone('/api/assistant/turn');
    const request = http.expectOne('/api/assistant/plan/approve');
    expect(request.request.body).toEqual({ planId: 'plan-1', approvalToken: 'token-1' });
    expect(service.entries().at(-1)?.kind).toBe('user');
    expect(service.entries().at(-1)?.text).toBe('Go ahead.');

    request.flush({
      success: true,
      errorCode: null,
      errorMessage: null,
      steps: [
        {
          capability: 'library_delete_collection',
          success: true,
          errorCode: null,
          errorMessage: null,
          data: { reply: 'Deleted the obsolete collection.' },
        },
      ],
    });

    expect(service.pendingPlan()).toBeNull();
    expect(service.entries().at(-1)?.text).toBe('Deleted the obsolete collection.');
    expect(service.history().slice(-2).map((entry) => entry.text)).toEqual([
      'Go ahead.',
      'Deleted the obsolete collection.',
    ]);
  });

  it('does not mistake a qualified yes for destructive approval and disarms later generic yes', () => {
    const plan = {
      planId: 'plan-1',
      summary: 'Delete the obsolete collection',
      steps: [],
      approvalToken: 'token-1',
    };

    service.updateDraft('Remove the obsolete collection.');
    service.submit();
    http.expectOne('/api/assistant/turn').flush(turn({ pendingPlan: plan }));

    service.updateDraft('Yes, but explain what will happen first.');
    service.submit();

    http.expectNone('/api/assistant/plan/approve');
    const discussion = http.expectOne('/api/assistant/turn');
    discussion.flush(turn({ reply: 'The collection will be deleted; its books remain.' }));

    expect(service.pendingPlan()?.planId).toBe('plan-1');

    // The plan remains visible, but a generic yes after an intervening turn is
    // no longer enough to execute stale destructive work.
    service.updateDraft('yes');
    service.submit();
    http.expectNone('/api/assistant/plan/approve');
    const later = http.expectOne('/api/assistant/turn');
    later.flush(turn());
  });

  it('cancels a fresh destructive confirmation on an explicit no', () => {
    const plan = {
      planId: 'plan-cancel',
      summary: 'Delete the obsolete collection',
      steps: [],
      approvalToken: 'token-cancel',
    };

    service.updateDraft('Remove the obsolete collection.');
    service.submit();
    http.expectOne('/api/assistant/turn').flush(turn({ pendingPlan: plan }));

    service.updateDraft('No thanks.');
    service.submit();

    http.expectNone('/api/assistant/turn');
    http.expectNone('/api/assistant/plan/approve');
    expect(service.pendingPlan()).toBeNull();
    expect(service.entries().at(-1)?.text).toBe("Okay. I won't make that change.");
    expect(service.entries().at(-1)?.meta).toBe('Cancelled');
  });

  it('keeps the natural approval vocabulary deliberately narrow', () => {
    expect(isExplicitPlanApproval('yes')).toBe(true);
    expect(isExplicitPlanApproval('Yes, do it.')).toBe(true);
    expect(isExplicitPlanApproval('go ahead')).toBe(true);
    expect(isExplicitPlanApproval('yes, but explain first')).toBe(false);
    expect(isExplicitPlanApproval('I guess so')).toBe(false);
    expect(isExplicitPlanApproval('do it after you check something else')).toBe(false);
    expect(isExplicitPlanRejection('no')).toBe(true);
    expect(isExplicitPlanRejection('No thanks.')).toBe(true);
    expect(isExplicitPlanRejection('cancel it')).toBe(true);
    expect(isExplicitPlanRejection('not yet')).toBe(false);
  });

  it('dismisses suggestions without touching the note', () => {
    service.suggestions.set([
      { kind: 'concept', label: 'Mountains', reason: 'Existing concept.', value: 'c-alpha' },
    ]);

    service.dismissSuggestions();

    expect(service.suggestions()).toEqual([]);
    http.expectNone('/api/assistant/turn');
  });
});
