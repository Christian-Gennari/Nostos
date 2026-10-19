import type { HttpRequest } from '@angular/common/http';

import type { NoteSearchHit, NoteSearchPage } from '../core/dtos/note.dtos';

/** The page's read of book b1's notes (the Brain's browse, filtered to the book). */
export const bookNotes = (request: HttpRequest<unknown>): boolean =>
  request.url === '/api/notes' && request.params.get('bookId') === 'b1';

/** What the Brain's state reads on the side: the topic list and the counts. */
export const brainReads = (request: HttpRequest<unknown>): boolean =>
  request.url === '/api/topics' || request.url === '/api/notes/summary';

/** A note as the Brain's card reads it, from the fields a test cares about. */
export function cardNote(note: Partial<NoteSearchHit> & { id: string }): NoteSearchHit {
  return {
    bookId: 'b1',
    bookTitle: null,
    content: '',
    selectedText: null,
    snippet: null,
    topicNames: [],
    createdAt: '2026-08-01T08:00:00Z',
    ...note,
  };
}

/** One page holding every note of the book. */
export function notesPage(notes: Array<Partial<NoteSearchHit> & { id: string }>): NoteSearchPage {
  return { items: notes.map(cardNote), totalCount: notes.length, offset: 0, limit: 200 };
}
