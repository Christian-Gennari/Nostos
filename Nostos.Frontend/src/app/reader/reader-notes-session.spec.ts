import { TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { ReaderNotesSession } from './reader-notes-session';
import { NotesService } from '../core/services/notes.service';
import type { Book } from '../core/dtos/book.dtos';
import type { Note } from '../core/dtos/note.dtos';

function book(overrides: Partial<Book> = {}): Book {
  return {
    id: 'book-1',
    title: 'The Iliad',
    type: 'ebook',
    fileName: 'iliad.epub',
    workId: 'work-1',
    otherEditions: [],
    ...overrides,
  } as Book;
}

function note(overrides: Partial<Note> = {}): Note {
  return {
    id: 'n-1',
    bookId: 'book-1',
    content: 'About [[wrath]]',
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  } as Note;
}

describe('ReaderNotesSession (#1607)', () => {
  const list = vi.fn();
  const browse = vi.fn();
  const get = vi.fn();
  let session: ReaderNotesSession;

  beforeEach(() => {
    list.mockReset().mockReturnValue(of([note()]));
    browse.mockReset().mockReturnValue(of({ items: [], totalCount: 0 }));
    get.mockReset().mockReturnValue(of({ id: 'n-1', topicNames: [] }));
    TestBed.configureTestingModule({
      providers: [
        ReaderNotesSession,
        { provide: NotesService, useValue: { list, browse, get } },
      ],
    });
    session = TestBed.inject(ReaderNotesSession);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('reads the book’s notes and their topics into the rows the panel shows', () => {
    browse.mockReturnValue(of({ items: [{ ...note(), topicNames: ['wrath'] }], totalCount: 1 }));

    session.load(book());

    expect(list).toHaveBeenCalledWith('book-1');
    expect(session.loaded()).toBe(true);
    expect(session.notes().map((n) => n.id)).toEqual(['n-1']);
    expect(session.rows()[0].item.topicNames).toEqual(['wrath']);
    expect(session.readerNotes()?.map((n) => n.id)).toEqual(['n-1']);
  });

  it('keeps the notes of the book’s other editions apart, with the group label', () => {
    const book2 = book({
      workId: 'work-1',
      otherEditions: [{ id: 'book-2', hasFile: true, type: 'audiobook' } as never],
    });
    browse.mockReturnValue(
      of({
        items: [
          { ...note({ id: 'n-1', bookId: 'book-1' }), topicNames: [] },
          { id: 'n-2', bookId: 'book-2', bookFormat: 'Audiobook', topicNames: [] },
        ],
        totalCount: 2,
      }),
    );

    session.load(book2);

    expect(browse).toHaveBeenCalledWith(expect.objectContaining({ workId: 'work-1', limit: 200 }));
    expect(session.topicNames().has('n-1')).toBe(true);
    expect(session.otherEditionNotes().map((hit) => hit.id)).toEqual(['n-2']);
    expect(session.otherNotesLabel()).toBe('1 from the audiobook');
  });

  it('does not keep the last book’s notes when another one opens', () => {
    session.load(book());
    expect(session.loaded()).toBe(true);

    session.reset();

    expect(session.notes()).toEqual([]);
    expect(session.loaded()).toBe(false);
    expect(session.readerNotes()).toBeNull();
    expect(session.topicNames().size).toBe(0);
  });

  it('adds a saved note once, and only to the book it belongs to', () => {
    session.load(book());
    list.mockClear();

    session.addCreated('book-1', note({ id: 'fresh', content: '' }));
    session.addCreated('book-1', note({ id: 'fresh', content: '' }));
    session.addCreated('another-book', note({ id: 'other' }));

    expect(session.notes().map((n) => n.id)).toEqual(['fresh', 'n-1']);
    expect(list).not.toHaveBeenCalled();
  });

  it('re-reads a note’s topics after a [[link]] is saved, and removes a deleted note', () => {
    session.load(book());
    get.mockReturnValue(of({ id: 'n-1', topicNames: ['rage'] }));

    session.replace(note({ id: 'n-1', content: 'About [[rage]]' }));

    expect(session.notes()[0].content).toBe('About [[rage]]');
    expect(session.topicNames().get('n-1')).toEqual(['rage']);

    const removed = session.remove('n-1');
    expect(removed?.id).toBe('n-1');
    expect(session.notes()).toEqual([]);
  });

  it('drops a late answer once the reader is gone', () => {
    const late = new Subject<Note[]>();
    list.mockReturnValue(late);

    session.load(book());
    expect(session.loaded()).toBe(false);

    TestBed.resetTestingModule();
    late.next([note({ id: 'late' })]);
    late.complete();

    expect(session.notes()).toEqual([]);
    expect(session.loaded()).toBe(false);
  });
});
