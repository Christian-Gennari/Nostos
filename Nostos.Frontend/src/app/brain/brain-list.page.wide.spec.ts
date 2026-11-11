import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, type ParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject, Subject, of } from 'rxjs';

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

const note = (id: string, content = 'A note'): NoteSearchHit => ({
  id,
  bookId: 'b',
  bookTitle: 'Book',
  content,
  selectedText: null,
  snippet: null,
  topicNames: [],
  createdAt: '2026-11-08T10:00:00Z',
});

const originalMatchMedia = window.matchMedia;

function stubMatchMedia(wide: boolean): void {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    writable: true,
    value: (query: string) => ({
      matches: wide && query.includes('1280'),
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    }),
  });
}

describe('BrainListPage — wide list and detail (#1531)', () => {
  let fixture: ComponentFixture<BrainListPage>;
  let query: BehaviorSubject<ParamMap>;
  let navigate: ReturnType<typeof vi.spyOn>;
  let get: ReturnType<typeof vi.fn>;

  beforeAll(() => {
    // jsdom has no scrolling; the page resets the scroller when a list opens.
    Object.defineProperty(HTMLElement.prototype, 'scrollTo', {
      configurable: true,
      writable: true,
      value: vi.fn(),
    });
    Object.defineProperty(HTMLElement.prototype, 'scrollIntoView', {
      configurable: true,
      writable: true,
      value: vi.fn(),
    });
  });

  beforeEach(async () => {
    stubMatchMedia(true);
    query = new BehaviorSubject(convertToParamMap({}));
    get = vi.fn((id: string) => of(note(id, `Fetched ${id}`)));

    await TestBed.configureTestingModule({
      imports: [BrainListPage],
      providers: [
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            data: of({ place: 'all' }),
            paramMap: of(convertToParamMap({})),
            queryParamMap: query.asObservable(),
            snapshot: {
              data: { place: 'all' },
              paramMap: convertToParamMap({}),
              queryParamMap: convertToParamMap({}),
            },
          },
        },
        {
          provide: NotesService,
          useValue: {
            browse: vi.fn(() => of({
              items: [note('today-a', 'First note'), note('today-b', 'Second note'), note('today-c', 'Third note')],
              totalCount: 3,
              offset: 0,
              limit: 25,
            })),
            books: vi.fn(() => of([])),
            get,
          },
        },
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
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  });

  afterEach(() => {
    fixture.nativeElement.remove();
    if (originalMatchMedia) window.matchMedia = originalMatchMedia;
    else delete (window as { matchMedia?: unknown }).matchMedia;
  });

  function el(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function rows(): HTMLElement[] {
    return Array.from(el().querySelectorAll<HTMLElement>('[data-note-row]'));
  }

  function setQuery(params: Record<string, string>): void {
    query.next(convertToParamMap(params));
    fixture.detectChanges();
    fixture.detectChanges();
  }

  function press(target: Element, key: string): void {
    target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
    fixture.detectChanges();
  }

  it('splits into the list and a calm empty pane until a note is chosen', () => {
    expect(el().querySelector('.brain-detail-pane')).not.toBeNull();
    expect(rows()).toHaveLength(3);
    expect(el().querySelector('.note-list')?.getAttribute('role')).toBe('listbox');
    expect(el().querySelector('.detail-state')?.textContent).toContain('Choose a note');
    expect(rows()[0].getAttribute('tabindex')).toBe('0');
    expect(rows()[1].getAttribute('tabindex')).toBe('-1');
  });

  it('writes the chosen note to the URL, keeping the list and its filters', () => {
    setQuery({ q: 'note' });
    rows()[1].click();
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({
      relativeTo: expect.anything(),
      queryParams: { note: 'today-b' },
      queryParamsHandling: 'merge',
      replaceUrl: false,
    }));
  });

  it('restores the note in the URL beside its list, with the filters still applied', () => {
    setQuery({ q: 'note', note: 'today-b' });

    expect((el().querySelector('#brain-search') as HTMLInputElement).value).toBe('note');
    expect(rows().map((row) => row.getAttribute('aria-selected'))).toEqual(['false', 'true', 'false']);
    expect(rows()[1].getAttribute('tabindex')).toBe('0');
    expect(el().querySelector('.brain-detail-pane app-note-item')?.getAttribute('data-note-id')).toBe('today-b');
    expect(el().querySelector('.detail-state')).toBeNull();
  });

  it('reads a note that is not in the loaded list, for a deep link with filters', () => {
    setQuery({ q: 'elsewhere', note: 'note-from-link' });

    expect(get).toHaveBeenCalledWith('note-from-link');
    expect(el().querySelector('.brain-detail-pane app-note-item')?.getAttribute('data-note-id')).toBe('note-from-link');
    expect(rows().every((row) => row.getAttribute('aria-selected') === 'false')).toBe(true);
  });

  it('moves with ↑/↓, hands focus to the pane on Enter, and back with Esc', () => {
    document.body.appendChild(el());
    setQuery({ note: 'today-a' });

    const first = rows()[0];
    first.focus();
    press(first, 'ArrowDown');
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({
      queryParams: { note: 'today-b' },
      replaceUrl: true,
    }));
    expect(document.activeElement).toBe(rows()[1]);

    setQuery({ note: 'today-b' });
    press(document.activeElement as Element, 'Enter');
    const pane = el().querySelector('.brain-detail-pane') as HTMLElement;
    expect(document.activeElement).toBe(pane);

    press(pane, 'Escape');
    expect(document.activeElement).toBe(rows()[1]);
  });

  it('goes to the note page, not the pane, below the wide breakpoint', () => {
    stubMatchMedia(false);
    fixture.destroy();
    fixture = TestBed.createComponent(BrainListPage);
    fixture.detectChanges();
    fixture.detectChanges();

    expect(el().querySelector('.brain-detail-pane')).toBeNull();
    setQuery({ note: 'today-a' });
    expect(navigate).toHaveBeenCalledWith(['/brain/notes', 'today-a'], { replaceUrl: true });
  });
});
