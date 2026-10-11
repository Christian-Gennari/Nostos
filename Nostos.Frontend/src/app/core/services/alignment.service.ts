import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import {
  LocateTextRequest,
  LocateTextResult,
} from '../dtos/alignment.dtos';

/**
 * Locates the passage preceding an audiobook position in another EPUB
 * edition of the same work.
 *
 * This is an expensive, non-idempotent operation. The service does not
 * automatically retry failed requests.
 */
@Injectable({ providedIn: 'root' })
export class AlignmentService {
  private readonly http = inject(HttpClient);

  locateText(
    audioBookId: string,
    request: LocateTextRequest,
  ): Observable<LocateTextResult> {
    return this.http.post<LocateTextResult>(
      `/api/books/${encodeURIComponent(audioBookId)}/alignment/locate-text`,
      request,
    );
  }
}
