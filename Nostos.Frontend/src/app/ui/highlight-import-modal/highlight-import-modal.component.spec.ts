import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import { HighlightImportModal } from './highlight-import-modal.component';
import {
  HighlightImportPreviewBook,
  HighlightImportResultBook,
  HighlightImportService,
} from '../../core/services/highlight-import.service';
import { BooksService } from '../../core/services/books.service';
import { Book, PaginatedResponse } from '../../core/dtos/book.dtos';

const previewBook = (overrides: Partial<HighlightImportPreviewBook>): HighlightImportPreviewBook => ({
  sourceKey: 'vol-1',
  title: 'Synthetic Book',
  author: 'Ada Reader',
  annotationCount: 3,
  newCount: 3,
  match: 'exact',
  bookId: 'b1',
  candidates: [{ bookId: 'b1', title: 'Synthetic Book', author: 'Ada Reader', reason: 'Same title and author', format: 'ebook' }],
  ...overrides,
});

const resultBook = (overrides: Partial<HighlightImportResultBook>): HighlightImportResultBook => ({
  sourceKey: 'vol-1',
  status: 'imported',
  bookId: 'b1',
  bookTitle: 'Synthetic Book',
  sourceTitle: 'Synthetic Book',
  sourceAuthor: 'Ada Reader',
  annotationCount: 3,
  importedCount: 3,
  duplicateCount: 0,
  created: false,
  message: null,
  ...overrides,
});

const suggested = previewBook({
  sourceKey: 'vol-2',
  title: 'The Possessed; or, The Devils',
  author: 'Fyodor Dostoyevsky',
  match: 'suggested',
  bookId: null,
  candidates: [{ bookId: 'b2', title: 'Devils', author: 'Fyodor Dostoevsky', reason: 'Similar title and author', format: 'audiobook' }],
});

const missing = previewBook({
  sourceKey: 'vol-3',
  title: 'Not In The Library',
  author: 'Nobody',
  match: 'none',
  bookId: null,
  candidates: [],
});

