import { Component, signal, ViewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { provideRouter, Router, RouterOutlet } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { vi } from 'vitest';

import { BookSort } from '../core/dtos/book.enums';
import type { Book, PaginatedResponse } from '../core/dtos/book.dtos';
import { BooksService, type BookListOptions } from '../core/services/books.service';
import { CloudEntryService } from '../core/services/cloud-entry.service';
import { CollectionsService } from '../core/services/collections.service';
import { ExternalMediaService } from '../core/services/external-media.service';
import { ImportService } from '../core/services/import.service';
import { LibraryChangesService } from '../core/services/library-changes.service';
import { LibraryPreferencesService } from '../core/services/library-preferences.service';
import { LibraryFilterService } from './library-filter.service';
import { Library } from './library.component';
import { LIBRARY_BACK_STATE } from './library-list-cache.service';

@Component({ template: '<p class="book-page">Book</p>' })
class BookPage {}

@Component({ template: '<router-outlet />', imports: [RouterOutlet] })
class Shell {
  @ViewChild(RouterOutlet) outlet!: RouterOutlet;
}

const PAGE_SIZE = 20;
const TOTAL = 150;
/** Long enough for a swap's out-phase to finish and commit. */
const SWAP_BUDGET = 300;

function book(index: number, overrides: Partial<Book> = {}): Book {
  return { id: `book-${index}`, title: `Book ${index}`, type: 'physical', status: 0, ...overrides } as Book;
}

/** A library of 90 books, served a page at a time like the server does. */
function pageOf(options: BookListOptions): PaginatedResponse<Book> {
  const size = options.pageSize!;
  const start = (options.page! - 1) * size;
  const items = Array.from({ length: TOTAL }, (_, i) => book(i)).slice(start, start + size);
  return { items, totalCount: TOTAL, page: options.page!, pageSize: size };
}

/** One answer of a single page, for a library that changed while it was away. */
function answer(items: Book[]): PaginatedResponse<Book> {
  return { items, totalCount: items.length, page: 1, pageSize: items.length };
}

/**
 * Back to the Library, driven through real navigations, because the point is what
 * a destroyed page leaves behind for the next one. The Router's popstate is what
 * tells Back from a link, so a mocked one would prove nothing.
 */
describe('Library Back restore', () => {
  let list: ReturnType<typeof vi.fn>;
  let libraryChanged: Subject<void>;
  let imports: { ensureConnected: ReturnType<typeof vi.fn>; ensureConnectedUnlessIdle: ReturnType<typeof vi.fn> };
  let router: Router;
  let location: Location;
  let fixture: ComponentFixture<Shell>;
  let host: HTMLElement;

  const lib = () => fixture.componentInstance.outlet.component as Library;
  const scroller = () => host.querySelector<HTMLElement>('.library-right-side');
  const cards = () => host.querySelectorAll('.table-row').length;

  /** Lets the Router's popstate timer, its navigation and the render all finish. */
  async function settle(ms = 0): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, ms));
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  /** Opens the Library and pages down to three pages at the given scroll offset. */
  async function browse(scrollTop = 1800): Promise<void> {
    await router.navigateByUrl('/library');
    await settle();
    lib().loadMore();
    await settle();
    lib().loadMore();
    await settle();
    expect(cards()).toBe(60);
    scroller()!.scrollTop = scrollTop;
    list.mockClear();
    imports.ensureConnected.mockClear();
    imports.ensureConnectedUnlessIdle.mockClear();
  }

  async function openBook(): Promise<void> {
    await router.navigateByUrl('/library/book-30');
    await settle();
    expect(host.querySelector('.book-page')).not.toBeNull();
  }

  async function goBack(): Promise<void> {
    location.back();
    await settle();
  }

  beforeEach(async () => {
    localStorage.clear();
    list = vi.fn((options: BookListOptions) => of(pageOf(options)));
    libraryChanged = new Subject<void>();
    imports = { ensureConnected: vi.fn(), ensureConnectedUnlessIdle: vi.fn() };

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'library', component: Library },
          { path: 'library/:id', component: BookPage },
        ]),
        provideLocationMocks(),
        {
          provide: BooksService,
          useValue: {
            list,
            getStatusCounts: vi.fn(() =>
              of({ all: 0, notStarted: 0, reading: 0, favorites: 0, finished: 0, unsorted: 0 }),
            ),
          } as unknown as BooksService,
        },
        {
          provide: CollectionsService,
          useValue: {
            sidebarExpanded: signal(true),
            list: vi.fn(() => of([])),
            getCounts: vi.fn(() => of([])),
          } as unknown as CollectionsService,
        },
        { provide: LibraryChangesService, useValue: { watch: () => libraryChanged } },
        { provide: CloudEntryService, useValue: { supportsExternalMediaLibraries: () => false } },
        { provide: ExternalMediaService, useValue: { book: vi.fn() } },
        {
          provide: ImportService,
          useValue: {
            ...imports,
            bookPatched: new Subject<Book>(),
            imports: () => [],
            progressByBookId: () => new Map(),
            inFlightBookIds: () => [],
          } as unknown as ImportService,
        },
      ],
    });

    router = TestBed.inject(Router);
    location = TestBed.inject(Location);
    TestBed.inject(LibraryPreferencesService).setViewMode('list');

    fixture = TestBed.createComponent(Shell);
    host = fixture.nativeElement;
    document.body.appendChild(host);
    // Listens for popstate; a test bed has no bootstrap to do it.
    router.initialNavigation();
  });

  afterEach(() => {
    host.remove();
  });

  it('paints the list on the same card on Back, with no blank grid and no page-one read', async () => {
    await browse(1800);
    await openBook();

    await goBack();

    expect(cards()).toBe(60);
    expect(lib().currentPage()).toBe(3);
    expect(lib().loading()).toBe(false);
    expect(scroller()!.scrollTop).toBe(1800);
    // Nothing replaced the list with page one on the way back.
    expect(list.mock.calls.every(([options]) => options.pageSize === 60)).toBe(true);
  });

  it('re-reads every restored page once, quietly, so a changed library still shows', async () => {
    await browse();
    await openBook();
    list.mockImplementationOnce(() =>
      of(answer([book(0, { title: 'Renamed' })])),
    );

    await goBack();

    expect(list).toHaveBeenCalledTimes(1);
    expect(list.mock.calls[0][0]).toMatchObject({ page: 1, pageSize: 60 });
    expect(lib().books().map((b) => b.title)).toEqual(['Renamed']);
    expect(lib().totalItems()).toBe(1);
  });

  it('keeps a restored list following the change feed, across every page', async () => {
    await browse();
    await openBook();
    await goBack();
    list.mockClear();

    libraryChanged.next();
    await settle();

    expect(list).toHaveBeenCalledTimes(1);
    expect(list.mock.calls[0][0]).toMatchObject({ page: 1, pageSize: 60 });
  });

  it('re-reads a long list as ceil(loaded/100) pages of 100, not one request per loaded page', async () => {
    await router.navigateByUrl('/library');
    await settle();
    for (let page = 2; page <= 6; page++) {
      lib().loadMore();
      await settle();
    }
    expect(cards()).toBe(120);
    await openBook();
    list.mockClear();

    await goBack();

    expect(list.mock.calls.map(([options]) => [options.page, options.pageSize])).toEqual([
      [1, 100],
      [2, 100],
    ]);
    // The second page held 50 books; only the 120 the reader had are kept.
    expect(lib().books()).toHaveLength(120);
    expect(lib().currentPage()).toBe(6);
  });

  it('goes on paging from where the restored list stopped', async () => {
    await browse();
    await openBook();
    await goBack();
    list.mockClear();

    lib().loadMore();
    await settle();

    expect(list.mock.calls[0][0]).toMatchObject({ page: 4, pageSize: PAGE_SIZE });
    expect(cards()).toBe(80);
  });

  it('still re-reads when a page the user asked for overtook the quiet read', async () => {
    await browse();
    await openBook();
    const quiet = new Subject<PaginatedResponse<Book>>();
    list.mockImplementationOnce(() => quiet);

    await goBack();
    lib().loadMore();
    await settle();
    quiet.next(answer([]));
    await settle();

    expect(cards()).toBe(80);
    // Quiet read, the next page, and the quiet read again over all four pages.
    expect(list.mock.calls.map(([options]) => options.page)).toEqual([1, 4, 1]);
    expect(list.mock.calls[2][0].pageSize).toBe(80);
  });

  it('does not count a page still on its way as loaded', async () => {
    await browse();
    list.mockImplementationOnce(() => new Subject<PaginatedResponse<Book>>());
    lib().loadMore();
    await openBook();

    await goBack();
    list.mockClear();
    lib().loadMore();
    await settle();

    expect(list.mock.calls[0][0]).toMatchObject({ page: 4 });
    expect(cards()).toBe(80);
  });

  it('brings the search back with the list it answered', async () => {
    await router.navigateByUrl('/library');
    await settle();
    lib().searchQuery.set('dune');
    lib().refreshBooks();
    await settle(SWAP_BUDGET);
    list.mockClear();
    await openBook();

    await goBack();

    expect(lib().searchQuery()).toBe('dune');
    expect(host.querySelector<HTMLInputElement>('input[type="search"]')!.value).toBe('dune');
    expect(list.mock.calls[0][0]).toMatchObject({ search: 'dune' });
  });

  it('loads fresh when the filter changed since the list was left', async () => {
    await browse();
    await openBook();
    TestBed.inject(LibraryFilterService).status.set('reading');

    await goBack();

    expect(cards()).toBe(20);
    expect(scroller()!.scrollTop).toBe(0);
    expect(list.mock.calls[0][0]).toMatchObject({ page: 1, pageSize: PAGE_SIZE, filter: 'reading' });
  });

  it('loads fresh when the sort changed since the list was left', async () => {
    await browse();
    await openBook();
    TestBed.inject(LibraryPreferencesService).setSort(BookSort.Title);

    await goBack();

    expect(cards()).toBe(20);
    expect(list.mock.calls[0][0]).toMatchObject({ page: 1, sort: BookSort.Title });
  });

  it('a filter change on a restored list starts over from page one', async () => {
    await browse();
    await openBook();
    await goBack();
    list.mockClear();

    TestBed.inject(LibraryFilterService).status.set('reading');
    await settle(SWAP_BUDGET);

    expect(list.mock.calls.at(-1)![0]).toMatchObject({ page: 1, pageSize: PAGE_SIZE, filter: 'reading' });
    expect(cards()).toBe(20);
  });

  it('loads fresh when the Library is opened by a link rather than Back', async () => {
    await browse();
    await openBook();

    await router.navigateByUrl('/library');
    await settle();

    expect(cards()).toBe(20);
    expect(scroller()!.scrollTop).toBe(0);
    expect(imports.ensureConnected).toHaveBeenCalledTimes(1);
    expect(imports.ensureConnectedUnlessIdle).not.toHaveBeenCalled();
  });

  it('restores from the book page\'s own Library link, which means Back', async () => {
    await browse(1800);
    await openBook();

    await router.navigateByUrl('/library', { state: LIBRARY_BACK_STATE });
    await settle();

    expect(cards()).toBe(60);
    expect(scroller()!.scrollTop).toBe(1800);
  });

  it('spares the import feed its read on Back, and only there', async () => {
    await browse();
    await openBook();

    await goBack();

    expect(imports.ensureConnectedUnlessIdle).toHaveBeenCalledTimes(1);
    expect(imports.ensureConnected).not.toHaveBeenCalled();
  });

  it('wakes the import feed when the quiet read finds an import it does not know', async () => {
    await browse();
    await openBook();
    list.mockImplementationOnce(() =>
      of(answer([book(0, { status: 1 })])),
    );

    await goBack();

    expect(imports.ensureConnected).toHaveBeenCalledTimes(1);
  });

  it('keeps nothing from a list that failed to load', async () => {
    await browse();
    list.mockImplementationOnce(() => throwError(() => new Error('down')));
    lib().retryLoad();
    await settle(SWAP_BUDGET);
    expect(lib().loadFailed()).toBe(true);
    await openBook();
    list.mockClear();

    await goBack();

    expect(cards()).toBe(20);
    expect(list.mock.calls[0][0]).toMatchObject({ page: 1, pageSize: PAGE_SIZE });
  });

  it('keeps nothing when the page is left in the middle of a swap', async () => {
    await browse();
    list.mockImplementationOnce(() => new Subject<PaginatedResponse<Book>>());
    TestBed.inject(LibraryFilterService).status.set('reading');
    await settle();
    expect(lib().swapping()).toBe(true);
    await openBook();
    list.mockClear();

    await goBack();

    expect(cards()).toBe(20);
    expect(list.mock.calls[0][0]).toMatchObject({ page: 1, filter: 'reading' });
  });
});
