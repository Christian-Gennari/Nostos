import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { HttpEventType, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import {
  ASSISTANT_SESSION_STORAGE_KEY,
  AssistantService,
  TRANSCRIPT_AUTO_SEND_DELAY_MS,
  type AssistantChangesetDto,
  type AssistantTurnResponse,
} from './assistant.service';
import { AssistantContext, AssistantContextService } from './assistant-context.service';

function context(): AssistantContext {
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
    topic: null,
    collectionId: null,
    anchor: null,
  };
}

function reviewChangeset(): AssistantChangesetDto {
  return {
    changesetId: 'cs-cancel',
    status: 'review',
    steps: [{ index: 0, summary: 'Create collection “Fresh”', included: true, dependsOn: [] }],
    diff: {
      collections: [],
      memberships: [],
      bookEdits: [],
      counts: {
        collectionsCreated: 1,
        collectionsDeleted: 0,
        collectionsUpdated: 0,
        booksTouched: 0,
        membershipsAdded: 0,
        membershipsRemoved: 0,
        booksLosingMembership: 0,
        bookEdits: 0,
      },
    },
    diffHash: 'hash-cancel',
    actionToken: 'token-cancel',
    reviewReasons: [],
    canUndo: false,
  };
}

function turn(overrides: Partial<AssistantTurnResponse> = {}): AssistantTurnResponse {
  return {
    reply: 'Noted.',
    acknowledgement: null,
    anchorPrompt: null,
    suggestions: [],
    changeset: null,
    capturedNoteId: null,
    ...overrides,
  };
}

describe('assistant request cancellation', () => {
  let service: AssistantService;
  let http: HttpTestingController;

  function configure(): void {
    TestBed.configureTestingModule({
      providers: [
        { provide: AssistantContextService, useValue: { context: signal(context()).asReadonly() } },
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(AssistantService);
    http = TestBed.inject(HttpTestingController);
  }

  /** Completes one streamed turn carrying a review changeset. */
  function deliverReview(): void {
    service.open();
    service.updateDraft('Regroup my library.');
    service.submit();
    const request = http.expectOne('/api/assistant/turn/stream');
    const turnId = request.request.body.turnId as string;
    request.flush(
      JSON.stringify({ turnId, sequence: 1, kind: 'started' }) + '\n'
      + JSON.stringify({
        turnId,
        sequence: 2,
        kind: 'completed',
        response: turn({ reply: 'Here is the plan.', changeset: reviewChangeset() }),
      }) + '\n',
    );
    expect(service.sending()).toBe(false);
  }

  beforeEach(() => {
    sessionStorage.removeItem(ASSISTANT_SESSION_STORAGE_KEY);
    configure();
  });

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  it('cancels an in-flight changeset action and clears its progress on a new conversation', () => {
    deliverReview();
    service.applyChangeset('cs-cancel');

    const request = http.expectOne('/api/assistant/changesets/cs-cancel/apply');
    expect(service.changesetBusy()).toBe('cs-cancel');

    service.newConversation();

    expect(request.cancelled).toBe(true);
    expect(service.changesetBusy()).toBeNull();
    expect(service.pendingReview()).toBeNull();
  });

  it('cancels an in-flight changeset action when the service is destroyed', () => {
    deliverReview();
    service.applyChangeset('cs-cancel');
    const request = http.expectOne('/api/assistant/changesets/cs-cancel/apply');

    TestBed.resetTestingModule();

    expect(request.cancelled).toBe(true);
    // Reset also destroys the testing controller; a new module must be built
    // for the afterEach verify to have something to verify.
    configure();
  });

  it('cancels the turn stream and the Stop request on destroy', () => {
    service.open();
    service.updateDraft('Search my notes.');
    service.submit();
    const stream = http.expectOne('/api/assistant/turn/stream');
    const turnId = stream.request.body.turnId as string;
    // DownloadProgress keeps the request open, unlike flush() which also ends
    // the stream; Stop is only exposed after this `started` event.
    stream.event({
      type: HttpEventType.DownloadProgress,
      loaded: 1,
      total: 2,
      partialText:
        JSON.stringify({ turnId, sequence: 1, kind: 'started' }) + '\n'
        + JSON.stringify({ turnId, sequence: 2, kind: 'activity', activity: { code: 'searching', message: 'Searching…' } }) + '\n',
    });
    expect(service.activeTurnId()).toBe(turnId);

    service.stopActiveTurn();
    const stop = http.expectOne('/api/assistant/turn/cancel');

    TestBed.resetTestingModule();

    expect(stop.cancelled).toBe(true);
    expect(stream.cancelled).toBe(true);
    configure();
  });

  it('clears the voice auto-send on a new conversation so nothing is dispatched', () => {
    vi.useFakeTimers();
    service.insertTranscript('Remember the harbour bell.');
    expect(service.autoSendPending()).toBe(true);

    service.newConversation();
    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);

    expect(service.autoSendPending()).toBe(false);
    http.expectNone('/api/assistant/turn/stream');
  });

  it('clears the voice auto-send on destroy so nothing is dispatched', () => {
    vi.useFakeTimers();
    service.insertTranscript('Remember the harbour bell.');

    TestBed.resetTestingModule();
    vi.advanceTimersByTime(TRANSCRIPT_AUTO_SEND_DELAY_MS);

    configure();
    http.expectNone('/api/assistant/turn/stream');
  });
});
