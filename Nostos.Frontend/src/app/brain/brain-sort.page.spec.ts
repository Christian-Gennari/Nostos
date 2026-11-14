import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { AssistantContextService } from '../ui/assistant/assistant-context.service';
import {
  AssistantService,
  type AssistantTurnFinishedEvent,
  type AssistantTurnResponse,
} from '../ui/assistant/assistant.service';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { BrainSortPage } from './brain-sort.page';
import { BrainSortPositionService } from './brain-sort-position.service';
import { BrainStateService } from './brain-state.service';

describe('BrainSortPage when the notes cannot be loaded', () => {
  let fixture: ComponentFixture<BrainSortPage>;
  let browse: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    browse = vi.fn().mockReturnValue(throwError(() => new Error('offline')));
    await TestBed.configureTestingModule({
      imports: [BrainSortPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: NotesService, useValue: { browse } },
        { provide: TopicsService, useValue: {} },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
        { provide: BrainSortPositionService, useValue: { read: () => of(null), write: vi.fn(), clear: vi.fn() } },
        { provide: AssistantService, useValue: { surfaceAvailable: signal(false), turnFinished: new Subject() } },
        {
          provide: BrainStateService,
          useValue: { ensureLoaded: vi.fn(), refresh: vi.fn(), topics: signal([]), lookup: signal(new Map()) },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(BrainSortPage);
    fixture.detectChanges();
  });

  it('says so, with Try again, and loads the first page again from there', () => {
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('The notes could not be loaded.');

    browse.mockReturnValue(of({ items: [], totalCount: 0, offset: 0, limit: 50 }));
    (el.querySelector('[role="alert"] button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(browse).toHaveBeenCalledTimes(2);
    expect(el.querySelector('[role="alert"]')).toBeNull();
    expect(el.textContent).toContain('All sorted');
  });
});

const UNSPORTED_NOTE: NoteSearchHit = {
  id: 'note-1',
  bookId: 'book-1',
  bookTitle: 'The Republic',
  content: 'The magnificent myth of the metals.',
  selectedText: 'The magnificent myth of the metals…',
  snippet: null,
  topicNames: ['political philosophy'],
  createdAt: '2026-11-08T10:00:00Z',
};

describe('BrainSortPage AI suggestion outcomes', () => {
  let fixture: ComponentFixture<BrainSortPage>;
  let finished: Subject<AssistantTurnFinishedEvent>;
  let requestTopicProposals: ReturnType<typeof vi.fn>;

  async function setup(): Promise<void> {
    finished = new Subject<AssistantTurnFinishedEvent>();
    requestTopicProposals = vi.fn(() => 'turn-1');
    await TestBed.configureTestingModule({
      imports: [BrainSortPage],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        {
          provide: NotesService,
          useValue: {
            browse: vi.fn().mockReturnValue(
              of({ items: [UNSPORTED_NOTE], totalCount: 1, offset: 0, limit: 25 }),
            ),
          },
        },
        { provide: TopicsService, useValue: {} },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
        { provide: BrainSortPositionService, useValue: { read: () => of(null), write: vi.fn(), clear: vi.fn() } },
        {
          provide: AssistantService,
          useValue: {
            surfaceAvailable: signal(true),
            turnFinished: finished,
            requestTopicProposals,
            activeTurnId: signal<string | null>(null),
            stopActiveTurn: vi.fn(),
            dismissSuggestionsForNote: vi.fn(),
          },
        },
        { provide: AssistantContextService, useValue: { register: () => () => undefined } },
        {
          provide: BrainStateService,
          useValue: {
            ensureLoaded: vi.fn(),
            refresh: vi.fn(),
            topics: signal([]),
            sidebarTopics: signal([]),
            lookup: signal(new Map()),
          },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(BrainSortPage);
    fixture.detectChanges();
  }

  function clickButton(label: string): void {
    const buttons = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('button'),
    ) as HTMLButtonElement[];
    const button = buttons.find((candidate) => candidate.textContent?.trim() === label);
    expect(button, `no "${label}" button`).toBeDefined();
    button!.click();
    fixture.detectChanges();
  }

  it('says no topics suggest themselves when the turn found none', async () => {
    await setup();
    clickButton('Suggest topics');

    finished.next({
      turnId: 'turn-1',
      response: {
        reply: '',
        acknowledgement: null,
        anchorPrompt: null,
        suggestions: [],
      } as AssistantTurnResponse,
      error: null,
      code: null,
    });
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('No topics suggest themselves for this note.');
    expect(text).not.toContain('Suggestions are unavailable right now.');
  });

  it('shows the failure sentence with Try again, which asks again', async () => {
    await setup();
    clickButton('Suggest topics');
    expect(requestTopicProposals).toHaveBeenCalledTimes(1);

    finished.next({
      turnId: 'turn-1',
      response: null,
      error: 'The AI service could not complete this turn. Try again.',
      code: 'assistant_provider_error',
    });
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Could not get suggestions.');
    expect(el.textContent).not.toContain('Suggestions are unavailable right now.');

    clickButton('Try again');

    expect(requestTopicProposals).toHaveBeenCalledTimes(2);
    expect(el.textContent).toContain('Asking…');
  });
});
