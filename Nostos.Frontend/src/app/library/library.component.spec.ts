import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Component, computed, Signal, signal, WritableSignal } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { of, Subject } from 'rxjs';

import { Library } from './library.component';
import { BooksService } from '../core/services/books.service';
import { CollectionsService } from '../core/services/collections.service';
import { ImportService } from '../core/services/import.service';
import { ToastService } from '../core/services/toast.service';
import { ImportActivity } from '../core/dtos/import.dtos';
import { PaginatedResponse } from '../core/dtos/book.dtos';
import { Book } from '../core/dtos/book.dtos';
import { BookSort } from '../core/dtos/book.enums';
import {
  LIBRARY_PREFERENCES_STORAGE_KEY,
  LibraryPreferencesService,
} from '../core/services/library-preferences.service';

@Component({ template: '' })
class DummyComponent {}

describe('Library', () => {
  let component: Library;
  let fixture: ComponentFixture<Library>;
  let listSpy: ReturnType<typeof vi.fn>;

  /** The feed, driven per test: this is what an in-flight import looks like. */
  let importEntries: WritableSignal<ImportActivity[]>;
  let progressByBookId: Signal<Map<string, ImportActivity>>;
  let importPatched: Subject<Book>;

  function activity(overrides: Partial<ImportActivity> = {}): ImportActivity {
    return {
      id: 'job-1',
      source: 'job',
      state: 'running',
      stage: 'downloading',
      percent: 42,
      detail: '3/12 files',
      providerId: 'gutenberg',
      externalId: '84',
      assetId: null,
      bookId: 'book-importing',
      title: 'An Importing Title',
      author: 'An Author',
      coverUrl: null,
      errorCode: null,
      message: null,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:10Z',
      canRetry: true,
      ...overrides,
    };
  }

  /** Wall-clock budget a spec must advance to let a deferred swap commit. */
  const SWAP_BUDGET = 400;

  beforeEach(async () => {
    localStorage.clear();
    listSpy = vi.fn(() => of({ items: [], totalCount: 0 } as PaginatedResponse<never>));

    importEntries = signal<ImportActivity[]>([]);
    importPatched = new Subject<Book>();
    progressByBookId = computed(() => {
      const byBook = new Map<string, ImportActivity>();
      for (const entry of importEntries()) {
        if (entry.bookId) byBook.set(entry.bookId, entry);
      }
      return byBook;
    });

    await TestBed.configureTestingModule({
      imports: [Library],
      providers: [
        provideRouter([{ path: 'library', component: DummyComponent }]),
        {
          provide: BooksService,
          useValue: {
            list: listSpy,
            getStatusCounts: vi.fn(() =>
              of({ all: 0, notStarted: 0, reading: 0, favorites: 0, finished: 0, unsorted: 0 }),
            ),
            update: vi.fn(() => of({})),
            delete: vi.fn(() => of(null)),
          } as unknown as BooksService,
        },
        {
          provide: CollectionsService,
          useValue: {
            sidebarExpanded: signal(true),
            list: vi.fn(() => of([])),
            getCounts: vi.fn(() => of([])),
            create: vi.fn(() => of({})),
            update: vi.fn(() => of({})),
            delete: vi.fn(() => of(null)),
          } as unknown as CollectionsService,
        },
        {
          // The library reads the feed as signals: it starts the connection,
          // looks up the import driving a given book, and patches the single book
          // a finished import names. The feed's own behaviour (connection, events,
          // re-sync) is covered by import.service.spec.ts.
          provide: ImportService,
          useValue: {
            bookPatched: importPatched,
            ensureConnected: vi.fn(),
            imports: () => importEntries(),
            progressByBookId: () => progressByBookId(),
            // Same shape as the real signal: the BOOK ROWS being imported, and
            // nothing else. A job with no row yet, or a percentage moving, is not a
            // change the list has to catch up with.
            inFlightBookIds: () =>
              importEntries()
                .filter((entry) => entry.state === 'queued' || entry.state === 'running')
                .map((entry) => entry.bookId)
                .filter((bookId): bookId is string => !!bookId)
                .sort(),
            cancel: vi.fn(),
            retry: vi.fn(),
            dismiss: vi.fn(),
          } as unknown as ImportService,
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(Library);
    component = fixture.componentInstance;
    await fixture.whenStable();
  });

  it('loads books exactly once on init (no duplicate collection load)', () => {
    expect(listSpy).toHaveBeenCalledTimes(1);
    expect(listSpy.mock.calls[0][0].collectionId).toBeUndefined();
  });

  it('re-reads the page when an import gets its book row, so the book can appear', () => {
    const callsBefore = listSpy.mock.calls.length;

    // The row exists now: this is the moment the library should contain the book.
    importEntries.set([activity({ bookId: 'book-importing' })]);
    fixture.detectChanges();

    expect(listSpy.mock.calls.length).toBeGreaterThan(callsBefore);
  });

  it('re-reads the page when an import ends, so the book takes its sorted place', () => {
    importEntries.set([activity({ bookId: 'book-importing' })]);
    fixture.detectChanges();
    const callsWhileImporting = listSpy.mock.calls.length;

    // No longer in flight: the book is an ordinary library book again.
    importEntries.set([activity({ bookId: 'book-importing', state: 'succeeded', percent: 100 })]);
    fixture.detectChanges();

    expect(listSpy.mock.calls.length).toBeGreaterThan(callsWhileImporting);
  });

  it('does not re-read the page for an import that has no book row yet', () => {
    // The feed announces a job before its row exists, so the first appearance of an
    // import is not a change the list can catch up with — re-reading then is a
    // reload the user sees and nothing they gain.
    importEntries.set([activity({ bookId: null, title: null, state: 'queued', stage: 'preparing' })]);
    fixture.detectChanges();
    const calls = listSpy.mock.calls.length;

    importEntries.set([activity({ bookId: null, title: 'Treasure Island', state: 'running', percent: 0 })]);
    fixture.detectChanges();
    expect(listSpy.mock.calls.length).toBe(calls);

    // Now the row exists: this is the change the page is for.
    importEntries.set([activity({ bookId: 'book-importing', title: 'Treasure Island', percent: 4 })]);
    fixture.detectChanges();
    expect(listSpy.mock.calls.length).toBeGreaterThan(calls);
  });

  it('does not re-read the page when the importing book is already on it', () => {
    // Arriving on the library while an import runs: the page already holds the book,
    // because the server sorts an import first. There is nothing to catch up with.
    component.rawBooks.set([importingBook()]);
    importEntries.set([activity()]);
    fixture.detectChanges();
    const calls = listSpy.mock.calls.length;

    importEntries.set([activity({ percent: 12 })]);
    fixture.detectChanges();
    expect(listSpy.mock.calls.length).toBe(calls);

    // When it ends the book stops being sorted first, so now the page must be re-read.
    importEntries.set([]);
    fixture.detectChanges();
    expect(listSpy.mock.calls.length).toBeGreaterThan(calls);
  });

  it('does not re-read the page for progress ticks', () => {
    importEntries.set([activity({ bookId: 'book-importing', percent: 42 })]);
    fixture.detectChanges();
    const calls = listSpy.mock.calls.length;

    // Same set, same row, only the number moved — several times a second.
    importEntries.set([activity({ bookId: 'book-importing', percent: 43 })]);
    fixture.detectChanges();
    importEntries.set([activity({ bookId: 'book-importing', percent: 44, detail: '5/12 files' })]);
    fixture.detectChanges();

    expect(listSpy.mock.calls.length).toBe(calls);
  });

  it('draws nothing at all for an import the page cannot show', () => {
    // The reported glitch, as a test: a strip used to appear for the second between
    // "the row exists" and "the re-read page contains it", then vanish as the card
    // arrived. The item is now the only surface, so nothing may be drawn for an
    // import the current page does not hold — whether it is still being planned (no
    // row yet) or simply not on this page.
    const host = fixture.nativeElement as HTMLElement;

    importEntries.set([activity({ bookId: null, title: null, state: 'queued', stage: 'preparing' })]);
    fixture.detectChanges();
    expect(host.querySelector('.offscreen-imports')).toBeNull();

    importEntries.set([activity({ bookId: 'book-far-away', percent: 17 })]);
    component.rawBooks.set([importingBook({ id: 'some-other-book' })]);
    fixture.detectChanges();
    expect(host.querySelector('.offscreen-imports')).toBeNull();

    // Nothing was inserted above the results either: the toolbar is still first.
    const rightSide = host.querySelector('.library-right-side') as HTMLElement;
    expect(rightSide.firstElementChild!.classList.contains('toolbar')).toBe(true);
  });

  it('draws nothing for a failure with no library row — the feed toasts it instead', () => {
    importEntries.set([
      activity({ bookId: null, title: null, state: 'failed', stage: 'failed', message: 'No downloadable assets.' }),
    ]);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.offscreen-imports')).toBeNull();
  });

  it('announces a finished import whose book is not on the page', () => {
    // The page holds other books; the finished import is not among them, which is
    // what Last Read does to a book nobody has opened yet.
    component.rawBooks.set([{ id: 'other-book', title: 'Some Other Book', type: 'ebook' } as unknown as Book]);
    const successSpy = vi.spyOn(TestBed.inject(ToastService), 'success');

    importPatched.next({ id: 'book-importing', title: 'The Jungle Book', type: 'ebook' } as unknown as Book);

    expect(successSpy).toHaveBeenCalledWith(expect.stringContaining('The Jungle Book'));
  });

  it('stays quiet when the re-read page keeps the finished import in front of the user', () => {
    vi.useFakeTimers();
    try {
      component.rawBooks.set([
        { id: 'book-importing', title: 'The Jungle Book', type: 'ebook', status: 1 } as unknown as Book,
      ]);
      const successSpy = vi.spyOn(TestBed.inject(ToastService), 'success');

      importPatched.next({ id: 'book-importing', title: 'The Jungle Book', type: 'ebook', status: 0 } as unknown as Book);

      // The page is re-read and still holds it (Recently Added: an import is the
      // newest thing there is, so it stays exactly where its card already was).
      listSpy.mockReturnValue(
        of({ items: [{ id: 'book-importing', title: 'The Jungle Book', type: 'ebook' } as unknown as Book], totalCount: 1 }),
      );
      component.refreshBooks();
      vi.advanceTimersByTime(1000);
      fixture.detectChanges();

      expect(successSpy).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it('announces a finished import that the re-read page did not keep', () => {
    vi.useFakeTimers();
    try {
      component.rawBooks.set([
        { id: 'book-importing', title: 'The Jungle Book', type: 'ebook', status: 1 } as unknown as Book,
      ]);
      const successSpy = vi.spyOn(TestBed.inject(ToastService), 'success');

      // It ended while its card was on the page — the server sorts an import first,
      // so that is the normal case and nothing is announced yet.
      importPatched.next({ id: 'book-importing', title: 'The Jungle Book', type: 'ebook', status: 0 } as unknown as Book);
      expect(successSpy).not.toHaveBeenCalled();

      // The re-read page does not keep it: under Last Read a book nobody has opened
      // sorts behind every book that has been read, so the card leaves the page with
      // no sign it arrived anywhere.
      listSpy.mockReturnValue(
        of({ items: [{ id: 'some-other-book', title: 'Some Other Book', type: 'ebook' } as unknown as Book], totalCount: 1 }),
      );
      component.refreshBooks();
      vi.advanceTimersByTime(1000);
      fixture.detectChanges();

      expect(successSpy).toHaveBeenCalledWith(expect.stringContaining('The Jungle Book'));
    } finally {
      vi.useRealTimers();
    }
  });

  it('renders no import chrome at all when nothing is importing', () => {
    const host = fixture.nativeElement as HTMLElement;
    const rightSide = host.querySelector('.library-right-side') as HTMLElement;

    // The regression this exists for: the removed panel was an always-present
    // element with its own top padding, so the library carried a dead band above
    // the toolbar whether or not anything was importing. Nothing may precede the
    // toolbar.
    expect(rightSide.firstElementChild!.classList.contains('toolbar')).toBe(true);
    expect(host.querySelector('.offscreen-imports')).toBeNull();
  });

  it('starts the import feed when the library mounts', () => {
    const feed = TestBed.inject(ImportService) as unknown as { ensureConnected: () => void };
    expect(feed.ensureConnected).toHaveBeenCalled();
  });

  it('patches the one book a finished import names instead of refetching', () => {
    const feed = TestBed.inject(ImportService) as unknown as { bookPatched: Subject<Book> };
    const existing = { id: 'b1', title: 'Old Title' } as Book;
    component.rawBooks.set([existing, { id: 'b2', title: 'Untouched' } as Book]);
    listSpy.mockClear();

    feed.bookPatched.next({ id: 'b1', title: 'New Title' } as Book);

    expect(component.rawBooks()[0].title).toBe('New Title');
    expect(component.rawBooks()[1].title).toBe('Untouched');
    // The rest of the library did not change, so it is not re-queried.
    expect(listSpy).not.toHaveBeenCalled();
  });

  it('one collection change triggers exactly one books request with collectionId', () => {
    listSpy.mockClear();

    component.filters.toggleCollection('c1');
    TestBed.flushEffects();

    const collectionCalls = listSpy.mock.calls.filter(
      (args: unknown[]) => (args[0] as { collectionId?: string }).collectionId !== undefined,
    );
    expect(collectionCalls).toHaveLength(1);
    expect((collectionCalls[0][0] as { collectionId: string }).collectionId).toBe('c1');
  });

  it('requests books for the selected collection', () => {
    listSpy.mockClear();

    component.filters.toggleCollection('c1');
    TestBed.flushEffects();

    expect(component.filters.collectionId()).toBe('c1');
    expect(listSpy).toHaveBeenCalledWith(expect.objectContaining({ collectionId: 'c1' }));
  });

  it('toggling the collection off reloads all books', () => {
    component.filters.toggleCollection('c1');
    TestBed.flushEffects();
    listSpy.mockClear();

    component.filters.toggleCollection('c1');
    TestBed.flushEffects();

    expect(component.filters.collectionId()).toBeNull();
    expect(listSpy).toHaveBeenCalledTimes(1);
    expect(listSpy.mock.calls[0][0].collectionId).toBeUndefined();
  });

  it('renders no toolbar progress-filter dropdown (trigger/menu/options removed)', () => {
    expect(fixture.nativeElement.querySelector('.filter-dropdown')).toBeNull();
    expect(fixture.nativeElement.querySelector('.filter-trigger')).toBeNull();
    expect(fixture.nativeElement.querySelector('.filter-menu')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.filter-option').length).toBe(0);

    const filterButtons = Array.from(
      fixture.nativeElement.querySelectorAll('.toolbar button') as NodeListOf<HTMLButtonElement>,
    ).filter((el) => el.getAttribute('title')?.includes('Filter books') ?? false,
    );
    expect(filterButtons).toHaveLength(0);
  });

  it('does not render a "Reading" label anywhere in the library UI', () => {
    expect(fixture.nativeElement.textContent).not.toContain('Reading');
    const readingTooltips = Array.from(
      fixture.nativeElement.querySelectorAll('[title]') as NodeListOf<HTMLElement>,
    ).filter((el) => el.getAttribute('title')?.includes('Reading') ?? false);
    expect(readingTooltips).toHaveLength(0);
  });

  it('filter changes trigger exactly one books request', () => {
    listSpy.mockClear();

    component.filters.toggleStatus('reading');
    TestBed.flushEffects();

    expect(listSpy).toHaveBeenCalledTimes(1);
    expect(listSpy).toHaveBeenCalledWith(expect.objectContaining({ filter: 'reading' }));
  });

  it('hydrates a stored valid viewMode from localStorage', () => {
    const preferences = TestBed.inject(LibraryPreferencesService);
    preferences.setViewMode('list');

    expect(component.viewMode()).toBe('list');
  });

  it('ignores an invalid stored viewMode and falls back to the default', () => {
    const preferences = TestBed.inject(LibraryPreferencesService);
    preferences.setViewMode('grid');

    expect(component.viewMode()).toBe('grid');
  });

  it('persists the viewMode preference to localStorage when toggled', () => {
    const toggles = Array.from(
      fixture.nativeElement.querySelectorAll('.vt-opt'),
    ) as HTMLButtonElement[];

    toggles[0].click();
    fixture.detectChanges();
    expect(component.viewMode()).toBe('list');
    expect(JSON.parse(localStorage.getItem(LIBRARY_PREFERENCES_STORAGE_KEY)!).viewMode).toBe('list');

    toggles[1].click();
    fixture.detectChanges();
    expect(component.viewMode()).toBe('grid');
    expect(JSON.parse(localStorage.getItem(LIBRARY_PREFERENCES_STORAGE_KEY)!).viewMode).toBe('grid');
  });

  it('exposes the view toggle to assistive tech as a labelled, state-carrying group', () => {
    // The icons carry no text, so without this the two buttons announce as
    // "button" with a name derived from nothing, and the selected view is a
    // colour-only difference. Brain's equivalent control already used this
    // contract (role="group" + aria-pressed); Library's was the outlier.
    const group = fixture.nativeElement.querySelector('.control-group') as HTMLElement;
    expect(group.getAttribute('role')).toBe('group');
    expect(group.getAttribute('aria-label')).toBeTruthy();

    const toggles = Array.from(
      fixture.nativeElement.querySelectorAll('.vt-opt'),
    ) as HTMLButtonElement[];

    // Every option is named...
    for (const t of toggles) {
      expect(t.getAttribute('aria-label')).toBeTruthy();
    }

    // ...and exactly one reports itself pressed, tracking the visible state.
    component.setViewMode('list');
    fixture.detectChanges();
    expect(toggles[0].getAttribute('aria-pressed')).toBe('true');
    expect(toggles[1].getAttribute('aria-pressed')).toBe('false');

    component.setViewMode('grid');
    fixture.detectChanges();
    expect(toggles[0].getAttribute('aria-pressed')).toBe('false');
    expect(toggles[1].getAttribute('aria-pressed')).toBe('true');
  });

  it('renders both glyph boxes at the control\'s shared 18px, on whole pixels', () => {
    // The option box is 30x26, so an odd or fractional glyph size leaves half-pixel
    // margins; the browser then snaps the svg's layout origin to the device grid and
    // the glyph renders off-centre in the raised tile. A 20.5px grid glyph shipped
    // once and did exactly that — measured off the pixels in the running app, its
    // margins inside the tile were 8.00/7.50 and 6.00/5.50 against the list glyph's
    // symmetric 8.25/8.25 and 7.75/7.75 — and the per-glyph size that produced it is
    // gone: both options render the one even 18px box the Brain's toggle uses.
    fixture.detectChanges();

    const widths = Array.from(fixture.nativeElement.querySelectorAll('.vt-opt svg')).map((svg) =>
      Number((svg as SVGElement).getAttribute('width')),
    );

    expect(widths).toEqual([18, 18]);
    for (const width of widths) expect(width % 2).toBe(0);
  });

  it('uses kit primitives for generic Library chrome without changing the segmented view contract', () => {
    fixture.detectChanges();

    const search = fixture.nativeElement.querySelector('.search-input') as HTMLInputElement;
    const sort = fixture.nativeElement.querySelector('.sort-select') as HTMLElement;
    const add = fixture.nativeElement.querySelector('.library-add-button') as HTMLButtonElement;

    expect(search.classList.contains('nostos-form-control--input')).toBe(true);
    expect(search.classList.contains('nostos-form-control--compact')).toBe(true);
    expect(sort.tagName).toBe('APP-DROPDOWN');
    expect(sort.classList.contains('nostos-dropdown--compact')).toBe(true);
    expect(add.classList.contains('nostos-button')).toBe(true);
    expect(add.classList.contains('nostos-button--primary')).toBe(true);

    component.filters.toggleStatus('reading');
    fixture.detectChanges();
    const chip = fixture.nativeElement.querySelector('.filter-chip') as HTMLButtonElement;
    expect(chip.classList.contains('nostos-chip')).toBe(true);

    const toggles = Array.from(
      fixture.nativeElement.querySelectorAll('.vt-opt'),
    ) as HTMLButtonElement[];
    expect(toggles.every((toggle) => toggle.hasAttribute('aria-pressed'))).toBe(true);
    expect(toggles.every((toggle) => !toggle.classList.contains('nostos-button'))).toBe(true);
  });

  it('updates the active sort and persists it through the preferences service', () => {
    const preferences = TestBed.inject(LibraryPreferencesService);

    component.setSort(BookSort.Title);

    expect(component.activeSort()).toBe(BookSort.Title);
    expect(preferences.sort()).toBe(BookSort.Title);
  });

  it('initializes the sort from preferences (not the URL)', () => {
    expect(component.activeSort()).toBe(TestBed.inject(LibraryPreferencesService).sort());
  });

  /**
   * The list view's row contract. The status toggles used to sit in the left
   * gutter BEFORE the cover, which pushed the artwork off the Title column's
   * start and put two bare controls against the row's leading edge. These
   * assertions pin the DOM order that keeps the row's left side clean: cover
   * first, then the title, then the status pair at the end of the column.
   */
  it('orders the title cell as cover, title, then the status toggles', () => {
    // Seeded through the component's own signal (the pattern the delete specs
    // below use) so the row renders without re-stubbing the service.
    component.rawBooks.set([
      {
        id: 'book-1',
        title: 'A Title',
        subtitle: 'A Subtitle',
        author: 'An Author',
        type: 'ebook',
        coverUrl: null,
        rating: 0,
        isFavorite: true,
        finishedAt: null,
        progressPercent: 0,
        createdAt: '2026-01-01T00:00:00Z',
        otherEditions: [],
      } as unknown as Book,
    ]);
    component.setViewMode('list');
    fixture.detectChanges();

    const cell = fixture.nativeElement.querySelector('.col.title') as HTMLElement;
    expect(cell).toBeTruthy();

    const ordered = Array.from(cell.children).map((el) => el.className.split(' ')[0]);
    expect(ordered).toEqual(['list-cover-frame', 'list-title-text', 'list-row-status']);

    // The toggles live INSIDE the status cluster, not loose in the cell, so the
    // cluster can own their spacing and hover reveal as one unit.
    const status = cell.querySelector('.list-row-status') as HTMLElement;
    expect(status.querySelectorAll('.fav-btn-list, .finished-btn-list')).toHaveLength(2);
  });

  /**
   * Clicking a status toggle must flip that status and NOT navigate to the book
   * — the whole row is a routerLink, so an unguarded click on the heart would
   * open the detail page instead. This is the behaviour the move into the
   * `.list-row-status` wrapper (which stops the click) exists to preserve.
   */
  it('toggles a row status without navigating to the book', () => {
    component.rawBooks.set([
      {
        id: 'book-1',
        title: 'A Title',
        author: 'An Author',
        type: 'ebook',
        coverUrl: null,
        rating: 0,
        isFavorite: false,
        finishedAt: null,
        progressPercent: 0,
        createdAt: '2026-01-01T00:00:00Z',
        otherEditions: [],
      } as unknown as Book,
    ]);
    component.setViewMode('list');
    fixture.detectChanges();

    const booksService = TestBed.inject(BooksService);
    const updated = {
      id: 'book-1',
      title: 'A Title',
      type: 'ebook',
      coverUrl: null,
      rating: 0,
      isFavorite: true,
      finishedAt: null,
      progressPercent: 0,
      createdAt: '2026-01-01T00:00:00Z',
      otherEditions: [],
    } as unknown as Book;
    const updateSpy = vi.spyOn(booksService, 'update').mockReturnValue(of(updated));
    const router = TestBed.inject(Router);
    const urlBefore = router.url;
    const navigateSpy = vi.spyOn(router, 'navigateByUrl');

    const fav = fixture.nativeElement.querySelector('.fav-btn-list') as HTMLButtonElement;
    fav.click();
    fixture.detectChanges();

    expect(updateSpy).toHaveBeenCalledWith('book-1', { isFavorite: true });
    expect(navigateSpy).not.toHaveBeenCalled();
    expect(router.url).toBe(urlBefore);
  });

  it('keeps the row status out of the title text so the title stays the row lead', () => {
    component.rawBooks.set([
      {
        id: 'book-1',
        title: 'A Title',
        author: 'An Author',
        type: 'ebook',
        coverUrl: null,
        rating: 0,
        isFavorite: false,
        finishedAt: null,
        progressPercent: 0,
        createdAt: '2026-01-01T00:00:00Z',
        otherEditions: [],
      } as unknown as Book,
    ]);
    component.setViewMode('list');
    fixture.detectChanges();

    const titleText = fixture.nativeElement.querySelector('.list-title-text') as HTMLElement;
    expect(titleText).toBeTruthy();
    // No toggle may be nested inside the title block: its text has to read as
    // the book's name alone, not as a name with controls woven into it.
    expect(titleText.querySelector('.fav-btn-list, .finished-btn-list')).toBeNull();
  });

  /**
   * The lag guard's DOM contract. The list reuses the grid's `.format-badge`
   * class, so a naive reading of the stylesheet suggests every row still gets a
   * frosted glass surface. This asserts the EFFECTIVE markup the row-status work
   * depends on: the badge is a static in-row element whose wrapper exists for
   * every row, and the status cluster is a sibling that owns the toggles.
   *
   * (The rendered `backdrop-filter: none` and the composited-layer count are
   * asserted in tools/verify-library-list.mjs — Vitest does not apply the
   * component stylesheet, so claiming computed styles here would be a false
   * pass.)
   */
  it('renders the format badge as a static in-row element with the status cluster as a sibling', () => {
    component.rawBooks.set([
      {
        id: 'book-1',
        title: 'A Title',
        author: 'An Author',
        type: 'ebook',
        coverUrl: null,
        rating: 0,
        isFavorite: false,
        finishedAt: null,
        progressPercent: 0,
        createdAt: '2026-01-01T00:00:00Z',
        otherEditions: [],
      } as unknown as Book,
    ]);
    component.setViewMode('list');
    fixture.detectChanges();

    const row = fixture.nativeElement.querySelector('.table-row') as HTMLElement;
    expect(row).toBeTruthy();

    // The badge must not be nested in the cover frame (where the grid's overlay
    // lives) — in the list it belongs to the format column.
    const badge = row.querySelector('.col.format .format-badge') as HTMLElement;
    expect(badge).toBeTruthy();
    expect(row.querySelector('.list-cover-frame .format-badge')).toBeNull();

    // Exactly one status cluster per row, holding exactly the two toggles.
    expect(row.querySelectorAll('.list-row-status')).toHaveLength(1);
    expect(row.querySelectorAll('.list-row-status button')).toHaveLength(2);
  });

  it('renders importing status overlay when book status is Downloading or Transcoding', () => {
    component.viewMode.set('grid');
    const baseBook = {
      title: 'A Title',
      subtitle: 'A Subtitle',
      author: 'An Author',
      type: 'ebook',
      coverUrl: null,
      rating: 0,
      isFavorite: false,
      finishedAt: null,
      progressPercent: 0,
      createdAt: '2026-01-01T00:00:00Z',
      otherEditions: [],
    };
    component.rawBooks.set([
      {
        ...baseBook,
        id: 'book-downloading',
        status: 1, // Downloading
      } as unknown as Book,
      {
        ...baseBook,
        id: 'book-transcoding',
        status: 2, // Transcoding
      } as unknown as Book,
      {
        ...baseBook,
        id: 'book-failed',
        status: 3, // Failed
        statusMessage: 'Disk full',
      } as unknown as Book,
    ]);
    fixture.detectChanges();

    const cards = fixture.nativeElement.querySelectorAll('.book-card');
    expect(cards.length).toBe(3);

    expect(cards[0].classList.contains('is-importing')).toBe(true);
    expect(cards[0].querySelector('.import-status-label')?.textContent).toBe('Downloading');

    expect(cards[1].classList.contains('is-importing')).toBe(true);
    expect(cards[1].querySelector('.import-status-label')?.textContent).toBe('Transcoding');

    expect(cards[2].classList.contains('is-failed')).toBe(true);
    expect(cards[2].querySelector('.import-status-label')?.textContent).toBe('Failed');
  });

  // --- Import progress ON the item (no separate surface) ------------------

  function importingBook(overrides: Record<string, unknown> = {}): Book {
    return {
      id: 'book-importing',
      title: 'An Importing Title',
      subtitle: null,
      author: 'An Author',
      type: 'ebook',
      coverUrl: null,
      rating: 0,
      isFavorite: false,
      finishedAt: null,
      progressPercent: 0,
      createdAt: '2026-01-01T00:00:00Z',
      otherEditions: [],
      status: 1,
      statusMessage: null,
      ...overrides,
    } as unknown as Book;
  }

  it('draws the live percentage on the card that is being imported', () => {
    component.viewMode.set('grid');
    component.rawBooks.set([importingBook()]);
    importEntries.set([activity({ percent: 42, stage: 'downloading' })]);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const card = host.querySelector('.book-card') as HTMLElement;
    const cover = card.querySelector('.cover-wrapper') as HTMLElement;

    const progress = cover.querySelector('.cover-progress') as HTMLElement;
    expect(progress).not.toBeNull();
    // Inside the cover, which is the whole reason it costs the card no height:
    // the finished card is this same box.
    expect(progress.parentElement).toBe(cover);
    expect(progress.getAttribute('role')).toBe('progressbar');
    expect(progress.getAttribute('aria-valuenow')).toBe('42');
    expect(progress.querySelector('.import-status-label')!.textContent).toBe('Downloading');
    expect(progress.querySelector('.cover-progress-percent')!.textContent).toBe('42%');
    expect((progress.querySelector('.cover-progress-track i') as HTMLElement).style.width).toBe(
      '42%',
    );
  });

  it('names the saving stage rather than calling everything downloading', () => {
    component.viewMode.set('grid');
    component.rawBooks.set([importingBook({ status: 1 })]);
    importEntries.set([activity({ stage: 'importing', percent: 82 })]);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.import-status-label')!.textContent).toBe('Saving');
    expect(host.querySelector('.cover-progress-percent')!.textContent).toBe('82%');
  });

  it('falls back to the book status while the feed has not caught up', () => {
    // A page loaded mid-import renders status 1/2 books for a fraction of a
    // second before the first frame lands; the card must never look wrong.
    component.viewMode.set('grid');
    component.rawBooks.set([importingBook({ status: 2 })]);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.import-status-label')!.textContent).toBe('Transcoding');
    expect(host.querySelector('.cover-progress-percent')).toBeNull();
  });

  it('draws the same progress on the list row', () => {
    component.setViewMode('list');
    component.rawBooks.set([importingBook()]);
    importEntries.set([activity({ percent: 61 })]);
    fixture.detectChanges();

    const row = fixture.nativeElement.querySelector('.table-row') as HTMLElement;
    expect(row.classList.contains('is-importing')).toBe(true);
    expect(row.querySelector('.list-progress')!.getAttribute('aria-valuenow')).toBe('61');
    expect(row.querySelector('.list-progress-caption')!.textContent).toContain('61%');
    expect((row.querySelector('.list-progress-track i') as HTMLElement).style.width).toBe('61%');
  });

  it('explains a failure on the card itself, with the actions reachable', () => {
    component.viewMode.set('grid');
    component.rawBooks.set([
      importingBook({ status: 3, statusMessage: 'The source refused the download.' }),
    ]);
    importEntries.set([
      activity({ state: 'failed', stage: 'failed', message: 'The source refused the download.' }),
    ]);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    // Not a tooltip: the message is text on the card, so a phone can read it.
    expect(host.querySelector('.item-failure')!.textContent).toContain(
      'The source refused the download.',
    );

    const buttons = [...host.querySelectorAll('.item-failure-actions .item-action')];
    expect(buttons.map((b) => b.textContent!.trim())).toEqual(['Retry', 'Dismiss']);

    buttons[0].dispatchEvent(new Event('click'));
    const feed = TestBed.inject(ImportService) as unknown as { retry: ReturnType<typeof vi.fn> };
    expect(feed.retry).toHaveBeenCalledWith(expect.objectContaining({ id: 'job-1' }));
  });

  it('shows a failed book message even when the feed no longer knows the import', () => {
    component.viewMode.set('grid');
    component.rawBooks.set([importingBook({ status: 3, statusMessage: 'Disk full.' })]);
    importEntries.set([]);
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.item-failure')!.textContent).toContain('Disk full.');
    // No entry means no provider/item ids, so there is nothing to retry from.
    expect(host.querySelectorAll('.item-failure-actions .item-action')).toHaveLength(0);
  });

  it('adds no chrome when the importing item is already in the list', () => {
    component.viewMode.set('grid');
    component.rawBooks.set([importingBook()]);
    importEntries.set([activity()]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.offscreen-imports')).toBeNull();
  });

  it('shows an empty state whose creation action opens Add Book directly', () => {
    fixture.detectChanges();

    const empty = fixture.nativeElement.querySelector(
      '.container.lg .books-empty-state',
    ) as HTMLElement;
    expect(empty.textContent).toContain('Your library is empty');
    empty.querySelector('button')!.click();

    expect(component.showAddModal()).toBe(true);
  });

  it('opens and closes Add Book without a separate acquisition chooser', () => {
    expect(fixture.nativeElement.querySelector('app-add-book-intent')).toBeNull();

    component.openAddModal();
    expect(component.showAddModal()).toBe(true);

    component.closeAddModal();
    expect(component.showAddModal()).toBe(false);
  });

  it('offers to clear the search when nothing matches', () => {
    component.searchQuery.set('zzz');
    fixture.detectChanges();

    const empty = fixture.nativeElement.querySelector('.container.lg .books-empty-state') as HTMLElement;
    expect(empty.textContent).toContain('No books match "zzz"');
  });

  it('shows "Library" with no chips when no filter is active', () => {
    expect(component.activeFilterChips()).toEqual([]);
    // The chip row stays mounted so its height can animate on the first chip;
    // with no chips it is collapsed and renders nothing to announce.
    expect(fixture.nativeElement.querySelector('.filter-bar.is-empty')).not.toBeNull();
    expect(fixture.nativeElement.querySelectorAll('.filter-chip').length).toBe(0);
  });

  it('reflects a status filter in chips', () => {
    component.filters.toggleStatus('reading');
    fixture.detectChanges();

    expect(component.activeFilterChips()).toEqual([{ key: 'status', label: 'In Progress' }]);
    expect(fixture.nativeElement.querySelector('.active-filters')).not.toBeNull();
  });

  it('combines format and collection in chips', () => {
    component.collections.set([{ id: 'c1', name: 'Science Fiction', parentId: null }]);
    component.filters.toggleFormat('audiobook');
    component.filters.toggleCollection('c1');
    fixture.detectChanges();

    expect(component.activeFilterChips()).toEqual([
      { key: 'format', label: 'Audiobooks' },
      { key: 'collection', label: 'Science Fiction' },
    ]);
  });

  it('keeps a static Library heading when filters and search change', () => {
    fixture.detectChanges();
    const title = fixture.nativeElement.querySelector('#library-title') as HTMLElement;

    expect(title.textContent?.trim()).toBe('Library');
    expect(title.classList.contains('library-title')).toBe(true);

    component.collections.set([{ id: 'c1', name: 'Science Fiction', parentId: null }]);
    component.filters.toggleStatus('reading');
    component.filters.toggleFormat('audiobook');
    component.filters.toggleCollection('c1');
    component.searchQuery.set('zzz');
    fixture.detectChanges();

    expect(title.textContent?.trim()).toBe('Library');
    expect(fixture.nativeElement.querySelectorAll('.title-swap').length).toBe(0);
  });

  it('clearing a chip resets just that filter and reloads', () => {
    component.filters.toggleStatus('finished');
    component.filters.toggleFormat('ebook');
    TestBed.flushEffects();
    expect(component.activeFilterChips().length).toBe(2);
    listSpy.mockClear();

    component.clearChip('status');
    TestBed.flushEffects();

    expect(component.filters.status()).toBe('all');
    expect(component.filters.format()).toBe('ebook');
    expect(listSpy).toHaveBeenCalledWith(
      expect.objectContaining({ filter: undefined, format: 'ebook' }),
    );
  });

  it('opens the confirm dialog when the delete button is clicked, and cancels', () => {
    const testBook = { id: 'b1', title: 'Test Book', type: 'ebook', progressPercent: 0 } as any;
    component.rawBooks.set([testBook]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeNull();

    component.openDeleteModal(testBook, new MouseEvent('click'));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.confirm-title').textContent).toContain('Test Book');

    component.cancelDelete();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeNull();
  });

  it('deletes book when confirmed through the confirm dialog', () => {
    const testBook = { id: 'b1', title: 'Test Book', type: 'ebook', progressPercent: 0 } as any;
    component.rawBooks.set([testBook]);
    fixture.detectChanges();

    component.openDeleteModal(testBook, new MouseEvent('click'));
    fixture.detectChanges();

    const booksService = TestBed.inject(BooksService);
    const deleteSpy = vi.spyOn(booksService, 'delete').mockReturnValue(of(null as any));

    component.confirmDelete();
    fixture.detectChanges();

    expect(deleteSpy).toHaveBeenCalledWith('b1');
    expect(component.rawBooks().length).toBe(0);
    expect(fixture.nativeElement.querySelector('.confirm-modal-card')).toBeNull();
  });

  // --- Filter/sort cross-fade (no waiting placeholder, no layout jitter) --

  it('never puts the waiting field back up after the first load', () => {
    expect(component.loading()).toBe(false);

    const onScreen = { id: 'b1', title: 'On Screen', type: 'ebook' } as never;
    component.rawBooks.set([onScreen]);
    component.filters.toggleStatus('reading');
    fixture.detectChanges();

    // The results region belongs to the first paint only; a filter change
    // cross-fades the results that are already on screen.
    expect(component.loading()).toBe(false);
    expect(component.swapping()).toBe(true);
    expect(fixture.nativeElement.querySelector('.results-stage.is-swapping')).not.toBeNull();
  });

  it('holds the current results on screen until the out-phase ends, then swaps', () => {
    vi.useFakeTimers();
    try {
      const oldBook = { id: 'old', title: 'Old', type: 'ebook' } as never;
      const newBook = { id: 'new', title: 'New', type: 'ebook' } as never;
      // There must be results on screen for the out-phase to be worth holding.
      component.rawBooks.set([oldBook]);

      listSpy.mockReturnValueOnce(of({ items: [newBook], totalCount: 1 } as never));

      component.filters.toggleStatus('reading');
      TestBed.flushEffects();

      expect(component.loading()).toBe(false);
      expect(component.swapping()).toBe(true);
      expect(component.rawBooks()).toEqual([oldBook]);

      vi.advanceTimersByTime(SWAP_BUDGET);
      expect(component.rawBooks()).toEqual([newBook]);
      expect(component.swapping()).toBe(false);

      const thirdBook = { id: 'third', title: 'Third', type: 'ebook' } as never;
      listSpy.mockReturnValueOnce(of({ items: [thirdBook], totalCount: 1 } as never));

      component.filters.toggleStatus('finished');
      TestBed.flushEffects();

      // Still the old page at this instant: the DOM swaps at the bottom of the blur.
      expect(component.rawBooks()).toEqual([newBook]);

      vi.advanceTimersByTime(SWAP_BUDGET);
      expect(component.rawBooks()).toEqual([thirdBook]);
      expect(component.swapping()).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it('commits at once when there is nothing on screen to blur out', () => {
    const book = { id: 'b1', title: 'Only', type: 'ebook' } as never;
    listSpy.mockReturnValueOnce(of({ items: [book], totalCount: 1 } as never));

    component.filters.toggleStatus('reading');
    TestBed.flushEffects();

    // No empty stage holding for 200ms: the results (and their fade-in) start now.
    expect(component.rawBooks()).toEqual([book]);
    expect(component.swapping()).toBe(false);
  });

  it('leaves the results region empty through a cold load, then resolves in', () => {
    vi.useFakeTimers();
    try {
      const preferences = TestBed.inject(LibraryPreferencesService);
      const book = { id: 'b1', title: 'First', type: 'ebook' } as never;
      // A genuine cold start: nothing has been shown in this session, so the
      // results region is held open and empty while the first page is in flight.
      preferences.hasLoadedBooks.set(false);
      const pending = new Subject<PaginatedResponse<never>>();
      listSpy.mockReturnValueOnce(pending);

      component.filters.toggleStatus('reading');
      TestBed.flushEffects();
      fixture.detectChanges();
      expect(component.loading()).toBe(true); // the region is reserved
      expect(fixture.nativeElement.querySelector('.results-stage.is-waiting')).not.toBeNull();
      // Nothing is drawn in the content's place — no placeholder to jitter.
      expect(fixture.nativeElement.querySelector('.book-grid')).toBeNull();
      expect(fixture.nativeElement.querySelector('.table-view')).toBeNull();

      // The page lands after the region has been open a while.
      vi.advanceTimersByTime(300);
      pending.next({ items: [book], totalCount: 1 } as never);
      pending.complete();

      // A cold load has no out-phase to wait for: the results commit at once and
      // fade in over the space that was already reserved for them.
      fixture.detectChanges();
      expect(component.loading()).toBe(false);
      expect(component.swapping()).toBe(false);
      expect(component.rawBooks()).toEqual([book]);
      expect(fixture.nativeElement.querySelector('.results-stage.is-waiting')).toBeNull();
      expect(fixture.nativeElement.querySelector('.book-grid')).not.toBeNull();
      expect(preferences.hasLoadedBooks()).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it('ignores a stale response that arrives after a newer filter change', () => {
    vi.useFakeTimers();
    try {
      const pending: Subject<PaginatedResponse<never>>[] = [];
      listSpy.mockImplementation(() => {
        const subject = new Subject<PaginatedResponse<never>>();
        pending.push(subject);
        return subject;
      });

      component.filters.toggleStatus('reading');
      TestBed.flushEffects();
      component.filters.toggleStatus('finished');
      TestBed.flushEffects();
      expect(pending).toHaveLength(2);

      const newer = { id: 'newer', title: 'Newer', type: 'ebook' } as never;
      const stale = { id: 'stale', title: 'Stale', type: 'ebook' } as never;
      pending[1].next({ items: [newer], totalCount: 1 } as never);
      pending[1].complete();
      vi.advanceTimersByTime(SWAP_BUDGET);
      expect(component.rawBooks()).toEqual([newer]);

      // The abandoned request answers late: it must not repaint the list.
      pending[0].next({ items: [stale], totalCount: 1 } as never);
      pending[0].complete();
      vi.advanceTimersByTime(SWAP_BUDGET);
      expect(component.rawBooks()).toEqual([newer]);
    } finally {
      vi.useRealTimers();
    }
  });

  it('reserves the page scrollbar gutter only while the library is mounted', () => {
    expect(document.body.classList.contains('nostos-library')).toBe(true);

    fixture.destroy();

    expect(document.body.classList.contains('nostos-library')).toBe(false);
  });

  it('reflects sidebarExpanded on layout-wrapper directly without depending on child initialization', () => {
    const preferences = TestBed.inject(LibraryPreferencesService);
    preferences.setSidebarExpanded(false);
    fixture.detectChanges();

    const wrapper = fixture.nativeElement.querySelector('.layout-wrapper') as HTMLElement;
    expect(wrapper.classList.contains('sidebar-collapsed')).toBe(true);

    component.toggleSidebar();
    fixture.detectChanges();
    expect(preferences.sidebarExpanded()).toBe(true);
    expect(wrapper.classList.contains('sidebar-collapsed')).toBe(false);
  });

  it('cross-fades rather than waiting again when re-entering the library', () => {
    // First visit: the waiting field is legitimate and the results are now known.
    expect(component.loading()).toBe(false);
    expect(TestBed.inject(LibraryPreferencesService).hasLoadedBooks()).toBe(true);

    // Leaving for the Studio / Second Brain destroys this component; coming back
    // rebuilds it with no results in hand.
    fixture.destroy();
    const second = TestBed.createComponent(Library);
    const secondComponent = second.componentInstance;
    TestBed.flushEffects();
    second.detectChanges();

    expect(secondComponent.loading()).toBe(false);
    expect(second.nativeElement.querySelector('.results-stage')).not.toBeNull();

    second.destroy();
  });
});
