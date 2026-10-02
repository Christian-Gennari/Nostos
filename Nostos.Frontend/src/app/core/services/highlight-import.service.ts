import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

/**
 * How sure the server is that a book on the device is a book in the library.
 * `exact` and `remembered` are ready to import; `suggested` is a resemblance
 * the owner has to confirm; `none` has no plausible library book.
 */
export type HighlightImportMatch = 'exact' | 'remembered' | 'suggested' | 'none';

export interface HighlightImportCandidate {
  bookId: string;
  title: string;
  author: string | null;
  reason: string;
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
}

export interface HighlightImportPreview {
  source: 'kobo' | 'koreader';
  books: HighlightImportPreviewBook[];
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
  source: 'kobo' | 'koreader';
  books: HighlightImportResultBook[];
}

export interface HighlightImportBatch {
  id: string;
  source: 'kobo' | 'koreader';
  fileName: string | null;
  createdAtUtc: string;
  noteCount: number;
  bookCount: number;
}

/**
 * E-reader highlight import (issue #656). Also owns whether the import dialog
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
  commit(file: File, decisions: HighlightImportDecision[]): Observable<HighlightImportResult> {
    const body = this.body(file);
    body.append('decisions', JSON.stringify(decisions));
    return this.http.post<HighlightImportResult>('/api/notes/imports/commit', body);
  }

  recent(): Observable<HighlightImportBatch[]> {
    return this.http.get<HighlightImportBatch[]>('/api/notes/imports/batches');
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
