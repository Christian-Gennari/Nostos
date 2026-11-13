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
import { ReadwiseService } from '../../core/services/readwise.service';
import { DeploymentCapabilitiesService } from '../../core/services/deployment-capabilities.service';
import { createDeploymentCapabilitiesServiceMock } from '../../core/services/deployment-capabilities.testing';
import { Book, PaginatedResponse } from '../../core/dtos/book.dtos';
import { StarterDraftService } from '../../writing-studio/starter-draft/starter-draft.service';
import type { NoteSearchHit } from '../../core/dtos/note.dtos';

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
        // Self-hosted: the beta feedback link goes to GitHub, and no request is left open.
        {
          provide: DeploymentCapabilitiesService,
          useValue: createDeploymentCapabilitiesServiceMock(
            { deploymentMode: 'SelfHosted' } as unknown as import('../../core/dtos/deployment-capabilities.dtos').DeploymentCapabilities,
          ),
        },
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
    expect(fixture.nativeElement.querySelector('[data-testid="import-drop-zone-input"]')).not.toBeNull();
    expect(text()).toContain('Where to find your file');
  });

  it('imports a Readwise export API page without a file and commits to its endpoint', async () => {
    const readwise = TestBed.inject(ReadwiseService);
    vi.spyOn(readwise, 'preview').mockReturnValue(
      of({
        source: 'readwise',
        books: [previewBook({ sourceKey: 'readwise:meta:A|B', match: 'none', bookId: null, candidates: [] })],
        skipped: [{ title: 'An article', author: 'A writer', count: 2, reason: 'A web article.' }],
      }),
    );
    const commit = vi
      .spyOn(readwise, 'commit')
      .mockReturnValue(of({ batchId: 'rw-batch', source: 'readwise', books: [resultBook({ sourceKey: 'readwise:meta:A|B' })] }));

    service.open('readwise');
    await vi.waitFor(() => expect(component.stage()).toBe('review'));
    fixture.detectChanges();

    expect(component.rows()).toHaveLength(1);
    expect(component.unimportable()).toEqual([
      expect.objectContaining({ title: 'An article', count: 2, fileName: 'Readwise' }),
    ]);
    component.addToLibrary(component.rows()[0]);
    fixture.detectChanges();

    await component.importReviewed();

    expect(commit).toHaveBeenCalledWith(
      [expect.objectContaining({ sourceKey: 'readwise:meta:A|B', create: true })],
      null,
    );
    expect(component.batchIds()).toEqual(['rw-batch']);
    expect(component.stage()).toBe('result');
  });

  it('sends Apple Books files chosen together as one upload, and commits the same set', async () => {
    const annotations = file('AEAnnotation_v10312011_1727_local.sqlite');
    const wal = file('AEAnnotation_v10312011_1727_local.sqlite-wal');
    const library = file('BKLibrary-1-091020131601.sqlite');
    const kindle = file('My Clippings.txt');
    const preview = vi.spyOn(service, 'preview').mockImplementation((main) =>
      of({ source: main === kindle ? 'kindle' : 'applebooks', books: [previewBook({ sourceKey: main.name })] }),
    );
    const commit = vi.spyOn(service, 'commit').mockReturnValue(of({ batchId: 'b', source: 'applebooks', books: [resultBook({})] }));
    service.open();

    await component.readFiles([library, kindle, wal, annotations]);

    expect(preview.mock.calls.map(([main, companions]) => [main.name, (companions ?? []).map((f) => f.name)])).toEqual([
      ['AEAnnotation_v10312011_1727_local.sqlite', ['BKLibrary-1-091020131601.sqlite', 'AEAnnotation_v10312011_1727_local.sqlite-wal']],
      ['My Clippings.txt', []],
    ]);
    expect(component.fileCount()).toBe(2);

    await component.importReviewed();
    const appleCommit = commit.mock.calls.find(([main]) => main === annotations)!;
    expect(appleCommit[3]).toEqual([library, wal]);
  });

  it('opens on Apple Books steps, copies the folder location and sends only Books files from the folder', async () => {
    const writeText = vi.fn(() => Promise.resolve());
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const preview = vi.spyOn(service, 'preview').mockReturnValue(of({ source: 'applebooks', books: [previewBook({})] }));

    service.open('applebooks');
    fixture.detectChanges();
    expect(component.stage()).toBe('applebooks');
    expect(text()).toContain('Import from Apple Books');
    expect(text()).not.toContain('sqlite');
    expect(fixture.nativeElement.querySelector('[data-testid="highlight-applebooks-beta"]')).not.toBeNull();
    expect(
      (fixture.nativeElement.querySelector('[data-testid="highlight-applebooks-feedback"] a') as HTMLAnchorElement).href,
    ).toContain('github.com/Christian-Gennari/Nostos/issues/1357');

    (fixture.nativeElement.querySelector('[data-testid="highlight-applebooks-copy"]') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(writeText).toHaveBeenCalledWith('~/Library/Containers/com.apple.iBooksX/Data/Documents');
    expect(text()).toContain('Copied');

    const folder = (names: string[]) => ({ target: { files: names.map((name) => file(name)), value: 'x' } }) as unknown as Event;
    component.onAppleBooksFolderSelected(folder(['BCCloudAsset-1.sqlite', '.DS_Store']));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="highlight-applebooks-error"]')).not.toBeNull();
    expect(preview).not.toHaveBeenCalled();

    component.onAppleBooksFolderSelected(
      folder(['.DS_Store', 'BKLibrary-1-091020131601.sqlite', 'BCCloudAsset-1.sqlite', 'AEAnnotation_v10312011_1727_local.sqlite']),
    );
    await vi.waitFor(() => expect(component.stage()).toBe('review'));
    expect(preview.mock.calls[0][0].name).toBe('AEAnnotation_v10312011_1727_local.sqlite');
    expect(preview.mock.calls[0][1]!.map((f) => f.name)).toEqual(['BKLibrary-1-091020131601.sqlite']);

    // Back from the review returns to the Apple Books steps.
    component.backToPick();
    expect(component.stage()).toBe('applebooks');
  });

  it('offers no "Add to library" for a book whose title the file does not give', async () => {
    await review(previewBook({ sourceKey: 'applebooks:A', title: null, author: null, match: 'none', bookId: null, candidates: [] }));
    expect(fixture.nativeElement.querySelector('[data-testid="highlight-add"]')).toBeNull();
    expect(text()).toContain('Choose a book');
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

  const importResults = async (books: HighlightImportResultBook[]) => {
    await review(previewBook({}));
    vi.spyOn(service, 'commit').mockReturnValue(of({ batchId: 'batch-1', source: 'kindle', books }));
    await component.importReviewed();
    fixture.detectChanges();
  };

  it('says one more was already in Nostos when exactly one was', async () => {
    await importResults([
      resultBook({ sourceKey: 'vol-1', bookId: 'b1', importedCount: 3, duplicateCount: 1 }),
      resultBook({ sourceKey: 'vol-2', bookId: 'b2', bookTitle: 'Second Book', importedCount: 2 }),
      resultBook({ sourceKey: 'vol-3', bookId: 'b3', bookTitle: 'Third Book', importedCount: 1 }),
    ]);

    expect(component.summary()).toBe('Into 3 books. 1 more was already in Nostos.');
    expect(text()).toContain('Into 3 books. 1 more was already in Nostos.');
  });

  it('keeps the plural when two or more were already in Nostos', async () => {
    await importResults([resultBook({ importedCount: 3, duplicateCount: 2 })]);

    expect(component.summary()).toBe('Into 1 book. 2 more were already in Nostos.');
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

  describe('a starter draft from the import (issue #1220)', () => {
    const hit = { id: 'n1', bookId: 'b1', bookTitle: 'Synthetic Book', content: '', selectedText: 'A passage.' } as NoteSearchHit;
    const imported = async (batchId: string | null = 'batch-1', books = [resultBook({})]) => {
      await review(previewBook({}));
      vi.spyOn(service, 'commit').mockReturnValue(of({ batchId, source: 'kobo', books }));
      await component.importReviewed();
      fixture.detectChanges();
    };
    const action = () => fixture.nativeElement.querySelector('[data-testid="highlight-start-draft"]') as HTMLButtonElement | null;

    beforeEach(() => localStorage.removeItem('nostos.studio.starterDraftOffer'));

    it('offers one quiet action once highlights are in, and opens the Studio on the draft it makes', async () => {
      const starter = TestBed.inject(StarterDraftService);
      const passages = vi.spyOn(starter, 'passages').mockResolvedValue([hit]);
      const create = vi.spyOn(starter, 'create').mockResolvedValue('w-new');
      await imported();

      expect(action()?.textContent).toContain('Start a draft from these');
      action()!.click();
      await vi.waitFor(() => expect(router.navigate).toHaveBeenCalled());

      // The passages come from this import, not the whole library.
      expect(passages).toHaveBeenCalledWith(['batch-1']);
      expect(create).toHaveBeenCalledWith([hit]);
      expect(router.navigate).toHaveBeenCalledWith(['/studio'], { queryParams: { writingId: 'w-new' } });
      expect(service.isOpen()).toBe(false);
      // The empty Studio does not offer it a second time.
      expect(localStorage.getItem('nostos.studio.starterDraftOffer')).toBe('taken');
    });

    it('is not offered when nothing new came in, or once the import is undone', async () => {
      await imported(null, [resultBook({ importedCount: 0, duplicateCount: 3 })]);
      expect(action()).toBeNull();

      await imported();
      expect(action()).not.toBeNull();
      vi.spyOn(service, 'undo').mockReturnValue(of({ removed: 3 }));
      await component.undoImport();
      fixture.detectChanges();
      expect(action()).toBeNull();
    });

    it('says so and stays open when the draft cannot be made', async () => {
      const starter = TestBed.inject(StarterDraftService);
      vi.spyOn(starter, 'passages').mockRejectedValue(new Error('offline'));
      await imported();

      await component.startDraft();
      fixture.detectChanges();

      expect(text()).toContain('The draft could not be started. Try again.');
      expect(router.navigate).not.toHaveBeenCalled();
      expect(service.isOpen()).toBe(true);
    });
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
      of([{ id: 'b', source: 'kindle', fileName: 'My Clippings.txt', createdAtUtc: '2026-10-24T08:00:00Z', noteCount: 3, bookCount: 1, report: null }]),
    );
    service.open();
    fixture.detectChanges();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="highlight-recent-row"]').textContent).toContain('Kindle ·');
  });

  it('shows what a recent import found and where every entry went', () => {
    vi.spyOn(service, 'recent').mockReturnValue(
      of([{
        id: 'b',
        source: 'kindle',
        fileName: 'My Clippings.txt',
        createdAtUtc: '2026-10-24T08:00:00Z',
        noteCount: 380,
        bookCount: 12,
        report: {
          found: 412,
          imported: 380,
          duplicates: 20,
          skipped: 12,
          leftOut: 0,
          skippedReasons: [],
          booksMatched: 10,
          booksCreated: 2,
          booksLeftOut: 3,
        },
      }]),
    );
    service.open();
    fixture.detectChanges();
    fixture.detectChanges();

    const row = fixture.nativeElement.querySelector('[data-testid="highlight-recent-row"]') as HTMLElement;
    expect(row.textContent).toContain('412 found · 380 imported · 20 already here · 12 skipped');
    expect(row.textContent).toContain('10 books matched · 2 added · 3 left out');
  });

  it('expands a recent import into the reasons its entries were skipped', () => {
    vi.spyOn(service, 'recent').mockReturnValue(
      of([{
        id: 'b',
        source: 'kindle',
        fileName: 'My Clippings.txt',
        createdAtUtc: '2026-10-24T08:00:00Z',
        noteCount: 4,
        bookCount: 1,
        report: {
          found: 6,
          imported: 4,
          duplicates: 0,
          skipped: 2,
          leftOut: 0,
          skippedReasons: [
            { title: 'Pride and Prejudice', author: 'Jane Austen', count: 1, reason: 'Bookmarks mark a place and hold no text.' },
            { title: 'Walden', author: null, count: 1, reason: 'The entry has no text.' },
          ],
          booksMatched: 1,
          booksCreated: 0,
          booksLeftOut: 0,
        },
      }]),
    );
    service.open();
    fixture.detectChanges();
    fixture.detectChanges();

    const details = fixture.nativeElement.querySelector('[data-testid="highlight-recent-reasons"]') as HTMLDetailsElement;
    expect(details.open).toBe(false);
    expect(details.querySelector('summary')!.textContent).toContain('6 found · 4 imported · 2 skipped');
    expect(details.textContent).toContain('Pride and Prejudice');
    expect(details.textContent).toContain('Bookmarks mark a place and hold no text.');
    expect(details.textContent).toContain('The entry has no text.');
  });

  it('shows a batch from before reports existed without invented numbers', () => {
    vi.spyOn(service, 'recent').mockReturnValue(
      of([{ id: 'old', source: 'kobo', fileName: null, createdAtUtc: '2026-10-24T08:00:00Z', noteCount: 3, bookCount: 1, report: null }]),
    );
    service.open();
    fixture.detectChanges();
    fixture.detectChanges();

    const row = fixture.nativeElement.querySelector('[data-testid="highlight-recent-row"]') as HTMLElement;
    expect(row.textContent).toContain('3 highlights into 1 book');
    expect(row.textContent).not.toContain('found');
    expect(row.querySelector('[data-testid="highlight-recent-reasons"]')).toBeNull();
  });

  it('cannot be closed while it is working', () => {
    service.open();
    component.stage.set('importing');

    component.close();

    expect(service.isOpen()).toBe(true);
  });

  describe('highlights in the review', () => {
    const withHighlights = (overrides: Partial<HighlightImportPreviewBook> = {}) =>
      previewBook({
        annotationCount: 3,
        newCount: 2,
        highlights: [
          { index: 0, text: 'First passage', note: 'my note', isNew: true },
          { index: 1, text: 'Second passage', note: null, isNew: true },
          { index: 2, text: 'Third passage', note: null, isNew: false },
        ],
        ...overrides,
      });

    it('lists the highlights of a book on demand, with a checkbox each', async () => {
      await review(withHighlights());
      expect(fixture.nativeElement.querySelector('[data-testid="highlight-quotes"]')).toBeNull();

      (fixture.nativeElement.querySelector('[data-testid="highlight-review-toggle"]') as HTMLElement).click();
      fixture.detectChanges();

      const checks = fixture.nativeElement.querySelectorAll('[data-testid="highlight-quote-check"]');
      expect(checks).toHaveLength(3);
      expect(text()).toContain('First passage');
      expect(text()).toContain('my note');
      expect(text()).toContain('Already in Nostos');
    });

    it('counts and sends only the highlights left ticked', async () => {
      await review(withHighlights());
      const commit = vi
        .spyOn(service, 'commit')
        .mockReturnValue(of({ batchId: 'batch-1', source: 'kobo', books: [resultBook({})] }));
      const row = component.ready()[0];
      expect(component.rowCount(row)).toBe(2);

      component.setHighlight(row, 1, false);
      fixture.detectChanges();
      expect(component.rowCount(component.ready()[0])).toBe(1);
      expect(text()).toContain('2 of 3 highlights');

      await component.importReviewed();
      expect(commit.mock.calls[0][1]).toEqual([{ sourceKey: 'vol-1', bookId: 'b1', excludedHighlights: [1] }]);
    });

    it('treats a book with every highlight unticked as left out', async () => {
      await review(withHighlights());
      const commit = vi.spyOn(service, 'commit');
      const row = component.ready()[0];
      for (const index of [0, 1, 2]) component.setHighlight(row, index, false);
      fixture.detectChanges();

      expect(component.included()).toEqual([]);
      expect(text()).toContain('Nothing selected');
      await component.importReviewed();
      expect(commit).not.toHaveBeenCalled();
    });
  });

  describe('undo confirmation', () => {
    it('asks first and removes nothing until confirmed', async () => {
      await review(previewBook({}));
      vi.spyOn(service, 'commit').mockReturnValue(
        of({ batchId: 'batch-1', source: 'kobo', books: [resultBook({})] }),
      );
      const undo = vi.spyOn(service, 'undo').mockReturnValue(of({ removed: 3 }));
      await component.importReviewed();
      fixture.detectChanges();

      (fixture.nativeElement.querySelector('[data-testid="highlight-import-undo"]') as HTMLElement).click();
      fixture.detectChanges();
      expect(undo).not.toHaveBeenCalled();
      expect(document.body.textContent).toContain('Undo this import?');
      expect(document.body.textContent).toContain('A book this import added to your library is removed too');

      component.cancelUndo();
      expect(undo).not.toHaveBeenCalled();

      component.askUndoImport();
      await component.confirmUndo();
      expect(undo).toHaveBeenCalledWith('batch-1');
      expect(component.pendingUndo()).toBeNull();
    });

    it('asks before undoing a recent import too', async () => {
      const batch = { id: 'old', source: 'kobo' as const, fileName: null, createdAtUtc: '2026-11-01T00:00:00Z', noteCount: 4, bookCount: 1, report: null };
      const undo = vi.spyOn(service, 'undo').mockReturnValue(of({ removed: 4 }));
      component.askUndoRecent(batch);
      expect(undo).not.toHaveBeenCalled();
      expect(component.undoDescription()).toContain('4 highlights will be removed');
      await component.confirmUndo();
      expect(undo).toHaveBeenCalledWith('old');
    });
  });

  it('says that books left out are not kept and can be imported later', async () => {
    await review(previewBook({}));
    vi.spyOn(service, 'commit').mockReturnValue(
      of({
        batchId: 'batch-1',
        source: 'kobo',
        books: [resultBook({}), resultBook({ sourceKey: 'vol-3', status: 'skipped', bookId: null, importedCount: 0 })],
      }),
    );
    await component.importReviewed();
    fixture.detectChanges();

    const note = fixture.nativeElement.querySelector('[data-testid="highlight-left-out-note"]') as HTMLElement;
    expect(note.textContent).toContain('does not keep these');
    expect(note.textContent).toContain('Import the same file again');
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
