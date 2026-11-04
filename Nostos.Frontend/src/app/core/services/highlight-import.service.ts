import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import type { BookFormatConcept } from '../book-format';
import type { NoteSearchHit } from '../dtos/note.dtos';

/**
 * How sure the server is that a book on the device is a book in the library.
 * `exact` and `remembered` are ready to import; `suggested` is a resemblance
 * the owner has to confirm; `none` has no plausible library book.
 */
export type HighlightImportMatch = 'exact' | 'remembered' | 'suggested' | 'none';

/** Where a file came from, as the server tells it apart by content. */
export type HighlightImportSource = 'kobo' | 'koreader' | 'kindle' | 'readwise';

export const HIGHLIGHT_IMPORT_SOURCE_LABEL: Record<HighlightImportSource, string> = {
  kobo: 'Kobo',
  koreader: 'KOReader',
  kindle: 'Kindle',
  readwise: 'Readwise',
};

export interface HighlightImportCandidate {
  bookId: string;
  title: string;
  author: string | null;
  reason: string;
  /** The library book's format, for the row's format glyph. */
  format: BookFormatConcept;
}

export interface HighlightImportPreviewBook {
  sourceKey: string;
  title: string | null;
  author: string | null;
  annotationCount: number;
  newCount: number;
  match: HighlightImportMatch;
  bookId: string | null;
  candidates: HighlightImportCandidate[];
  /** Something to know before choosing, such as a Readwise row that looks like a web article. */
  notice?: string | null;
}

/** Entries a file holds that cannot be imported, by book and reason. */
export interface HighlightImportSkipped {
  title: string | null;
  author: string | null;
  count: number;
  reason: string;
}

export interface HighlightImportPreview {
  source: HighlightImportSource;
  books: HighlightImportPreviewBook[];
  skipped?: HighlightImportSkipped[] | null;
}

/** One book's answer: an existing library book, or `create` to add it. */
export interface HighlightImportDecision {
  sourceKey: string;
  bookId?: string;
  create?: boolean;
}

export interface HighlightImportResultBook {
  sourceKey: string;
  status: 'imported' | 'skipped';
  bookId: string | null;
  bookTitle: string | null;
  sourceTitle: string | null;
  sourceAuthor: string | null;
  annotationCount: number;
  importedCount: number;
  duplicateCount: number;
  created: boolean;
  message: string | null;
}

export interface HighlightImportResult {
  batchId: string | null;
  source: HighlightImportSource;
  books: HighlightImportResultBook[];
}

export interface HighlightImportBatch {
  id: string;
  source: HighlightImportSource;
  fileName: string | null;
  createdAtUtc: string;
  noteCount: number;
  bookCount: number;
}

/**
 * Highlight import from an e-reader or an export (issues #656, #1219). Also owns whether the import dialog
 * is open, so Settings and the command palette open the same one.
 */
@Injectable({ providedIn: 'root' })
export class HighlightImportService {
  readonly isOpen = signal(false);

  constructor(private http: HttpClient) {}

  open(): void {
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
  }

  /** Reads a file and proposes a library book per device book. Writes nothing. */
  preview(file: File): Observable<HighlightImportPreview> {
    return this.http.post<HighlightImportPreview>('/api/notes/imports/preview', this.body(file));
  }

  /** Imports the books that have a decision; a book without one is left out. */
  commit(
    file: File,
    decisions: HighlightImportDecision[],
    batchId: string | null = null,
  ): Observable<HighlightImportResult> {
    const body = this.body(file);
    body.append('decisions', JSON.stringify(decisions));
    // Files chosen together are one import, and so one thing to undo.
    if (batchId) body.append('batchId', batchId);
    return this.http.post<HighlightImportResult>('/api/notes/imports/commit', body);
  }

  recent(): Observable<HighlightImportBatch[]> {
    return this.http.get<HighlightImportBatch[]>('/api/notes/imports/batches');
  }

  /**
   * The imported highlights a starter draft quotes (issue #1220), at most
   * three: from these imports, or with none given, the library's latest.
   */
  starterPassages(batchIds: readonly string[] = []): Observable<NoteSearchHit[]> {
    const params: Record<string, string> = batchIds.length ? { batchIds: batchIds.join(',') } : {};
    return this.http.get<NoteSearchHit[]>('/api/notes/imports/starter', { params });
  }

  /** Removes the notes one import created. */
  undo(batchId: string): Observable<{ removed: number }> {
    return this.http.delete<{ removed: number }>(`/api/notes/imports/batches/${batchId}`);
  }

  private body(file: File): FormData {
    const body = new FormData();
    body.append('file', file, file.name);
    return body;
  }
}
