/**
 * The book page for one book across editions (issue #1301): the book's status,
 * a notes feed that holds still across editions, the edition's own rating and
 * the reviews an edition keeps.
 */
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { BookDetail } from './book-detail.component';
import type { Book, EditionSummaryDto } from '../core/dtos/book.dtos';
import { brainReads, notesPage } from './book-detail.testing';

const epub: Book = {
  id: 'b1',
  title: 'The Magic Mountain',
  subtitle: null,
  author: 'Thomas Mann',
  editor: null,
  translator: null,
  narrator: null,
  description: null,
  type: 'ebook',
  edition: null,
  asin: null,
  duration: null,
  isbn: null,
  publisher: null,
  placeOfPublication: null,
  publishedDate: null,
  pageCount: null,
  language: null,
  categories: null,
  series: null,
  volumeNumber: null,
  createdAt: '2026-08-01T08:00:00+02:00',
  hasFile: true,
  fileName: 'mountain.epub',
  coverUrl: null,
  collectionIds: [],
  lastLocation: null,
  progressPercent: 0,
  lastReadAt: null,
  rating: 4,
  isFavorite: true,
  personalReview: 'A sanatorium for the whole of Europe.',
  finishedAt: null,
  workId: 'w1',
  editionCount: 2,
};

const audioSummary: EditionSummaryDto = {
  id: 'b2',
  type: 'audiobook',
  progressPercent: 0,
  hasFile: true,
  fileName: null,
};

