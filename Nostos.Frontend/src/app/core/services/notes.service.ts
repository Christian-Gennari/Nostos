import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  Note, CreateNoteDto, NoteBookFacet, NoteOrder, NoteSearchHit, NoteSearchPage, NoteRawTranscript, NoteSummary,
  Passage, UpdateNoteDto,
} from '../dtos/note.dtos';

/** One place of the Brain: everything, unsorted, a kind, a topic, or what two topics share. */
export interface NoteBrowseFilter {
  query?: string;
  bookId?: string;
  /** Every note of every edition of this book (the work). */
  workId?: string;
  withoutTopics?: boolean;
  /** Only notes of this kind (issue #1014: Brain's Passages filter). */
  kind?: 'note' | 'passage';
  topicId?: string;
  /** With `topicId`: only the notes that also carry this topic. */
  withTopicId?: string;
}

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

  /**
   * Several notes by id, with their topics (issue #1162: the Studio shows the
   * topics of the sources a writing cites). Ids that name no note in this
   * library are simply absent from the answer.
   */
  getMany(ids: readonly string[]): Observable<NoteSearchHit[]> {
    return this.http.get<NoteSearchHit[]>('/api/notes/by-ids', { params: { ids: ids.join(',') } });
  }

  /** A passage in full (issue #1014); the lists carry only its opening. */
  getPassage(id: string): Observable<Passage> {
    return this.http.get<Passage>(`/api/notes/${id}/passage`);
  }

  create(bookId: string, dto: CreateNoteDto): Observable<Note> {
    return this.http.post<Note>(`/api/books/${bookId}/notes`, dto);
  }

  update(id: string, dto: UpdateNoteDto): Observable<Note> {
    return this.http.put<Note>(`/api/notes/${id}`, dto);
  }

  /** Records where the reader found an imported highlight; the first location is kept. */
  setLocation(id: string, cfiRange: string): Observable<Note> {
    return this.http.put<Note>(`/api/notes/${id}/location`, { cfiRange });
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

  browse(options: NoteBrowseFilter & {
    oldestFirst?: boolean;
    order?: NoteOrder;
    limit?: number;
    offset?: number;
  } = {}): Observable<NoteSearchPage> {
    const params = browseParams(options);
    params['limit'] = options.limit ?? 25;
    params['offset'] = options.offset ?? 0;
    if (options.order) params['order'] = options.order;
    else if (options.oldestFirst) params['oldestFirst'] = true;
    return this.http.get<NoteSearchPage>('/api/notes', { params });
  }

  /** The books with notes in one place, and how many each (its book filter is ignored). */
  books(filter: NoteBrowseFilter = {}): Observable<NoteBookFacet[]> {
    const { bookId: _ignored, ...rest } = filter;
    return this.http.get<NoteBookFacet[]>('/api/notes/books', { params: browseParams(rest) });
  }

  /** How many notes, passages and unsorted notes there are. */
  summary(): Observable<NoteSummary> {
    return this.http.get<NoteSummary>('/api/notes/summary');
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

function browseParams(filter: NoteBrowseFilter): Record<string, string | number | boolean> {
  const params: Record<string, string | number | boolean> = {};
  if (filter.query?.trim()) params['query'] = filter.query.trim();
  if (filter.bookId) params['bookId'] = filter.bookId;
  if (filter.workId) params['workId'] = filter.workId;
  if (filter.withoutTopics) params['withoutTopics'] = true;
  if (filter.kind) params['kind'] = filter.kind;
  if (filter.topicId) params['topicId'] = filter.topicId;
  if (filter.withTopicId) params['withTopicId'] = filter.withTopicId;
  return params;
}
