import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import type { HighlightImportCandidate } from './highlight-import.service';

/** One supported file of a Calibre book, as the review screen offers it. */
export interface CalibreFormatChoice {
  key: string;
  format: string;
  fileName: string;
  bytes: number;
  kind: 'ebook' | 'audiobook';
  /** An edition of the matched work already has a file of this format. */
  alreadyHere: boolean;
  existingBookId: string | null;
  selectedByDefault: boolean;
}

export interface CalibreUnsupportedFormat {
  fileName: string;
  format: string;
  reason: string;
}

export interface CalibrePreviewBook {
  sourceKey: string;
  calibreId: number;
  title: string;
  author: string | null;
  isbn: string | null;
  series: string | null;
  volumeNumber: string | null;
  publisher: string | null;
  publishedDate: string | null;
  rating: number;
  description: string | null;
  hasCover: boolean;
  tags: string[];
  readAt: string | null;
  readNotice: string | null;
  match: 'exact' | 'suggested' | 'none';
  bookId: string | null;
  candidates: HighlightImportCandidate[];
  formats: CalibreFormatChoice[];
  unsupportedFormats: CalibreUnsupportedFormat[];
  existingTitle: string | null;
  notice: string | null;
}

export interface CalibrePreviewSummary {
  books: number;
  ready: number;
  inLibrary: number;
  alreadyHere: number;
  needsReview: number;
  skipped: number;
  formats: number;
  unsupportedFormats: number;
  truncated: boolean;
}

export interface CalibreCustomColumn {
  label: string;
  name: string;
  dataType: string;
  status: 'used' | 'skipped';
  reason: string | null;
}

export interface CalibrePreview {
  source: string;
  summary: CalibrePreviewSummary;
  books: CalibrePreviewBook[];
  skipped: { title: string | null; author: string | null; count: number; reason: string }[];
  customColumns: CalibreCustomColumn[];
  warnings: string[];
}

/** One Calibre book the owner answered. No formats means "leave it out". */
export interface CalibreImportDecision {
  sourceKey: string;
  formats?: string[];
  bookId?: string;
  createCollections?: boolean;
}

export type CalibreImportStatus = 'idle' | 'importing' | 'done' | 'interrupted' | 'failed';

export interface CalibreImportResultBook {
  sourceKey: string;
  calibreId: number;
  title: string;
  status: 'imported' | 'unchanged' | 'skipped' | 'failed';
  bookId: string | null;
  bookTitle: string | null;
  fileCount: number;
  created: boolean;
  message: string | null;
}

export interface CalibreImportJobState {
  status: CalibreImportStatus;
  startedAt: string | null;
  finishedAt: string | null;
  total: number;
  completed: number;
  current: string | null;
  batchId: string | null;
  results: CalibreImportResultBook[];
  error: string | null;
}

export type CalibreUploadState = 'receiving' | 'staged' | 'failed';

/** One resumable zip upload of a Calibre library (#1572). */
export interface CalibreUploadStatus {
  sessionId: string;
  state: CalibreUploadState;
  fileName: string;
  totalBytes: number;
  partBytes: number;
  totalParts: number;
  receivedParts: number[];
  receivedBytes: number;
  createdAtUtc: string;
  expiresAtUtc: string;
  maxLibraryBytes: number;
  maxExtractedBytes: number;
  maxFiles: number;
  books: number | null;
  stagedBytes: number | null;
  failureCode: string | null;
  failureMessage: string | null;
}

export interface CalibreUploadPartReceipt {
  sessionId: string;
  partIndex: number;
  alreadyPresent: boolean;
}

export interface CalibreStatus {
  configured: boolean;
  available: boolean;
  import: CalibreImportJobState;
  /** Which source the server will read: a mounted folder or an uploaded zip. */
  source?: 'folder' | 'upload' | null;
  /** The server can read a folder on this machine. */
  folderSupported?: boolean;
  /** The server accepts a zipped library through the resumable upload. */
  uploadSupported?: boolean;
  maxUploadBytes?: number | null;
  upload?: CalibreUploadStatus | null;
}

export interface CalibreBatch {
  id: string;
  source: string;
  fileName: string | null;
  createdAtUtc: string;
  bookCount: number;
  changeCount: number;
}

export interface CalibreUndoResult {
  removed: number;
  kept: number;
}

/**
 * Import a Calibre library (issue #1527, SelfHosted only). Also owns whether
 * the dialog is open, so Settings has one entry point.
 */
@Injectable({ providedIn: 'root' })
export class CalibreImportService {
  readonly isOpen = signal(false);
  /**
   * The zip a drop zone handed over (#1572). The dialog reads it once, then
   * clears it, so a reload cannot replay an upload.
   */
  readonly pendingFile = signal<File | null>(null);

  constructor(private http: HttpClient) {}

  open(): void {
    this.isOpen.set(true);
  }

  /** Opens the dialog on a dropped Calibre zip; nothing is chosen first. */
  openWithFile(file: File): void {
    this.pendingFile.set(file);
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
  }

  /** Whether a library is set up, whether it is reachable, and the last import. */
  status(): Observable<CalibreStatus> {
    return this.http.get<CalibreStatus>('/api/calibre/');
  }

  /** Reads the library and proposes every book. Writes nothing. */
  preview(): Observable<CalibrePreview> {
    return this.http.get<CalibrePreview>('/api/calibre/preview');
  }

  /** Starts the server-side import and returns its first progress. */
  start(decisions: CalibreImportDecision[]): Observable<CalibreImportJobState> {
    return this.http.post<CalibreImportJobState>('/api/calibre/import', { decisions });
  }

  progress(): Observable<CalibreImportJobState> {
    return this.http.get<CalibreImportJobState>('/api/calibre/import');
  }

  recent(): Observable<CalibreBatch[]> {
    return this.http.get<CalibreBatch[]>('/api/calibre/batches');
  }

  /** Takes one import back: its files, covers, memberships and unused books. */
  undo(batchId: string): Observable<CalibreUndoResult> {
    return this.http.delete<CalibreUndoResult>(`/api/calibre/batches/${batchId}`);
  }

  /**
   * Verifies, extracts and validates the uploaded zip. Answers with the
   * staged session (including how many books it holds), or a stable error.
   */
  completeUpload(sessionId: string): Observable<CalibreUploadStatus> {
    return this.http.post<CalibreUploadStatus>(`/api/calibre/upload/${sessionId}/complete`, null);
  }
}
