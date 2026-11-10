import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { of, throwError } from 'rxjs';

import { ReadingHistoryImportModal } from './reading-history-import-modal.component';
import {
  ReadingHistoryImportBatch,
  ReadingHistoryPreviewBook,
  ReadingHistoryImportResultBook,
  ReadingHistoryImportService,
  ReadingHistoryValues,
} from '../../core/services/reading-history-import.service';
import { BooksService } from '../../core/services/books.service';

const values = (overrides: Partial<ReadingHistoryValues> = {}): ReadingHistoryValues => ({
  rating: 0,
  review: null,
  privateNotes: null,
  finishedAt: null,
  toRead: false,
  readStatus: 'read',
  readCount: 1,
  shelves: [],
  ratingNotice: null,
  ...overrides,
});

const previewBook = (overrides: Partial<ReadingHistoryPreviewBook> = {}): ReadingHistoryPreviewBook => ({
  sourceKey: 'goodreads:id:101',
  title: "The Cartographer's Daughter",
  author: 'Ada Vance',
  isbn: '9780000000019',
  match: 'exact',
  bookId: 'b1',
  candidates: [
    { bookId: 'b1', title: "The Cartographer's Daughter", author: 'Ada Vance', reason: 'Same ISBN', format: 'physical' },
  ],
  values: values({
    rating: 5,
    review: 'A map of a town that never was.',
    finishedAt: '2021-03-14T00:00:00Z',
    shelves: ['fantasy', 'favourites'],
  }),
  hasChanges: true,
  existingTitle: "The Cartographer's Daughter",
  notice: null,
  ...overrides,
});

const suggested = previewBook({
  sourceKey: 'goodreads:id:102',
  title: 'A Winter in the Salt Marshes',
  author: 'Nils Berg',
  match: 'suggested',
  bookId: null,
  candidates: [
    { bookId: 'b2', title: 'A Winter in the Salt Marsh', author: 'Nils Berg', reason: 'Similar title and author', format: 'ebook' },
  ],
  values: values({ rating: 4, finishedAt: '2022-11-02T00:00:00Z' }),
});

const missing = previewBook({
  sourceKey: 'goodreads:id:110',
  title: 'Dune, or: "The Spice" Must Flow',
  author: 'Fran Reader',
  match: 'none',
  bookId: null,
  candidates: [],
  values: values({ rating: 4 }),
});

const resultBook = (overrides: Partial<ReadingHistoryImportResultBook> = {}): ReadingHistoryImportResultBook => ({
  sourceKey: 'goodreads:id:101',
  status: 'imported',
  bookId: 'b1',
  bookTitle: "The Cartographer's Daughter",
  sourceTitle: "The Cartographer's Daughter",
  sourceAuthor: 'Ada Vance',
  changeCount: 4,
  created: false,
  message: null,
  ...overrides,
});

