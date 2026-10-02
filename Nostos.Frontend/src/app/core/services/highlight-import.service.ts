import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map } from 'rxjs';

export type HighlightImportSource = 'kobo' | 'koreader';
export type HighlightImportStatus = 'matched' | 'unmatched' | 'ambiguous';

/** One book's outcome, whichever e-reader the file came from. */
export interface HighlightImportBook {
  status: HighlightImportStatus;
  bookId: string | null;
  bookTitle: string | null;
  sourceTitle: string | null;
  sourceAuthor: string | null;
  annotationCount: number;
  importedCount: number;
  duplicateCount: number;
  skippedCount: number;
  message: string | null;
}

interface KoboImportReport {
  books: HighlightImportBook[];
}

interface KoreaderImportReport {
  status: HighlightImportStatus;
  bookId: string | null;
  bookTitle: string | null;
  annotationCount: number;
  importedCount: number;
  duplicateCount: number;
  skippedCount: number;
  message: string | null;
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

  /**
   * Which e-reader a file came from, by name: KOReader sidecars are
   * `metadata.<ext>.lua`, a Kobo database is `KoboReader.sqlite`.
   */
  detectSource(fileName: string): HighlightImportSource | null {
    const name = fileName.toLowerCase();
    if (name.endsWith('.lua')) return 'koreader';
    if (name.endsWith('.sqlite') || name.endsWith('.sqlite3') || name.endsWith('.db')) return 'kobo';
    return null;
  }

  /**
   * Imports one file. A Kobo database reports every book on the device; a
   * KOReader sidecar is one book, returned here as a one-book list so the
   * dialog renders a single shape.
   */
  import(file: File, source: HighlightImportSource): Observable<HighlightImportBook[]> {
    const body = new FormData();
    body.append('file', file, file.name);

    if (source === 'kobo') {
      return this.http
        .post<KoboImportReport>('/api/notes/import/kobo', body)
        .pipe(map((report) => report.books));
    }

    return this.http.post<KoreaderImportReport>('/api/notes/import/koreader', body).pipe(
      map((report) => [
        {
          ...report,
          // The sidecar report carries no title for a book it could not place.
          sourceTitle: report.bookTitle ?? file.name,
          sourceAuthor: null,
        },
      ]),
    );
  }
}
