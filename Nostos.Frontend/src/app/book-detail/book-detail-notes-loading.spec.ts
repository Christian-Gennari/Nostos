/**
 * The notes of a book are asked for beside the book, not after it (issue #1315),
 * and the wrong notes are never shown while the book says which are its own.
 */
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { BookDetailStore } from './book-detail.store';
import type { Book } from '../core/dtos/book.dtos';
import { notesPage } from './book-detail.testing';

const single = { id: 'b1', title: 'Candide', type: 'ebook', workId: 'w1', otherEditions: [] } as unknown as Book;
const withEditions = {
  ...single,
  otherEditions: [{ id: 'b2', type: 'audiobook' }],
} as unknown as Book;

describe('BookDetailStore notes loading', () => {
  let store: BookDetailStore;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [BookDetailStore, provideHttpClient(), provideHttpClientTesting()],
    });
    store = TestBed.inject(BookDetailStore);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.match('/api/collections');
    http.verify();
  });

  const notesBy = (key: 'bookId' | 'workId') =>
    http.match((r) => r.url === '/api/notes' && r.params.has(key));

  it('asks for the book and its notes together', () => {
    store.loadAllData('b1');

    expect(http.match('/api/books/b1')).toHaveLength(1);
    const reads = notesBy('bookId');
    expect(reads).toHaveLength(1);
    expect(reads[0].request.params.get('bookId')).toBe('b1');
    http.match('/api/collections').forEach((r) => r.flush([]));
  });

  it('shows a single edition\'s notes whichever answers first, with no second read', () => {
    for (const notesFirst of [true, false]) {
      store.loadAllData('b1');
      const book = http.expectOne('/api/books/b1');
      const [notes] = notesBy('bookId');
      if (notesFirst) {
        notes.flush(notesPage([{ id: 'n1' }]));
        expect(store.notes()).toEqual([]);
        book.flush(single);
      } else {
        book.flush(single);
        expect(store.notes()).toEqual([]);
        notes.flush(notesPage([{ id: 'n1' }]));
      }
      expect(store.notes().map((n) => n.id)).toEqual(['n1']);
      expect(store.notesFailed()).toBe(false);
      expect(notesBy('bookId')).toHaveLength(0);
      expect(notesBy('workId')).toHaveLength(0);
      http.match('/api/collections').forEach((r) => r.flush([]));

      // Back to a clean store for the other order.
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [BookDetailStore, provideHttpClient(), provideHttpClientTesting()],
      });
      store = TestBed.inject(BookDetailStore);
      http = TestBed.inject(HttpTestingController);
    }
  });

  it('never shows the by-book notes of a book with editions, whichever answers first', () => {
    for (const notesFirst of [true, false]) {
      store.loadAllData('b1');
      const book = http.expectOne('/api/books/b1');
      const [byBook] = notesBy('bookId');
      const feed: string[][] = [];
      const sample = () => feed.push(store.notes().map((n) => n.id));

      if (notesFirst) {
        byBook.flush(notesPage([{ id: 'only-this-edition' }]));
        sample();
        book.flush(withEditions);
      } else {
        book.flush(withEditions);
        sample();
        byBook.flush(notesPage([{ id: 'only-this-edition' }]));
      }
      sample();

      // The book has editions: its notes are asked for by the work.
      const [byWork] = notesBy('workId');
      expect(byWork.request.params.get('workId')).toBe('w1');
      byWork.flush(notesPage([{ id: 'n1' }, { id: 'n2' }]));
      sample();

      // Nothing is on screen until the work's own notes land, and the by-book
      // notes are never on screen at all.
      expect(feed.slice(0, -1).every((seen) => seen.length === 0)).toBe(true);
      expect(feed.at(-1)).toEqual(['n1', 'n2']);
      http.match('/api/collections').forEach((r) => r.flush([]));

      TestBed.resetTestingModule();
      TestBed.configureTestingModule({
        providers: [BookDetailStore, provideHttpClient(), provideHttpClientTesting()],
      });
      store = TestBed.inject(BookDetailStore);
      http = TestBed.inject(HttpTestingController);
    }
  });

  it('pages a long feed 200 at a time after the first page', () => {
    store.loadAllData('b1');
    http.expectOne('/api/books/b1').flush(single);
    const [first] = notesBy('bookId');
    first.flush({ ...notesPage([{ id: 'n1' }]), totalCount: 2 });
    expect(store.notes()).toEqual([]);

    const [second] = notesBy('bookId');
    expect(second.request.params.get('offset')).toBe('1');
    second.flush({ ...notesPage([{ id: 'n2' }]), totalCount: 2 });
    expect(store.notes().map((n) => n.id)).toEqual(['n1', 'n2']);
    http.match('/api/collections').forEach((r) => r.flush([]));
  });

  it('says the notes could not be read when the by-book read fails on a single edition', () => {
    store.loadAllData('b1');
    http.expectOne('/api/books/b1').flush(single);
    notesBy('bookId')[0].flush('boom', { status: 500, statusText: 'Server Error' });

    expect(store.notesFailed()).toBe(true);
    expect(store.notes()).toEqual([]);
    http.match('/api/collections').forEach((r) => r.flush([]));
  });

  it('does not show notes beside a book that was not found', () => {
    store.loadAllData('b1');
    http.expectOne('/api/books/b1').flush('missing', { status: 404, statusText: 'Not Found' });
    notesBy('bookId')[0].flush(notesPage([{ id: 'n1' }]));

    expect(store.error()).toBe('Book not found');
    expect(store.notes()).toEqual([]);
    http.match('/api/collections').forEach((r) => r.flush([]));
  });
});
