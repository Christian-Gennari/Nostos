import { createEnvironmentInjector, EnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { BookDetailStore } from './book-detail.store';
import type { Book, LinkableBookDto } from '../core/dtos/book.dtos';
import type { NoteSearchHit, NoteSearchPage } from '../core/dtos/note.dtos';
import type { BookUploadStatusDto } from '../core/dtos/book-upload.dtos';
import { BookUploadService } from '../core/services/book-upload.service';
import { ImportService } from '../core/services/import.service';
import { ToastService } from '../core/services/toast.service';
import { UploadFailure } from '../core/uploads/upload-error';
import { cardNote, notesPage } from './book-detail.testing';

const BOOK: Book = {
  id: 'b1',
  title: 'After Virtue',
  subtitle: null,
  author: 'Alasdair MacIntyre',
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
  createdAt: '2026-08-01T08:00:00Z',
  hasFile: true,
  fileName: 'after-virtue.epub',
  coverUrl: null,
  collectionIds: [],
  lastLocation: null,
  progressPercent: 0,
  lastReadAt: null,
  rating: 0,
  isFavorite: false,
  personalReview: null,
  finishedAt: null,
};

const PAPER_BOOK: Book = {
  ...BOOK,
  type: 'physical',
  hasFile: false,
  fileName: null,
  pageCount: 880,
};

const EDITION = {
  id: 'e2',
  type: 'audiobook',
  progressPercent: 0,
  hasFile: true,
  fileName: 'after-virtue.m4b',
  personalReview: 'The narration is remarkable.',
};

/** A book with other editions, so its notes are read by its work. */
const MULTI_EDITION_BOOK: Book = {
  ...BOOK,
  workId: 'w1',
  editionCount: 2,
  otherEditions: [EDITION],
};

const BOOK_UPLOAD_STATUS: BookUploadStatusDto = {
  sessionId: 's1',
  bookId: 'b1',
  state: 'Complete',
  totalBytes: 8,
  chunkSize: 4,
  totalChunks: 2,
  fileName: 'after-virtue.epub',
  fileSha256: 'a'.repeat(64),
  receivedParts: [0, 1],
  receivedBytes: 8,
  createdAtUtc: '2026-08-01T08:00:00Z',
  expiresAtUtc: '2026-08-02T08:00:00Z',
};

function note(id: string, bookId = 'b1'): NoteSearchHit {
  return cardNote({ id, bookId });
}

function page(notes: NoteSearchHit[], totalCount = notes.length, offset = 0): NoteSearchPage {
  return { items: notes, totalCount, offset, limit: 200 };
}

describe('BookDetailStore', () => {
  let httpMock: HttpTestingController;
  let root: EnvironmentInjector;
  let env: EnvironmentInjector;
  let store: BookDetailStore;
  let toast: ToastService;
  let imports: ImportService;
  let bookUploads: BookUploadService;
  let destroyed: boolean;

  const notesRequest = (params: Record<string, string> = {}) =>
    httpMock.expectOne(
      (request) =>
        request.url === '/api/notes' &&
        Object.entries(params).every(([key, value]) => request.params.get(key) === value),
    );

  /** Reads the book, its notes and the collections the way the page does. */
  function open(book: Book, notes: NoteSearchHit[] = []): void {
    store.loadAllData(book.id);
    const prefetched = notesRequest({ bookId: book.id });
    httpMock.expectOne(`/api/books/${book.id}`).flush(book);

    if (book.workId && (book.otherEditions?.length ?? 0) > 0) {
      // The book's own id was a guess; its notes are the work's.
      prefetched.flush(page([]));
      notesRequest({ workId: book.workId }).flush(notesPage(notes));
    } else {
      prefetched.flush(notesPage(notes));
    }

    httpMock.expectOne('/api/collections').flush([]);
  }

  function destroyStore(): void {
    if (destroyed) return;
    destroyed = true;
    env.destroy();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    toast = TestBed.inject(ToastService);
    imports = TestBed.inject(ImportService);
    bookUploads = TestBed.inject(BookUploadService);
    root = TestBed.inject(EnvironmentInjector);
    env = createEnvironmentInjector([BookDetailStore], root);
    store = env.get(BookDetailStore);
    destroyed = false;
  });

  afterEach(() => {
    destroyStore();
    vi.useRealTimers();
    vi.restoreAllMocks();
    httpMock.verify();
  });

  describe('loading', () => {
    it('shows the book and the notes prefetched beside it, then stops loading', () => {
      store.loadAllData('b1');

      expect(store.loading()).toBe(true);
      expect(store.book()).toBeNull();

      const notes = notesRequest({ bookId: 'b1', order: 'oldest', limit: '200', offset: '0' });
      expect(notes.request.method).toBe('GET');
      notes.flush(page([note('n1')]));
      // The book has not arrived yet, so its notes stay off screen.
      expect(store.notes()).toEqual([]);

      httpMock.expectOne('/api/books/b1').flush(BOOK);
      httpMock.expectOne('/api/collections').flush([]);

      expect(store.loading()).toBe(false);
      expect(store.book()?.id).toBe('b1');
      expect(store.notes().map((n) => n.id)).toEqual(['n1']);
    });

    it('reads every page of the notes, oldest first', () => {
      store.loadAllData('b1');
      notesRequest({ bookId: 'b1', offset: '0' }).flush(page([note('n1')], 2, 0));
      httpMock.expectOne('/api/books/b1').flush(BOOK);
      httpMock.expectOne('/api/collections').flush([]);

      const second = notesRequest({ bookId: 'b1', offset: '1' });
      second.flush(page([note('n2')], 2, 1));

      expect(store.notes().map((n) => n.id)).toEqual(['n1', 'n2']);
    });

    it('drops notes prefetched by book id when the book turns out to have editions', () => {
      store.loadAllData('b1');
      const prefetched = notesRequest({ bookId: 'b1' });

      httpMock.expectOne('/api/books/b1').flush(MULTI_EDITION_BOOK);
      httpMock.expectOne('/api/collections').flush([]);

      // The book says its notes are the work's, so the book-scoped read is
      // reissued by work id and its answer is never shown.
      const byWork = notesRequest({ workId: 'w1' });
      prefetched.flush(page([note('stale')]));
      expect(store.notes()).toEqual([]);

      byWork.flush(page([note('fresh')]));
      expect(store.notes().map((n) => n.id)).toEqual(['fresh']);
    });

    it('reports notes that cannot be read rather than showing an empty list', () => {
      store.loadAllData('b1');
      notesRequest({ bookId: 'b1' }).flush(
        { message: 'nope' },
        { status: 500, statusText: 'Server Error' },
      );
      httpMock.expectOne('/api/books/b1').flush(BOOK);
      httpMock.expectOne('/api/collections').flush([]);

      expect(store.notesFailed()).toBe(true);
      expect(store.notes()).toEqual([]);
    });

    it('reports a book that cannot be read, and keeps its notes unseen', () => {
      store.loadAllData('b1');
      const notes = notesRequest({ bookId: 'b1' });

      httpMock.expectOne('/api/books/b1').flush(
        { error: 'not_found' },
        { status: 404, statusText: 'Not Found' },
      );
      httpMock.expectOne('/api/collections').flush([]);

      expect(store.loading()).toBe(false);
      expect(store.book()).toBeNull();
      expect(store.error()).toBe('Book not found');
      expect(toast.toasts().map((t) => t.message)).toContain('Book not found');

      // The notes were asked for beside a book that does not exist: dropped.
      notes.flush(page([note('n1')]));
      expect(store.notes()).toEqual([]);
    });

    it('retries a failed read and asks for the notes again', () => {
      store.loadAllData('b1');
      notesRequest({ bookId: 'b1' }).flush(page([]));
      httpMock.expectOne('/api/books/b1').flush({}, { status: 404, statusText: 'Not Found' });
      httpMock.expectOne('/api/collections').flush([]);
      expect(store.error()).toBe('Book not found');

      store.retry('b1');
      expect(store.error()).toBeNull();
      httpMock.expectOne('/api/books/b1').flush(BOOK);

      const notes = notesRequest({ bookId: 'b1' });
      notes.flush(page([note('n1')]));

      expect(store.book()?.id).toBe('b1');
      expect(store.notes().map((n) => n.id)).toEqual(['n1']);
    });

    it('keeps the notes when another edition of the same work is opened', () => {
      open(MULTI_EDITION_BOOK, [note('n1')]);
      expect(store.notes().map((n) => n.id)).toEqual(['n1']);

      store.loadAllData('e2');
      expect(store.book()).toBeNull();
      expect(store.loading()).toBe(true);
      expect(store.notes().map((n) => n.id)).toEqual(['n1']);

      const edition: Book = {
        ...BOOK,
        id: 'e2',
        workId: 'w1',
        otherEditions: [{ ...EDITION, id: 'b1' }],
      };
      httpMock.expectOne('/api/books/e2').flush(edition);
      httpMock.expectOne('/api/collections').flush([]);

      // Same work, so the feed is already the right one.
      expect(store.notes().map((n) => n.id)).toEqual(['n1']);
      httpMock.expectNone((request) => request.url === '/api/notes');
    });

    it('refreshes quietly in the background and keeps the page on a failed read', () => {
      open(BOOK, [note('n1')]);

      store.refreshInBackground('b1');
      expect(store.loading()).toBe(false);

      httpMock.expectOne('/api/books/b1').flush({}, { status: 500, statusText: 'Server Error' });
      httpMock.expectOne('/api/collections').flush([]);
      notesRequest({ bookId: 'b1' }).flush(page([note('n2')]));

      expect(store.book()?.id).toBe('b1');
      expect(store.error()).toBeNull();
      expect(store.notes().map((n) => n.id)).toEqual(['n2']);
    });

    it('ignores a background read that arrives after the page moved on', () => {
      open(BOOK);
      store.refreshInBackground('b1');

      // Another book opened before the answer landed.
      store.loadAllData('b2');
      httpMock.expectOne('/api/books/b1').flush({ ...BOOK, title: 'Stale' });
      httpMock.expectOne('/api/books/b2').flush({ ...BOOK, id: 'b2', title: 'The New One' });
      httpMock.match('/api/collections').forEach((request) => request.flush([]));
      httpMock
        .match((request) => request.url === '/api/notes')
        .forEach((request) => request.flush(page([])));

      expect(store.book()?.id).toBe('b2');
      expect(store.book()?.title).toBe('The New One');
    });
  });

  describe('optimistic values', () => {
    beforeEach(() => open(BOOK));

    it('toggles the favourite at once, then sends it', () => {
      store.toggleFavorite();

      expect(store.book()?.isFavorite).toBe(true);
      const write = httpMock.expectOne('/api/books/b1');
      expect(write.request.method).toBe('PUT');
      expect(write.request.body).toEqual({ isFavorite: true });
      write.flush({ ...BOOK, isFavorite: true });

      expect(store.book()?.isFavorite).toBe(true);
    });

    it('puts the favourite back and says so when the write fails', () => {
      store.toggleFavorite();
      httpMock.expectOne('/api/books/b1').flush(
        { error: 'boom' },
        { status: 500, statusText: 'Server Error' },
      );

      expect(store.book()?.isFavorite).toBe(false);
      expect(toast.toasts().map((t) => t.message)).toContain('Could not update the favourite');
    });

    it('marks the book finished at 100% and takes the server’s answer', () => {
      store.setFinished(true);

      expect(store.book()?.finishedAt).toBeTruthy();
      expect(store.book()?.progressPercent).toBe(100);

      const write = httpMock.expectOne('/api/books/b1');
      expect(write.request.method).toBe('PUT');
      expect(write.request.body).toEqual({ isFinished: true });

      const finished: Book = {
        ...BOOK,
        finishedAt: '2026-10-01T10:00:00Z',
        progressPercent: 100,
      };
      write.flush(finished);

      expect(store.book()?.finishedAt).toBe('2026-10-01T10:00:00Z');
      expect(toast.toasts().map((t) => t.message)).toContain('Marked as finished');
    });

    it('starts a finished book over when marked as reading', () => {
      store.book.set({ ...BOOK, finishedAt: '2026-10-01T10:00:00Z', progressPercent: 100 });
      store.setFinished(false);

      expect(store.book()?.progressPercent).toBe(0);
      expect(store.book()?.finishedAt).toBeNull();
      expect(httpMock.expectOne('/api/books/b1').request.body).toEqual({ isFinished: false });
    });

    it('re-reads the book and says so when the status write fails', () => {
      store.setFinished(true);
      httpMock.expectOne('/api/books/b1').flush(
        { error: 'boom' },
        { status: 500, statusText: 'Server Error' },
      );

      // Background re-read restores what the server really holds.
      httpMock.expectOne('/api/books/b1').flush(BOOK);
      expect(store.book()?.finishedAt).toBeNull();
      expect(store.loading()).toBe(false);
      expect(toast.toasts().map((t) => t.message)).toContain('Could not update the status');
    });

    it('saves the day a book was finished, at midday local time', () => {
      store.book.set({ ...BOOK, finishedAt: new Date(2026, 9, 1, 12).toISOString() });

      // A malformed day, and the day it already has, are both no-ops.
      store.setFinishedDate('2026-10-1');
      store.setFinishedDate('2026-10-01');
      httpMock.expectNone('/api/books/b1');

      store.setFinishedDate('2026-10-05');
      const expected = new Date(2026, 9, 5, 12).toISOString();
      expect(store.book()?.finishedAt).toBe(expected);
      const write = httpMock.expectOne('/api/books/b1');
      expect(write.request.method).toBe('PUT');
      expect(write.request.body).toEqual({ finishedAt: expected });
      write.flush({ ...BOOK, finishedAt: expected });
      expect(toast.toasts().map((t) => t.message)).toContain('Finished date saved');
    });

    it('ignores a finished date for a book that is not finished', () => {
      store.setFinishedDate('2026-10-05');

      httpMock.expectNone('/api/books/b1');
      expect(store.book()?.finishedAt).toBeNull();
    });

    it('rates the book and keeps the tap on a failed write, saying it failed', () => {
      store.rate(4);

      expect(store.book()?.rating).toBe(4);
      const write = httpMock.expectOne('/api/books/b1');
      expect(write.request.method).toBe('PUT');
      expect(write.request.body).toEqual({ rating: 4 });
      write.flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });

      expect(toast.toasts().map((t) => t.message)).toContain('Could not update the rating');
    });

    it('rates an edition and puts its previous rating back on failure', () => {
      store.book.set({ ...BOOK, editionRating: 2 });
      store.rateEdition(5);

      expect(store.book()?.editionRating).toBe(5);
      const write = httpMock.expectOne('/api/books/b1');
      expect(write.request.body).toEqual({ editionRating: 5 });
      write.flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });

      expect(store.book()?.editionRating).toBe(2);
    });

    it('removes this edition’s own review, and restores it on failure', () => {
      store.book.set({ ...BOOK, editionReview: 'Narration.' });
      store.removeEditionReview('b1');

      expect(store.book()?.editionReview).toBeNull();
      const write = httpMock.expectOne('/api/books/b1');
      expect(write.request.method).toBe('PUT');
      expect(write.request.body).toEqual({ editionReview: '' });
      write.flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });

      expect(store.book()?.editionReview).toBe('Narration.');
    });

    it('removes a sibling edition’s review from the editions list', () => {
      store.book.set(MULTI_EDITION_BOOK);
      store.removeEditionReview('e2');

      expect(store.book()?.otherEditions?.[0].personalReview).toBeNull();
      const write = httpMock.expectOne('/api/books/e2');
      expect(write.request.body).toEqual({ editionReview: '' });
      write.flush({});
    });
  });

  describe('paper page', () => {
    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      open(PAPER_BOOK);
    });

    const progressWrites = () =>
      httpMock.match((request) => request.method === 'PUT' && request.url === '/api/books/b1/progress');

    it('shows a page at once and saves it after the pause', () => {
      store.setPaperPage(143);

      expect(store.book()?.lastLocation).toBe('{"page":143}');
      expect(store.book()?.progressPercent).toBe(16);
      expect(progressWrites()).toHaveLength(0);

      vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
      const [write] = progressWrites();
      expect(write.request.body).toEqual({ location: '{"page":143}', percentage: 16 });
      write.flush({});
    });

    it('sends one write for a burst of pages, with the last one', () => {
      store.setPaperPage(10);
      vi.advanceTimersByTime(100);
      store.setPaperPage(14);
      vi.advanceTimersByTime(100);
      store.setPaperPage(20);
      expect(progressWrites()).toHaveLength(0);

      vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
      const writes = progressWrites();
      expect(writes).toHaveLength(1);
      expect(writes[0].request.body).toEqual({ location: '{"page":20}', percentage: 2 });
      writes[0].flush({});
    });

    it('clamps to the book and finishes it at the last page', () => {
      store.setPaperPage(5000);
      expect(store.book()?.lastLocation).toBe('{"page":880}');
      expect(store.book()?.progressPercent).toBe(100);
      expect(store.book()?.finishedAt).toBeTruthy();

      store.setPaperPage(879);
      expect(store.book()?.progressPercent).toBe(99);
      expect(store.book()?.finishedAt).toBeNull();

      store.flushPaperPage();
      progressWrites()[0].flush({});
    });

    it('ignores a book that is not paper, and a page that is not finite', () => {
      store.book.set(BOOK);
      store.setPaperPage(10);
      store.book.set(PAPER_BOOK);
      store.setPaperPage(Number.NaN);
      vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);

      expect(store.book()?.lastLocation).toBeNull();
      expect(progressWrites()).toHaveLength(0);
    });

    it('sends the pending page when flushed by hand, once', () => {
      store.setPaperPage(200);
      store.flushPaperPage();
      store.flushPaperPage();

      const writes = progressWrites();
      expect(writes).toHaveLength(1);
      expect(writes[0].request.body).toEqual({ location: '{"page":200}', percentage: 22 });
      writes[0].flush({});
    });

    it('re-reads the book and says so when the page cannot be saved', () => {
      store.setPaperPage(200);
      vi.advanceTimersByTime(BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS);
      progressWrites()[0].flush({}, { status: 500, statusText: 'Server Error' });

      httpMock.expectOne('/api/books/b1').flush(PAPER_BOOK);
      expect(store.loading()).toBe(false);
      expect(toast.toasts().map((t) => t.message)).toContain('Could not save the page');
    });

    it('saves a page set just before the page is left', () => {
      store.setPaperPage(250);
      destroyStore();

      const [write] = progressWrites();
      expect(write.request.body).toEqual({ location: '{"page":250}', percentage: 28 });
      write.flush({});
    });
  });

  describe('reset progress', () => {
    beforeEach(() => open(BOOK));

    it('resets every edition and refreshes the book afterwards', () => {
      store.book.set(MULTI_EDITION_BOOK);
      const done = vi.fn();

      store.resetProgress(done);
      expect(store.resettingProgress()).toBe(true);

      const write = httpMock.expectOne('/api/books/b1/progress/reset?allEditions=true');
      expect(write.request.method).toBe('POST');

      // A second click while it is in flight asks for nothing.
      store.resetProgress(done);
      httpMock.expectNone('/api/books/b1/progress/reset?allEditions=true');
      expect(write.request.params.get('allEditions')).toBe('true');

      write.flush({});
      httpMock.expectOne('/api/books/b1').flush(MULTI_EDITION_BOOK);
      // The relinked book reads its notes by its work now.
      notesRequest({ workId: 'w1' }).flush(page([]));

      expect(store.resettingProgress()).toBe(false);
      expect(done).toHaveBeenCalledTimes(1);
      expect(toast.toasts().map((t) => t.message)).toContain('Reading progress reset');
    });

    it('resets one book without asking for every edition', () => {
      store.resetProgress();
      const write = httpMock.expectOne('/api/books/b1/progress/reset');
      expect(write.request.params.has('allEditions')).toBe(false);
      write.flush({});
      httpMock.expectOne('/api/books/b1').flush(BOOK);
    });

    it('says so and frees the guard when the reset fails', () => {
      store.resetProgress();
      httpMock.expectOne('/api/books/b1/progress/reset').flush(
        { error: 'boom' },
        { status: 500, statusText: 'Server Error' },
      );

      expect(store.resettingProgress()).toBe(false);
      expect(toast.toasts().map((t) => t.message)).toContain('Could not reset progress');
    });
  });

  describe('work membership', () => {
    beforeEach(() => open(BOOK));

    it('links the book and re-reads it, so the editions are the server’s', () => {
      const done = vi.fn();
      store.linkToWork('b2', done);

      expect(store.linkingWork()).toBe(true);
      const write = httpMock.expectOne('/api/books/b1/work/link');
      expect(write.request.method).toBe('POST');
      expect(write.request.body).toEqual({ targetBookId: 'b2' });

      // The second click is a no-op while the first is in flight.
      store.linkToWork('b2', done);
      httpMock.expectNone('/api/books/b1/work/link');

      write.flush({ bookId: 'b1', workId: 'w1', editionCount: 2 });
      httpMock.expectOne('/api/books/b1').flush(MULTI_EDITION_BOOK);
      notesRequest({ workId: 'w1' }).flush(page([]));

      expect(store.linkingWork()).toBe(false);
      expect(done).toHaveBeenCalledTimes(1);
      expect(toast.toasts().map((t) => t.message)).toContain('Linked as editions');
    });

    it('says so when linking fails', () => {
      store.linkToWork('b2');
      httpMock.expectOne('/api/books/b1/work/link').flush(
        { error: 'boom' },
        { status: 500, statusText: 'Server Error' },
      );

      expect(store.linkingWork()).toBe(false);
      expect(toast.toasts().map((t) => t.message)).toContain('Could not link these books');
    });

    it('unlinks the open book when no id is given', () => {
      const done = vi.fn();
      store.unlinkFromWork(undefined, done);

      const write = httpMock.expectOne('/api/books/b1/work/unlink');
      expect(write.request.method).toBe('POST');
      expect(write.request.body).toBeNull();

      write.flush({ bookId: 'b1', workId: 'w2', editionCount: 1 });
      httpMock.expectOne('/api/books/b1').flush(BOOK);

      expect(done).toHaveBeenCalledTimes(1);
      expect(toast.toasts().map((t) => t.message)).toContain('Unlinked into its own edition set');
    });

    it('offers only the first suggestion, and only for the book on screen', () => {
      const offered: LinkableBookDto[] = [];
      store.loadWorkSuggestion('b1', (candidate) => offered.push(candidate));

      const read = httpMock.expectOne('/api/books/b1/work/suggestions');
      expect(read.request.method).toBe('GET');
      read.flush([
        { id: 'b2', title: 'Whose Justice?', author: 'MacIntyre' },
        { id: 'b3', title: 'Three Rival Versions', author: 'MacIntyre' },
      ]);

      expect(offered.map((c) => c.id)).toEqual(['b2']);
    });

    it('offers nothing once the page has moved to another book', () => {
      const offered: LinkableBookDto[] = [];
      store.loadWorkSuggestion('b1', (candidate) => offered.push(candidate));
      store.book.set({ ...BOOK, id: 'b9' });

      httpMock.expectOne('/api/books/b1/work/suggestions').flush([
        { id: 'b2', title: 'Whose Justice?', author: 'MacIntyre' },
      ]);

      expect(offered).toEqual([]);
    });

    it('deduplicates link candidates by work and leaves out this book and its work', () => {
      store.book.set({ ...BOOK, workId: 'w1', otherEditions: [EDITION] });
      store.loadLinkCandidates('justice');

      expect(store.candidatesLoading()).toBe(true);
      const read = httpMock.expectOne((request) => request.url === '/api/books');
      expect(read.request.method).toBe('GET');
      expect(read.request.params.get('search')).toBe('justice');
      expect(read.request.params.get('pageSize')).toBe('20');
      expect(read.request.params.get('groupByWork')).toBe('false');

      read.flush({
        items: [
          { ...BOOK, id: 'b1' }, // this book
          { ...BOOK, id: 'b3', workId: 'w1' }, // already in this work
          { ...BOOK, id: 'b4', title: 'Whose Justice?', workId: 'w2', editionCount: 2 },
          { ...BOOK, id: 'b5', title: 'Whose Justice?', workId: 'w2' }, // same work
          { ...BOOK, id: 'b6', title: 'Three Rival Versions', workId: null },
          { ...BOOK, id: 'b7', title: 'Dependent Rational Animals', workId: null },
        ],
        totalCount: 6,
        page: 1,
        pageSize: 20,
      });

      expect(store.candidatesLoading()).toBe(false);
      expect(store.linkCandidates().map((c) => c.id)).toEqual(['b4', 'b6', 'b7']);
      expect(store.linkCandidates()[0].editionCount).toBe(2);
    });

    it('clears the candidates and says so when the library cannot be read', () => {
      store.loadLinkCandidates('');
      httpMock.expectOne((request) => request.url === '/api/books').flush(
        { error: 'boom' },
        { status: 500, statusText: 'Server Error' },
      );

      expect(store.linkCandidates()).toEqual([]);
      expect(store.candidatesLoading()).toBe(false);
      expect(toast.toasts().map((t) => t.message)).toContain('Could not load the library’s books');
    });
  });

  describe('notes', () => {
    beforeEach(() => open(BOOK));

    it('saves a note with its topics and the page typed by hand', () => {
      const saved = vi.fn();
      store.addNote('Virtue is a practice.', saved, ['virtue'], 42);

      expect(store.noteSaving()).toBe(true);
      const write = httpMock.expectOne('/api/books/b1/notes');
      expect(write.request.method).toBe('POST');
      expect(write.request.body).toEqual({
        content: 'Virtue is a practice.',
        topics: ['virtue'],
        sourceAnchorKind: 'physical_page',
        sourceAnchorValue: '42',
        anchorVerified: false,
      });

      write.flush({ id: 'n9' });
      notesRequest({ bookId: 'b1' }).flush(page([note('n9')]));

      expect(store.noteSaving()).toBe(false);
      expect(saved).toHaveBeenCalledTimes(1);
      expect(store.notes().map((n) => n.id)).toEqual(['n9']);
    });

    it('sends no anchor when no page was given', () => {
      store.addNote('A note');

      const write = httpMock.expectOne('/api/books/b1/notes');
      expect(write.request.body).toEqual({ content: 'A note' });
      write.flush({ id: 'n9' });
      notesRequest({ bookId: 'b1' }).flush(page([]));
    });

    it('refuses a note with no words, unless it carries topics', () => {
      store.addNote('   ');
      httpMock.expectNone('/api/books/b1/notes');

      store.addNote('', undefined, ['virtue']);
      const write = httpMock.expectOne('/api/books/b1/notes');
      expect(write.request.body).toEqual({ content: '', topics: ['virtue'] });
      write.flush({ id: 'n9' });
      notesRequest({ bookId: 'b1' }).flush(page([]));
    });

    it('does not save the same note twice', () => {
      store.addNote('One');
      store.addNote('Two');

      const writes = httpMock.match('/api/books/b1/notes');
      expect(writes).toHaveLength(1);
      writes[0].flush({ id: 'n9' });
      notesRequest({ bookId: 'b1' }).flush(page([]));
    });

    it('keeps the words where they are and says so when saving fails', () => {
      const saved = vi.fn();
      store.addNote('One', saved);
      httpMock.expectOne('/api/books/b1/notes').flush(
        { error: 'boom' },
        { status: 500, statusText: 'Server Error' },
      );

      expect(store.noteSaving()).toBe(false);
      expect(saved).not.toHaveBeenCalled();
      expect(toast.toasts().map((t) => t.message)).toContain('Could not save the note. Try again.');
    });

    it('saves a photo quote as a quotation with the comment as the note', () => {
      const saved = vi.fn();
      store.addPhotoQuote(
        { text: '  Virtue is a practice.  ', comment: ' On page 42. ', page: 42, topics: ['virtue'] },
        saved,
      );

      const write = httpMock.expectOne('/api/books/b1/notes');
      expect(write.request.body).toEqual({
        content: 'On page 42.',
        topics: ['virtue'],
        selectedText: 'Virtue is a practice.',
        captureSource: 'photo',
        sourceAnchorKind: 'physical_page',
        sourceAnchorValue: '42',
        anchorVerified: false,
      });

      write.flush({ id: 'n9' });
      notesRequest({ bookId: 'b1' }).flush(page([note('n9')]));

      expect(saved).toHaveBeenCalledTimes(1);
      expect(toast.toasts().map((t) => t.message)).toContain('Quote saved.');
    });

    it('refuses a photo quote with no recognised text', () => {
      const saved = vi.fn();
      store.addPhotoQuote({ text: '   ', comment: 'see above', page: 4 }, saved);

      httpMock.expectNone('/api/books/b1/notes');
      expect(saved).not.toHaveBeenCalled();
    });

    it('replaces and removes a note in the feed', () => {
      store.notes.set([note('n1'), note('n2')]);

      store.replaceNote(cardNote({ id: 'n1', content: 'Edited' }));
      expect(store.notes()[0].content).toBe('Edited');

      store.removeNote('n1');
      expect(store.notes().map((n) => n.id)).toEqual(['n2']);
    });
  });

  describe('covers', () => {
    beforeEach(() => open(BOOK));

    it('uploads a cover and re-reads the book for its new version', () => {
      store.uploadCover(new File(['cover'], 'cover.jpg'));

      const write = httpMock.expectOne('/api/books/b1/cover');
      expect(write.request.method).toBe('POST');
      const body = write.request.body as FormData;
      expect((body.get('file') as File).name).toBe('cover.jpg');
      expect(write.request.reportProgress).toBe(true);

      write.flush({});
      httpMock.expectOne('/api/books/b1').flush({ ...BOOK, coverVersion: '2' });

      expect(store.book()?.coverVersion).toBe('2');
    });

    it('says so when a cover cannot be uploaded', () => {
      store.uploadCover(new File(['cover'], 'cover.jpg'));
      httpMock.expectOne('/api/books/b1/cover').flush(
        { error: 'boom' },
        { status: 500, statusText: 'Server Error' },
      );

      expect(toast.toasts().map((t) => t.message)).toContain('Could not upload the cover');
    });

    it('removes a cover, re-reads the book and frees the confirm guard', () => {
      const done = vi.fn();
      store.deleteCover(done);

      expect(store.removingCover()).toBe(true);
      const write = httpMock.expectOne('/api/books/b1/cover');
      expect(write.request.method).toBe('DELETE');

      store.deleteCover(done);
      httpMock.expectNone('/api/books/b1/cover');

      write.flush(null);
      httpMock.expectOne('/api/books/b1').flush(BOOK);

      expect(store.removingCover()).toBe(false);
      expect(done).toHaveBeenCalledTimes(1);
      expect(toast.toasts().map((t) => t.message)).toContain('Cover removed');
    });

    it('says so when a cover cannot be removed', () => {
      store.deleteCover();
      httpMock.expectOne('/api/books/b1/cover').flush(
        { error: 'boom' },
        { status: 500, statusText: 'Server Error' },
      );

      expect(store.removingCover()).toBe(false);
      expect(toast.toasts().map((t) => t.message)).toContain('Could not remove the cover');
    });
  });

  describe('book file', () => {
    it('uploads through the legacy whole request when the host cannot resume', () => {
      open(BOOK);
      vi.spyOn(bookUploads, 'supportsResumable').mockReturnValue(false);

      store.uploadFile(new File(['book'], 'book.epub'));
      expect(store.uploadingFile()).toBe(true);

      const write = httpMock.expectOne('/api/books/b1/file?replace=true');
      expect(write.request.method).toBe('POST');
      expect(write.request.body instanceof FormData).toBe(true);

      write.flush({});
      httpMock.expectOne('/api/books/b1').flush(BOOK);

      expect(store.uploadingFile()).toBe(false);
    });

    it('explains a refused legacy upload and frees the guard', () => {
      open(BOOK);
      vi.spyOn(bookUploads, 'supportsResumable').mockReturnValue(false);

      store.uploadFile(new File(['book'], 'book.epub'));
      httpMock.expectOne('/api/books/b1/file?replace=true').flush(
        { error: 'book_upload_unsupported_type', message: 'No.' },
        { status: 415, statusText: 'Unsupported Media Type' },
      );

      expect(store.uploadingFile()).toBe(false);
      expect(toast.toasts().map((t) => t.message)).toContain(
        'This file type cannot be uploaded as a book.',
      );
    });

    it('hands the file to the resumable service and re-reads the book when it lands', async () => {
      open(BOOK);
      vi.spyOn(bookUploads, 'supportsResumable').mockReturnValue(true);
      const upload = vi.spyOn(bookUploads, 'upload').mockResolvedValue(BOOK_UPLOAD_STATUS);

      store.uploadFile(new File(['book'], 'book.epub'));
      expect(store.uploadingFile()).toBe(true);
      expect(upload).toHaveBeenCalledWith('b1', expect.any(File), { replace: true });

      await vi.waitFor(() => expect(store.uploadingFile()).toBe(false));
      httpMock.expectOne('/api/books/b1').flush(BOOK);
      httpMock.expectNone('/api/books/b1/file?replace=true');
    });

    it('explains a refused resumable upload and frees the guard', async () => {
      open(BOOK);
      vi.spyOn(bookUploads, 'supportsResumable').mockReturnValue(true);
      vi.spyOn(bookUploads, 'upload').mockRejectedValue(
        new UploadFailure('book_upload_expired', 410, 'Expired.'),
      );

      store.uploadFile(new File(['book'], 'book.epub'));
      await vi.waitFor(() => expect(store.uploadingFile()).toBe(false));

      expect(toast.toasts().map((t) => t.message)).toContain(
        'This upload expired. Choose the same file to resume.',
      );
      httpMock.expectNone('/api/books/b1/file?replace=true');
    });

    it('does not start a file upload while the book is still importing', () => {
      open({ ...BOOK, status: 1, id: 'b1' });
      httpMock.expectOne('/api/imports/active').flush([]);
      vi.spyOn(bookUploads, 'supportsResumable').mockReturnValue(false);

      store.uploadFile(new File(['book'], 'book.epub'));

      httpMock.expectNone((request) => request.url.startsWith('/api/books/b1/file'));
      expect(store.uploadingFile()).toBe(false);
    });
  });

  describe('import feed', () => {
    it('reads the import stage from the book’s status', () => {
      open({ ...BOOK, status: 1 });
      httpMock.expectOne('/api/imports/active').flush([]);

      expect(store.isImporting()).toBe(true);
      expect(store.importStage()).toBe('Downloading');
      expect(store.importPercent()).toBeNull();
    });

    it('says Transcoding while the server transcodes the file', () => {
      open({ ...BOOK, status: 2 });
      httpMock.expectOne('/api/imports/active').flush([]);

      expect(store.isImporting()).toBe(true);
      expect(store.importStage()).toBe('Transcoding');
    });

    it('is not importing a book that is ready', () => {
      open({ ...BOOK, status: 0 });

      expect(store.isImporting()).toBe(false);
    });
  });

  describe('teardown', () => {
    it('patches the open book from the import feed, and stops when destroyed', () => {
      open(BOOK);

      imports.bookPatched.next({ ...BOOK, title: 'Patched' });
      expect(store.book()?.title).toBe('Patched');

      destroyStore();
      imports.bookPatched.next({ ...BOOK, title: 'After teardown' });
      expect(store.book()?.title).toBe('Patched');
    });

    it('ignores a patch for another book', () => {
      open(BOOK);

      imports.bookPatched.next({ ...BOOK, id: 'b2', title: 'Other' });

      expect(store.book()?.title).toBe('After Virtue');
    });
  });
});
