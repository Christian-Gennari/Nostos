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

const previewBook = (overrides: Partial<HighlightImportPreviewBook>): HighlightImportPreviewBook => ({
  sourceKey: 'vol-1',
  title: 'Synthetic Book',
  author: 'Ada Reader',
  annotationCount: 3,
  newCount: 3,
  match: 'exact',
  bookId: 'b1',
  candidates: [{ bookId: 'b1', title: 'Synthetic Book', author: 'Ada Reader', reason: 'Same title and author' }],
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
  candidates: [{ bookId: 'b2', title: 'Devils', author: 'Fyodor Dostoevsky', reason: 'Similar title and author' }],
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
    expect(commit.mock.calls[0][1]).toEqual([
      { sourceKey: 'vol-2', bookId: 'b2' },
      { sourceKey: 'vol-3', create: true },
    ]);
    expect(component.stage()).toBe('result');
    expect(component.headline()).toBe('3 highlights imported');
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

    component.choose(row, { bookId: 'b9', title: 'Demons', author: 'Dostoevsky', reason: '' });

    expect(component.toCheck()[0].target).toEqual({
      kind: 'book',
      bookId: 'b9',
      title: 'Demons',
      author: 'Dostoevsky',
    });
    expect(component.pickingRowId()).toBeNull();
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

    service.commit(file(), [{ sourceKey: 'vol-1', bookId: 'b1' }]).subscribe();
    const request = http.expectOne('/api/notes/imports/commit');
    expect((request.request.body as FormData).get('decisions')).toBe(
      JSON.stringify([{ sourceKey: 'vol-1', bookId: 'b1' }]),
    );
    request.flush({ batchId: null, source: 'kobo', books: [] });

    service.undo('batch-1').subscribe();
    expect(http.expectOne('/api/notes/imports/batches/batch-1').request.method).toBe('DELETE');
    http.verify();
  });
});
