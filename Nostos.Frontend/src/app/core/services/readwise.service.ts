import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import type {
  HighlightImportDecision,
  HighlightImportPreview,
  HighlightImportResult,
} from './highlight-import.service';

/** What Settings → Import shows for Readwise's export API. */
export interface ReadwiseConnectionStatus {
  connected: boolean;
  lastImportedAtUtc: string | null;
  importedThroughUtc: string | null;
}

/**
 * The library's Readwise export-API connection (issue #1525). The token goes
 * only to the server, which checks it with Readwise, stores it encrypted and
 * never returns it. Preview and commit fetch the export server-side; the
 * browser never holds the owner's highlights between the two steps.
 */
@Injectable({ providedIn: 'root' })
export class ReadwiseService {
  constructor(private http: HttpClient) {}

  status(): Observable<ReadwiseConnectionStatus> {
    return this.http.get<ReadwiseConnectionStatus>('/api/notes/imports/readwise');
  }

  connect(token: string): Observable<ReadwiseConnectionStatus> {
    return this.http.post<ReadwiseConnectionStatus>('/api/notes/imports/readwise/connect', { token });
  }

  disconnect(): Observable<ReadwiseConnectionStatus> {
    return this.http.delete<ReadwiseConnectionStatus>('/api/notes/imports/readwise');
  }

  /** Reads the export from the saved watermark and proposes every book. Writes nothing. */
  preview(): Observable<HighlightImportPreview> {
    return this.http.post<HighlightImportPreview>('/api/notes/imports/readwise/preview', {});
  }

  /** Imports the chosen books as one undo batch, then advances the watermark. */
  commit(
    decisions: HighlightImportDecision[],
    batchId: string | null = null,
  ): Observable<HighlightImportResult> {
    return this.http.post<HighlightImportResult>('/api/notes/imports/readwise/commit', {
      decisions,
      batchId,
    });
  }
}
