import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

import {
  LocateAudioRequest,
  LocateAudioResult,
  LocateTextRequest,
  LocateTextResult,
} from '../dtos/alignment.dtos';

/**
 * Matches positions between audiobook and EPUB editions of the same work.
 *
 * Alignment is expensive and may consume transcription allowance.
 * Requests are never automatically retried.
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

  locateAudio(
    textBookId: string,
    request: LocateAudioRequest,
  ): Observable<LocateAudioResult> {
    return this.http.post<LocateAudioResult>(
      `/api/books/${encodeURIComponent(textBookId)}/alignment/locate-audio`,
      request,
    );
  }
}