describe('BookDetail across editions', () => {
  let fixture: ComponentFixture<BookDetail>;
  let component: BookDetail;
  let httpMock: HttpTestingController;
  let routeId = 'b1';

  function twoEditions(overrides: Partial<Book> = {}, audio: Partial<EditionSummaryDto> = {}): Book {
    return { ...epub, otherEditions: [{ ...audioSummary, ...audio }], ...overrides };
  }

  async function setup(initial: Book, notes: Parameters<typeof notesPage>[0] = []) {
    routeId = initial.id;
    fixture = TestBed.createComponent(BookDetail);
    component = fixture.componentInstance;
    await fixture.whenStable();
    fixture.detectChanges();

    httpMock.expectOne(`/api/books/${initial.id}`).flush(initial);
    httpMock
      .expectOne((request) => request.url === '/api/notes' && request.params.has(initial.workId && initial.otherEditions?.length ? 'workId' : 'bookId'))
      .flush(notesPage(notes));
    httpMock.expectOne('/api/collections').flush([]);
    httpMock.match(brainReads).forEach((request) => request.flush([]));
    fixture.detectChanges();
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const statusPill = () => el().querySelector('.status-dropdown .nostos-dropdown__trigger') as HTMLButtonElement;
  const switcherTrigger = () => el().querySelector('.edition-switcher .es-trigger') as HTMLButtonElement;

  function menuItems(): HTMLButtonElement[] {
    switcherTrigger().click();
    fixture.detectChanges();
    return Array.from(document.querySelectorAll('.nostos-menu-item')) as HTMLButtonElement[];
  }
  const labelOf = (item: Element) => item.querySelector('.nostos-menu-item__label')?.textContent?.trim();
  const hintOf = (item: Element) => item.querySelector('.nostos-menu-item__hint')?.textContent?.trim();

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BookDetail],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useFactory: () => ({ paramMap: of(convertToParamMap({ id: routeId })) }) },
      ],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    document.querySelectorAll('.cdk-overlay-container').forEach((overlay) => (overlay.innerHTML = ''));
    httpMock.match(brainReads).forEach((request) => {
      if (!request.cancelled) request.flush([]);
    });
    httpMock.verify();
  });

  // ── Status ──────────────────────────────────────────────────────────────────

  describe('reading status', () => {
    it('is Finished on the untouched audiobook when the EPUB was finished', async () => {
      await setup(twoEditions({ progressPercent: 100, finishedAt: '2026-09-01T08:00:00Z' }));
      expect(statusPill().textContent).toContain('Finished');
    });

    it('is Reading on a reread, with the figure of the edition read', async () => {
      await setup(
        twoEditions(
          { progressPercent: 100, finishedAt: '2026-01-01T08:00:00Z', lastReadAt: '2026-01-01T08:00:00Z' },
          { progressPercent: 34, lastReadAt: '2026-10-01T08:00:00Z' },
        ),
      );
      expect(statusPill().textContent).toContain('Reading');
      expect(statusPill().querySelector('.nostos-dropdown__detail')?.textContent?.trim()).toBe('· 34%');
    });

    it('shows the figure of the edition on screen when it is the one being read', async () => {
      await setup(
        twoEditions(
          { progressPercent: 37, lastReadAt: '2026-10-01T08:00:00Z', lastLocation: 'cfi' },
          { progressPercent: 34, lastReadAt: '2026-10-05T08:00:00Z' },
        ),
      );
      expect(statusPill().querySelector('.nostos-dropdown__detail')?.textContent?.trim()).toBe('· 37%');
    });

    it('asks to clear the place in both editions and resets them all', async () => {
      await setup(twoEditions({ progressPercent: 100, finishedAt: '2026-09-01T08:00:00Z' }));

      statusPill().click();
      fixture.detectChanges();
      const notStarted = Array.from(el().querySelectorAll('.status-dropdown .nostos-dropdown__option')).find((o) =>
        o.textContent?.includes('Not started'),
      ) as HTMLElement;
      notStarted.click();
      fixture.detectChanges();

      expect(el().querySelector('.status-confirm .confirm-modal-card')?.textContent).toContain(
        'clear your place in both editions of this book',
      );
      (el().querySelector('.status-confirm .btn-confirm') as HTMLButtonElement).click();
      fixture.detectChanges();

      const reset = httpMock.expectOne(
        (request) => request.method === 'POST' && request.url === '/api/books/b1/progress/reset',
      );
      expect(reset.request.params.get('allEditions')).toBe('true');
      reset.flush({});
      httpMock.expectOne('/api/books/b1').flush(twoEditions());
    });

    it('names the number of editions when there are more than two', async () => {
      await setup(
        twoEditions({ progressPercent: 100, finishedAt: '2026-09-01T08:00:00Z' }, {}),
      );
      component.store.book.update((book) =>
        book ? { ...book, otherEditions: [...book.otherEditions!, { ...audioSummary, id: 'b3', type: 'physical' }] } : book,
      );
      component.pendingStatus.set('notstarted');
      fixture.detectChanges();

      expect(component.statusConfirm()?.description).toContain('all 3 editions of this book');
    });

    it('keeps the single-edition question as it was', async () => {
      await setup({ ...epub, otherEditions: [], progressPercent: 40, lastLocation: 'cfi' });
      component.pendingStatus.set('notstarted');

      expect(component.statusConfirm()?.description).toContain('clear your saved page position');
      expect(component.statusConfirm()?.description).not.toContain('editions');
    });

    it('marks Finished on the edition on screen only', async () => {
      await setup(twoEditions({ progressPercent: 40, lastLocation: 'cfi', lastReadAt: '2026-10-01T08:00:00Z' }));

      component.selectStatusOption('finished');

      const put = httpMock.expectOne((request) => request.method === 'PUT' && request.url === '/api/books/b1');
      expect(put.request.body).toEqual({ isFinished: true });
      put.flush(twoEditions({ progressPercent: 100, finishedAt: '2026-10-02T08:00:00Z' }));
    });
  });

  // ── Notes ───────────────────────────────────────────────────────────────────

  describe('notes feed', () => {
    const notes = [
      { id: 'n1', bookId: 'b1', bookFormat: 'ebook', content: 'Chapter three.', sourceAnchorKind: 'unknown' },
      { id: 'n2', bookId: 'b2', bookFormat: 'audiobook', content: 'Too jolly.', sourceAnchorKind: 'audio_timestamp', sourceAnchorValue: '11560', anchorVerified: true },
      { id: 'n3', bookId: 'b1', bookFormat: 'ebook', content: 'Counting weeks.' },
    ];

    it('is read by the work for a book with several editions, and by the book for one', async () => {
      await setup(twoEditions(), notes);
      expect(component.filteredNotes().length).toBe(3);
    });

    it('asks for the book\'s own notes when it has one edition', async () => {
      // `setup` only accepts the request when it filters by the book.
      await setup({ ...epub, otherEditions: [], editionCount: 1 }, [{ id: 'n1', bookId: 'b1' }]);
      expect(component.filteredNotes().length).toBe(1);
    });

    it('names the edition on each note of a multi-edition book', async () => {
      await setup(twoEditions(), notes);

      const formats = Array.from(el().querySelectorAll('.notes-feed [data-testid="note-edition-format"]')).map((f) =>
        f.textContent?.trim(),
      );
      expect(formats).toEqual(['Ebook', 'Audiobook', 'Ebook']);
    });

    it('does not name the edition on a single-edition book', async () => {
      await setup({ ...epub, otherEditions: [], editionCount: 1 }, [{ id: 'n1', bookId: 'b1', bookFormat: 'ebook' }]);

      expect(el().querySelector('[data-testid="note-edition-format"]')).toBeNull();
      expect(el().querySelector('.notes-edition-filter')).toBeNull();
    });

    it('filters by edition, counts what is shown and falls back to all', async () => {
      await setup(twoEditions(), notes);

      const filter = el().querySelector('.notes-edition-filter') as HTMLElement;
      expect(filter.querySelector('.nostos-dropdown__value')?.textContent?.trim()).toBe('All editions');
      (filter.querySelector('.nostos-dropdown__trigger') as HTMLButtonElement).click();
      fixture.detectChanges();
      const options = Array.from(filter.querySelectorAll('.nostos-dropdown__option'));
      expect(options.map((o) => o.textContent?.trim())).toEqual(['All editions', 'Ebook · 2', 'Audiobook · 1']);

      (options[2] as HTMLElement).click();
      fixture.detectChanges();
      expect(el().querySelectorAll('.notes-feed app-note-item').length).toBe(1);
      expect(el().querySelector('.notes-header [appBadge], .notes-header .badge, .notes-header span')?.textContent).toContain('1');

      component.store.removeNote('n2');
      fixture.detectChanges();
      expect(component.notesEditionValue()).toBe('all');
      expect(el().querySelector('.notes-edition-filter')).toBeNull();
    });

    it('has no filter when the notes come from one edition', async () => {
      await setup(twoEditions(), [notes[0], notes[2]]);

      expect(el().querySelector('.notes-edition-filter')).toBeNull();
      expect(el().querySelectorAll('.notes-feed app-note-item').length).toBe(2);
    });

    it('names two editions of one format as the edition switcher does', async () => {
      await setup(
        twoEditions(
          { edition: '1st' },
          { type: 'ebook', fileName: 'other.epub', edition: '2nd' },
        ),
        [
          { id: 'n1', bookId: 'b1', bookFormat: 'ebook' },
          { id: 'n2', bookId: 'b2', bookFormat: 'ebook' },
        ],
      );

      expect(component.noteEditionOptions().map((o) => o.label)).toEqual([
        'All editions',
        'Ebook · 1st edition · 1',
        'Ebook · 2nd edition · 1',
      ]);
    });

    it('keeps the feed when another edition of the same book is opened', async () => {
      await setup(twoEditions(), notes);

      component.store.loadAllData('b2');
      fixture.detectChanges();
      expect(component.store.notes().length).toBe(3);

      const sibling = { ...epub, id: 'b2', type: 'audiobook', fileName: null, otherEditions: [{ ...audioSummary, id: 'b1', type: 'ebook' }] };
      httpMock.expectOne('/api/books/b2').flush(sibling);
      httpMock.match('/api/collections').forEach((request) => request.flush([]));
      fixture.detectChanges();

      // The same work: no second read of the notes, and nothing changed on screen.
      httpMock.expectNone((request) => request.url === '/api/notes');
      expect(component.store.notes().map((note) => note.id)).toEqual(['n1', 'n2', 'n3']);
    });
  });

  // ── Edition rating ──────────────────────────────────────────────────────────

  describe('edition rating', () => {
    it('adds "Rate this edition" after the editions, and nothing for a single edition', async () => {
      await setup(twoEditions());
      const items = menuItems();

      expect(items.map(labelOf)).toEqual(['Audiobook', 'Ebook', 'Rate this edition']);
      expect(document.querySelector('.nostos-menu [role="separator"], .nostos-menu hr')).not.toBeNull();
    });

    it('says "Rate the narration" on an audiobook', async () => {
      const audio = twoEditions({ id: 'b2', type: 'audiobook', fileName: null }, { id: 'b1', type: 'ebook', fileName: 'x.epub' });
      await setup(audio);

      expect(menuItems().map(labelOf)).toContain('Rate the narration');
    });

    it('offers no rating row on a single-edition book', async () => {
      await setup({ ...epub, otherEditions: [], editionCount: 1 });

      expect(el().querySelector('.edition-switcher')).toBeNull();
      expect(component.editionRatingAction()).toBeNull();
      expect(el().querySelector('.strip-edition-rating')).toBeNull();
    });

    it('reveals empty stars under the verdict row when chosen, and saves a tap', async () => {
      await setup(twoEditions());
      expect(el().querySelector('.strip-edition-rating')).toBeNull();

      menuItems().find((item) => labelOf(item) === 'Rate this edition')!.click();
      fixture.detectChanges();

      const row = el().querySelector('.strip-edition-rating') as HTMLElement;
      expect(row.textContent).toContain('This edition');
      expect(row.querySelector('app-star-rating')).not.toBeNull();
      expect(row.querySelectorAll('.star-icon.filled').length).toBe(0);
      expect(row.previousElementSibling?.classList.contains('strip-verdict')).toBe(true);

      (row.querySelectorAll('nostos-icon.star-icon')[1] as HTMLElement).click();
      const put = httpMock.expectOne((request) => request.method === 'PUT' && request.url === '/api/books/b1');
      expect(put.request.body).toEqual({ editionRating: 2 });
      put.flush({ ...twoEditions(), editionRating: 2 });
      fixture.detectChanges();

      expect(component.store.book()?.editionRating).toBe(2);
      expect(component.store.book()?.rating).toBe(4);
    });

    it('stays on the page once set, says so in the menu and the hint, and clears from the menu', async () => {
      await setup(twoEditions({ editionRating: 3 }));
      expect(el().querySelector('.strip-edition-rating')?.textContent).toContain('This edition');

      const items = menuItems();
      expect(items.map(labelOf)).toEqual(['Audiobook', 'Ebook', 'Clear edition rating']);
      expect(hintOf(items[1])).toBe('Not started · this edition ★★★');
      expect(items[1].getAttribute('aria-label')).toBe('Ebook, not started, this edition rated 3 out of 5');
      expect(switcherTrigger().title).toContain('this edition rated 3 out of 5');

      items[2].click();
      fixture.detectChanges();
      const put = httpMock.expectOne((request) => request.method === 'PUT' && request.url === '/api/books/b1');
      expect(put.request.body).toEqual({ editionRating: 0 });
      put.flush(twoEditions());
      expect(el().querySelector('.strip-edition-rating')).toBeNull();
    });

    it('labels the row "Narration" on an audiobook', async () => {
      await setup(twoEditions({ id: 'b2', type: 'audiobook', fileName: null, editionRating: 2 }, { id: 'b1', type: 'ebook' }));

      expect(el().querySelector('.strip-edition-rating')?.textContent).toContain('Narration');
      expect(menuItems().map(labelOf)).toContain('Clear narration rating');
    });

    it('shows the rating of another edition in the menu hint', async () => {
      await setup(twoEditions({}, { rating: 2, progressPercent: 34 }));

      expect(hintOf(menuItems()[0])).toBe('34% read · narration ★★');
    });
  });

  // ── Edition review ──────────────────────────────────────────────────────────

  describe('reviews kept by an edition', () => {
    it('shows every edition\'s own review under the book\'s, with its format', async () => {
      await setup(twoEditions({ editionReview: 'The print is small.' }, { personalReview: 'The narrator hurries.' }));

      const reviews = Array.from(el().querySelectorAll('[data-testid="edition-review"]'));
      expect(reviews.map((r) => r.querySelector('.edition-review-text')?.textContent)).toEqual([
        'The print is small.',
        'The narrator hurries.',
      ]);
      expect(reviews[1].textContent).toContain('Audiobook');
      expect(reviews[1].querySelector('nostos-icon')).not.toBeNull();
      expect(el().querySelector('.personal-review')?.textContent).toContain('A sanatorium');
    });

    it('shows nothing when no edition keeps a review', async () => {
      await setup(twoEditions());

      expect(el().querySelector('[data-testid="edition-review"]')).toBeNull();
    });

    it('removes another edition\'s review through that edition', async () => {
      await setup(twoEditions({}, { personalReview: 'The narrator hurries.' }));

      (el().querySelector('.edition-review-remove') as HTMLButtonElement).click();
      fixture.detectChanges();

      const put = httpMock.expectOne((request) => request.method === 'PUT' && request.url === '/api/books/b2');
      expect(put.request.body).toEqual({ editionReview: '' });
      put.flush({});
      expect(el().querySelector('[data-testid="edition-review"]')).toBeNull();
      expect(component.store.book()?.personalReview).toBe('A sanatorium for the whole of Europe.');
    });
  });

  // ── Dialog copy ─────────────────────────────────────────────────────────────

  describe('editions dialog copy', () => {
    it('says what ungrouping and grouping do to the rating, favourite and review', async () => {
      await setup(twoEditions());

      component.pendingWorkAction.set({ kind: 'unlink', bookId: 'b2', label: 'x', merge: false });
      const unlink = component.workActionDescription();
      expect(unlink).toContain('copy of this book\'s rating, favourite and review');
      expect(unlink).not.toMatch(/keeps? its own rating/i);

      component.pendingWorkAction.set({ kind: 'link', bookId: 'b3', label: 'x', merge: false });
      const link = component.workActionDescription();
      expect(link).toContain('combined');
      expect(link).toContain('a rating or review that differs stays with its edition');
    });
  });
});