describe('HighlightImportModal', () => {
  let fixture: ComponentFixture<HighlightImportModal>;
  let component: HighlightImportModal;
  let service: HighlightImportService;
  let router: Router;

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const file = (name = 'KoboReader.sqlite') => new File(['x'], name);

  const review = async (...books: HighlightImportPreviewBook[]) => {
    vi.spyOn(service, 'preview').mockReturnValue(of({ source: 'kobo', books }));
    service.open();
    await component.readFiles([file()]);
    fixture.detectChanges();
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HighlightImportModal],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: BooksService, useValue: { list: vi.fn(() => of({ items: [], totalCount: 0 })) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HighlightImportModal);
    component = fixture.componentInstance;
    service = TestBed.inject(HighlightImportService);
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigate').mockResolvedValue(true as never);
    vi.spyOn(service, 'recent').mockReturnValue(of([]));
  });

  it('renders nothing until the service opens it', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.modal-card')).toBeNull();

    service.open();
    fixture.detectChanges();
    expect(text()).toContain('Import highlights');
    expect(fixture.nativeElement.querySelector('[data-testid="highlight-import-input"]')).not.toBeNull();
  });

  it('decides a certain match, asks about a resemblance, and leaves a missing book out', async () => {
    await review(previewBook({}), suggested, missing);

    expect(component.stage()).toBe('review');
    expect(component.ready().map((row) => row.target)).toEqual([
      { kind: 'book', bookId: 'b1', title: 'Synthetic Book', author: 'Ada Reader' },
    ]);
    // A resemblance is a question, never an assumption.
    expect(component.toCheck()[0].target).toBeNull();
    expect(component.toCheck()[0].suggestion?.bookId).toBe('b2');
    expect(component.missing()[0].target).toBeNull();
    expect(component.unanswered()).toBe(1);
    expect(text()).toContain('Is this Devils');
    expect(text()).toContain('1 still to check');
  });

  it('sends only the books the owner decided on', async () => {
    await review(previewBook({}), suggested, missing);
    const commit = vi
      .spyOn(service, 'commit')
      .mockReturnValue(of({ batchId: 'batch-1', source: 'kobo', books: [resultBook({})] }));

    component.confirm(component.toCheck()[0]);
    component.addToLibrary(component.missing()[0]);
    component.skip(component.ready()[0]);
    await component.importReviewed();

    expect(commit).toHaveBeenCalledTimes(1);
    expect(commit.mock.calls[0][2]).toBeNull();
    expect(commit.mock.calls[0][1]).toEqual([
      { sourceKey: 'vol-2', bookId: 'b2' },
      { sourceKey: 'vol-3', create: true },
    ]);
    expect(component.stage()).toBe('result');
    expect(component.headline()).toBe('3 highlights imported');
  });

  it('shares one undoable batch across files and counts library books once', async () => {
    const files = [file('metadata.epub.lua'), file('KoboReader.sqlite')];
    vi.spyOn(service, 'preview').mockReturnValue(of({ source: 'koreader', books: [previewBook({})] }));
    const commit = vi.spyOn(service, 'commit').mockReturnValue(
      of({ batchId: 'batch-1', source: 'koreader', books: [resultBook({})] }),
    );
    const undo = vi.spyOn(service, 'undo').mockReturnValue(of({ removed: 6 }));
    service.open();
    await component.readFiles(files);

    await component.importReviewed();

    expect(commit.mock.calls.map((call) => call[2])).toEqual([null, 'batch-1']);
    expect(component.batchIds()).toEqual(['batch-1']);
    expect(component.summary()).toBe('Into 1 book.');
    await component.undoImport();
    expect(undo).toHaveBeenCalledTimes(1);
    expect(component.summary()).toBe('6 highlights removed from your library.');
  });

  it('imports nothing when nothing is selected', async () => {
    await review(suggested);
    const commit = vi.spyOn(service, 'commit');

    await component.importReviewed();

    expect(commit).not.toHaveBeenCalled();
    expect(component.stage()).toBe('review');
  });

  it('lets the owner pick another library book for a row', async () => {
    await review(suggested);
    const row = component.toCheck()[0];

    component.openPicker(row);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="highlight-picker"]')).not.toBeNull();

    component.choose(row, { bookId: 'b9', title: 'Demons', author: 'Dostoevsky', reason: '', format: 'physical' });

    expect(component.toCheck()[0].target).toEqual({
      kind: 'book',
      bookId: 'b9',
      title: 'Demons',
      author: 'Dostoevsky',
    });
    expect(component.pickingRowId()).toBeNull();
  });

  it('lists the books to pick as choice rows with the format glyph, as the Editions dialog does', async () => {
    await review(
      previewBook({
        ...suggested,
        candidates: [
          { bookId: 'b2', title: 'Devils', author: 'Fyodor Dostoevsky', reason: 'Similar title and author', format: 'audiobook' },
          { bookId: 'b3', title: 'Demons', author: null, reason: '', format: 'pdf' },
        ],
      }),
    );
    component.openPicker(component.toCheck()[0]);
    fixture.detectChanges();

    const rows = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('.highlight-picker-option'),
    );
    expect(rows.map((row) => row.classList.contains('nostos-choice-row'))).toEqual([true, true]);
    expect(rows[0].querySelector('.nostos-choice-row__title')?.textContent).toBe('Devils');
    expect(rows[0].querySelector('.nostos-choice-row__meta')?.textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Audiobook · Similar title and author',
    );
    expect(rows[1].querySelector('.nostos-choice-row__sub')?.textContent).toBe('Unknown author');
    expect(rows[1].querySelector('.nostos-choice-row__meta')?.textContent?.trim()).toBe('PDF');
  });

  it('undoes what it just imported', async () => {
    await review(previewBook({}));
    vi.spyOn(service, 'commit').mockReturnValue(
      of({ batchId: 'batch-1', source: 'kobo', books: [resultBook({})] }),
    );
    const undo = vi.spyOn(service, 'undo').mockReturnValue(of({ removed: 3 }));
    await component.importReviewed();

    await component.undoImport();
    fixture.detectChanges();

    expect(undo).toHaveBeenCalledWith('batch-1');
    expect(component.headline()).toBe('Import undone');
    expect(component.summary()).toBe('3 highlights removed from your library.');
    expect(component.batchIds()).toEqual([]);
  });

  it('keeps Undo available when the request fails and lets the owner retry', async () => {
    await review(previewBook({}));
    vi.spyOn(service, 'commit').mockReturnValue(
      of({ batchId: 'batch-1', source: 'kobo', books: [resultBook({})] }),
    );
    const undo = vi.spyOn(service, 'undo').mockReturnValueOnce(
      throwError(() => new HttpErrorResponse({ status: 0 })),
    ).mockReturnValue(of({ removed: 3 }));
    await component.importReviewed();

    await component.undoImport();
    fixture.detectChanges();

    expect(component.batchIds()).toEqual(['batch-1']);
    expect(component.undone()).toBeNull();
    expect(text()).toContain('The import could not be undone. Try again.');
    await component.undoImport();
    expect(undo).toHaveBeenCalledTimes(2);
    expect(component.summary()).toBe('3 highlights removed from your library.');
    expect(component.undoError()).toBeNull();
  });

  it('can search again after a library request fails', async () => {
    await review(missing);
    const list = vi.mocked(TestBed.inject(BooksService).list);
    list.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 0 })));
    component.openPicker(component.missing()[0]);
    component.onSearch('Devils');
    await new Promise((resolve) => setTimeout(resolve, 300));
    fixture.detectChanges();
    expect(text()).toContain('Search could not finish. Try again.');

    list.mockReturnValueOnce(of({ items: [{ id: 'b9', title: 'Demons', author: 'Dostoevsky' }], totalCount: 1 } as PaginatedResponse<Book>));
    component.onSearch('Demons');
    await new Promise((resolve) => setTimeout(resolve, 300));

    expect(component.searchResults()[0].bookId).toBe('b9');
    expect(component.searchError()).toBe(false);
  });

  it('shows the server reason when a file cannot be read', async () => {
    vi.spyOn(service, 'preview').mockReturnValue(
      throwError(
        () => new HttpErrorResponse({ status: 400, error: { error: 'Not a Kobo database.' } }),
      ),
    );
    service.open();

    await component.readFiles([file('cover.jpg')]);
    fixture.detectChanges();

    expect(component.stage()).toBe('result');
    expect(component.headline()).toBe('Import failed');
    expect(text()).toContain('Not a Kobo database.');
  });

  it('lists what a file holds that is not imported, and flags a row that may not be a book', async () => {
    vi.spyOn(service, 'preview').mockReturnValue(
      of({
        source: 'readwise',
        books: [missing, previewBook({ sourceKey: 'rw-2', title: 'How to Read Slowly', match: 'none', bookId: null, candidates: [], notice: 'Readwise keeps this like a web article.' })],
        skipped: [
          { title: 'Tweets From Ada Reader', author: '@adareader', count: 2, reason: 'A tweet. Only highlights from books are imported.' },
          { title: 'Episode 12', author: null, count: 1, reason: 'A podcast. Only highlights from books are imported.' },
        ],
      }),
    );
    service.open();
    await component.readFiles([file('readwise-export.csv')]);
    fixture.detectChanges();

    expect(component.stage()).toBe('review');
    expect(component.unimportableCount()).toBe(3);
    const list = fixture.nativeElement.querySelector('[data-testid="highlight-unimportable"]') as HTMLDetailsElement;
    expect(list.textContent).toContain('Not imported');
    expect(list.textContent?.replace(/\s+/g, ' ')).toContain('@adareader · 2 entries · A tweet.');
    expect(list.open).toBe(false);
    expect(text()).toContain('Readwise keeps this like a web article.');
    // Flagged, not decided: it waits for the owner like any book not in the library.
    expect(component.included()).toEqual([]);
  });

  it('explains a file that holds nothing to import', async () => {
    vi.spyOn(service, 'preview').mockReturnValue(
      of({
        source: 'kindle',
        books: [],
        skipped: [{ title: 'Walden', author: 'Henry David Thoreau', count: 4, reason: 'Bookmarks mark a place and hold no text.' }],
      }),
    );
    service.open();
    await component.readFiles([file('My Clippings.txt')]);
    fixture.detectChanges();

    expect(component.stage()).toBe('result');
    const list = fixture.nativeElement.querySelector('[data-testid="highlight-unimportable"]') as HTMLDetailsElement;
    expect(list.open).toBe(true);
    expect(list.textContent).toContain('Bookmarks mark a place and hold no text.');
  });

  it('names where each recent import came from', () => {
    vi.spyOn(service, 'recent').mockReturnValue(
      of([{ id: 'b', source: 'kindle', fileName: 'My Clippings.txt', createdAtUtc: '2026-10-24T08:00:00Z', noteCount: 3, bookCount: 1 }]),
    );
    service.open();
    fixture.detectChanges();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="highlight-recent-row"]').textContent).toContain('Kindle ·');
  });

  it('cannot be closed while it is working', () => {
    service.open();
    component.stage.set('importing');

    component.close();

    expect(service.isOpen()).toBe(true);
  });

  it('posts the file to preview, and the file with its decisions to commit', () => {
    const http = TestBed.inject(HttpTestingController);

    service.preview(file()).subscribe();
    http.expectOne('/api/notes/imports/preview').flush({ source: 'kobo', books: [] });

    service.commit(file(), [{ sourceKey: 'vol-1', bookId: 'b1' }], 'batch-1').subscribe();
    const request = http.expectOne('/api/notes/imports/commit');
    expect((request.request.body as FormData).get('decisions')).toBe(
      JSON.stringify([{ sourceKey: 'vol-1', bookId: 'b1' }]),
    );
    expect((request.request.body as FormData).get('batchId')).toBe('batch-1');
    request.flush({ batchId: null, source: 'kobo', books: [] });

    service.undo('batch-1').subscribe();
    expect(http.expectOne('/api/notes/imports/batches/batch-1').request.method).toBe('DELETE');
    http.verify();
  });
});
