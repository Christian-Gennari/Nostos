import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, firstValueFrom, of } from 'rxjs';

export interface ReadAloudServerVoice {
  id: string;
  name: string;
  /** BCP 47 tag, e.g. "en-GB". */
  language: string;
}

/** What the server can read a book in one language with. */
export interface ReadAloudStatus {
  available: boolean;
  /** True when the host supplies and meters the voice (Cloud). */
  managed: boolean;
  defaultVoice: string | null;
  /** Voices for the asked language, best match first. */
  voices: ReadAloudServerVoice[];
  maxCharacters: number;
  /** Why the natural voice is not available, as the server's stable code; null when it is. */
  reason?: string | null;
}

export const READ_ALOUD_UNAVAILABLE: ReadAloudStatus = {
  available: false,
  managed: false,
  defaultVoice: null,
  voices: [],
  maxCharacters: 0,
};

/** A speech request the server refused, with the stable code it gave. */
export class ReadAloudSpeechError extends Error {
  constructor(readonly code: string | null) {
    super(code ?? 'read_aloud_failed');
  }
}

/**
 * The server voice for read-aloud. Every failure is reported, never thrown
 * past the caller as something unexpected: the reader answers any of them by
 * carrying on with the device's own voice.
 */
@Injectable({ providedIn: 'root' })
export class ReadAloudService {
  private readonly http = inject(HttpClient);

  /** Never errors: a host with no server voice, or an unreachable one, is "not available". */
  status(language: string | null): Observable<ReadAloudStatus> {
    return this.http
      .get<ReadAloudStatus>('/api/read-aloud/status', { params: { language: language ?? '' } })
      .pipe(catchError(() => of(READ_ALOUD_UNAVAILABLE)));
  }

  /** One sentence as audio. Rejects with a {@link ReadAloudSpeechError}. */
  async speech(text: string, voice: string | null, language: string | null): Promise<Blob> {
    try {
      return await firstValueFrom(
        this.http.post('/api/read-aloud/speech', { text, voice, language }, { responseType: 'blob' }),
      );
    } catch (error) {
      throw new ReadAloudSpeechError(await problemCode(error));
    }
  }
}

/** The `title` of the problem document. A blob request delivers its error body as a blob too. */
async function problemCode(error: unknown): Promise<string | null> {
  if (!(error instanceof HttpErrorResponse)) return null;
  try {
    const raw = error.error;
    const body =
      raw instanceof Blob ? JSON.parse(await blobText(raw)) : typeof raw === 'string' ? JSON.parse(raw) : raw;
    return typeof body?.title === 'string' ? body.title : null;
  } catch {
    return null;
  }
}

function blobText(blob: Blob): Promise<string> {
  if (typeof blob.text === 'function') return blob.text();
  // Older engines have no Blob.text().
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result ?? ''));
    reader.onerror = () => reject(reader.error);
    reader.readAsText(blob);
  });
}
