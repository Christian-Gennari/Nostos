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

export type HighlightImportStart = 'all' | 'applebooks';

/** Where Books keeps its databases on a Mac; the owner pastes it into the folder window. */
export const APPLE_BOOKS_FOLDER = '~/Library/Containers/com.apple.iBooksX/Data/Documents';

/** Files that go to the server as one import: a main file and the files that belong with it. */
export interface HighlightImportUpload {
  file: File;
  companions: File[];
}

const APPLE_ANNOTATIONS = /^AEAnnotation.*\.sqlite$/i;
const APPLE_LIBRARY = /^BKLibrary.*\.sqlite$/i;
const APPLE_WAL = /^(AEAnnotation|BKLibrary).*\.sqlite-(wal|shm)$/i;

/** Apple Books' own databases and their -wal files, out of a whole folder. */
export function appleBooksFiles(files: File[]): File[] {
  return files.filter((file) => APPLE_ANNOTATIONS.test(file.name) || APPLE_LIBRARY.test(file.name) || APPLE_WAL.test(file.name));
}

/**
 * Apple Books keeps its highlights and its book titles in two databases
 * (issue #1357), and recent changes can sit in a `-wal` file beside each.
 * Those travel as one upload, led by the highlights database; every other
 * file is its own upload. The server still tells files apart by content.
 */
export function groupHighlightImportFiles(files: File[]): HighlightImportUpload[] {
  const apple = appleBooksFiles(files);
  const others = files.filter((file) => !apple.includes(file)).map((file) => ({ file, companions: [] }));
  const lead = apple.find((file) => APPLE_ANNOTATIONS.test(file.name)) ?? apple.find((file) => APPLE_LIBRARY.test(file.name));
  if (!lead) return [...others, ...apple.map((file) => ({ file, companions: [] }))];
  return [{ file: lead, companions: apple.filter((file) => file !== lead) }, ...others];
}

/** Where a file came from, as the server tells it apart by content. */
export type HighlightImportSource = 'kobo' | 'koreader' | 'kindle' | 'readwise' | 'applebooks';

export const HIGHLIGHT_IMPORT_SOURCE_LABEL: Record<HighlightImportSource, string> = {
  kobo: 'Kobo',
  koreader: 'KOReader',
  kindle: 'Kindle',
  readwise: 'Readwise',
  applebooks: 'Apple Books',
};

export interface HighlightImportCandidate {
  bookId: string;
  title: string;
  author: string | null;
  reason: string;
  /** The library book's format, for the row's format glyph. */
  format: BookFormatConcept;
}

/** One highlight of a device book, cut short; `index` names it in a decision. */
export interface HighlightImportPreviewHighlight {
  index: number;
  text: string | null;
  note: string | null;
  /** False for a highlight Nostos already holds (for the book proposed first). */
  isNew: boolean;
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
  highlights?: HighlightImportPreviewHighlight[] | null;
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
  /** Indexes of the highlights the owner unticked. */
  excludedHighlights?: number[];
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

/**
 * What one committed import found and where every entry went (issue #1522).
 * `found` always equals `imported + duplicates + skipped + leftOut`.
 */
export interface HighlightImportReport {
  found: number;
  imported: number;
  duplicates: number;
  skipped: number;
  leftOut: number;
  /** The file's unimportable entries, grouped by title, author and reason. */
  skippedReasons: HighlightImportSkipped[];
  booksMatched: number;
  booksCreated: number;
  booksLeftOut: number;
}

export interface HighlightImportBatch {
  id: string;
  source: HighlightImportSource;
  fileName: string | null;
  createdAtUtc: string;
  noteCount: number;
  bookCount: number;
  /** Null for an import made before reports existed. */
  report: HighlightImportReport | null;
}

/**
 * Highlight import from an e-reader, an export or Apple Books on a Mac (issues #656, #1219, #1357). Also owns whether the import dialog
 * is open, so Settings and the command palette open the same one.
 */
@Injectable({ providedIn: 'root' })
export class HighlightImportService {
  readonly isOpen = signal(false);
  /** Where the dialog opens: the list of every source, or straight on Apple Books' steps. */
  readonly startOn = signal<HighlightImportStart>('all');
  /**
   * Files a drop zone handed over (issue #1526). The dialog reads them once,
   * as if they had been chosen in its own box.
   */
  readonly pendingFiles = signal<File[] | null>(null);

  constructor(private http: HttpClient) {}

  open(start: HighlightImportStart = 'all'): void {
    this.startOn.set(start);
    this.isOpen.set(true);
  }

  /** Opens the dialog on files from the one drop zone; nothing is chosen first. */
  openWithFiles(files: readonly File[]): void {
    this.startOn.set('all');
    this.pendingFiles.set([...files]);
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
  }

  /** Reads a file and proposes a library book per device book. Writes nothing. */
  preview(file: File, companions: File[] = []): Observable<HighlightImportPreview> {
    return this.http.post<HighlightImportPreview>('/api/notes/imports/preview', this.body(file, companions));
  }

  /** Imports the books that have a decision; a book without one is left out. */
  commit(
    file: File,
    decisions: HighlightImportDecision[],
    batchId: string | null = null,
    companions: File[] = [],
  ): Observable<HighlightImportResult> {
    const body = this.body(file, companions);
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

  private body(file: File, companions: File[] = []): FormData {
    const body = new FormData();
    body.append('file', file, file.name);
    for (const companion of companions) body.append('companion', companion, companion.name);
    return body;
  }
}
