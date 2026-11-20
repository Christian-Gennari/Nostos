import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { AssistantContextService } from '../ui/assistant/assistant-context.service';
import {
  AssistantService,
  type AssistantTurnFinishedEvent,
  type AssistantTurnResponse,
} from '../ui/assistant/assistant.service';
import { BrainTopicSuggestionsService } from './brain-topic-suggestions.service';

const NOTE: NoteSearchHit = {
  id: 'note-1',
  bookId: 'book-1',
  bookTitle: 'The Republic',
  content: 'The magnificent myth of the metals.',
  selectedText: null,
  snippet: null,
  topicNames: [],
  createdAt: '2026-11-08T10:00:00Z',
};

function finishedEvent(overrides: Partial<AssistantTurnFinishedEvent> = {}): AssistantTurnFinishedEvent {
  return {
    turnId: 'turn-1',
    response: null,
    error: null,
    code: null,
    ...overrides,
  };
}

function responseWith(names: readonly string[], noteId = NOTE.id): AssistantTurnResponse {
  return {
    reply: '',
    acknowledgement: null,
    anchorPrompt: null,
    suggestions: names.map((label) => ({
      kind: 'topic',
      label,
      reason: '',
      value: `topic-${label}`,
      noteId,
    })),
  };
}

describe('BrainTopicSuggestionsService outcomes', () => {
  let service: BrainTopicSuggestionsService;
  let finished: Subject<AssistantTurnFinishedEvent>;

  function configure(): void {
    finished = new Subject<AssistantTurnFinishedEvent>();
    TestBed.configureTestingModule({
      providers: [
        BrainTopicSuggestionsService,
        {
          provide: AssistantService,
          useValue: {
            surfaceAvailable: signal(true),
            turnFinished: finished,
            requestTopicProposals: () => 'turn-1',
            activeTurnId: signal<string | null>(null),
            stopActiveTurn: vi.fn(),
            dismissSuggestionsForNote: vi.fn(),
          },
        },
        // The real service builds its context from Reader/Books; a suggestion
        // turn only registers one, so a stub keeps that graph out of the test.
        { provide: AssistantContextService, useValue: { register: () => () => undefined } },
      ],
    });
    service = TestBed.inject(BrainTopicSuggestionsService);
    service.request(NOTE);
  }

  it('is ready with the names the turn proposed', () => {
    configure();
    finished.next(finishedEvent({ response: responseWith(['political philosophy', 'Plato on women']) }));

    expect(service.stateFor(NOTE.id)).toBe('ready');
    expect(service.namesFor(NOTE.id)).toEqual(['political philosophy', 'Plato on women']);
  });

  it('is empty when the turn finished with no error and no names (#840)', () => {
    configure();
    finished.next(finishedEvent({ response: responseWith([]) }));

    expect(service.stateFor(NOTE.id)).toBe('empty');
    expect(service.namesFor(NOTE.id)).toEqual([]);
  });

  it('is empty when the retrieval found no evidence and proposed nothing', () => {
    configure();
    finished.next(finishedEvent({
      response: responseWith([]),
      error: 'I could not find usable evidence for that in your Nostos material.',
      code: 'assistant_no_evidence',
    }));

    expect(service.stateFor(NOTE.id)).toBe('empty');
  });

  it('is an error for any other terminal failure', () => {
    configure();
    finished.next(finishedEvent({
      error: 'The AI service could not complete this turn. Try again.',
      code: 'assistant_provider_error',
    }));

    expect(service.stateFor(NOTE.id)).toBe('error');
    expect(service.namesFor(NOTE.id)).toEqual([]);
  });

  it('keeps usable names when the same turn also ended with an error', () => {
    configure();
    finished.next(finishedEvent({
      response: responseWith(['extremes']),
      error: 'This turn reached its execution limit before it could finish.',
      code: 'assistant_execution_budget_exhausted',
    }));

    expect(service.stateFor(NOTE.id)).toBe('ready');
    expect(service.namesFor(NOTE.id)).toEqual(['extremes']);
  });

  it('ignores another turn and another note', () => {
    configure();
    finished.next(finishedEvent({ turnId: 'turn-2', response: responseWith([]) }));
    expect(service.stateFor(NOTE.id)).toBe('loading');

    finished.next(finishedEvent({ response: responseWith(['extremes'], 'note-2') }));
    expect(service.stateFor(NOTE.id)).toBe('empty');
    expect(service.namesFor(NOTE.id)).toEqual([]);
  });
});
