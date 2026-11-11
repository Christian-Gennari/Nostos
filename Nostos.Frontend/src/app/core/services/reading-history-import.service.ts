import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import type { HighlightImportCandidate } from './highlight-import.service';

/** The services Nostos reads a reading history from, told apart by the header row. */
export type ReadingHistoryImportSource = 'goodreads' | 'storygraph';

export type ReadingHistoryMatch = 'exact' | 'suggested' | 'none';

export const READING_HISTORY_SOURCE_LABEL: Record<ReadingHistoryImportSource, string> = {
  goodreads: 'Goodreads',
  storygraph: 'StoryGraph',
};

/** Where each service keeps its export, for the Settings card and the dialog. */
export const READING_HISTORY_SOURCE_EXPORT_HINT: Record<ReadingHistoryImportSource, string> = {
  goodreads: 'My Books → Import and export',
  storygraph: 'Manage Account → Export StoryGraph Library',
};

/** One row's values, as the server would store them. */
export interface ReadingHistoryValues {
  rating: number;
  review: string | null;
  privateNotes: string | null;
  finishedAt: string | null;
  toRead: boolean;
  readStatus: string;
  readCount: number;
  shelves: string[];
  ratingNotice: string | null;
}

export interface ReadingHistoryPreviewBook {
  sourceKey: string;
  title: string | null;
  author: string | null;
  isbn: string | null;
  match: ReadingHistoryMatch;
  bookId: string | null;
  candidates: HighlightImportCandidate[];
  values: ReadingHistoryValues;
  /** False when everything the row holds is already in the library. */
  hasChanges: boolean;
  existingTitle?: string | null;
  notice?: string | null;
}

export interface ReadingHistorySkipped {
  title: string | null;
  author: string | null;
  count: number;
  reason: string;
}

export interface ReadingHistoryImportPreview {
  source: ReadingHistoryImportSource;
  books: ReadingHistoryPreviewBook[];
  skipped?: ReadingHistorySkipped[] | null;
}

/**
 * One row's answer: an existing library book, `create` to add a paper book
 * (or a reading-list entry for a to-read row), or neither to leave it out.
 */
export interface ReadingHistoryImportDecision {
  sourceKey: string;
  bookId?: string;
  create?: boolean;
  /** Turn this row's shelves into collections. Never assumed. */
  mapShelves?: boolean;
}

export interface ReadingHistoryImportResultBook {
  sourceKey: string;
  status: 'imported' | 'unchanged' | 'skipped';
  bookId: string | null;
  bookTitle: string | null;
  sourceTitle: string | null;
  sourceAuthor: string | null;
  changeCount: number;
  created: boolean;
  message: string | null;
}

export interface ReadingHistoryImportResult {
  batchId: string | null;
  source: ReadingHistoryImportSource;
  books: ReadingHistoryImportResultBook[];
}

export interface ReadingHistoryImportBatch {
  id: string;
  source: ReadingHistoryImportSource;
  fileName: string | null;
  createdAtUtc: string;
  bookCount: number;
  changeCount: number;
}

/**
 * Import a Goodreads or StoryGraph library export (issue #1521). Also owns
 * whether the dialog is open, so Settings and any future entry point share one.
 */
@Injectable({ providedIn: 'root' })
export class ReadingHistoryImportService {
  readonly isOpen = signal(false);
  /** The file a drop zone handed over (issue #1526); read once, then cleared. */
  readonly pendingFile = signal<File | null>(null);

  constructor(private http: HttpClient) {}

  open(): void {
    this.isOpen.set(true);
  }

  /** Opens the dialog on a file from the one drop zone; nothing is chosen first. */
  openWithFile(file: File): void {
    this.pendingFile.set(file);
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
  }

  /** Reads the file and proposes a book and its values per row. Writes nothing. */
  preview(file: File): Observable<ReadingHistoryImportPreview> {
    return this.http.post<ReadingHistoryImportPreview>('/api/books/imports/preview', this.body(file));
  }

  /** Applies the rows that have a decision; a row without one is left out. */
  commit(file: File, decisions: ReadingHistoryImportDecision[]): Observable<ReadingHistoryImportResult> {
    const body = this.body(file);
    body.append('decisions', JSON.stringify(decisions));
    return this.http.post<ReadingHistoryImportResult>('/api/books/imports/commit', body);
  }

  recent(): Observable<ReadingHistoryImportBatch[]> {
    return this.http.get<ReadingHistoryImportBatch[]>('/api/books/imports/batches');
  }

  /** Takes one import back: the values it wrote and the rows it added. */
  undo(batchId: string): Observable<{ removed: number }> {
    return this.http.delete<{ removed: number }>(`/api/books/imports/batches/${batchId}`);
  }

  private body(file: File): FormData {
    const body = new FormData();
    body.append('file', file, file.name);
    return body;
  }
}