describe('ReadingHistoryImportModal', () => {
  let fixture: ComponentFixture<ReadingHistoryImportModal>;
  let component: ReadingHistoryImportModal;
  let service: ReadingHistoryImportService;

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const file = (name = 'goodreads_library_export.csv') => new File(['x'], name);

  const review = async (...books: ReadingHistoryPreviewBook[]) => {
    vi.spyOn(service, 'preview').mockReturnValue(of({ source: 'goodreads', books }));
    service.open();
    await component.readFile(file());
    fixture.detectChanges();
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ReadingHistoryImportModal],
      providers: [
        provideHttpClient(),
        { provide: BooksService, useValue: { list: vi.fn(() => of({ items: [], totalCount: 0 })) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(ReadingHistoryImportModal);
    component = fixture.componentInstance;
    service = TestBed.inject(ReadingHistoryImportService);
    vi.spyOn(service, 'recent').mockReturnValue(of([]));
  });

  it('renders nothing until the service opens it', () => {
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.modal-card')).toBeNull();

    service.open();
    fixture.detectChanges();
    expect(text()).toContain('Import reading history');
    expect(fixture.nativeElement.querySelector('[data-testid="reading-history-import-input"]')).not.toBeNull();
    expect(text()).toContain('My Books → Import and export');
    expect(text()).toContain('Manage Account → Export StoryGraph Library');
  });

  it('previews without writing, groups the rows and reads a certain match as ready', async () => {
    await review(previewBook(), suggested, missing);

    expect(component.stage()).toBe('review');
    expect(text()).toContain('Nothing has been added yet');
    expect(component.ready()).toHaveLength(1);
    expect(component.toCheck()).toHaveLength(1);
    expect(component.missing()).toHaveLength(1);
    // A resemblance is a question, never an assumption.
    expect(component.toCheck()[0].target).toBeNull();
    expect(component.unanswered()).toBe(1);
    expect(text()).toContain('Is this A Winter in the Salt Marsh');
    expect(text()).toContain('5 stars');
    expect(text()).toContain('Read 2021-03-14');
    expect(text()).toContain('3 books · 1 matched · 1 new');
    expect(text()).toContain('1 still to check');
  });

  it('sends only the rows the owner decided on, with shelves only when asked', async () => {
    await review(previewBook(), suggested, missing);
    component.confirm(component.toCheck()[0]);
    component.add(component.missing()[0]);
    fixture.detectChanges();

    const commit = vi
      .spyOn(service, 'commit')
      .mockReturnValue(
        of({
          batchId: 'batch-1',
          source: 'goodreads',
          books: [resultBook({}), resultBook({ sourceKey: 'goodreads:id:110', changeCount: 2, created: true })],
        }),
      );

    await component.importReviewed();
    fixture.detectChanges();
    const decisions = commit.mock.calls[0][1];
    expect(decisions).toEqual([
      { sourceKey: 'goodreads:id:101', bookId: 'b1', mapShelves: false },
      { sourceKey: 'goodreads:id:102', bookId: 'b2', mapShelves: false },
      { sourceKey: 'goodreads:id:110', create: true, mapShelves: false },
    ]);

    expect(component.stage()).toBe('result');
    expect(text()).toContain('2 books brought in');
    expect(component.batchId()).toBe('batch-1');
  });

  it('maps shelves only after the owner ticks the option', async () => {
    await review(previewBook());
    const commit = vi
      .spyOn(service, 'commit')
      .mockReturnValue(of({ batchId: 'batch-1', source: 'goodreads', books: [resultBook({})] }));

    const checkbox = fixture.nativeElement.querySelector(
      '[data-testid="reading-history-map-shelves"] input',
    ) as HTMLInputElement;
    expect(checkbox).not.toBeNull();
    checkbox.checked = true;
    checkbox.dispatchEvent(new Event('change'));

    await component.importReviewed();
    expect(commit.mock.calls[0][1][0].mapShelves).toBe(true);
  });

  it('does not show the shelves option when no row has shelves', async () => {
    await review(previewBook({ values: values({ rating: 5 }) }));
    expect(fixture.nativeElement.querySelector('[data-testid="reading-history-map-shelves"]')).toBeNull();
  });

  it('leaves a book out when the owner says so', async () => {
    await review(previewBook(), suggested);
    component.skip(component.toCheck()[0]);
    expect(component.included().map((row) => row.book.sourceKey)).toEqual(['goodreads:id:101']);
  });

  it('undoes the import from the result and reports what came back', async () => {
    await review(previewBook());
    vi.spyOn(service, 'commit').mockReturnValue(
      of({ batchId: 'batch-1', source: 'goodreads', books: [resultBook({})] }),
    );
    const undo = vi.spyOn(service, 'undo').mockReturnValue(of({ removed: 1 }));

    await component.importReviewed();
    component.askUndoImport();
    await component.confirmUndo();
    fixture.detectChanges();

    expect(undo).toHaveBeenCalledWith('batch-1');
    expect(component.stage()).toBe('result');
    expect(component.undone()).toBe(1);
    expect(text()).toContain('Import undone');
    expect(text()).toContain('1 row taken back out of your library');
  });

  it('recent imports offer the same undo', async () => {
    const batch: ReadingHistoryImportBatch = {
      id: 'old-batch',
      source: 'storygraph',
      fileName: 'storygraph_export.csv',
      createdAtUtc: '2026-11-01T10:00:00Z',
      bookCount: 3,
      changeCount: 8,
    };
    vi.spyOn(service, 'recent').mockReturnValue(of([batch]));
    service.open();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="reading-history-recent-row"]')).not.toBeNull();
    expect(text()).toContain('StoryGraph');

    const undo = vi.spyOn(service, 'undo').mockReturnValue(of({ removed: 3 }));
    component.askUndoRecent(batch);
    await component.confirmUndo();
    expect(undo).toHaveBeenCalledWith('old-batch');
  });

  it('shows why a file could not be read and keeps the picker available', async () => {
    vi.spyOn(service, 'preview').mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { error: 'This file is not an export Nostos can read.' },
          }),
      ),
    );
    service.open();
    fixture.detectChanges();

    await component.readFile(file('notes.csv'));
    fixture.detectChanges();

    expect(component.stage()).toBe('result');
    expect(text()).toContain('This file is not an export Nostos can read.');
    expect(text()).toContain('Import failed');
  });
});
