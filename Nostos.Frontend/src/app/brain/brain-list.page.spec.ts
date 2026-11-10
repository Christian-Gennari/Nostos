import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import { AssistantContextService } from '../ui/assistant/assistant-context.service';
import { AssistantService } from '../ui/assistant/assistant.service';
import { BrainListCacheService } from './brain-list-cache.service';
import { BrainListPage } from './brain-list.page';
import { BrainStateService } from './brain-state.service';
import { BrainTopicActionsService } from './brain-topic-actions.service';

const note = (id: string, createdAt: string): NoteSearchHit => ({
  id,
  bookId: 'b',
  bookTitle: 'Book',
  content: 'A note',
  selectedText: null,
  snippet: null,
  topicNames: [],
  createdAt,
});

describe('BrainListPage', () => {
  let fixture: ComponentFixture<BrainListPage>;
  let browse: ReturnType<typeof vi.fn>;

  beforeAll(() => {
    // jsdom has no scrolling; the page resets the scroller when a list opens.
    Object.defineProperty(HTMLElement.prototype, 'scrollTo', {
      configurable: true,
      writable: true,
      value: vi.fn(),
    });
  });

  beforeEach(async () => {
    const now = Date.now();
    browse = vi.fn().mockReturnValue(
      of({
        items: [
          note('today-a', new Date(now).toISOString()),
          note('today-b', new Date(now).toISOString()),
          note('yesterday', new Date(now - 86_400_000).toISOString()),
        ],
        totalCount: 3,
        offset: 0,
        limit: 25,
      }),
    );

    await TestBed.configureTestingModule({
      imports: [BrainListPage],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            data: of({ place: 'all' }),
            paramMap: of(convertToParamMap({})),
            queryParamMap: of(convertToParamMap({})),
            snapshot: {
              data: { place: 'all' },
              paramMap: convertToParamMap({}),
              queryParamMap: convertToParamMap({}),
            },
          },
        },
        { provide: NotesService, useValue: { browse, books: vi.fn(() => of([])) } },
        { provide: TopicsService, useValue: {} },
        { provide: ToastService, useValue: { success: vi.fn(), error: vi.fn() } },
        { provide: BrainListCacheService, useValue: { restore: () => null, save: vi.fn() } },
        { provide: AssistantService, useValue: { turnFinished: new Subject() } },
        { provide: AssistantContextService, useValue: {} },
        {
          provide: BrainStateService,
          useValue: {
            ensureLoaded: vi.fn(),
            topicById: () => null,
            topics: signal([]),
            lookup: signal(new Map()),
            drawerOpen: signal(false),
            dataVersion: signal(0),
            topicsRewritten: signal(0),
          },
        },
        {
          provide: BrainTopicActionsService,
          useValue: {
            renameId: signal<string | null>(null),
            renameSurface: signal('sidebar'),
            renameValue: signal(''),
            renameError: signal<string | null>(null),
            renaming: signal(false),
            startRename: vi.fn(),
            commitRename: vi.fn(),
            cancelRename: vi.fn(),
            openDelete: vi.fn(),
            openMerge: vi.fn(),
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BrainListPage);
    fixture.detectChanges();
    fixture.detectChanges();
  });

  function el(): HTMLElement {
    return fixture.nativeElement;
  }

  it('heads a date-ordered list with Today and Yesterday, each counting its notes', () => {
    const titles = Array.from(el().querySelectorAll('.note-group-title')).map((title) => title.textContent?.trim());
    const counts = Array.from(el().querySelectorAll('.note-group-meta')).map((meta) => meta.textContent?.trim());

    expect(titles).toEqual(['Today', 'Yesterday']);
    expect(counts).toEqual(['2 notes', '1 note']);
  });
});
