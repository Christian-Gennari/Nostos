import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Note, CreateNoteDto, NoteSearchHit, NoteSearchPage, NoteRawTranscript, UpdateNoteDto } from '../dtos/note.dtos';

/**
 * The assistant's capture payload (issue #261 §4). Appended: it extends the
 * canonical `CreateNoteDto` with the provenance/anchor fields the model already
 * carries (issue #260), without changing any existing signature or the shared
 * TypeScript `CreateNoteDto`. A backend that does not know a field ignores it,
 * so a capture still saves against the pre-#260-S2 route.
 */
export interface CaptureNoteDto {
  content: string;
  cfiRange?: string;
  selectedText?: string;
  rawContent?: string;
  captureSource?: string;
  processingMode?: string;
  sourceAnchorKind?: string;
  sourceAnchorValue?: string | null;
  anchorVerified?: boolean;
}

@Injectable({ providedIn: 'root' })
export class NotesService {
  constructor(private http: HttpClient) {}

  list(bookId: string): Observable<Note[]> {
    return this.http.get<Note[]>(`/api/books/${bookId}/notes`);
  }

  get(id: string): Observable<NoteSearchHit> {
    return this.http.get<NoteSearchHit>(`/api/notes/${id}`);
  }

  create(bookId: string, dto: CreateNoteDto): Observable<Note> {
    return this.http.post<Note>(`/api/books/${bookId}/notes`, dto);
  }

  update(id: string, dto: UpdateNoteDto): Observable<Note> {
    return this.http.put<Note>(`/api/notes/${id}`, dto);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`/api/notes/${id}`);
  }

  getOriginal(id: string): Observable<NoteRawTranscript> {
    return this.http.get<NoteRawTranscript>(`/api/notes/${id}/raw`);
  }

  restoreOriginal(id: string): Observable<Note> {
    return this.http.post<Note>(`/api/notes/${id}/raw/restore`, {});
  }

  /**
   * Notes whose text, quoted passage or book title match (issue #158). This is what
   * makes a word living only inside a note reachable at all.
   */
  search(query: string, limit = 50): Observable<NoteSearchHit[]> {
    return this.http.get<NoteSearchHit[]>('/api/notes/search', {
      params: { query, limit },
    });
  }

  browse(options: {
    query?: string;
    bookId?: string;
    withoutConcepts?: boolean;
    oldestFirst?: boolean;
    limit?: number;
    offset?: number;
  } = {}): Observable<NoteSearchPage> {
    const params: Record<string, string | number | boolean> = {
      limit: options.limit ?? 25,
      offset: options.offset ?? 0,
    };
    if (options.query?.trim()) params['query'] = options.query.trim();
    if (options.bookId) params['bookId'] = options.bookId;
    if (options.withoutConcepts) params['withoutConcepts'] = true;
    if (options.oldestFirst) params['oldestFirst'] = true;
    return this.http.get<NoteSearchPage>('/api/notes', { params });
  }

  /**
   * Notes linked to no concept — an exception queue, not a second content type.
   *
   * Since issue #256 this is paged rather than a single capped list. The page
   * carries the total, because review mode traverses the whole set and must never
   * present the first 50 rows as "the unlinked notes". The caller passes the
   * offset of the first row it does not already hold, so a queue that shrinks as
   * the user resolves notes still asks for the right next row.
   */
  unlinkedPage(limit = 25, offset = 0): Observable<NoteSearchPage> {
    return this.http.get<NoteSearchPage>('/api/notes/unlinked', { params: { limit, offset } });
  }

  /**
   * Assistant capture (issue #261 §4): the single call site that persists a
   * typed capture with its source anchor. It is deliberately separate from
   * `create` so 261-S2 can swap the implementation for the canonical
   * `CaptureAsync` route without touching every caller.
   */
  capture(bookId: string, dto: CaptureNoteDto): Observable<Note> {
    return this.http.post<Note>(`/api/books/${bookId}/notes`, dto);
  }
}
