import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

/**
 * `ready` can be imported as it is; `review` only when chosen by hand;
 * `linked` is already a book here; `unusable` cannot be imported.
 */
export type ExternalMediaItemStatus = 'ready' | 'linked' | 'review' | 'unusable';

export interface ExternalMediaLibraryMatch {
  bookId: string;
  title: string;
  author: string | null;
}

/** One book the scan found in the linked folder. */
export interface ExternalMediaScanItem {
  key: string;
  kind: 'audiobook' | 'ebook';
  layout: 'file' | 'tracks';
  /** Path below the linked folder. Never a path on the server. */
  location: string;
  status: ExternalMediaItemStatus;
  title: string;
  author: string | null;
  narrator: string | null;
  language: string | null;
  fileCount: number;
  totalBytes: number;
  concerns: string[];
  linkedBookId: string | null;
  libraryMatches: ExternalMediaLibraryMatch[];
}

export interface ExternalMediaScanSummary {
  discovered: number;
  ready: number;
  alreadyLinked: number;
  needsReview: number;
  unusable: number;
  unsupportedFiles: number;
  missingBooks: number;
  skippedLinks: number;
  unreadableFolders: number;
  unusableNames: number;
  truncated: boolean;
}

export interface ExternalMediaScanProgress {
  status: 'idle' | 'scanning' | 'done' | 'failed';
  startedAt: string | null;
  finishedAt: string | null;
  examined: number;
  total: number;
  error: string | null;
}

export interface ExternalMediaMissingBook {
  bookId: string;
  title: string;
  missingFiles: number;
}

export interface ExternalMediaScanResponse {
  scan: ExternalMediaScanProgress;
  summary: ExternalMediaScanSummary | null;
  items: ExternalMediaScanItem[];
  matchingItems: number;
  unsupportedSamples: string[];
  missing: ExternalMediaMissingBook[];
}

export interface ExternalMediaStatus {
  mounted: boolean;
  linkedBooks: number;
  scan: ExternalMediaScanProgress;
}

export interface ExternalMediaImportOutcome {
  key: string;
  title: string;
  outcome: 'imported' | 'already_linked' | 'skipped' | 'failed';
  bookId: string | null;
  message: string | null;
}

export interface ExternalMediaImportState {
  status: 'idle' | 'importing' | 'done' | 'interrupted';
  startedAt: string | null;
  finishedAt: string | null;
  total: number;
  completed: number;
  current: string | null;
  outcomes: ExternalMediaImportOutcome[];
}

/** Every ready book except `except`, plus the books named in `keys`. */
export interface ExternalMediaImportRequest {
  allReady: boolean;
  except?: string[];
  keys?: string[];
}

export interface ExternalMediaBookStatus {
  linked: boolean;
  files: number;
  unavailableFiles: number;
}

/**
 * Linking books from a folder the server administrator mounted (SelfHosted
 * only). Callers check `supportsExternalMediaLibraries` first: a server
 * without the feature answers these routes with 404.
 */
@Injectable({ providedIn: 'root' })
export class ExternalMediaService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/external-media';

  status(): Observable<ExternalMediaStatus> {
    return this.http.get<ExternalMediaStatus>(this.base);
  }

  startScan(): Observable<ExternalMediaScanProgress> {
    return this.http.post<ExternalMediaScanProgress>(`${this.base}/scan`, null);
  }

  scan(status?: ExternalMediaItemStatus, offset = 0, limit = 200): Observable<ExternalMediaScanResponse> {
    let params = new HttpParams().set('offset', offset).set('limit', limit);
    if (status) params = params.set('status', status);
    return this.http.get<ExternalMediaScanResponse>(`${this.base}/scan`, { params });
  }

  startImport(request: ExternalMediaImportRequest): Observable<ExternalMediaImportState> {
    return this.http.post<ExternalMediaImportState>(`${this.base}/import`, request);
  }

  importState(): Observable<ExternalMediaImportState> {
    return this.http.get<ExternalMediaImportState>(`${this.base}/import`);
  }

  book(bookId: string): Observable<ExternalMediaBookStatus> {
    return this.http.get<ExternalMediaBookStatus>(`${this.base}/books/${bookId}`);
  }
}
