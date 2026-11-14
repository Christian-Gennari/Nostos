import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router, type ParamMap } from '@angular/router';
import { BehaviorSubject, of, Subject } from 'rxjs';

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

describe('BrainListPage', () => {
  let fixture: ComponentFixture<BrainListPage>;
  let browse: ReturnType<typeof vi.fn>;
  let query: BehaviorSubject<ParamMap>;

  beforeAll(() => {
    // jsdom has no scrolling; the page resets the scroller when a list opens,
    // and brings the open note's row into view.
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
    stubMatchMedia(false);
    query = new BehaviorSubject<ParamMap>(convertToParamMap({}));
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
            queryParamMap: query.asObservable(),
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

  afterEach(() => {
    fixture.nativeElement.remove();
    if (originalMatchMedia) window.matchMedia = originalMatchMedia;
    else delete (window as { matchMedia?: unknown }).matchMedia;
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

  describe('selecting in the wide list (#1662)', () => {
    let navigate: ReturnType<typeof vi.spyOn>;

    beforeEach(() => {
      fixture.destroy();
      stubMatchMedia(true);
      fixture = TestBed.createComponent(BrainListPage);
      fixture.detectChanges();
      fixture.detectChanges();
      navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    });

    function setQuery(params: Record<string, string>): void {
      query.next(convertToParamMap(params));
      fixture.detectChanges();
      fixture.detectChanges();
    }

    function rows(): HTMLElement[] {
      return Array.from(el().querySelectorAll<HTMLElement>('[data-note-row]'));
    }

    function press(target: Element, key: string): void {
      target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
      fixture.detectChanges();
    }

    function count(): string {
      return el().querySelector('.selection-count')?.textContent?.trim() ?? '';
    }

    function actions(): HTMLButtonElement[] {
      return Array.from(el().querySelectorAll<HTMLButtonElement>('.selection-action'));
    }

    function select(): void {
      (el().querySelector('.select-toggle') as HTMLButtonElement).click();
      fixture.detectChanges();
    }

    it('ticks rows in Select mode instead of opening them, and restores the row press after', () => {
      setQuery({ note: 'today-b' });
      navigate.mockClear();

      select();
      expect(count()).toBe('0 selected');
      expect(actions()).toHaveLength(3);
      expect(actions().map((action) => action.disabled)).toEqual([true, true, true]);
      expect(rows().every((row) => !!row.querySelector('input[type=checkbox]'))).toBe(true);

      rows()[0].click();
      fixture.detectChanges();
      rows()[1].click();
      fixture.detectChanges();

      expect(count()).toBe('2 selected');
      expect(actions().map((action) => action.disabled)).toEqual([false, false, false]);
      expect(rows()[0].querySelector<HTMLInputElement>('input[type=checkbox]')?.checked).toBe(true);
      expect(rows()[1].querySelector<HTMLInputElement>('input[type=checkbox]')?.checked).toBe(true);
      expect(navigate).not.toHaveBeenCalled();
      expect(el().querySelector('.brain-detail-pane app-note-item')?.getAttribute('data-note-id')).toBe('today-b');

      select();
      expect(el().querySelector('.selection-bar')).toBeNull();
      expect(rows().some((row) => !!row.querySelector('input[type=checkbox]'))).toBe(false);

      rows()[0].click();
      fixture.detectChanges();
      expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({
        relativeTo: expect.anything(),
        queryParams: { note: 'today-a' },
        queryParamsHandling: 'merge',
        replaceUrl: false,
      }));
    });

    it('ticks the focused row with Space while selecting, and only ticks once from the checkbox', () => {
      select();

      press(rows()[0], ' ');
      expect(count()).toBe('1 selected');
      expect(rows()[0].querySelector<HTMLInputElement>('input[type=checkbox]')?.checked).toBe(true);
      expect(navigate).not.toHaveBeenCalled();

      press(rows()[0], ' ');
      expect(count()).toBe('0 selected');

      const box = rows()[0].querySelector<HTMLInputElement>('input[type=checkbox]') as HTMLInputElement;
      box.focus();
      press(box, ' ');
      expect(count()).toBe('0 selected');

      select();
      press(rows()[0], ' ');
      expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({
        queryParams: { note: 'today-a' },
        replaceUrl: true,
      }));
    });
  });
});
