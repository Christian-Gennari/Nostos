import { Injectable, inject } from '@angular/core';
import {
  HttpClient,
  HttpErrorResponse,
  HttpEvent,
  HttpEventType,
} from '@angular/common/http';
import { firstValueFrom, fromEvent, Observable, takeUntil } from 'rxjs';

import {
  BOOK_UPLOAD_BASE_SEGMENT,
  BookUploadCreateRequestDto,
  BookUploadErrorCode,
  BookUploadPartResultDto,
  BookUploadStatusDto,
} from '../dtos/book-upload.dtos';
import {
  isTransientStatus,
  parseRetryAfterMs,
  readErrorBody,
} from '../../library-transfer/services/migration-transport-error';

/**
 * HTTP adapter for the resumable book-file contract (#1266).
 *
 * One request per part, carrying the same `Content-Range` and
 * `X-Nostos-Chunk-SHA256` metadata the migration upload uses. A part body is
 * always a bounded `Blob` slice supplied by the caller; this adapter never
 * reads the whole file into memory.
 */
@Injectable({ providedIn: 'root' })
export class BookUploadClient {
  private readonly http = inject(HttpClient);

  /** The owner's current unfinished session for a book, or null. */
  async getCurrent(bookId: string, signal?: AbortSignal): Promise<BookUploadStatusDto | null> {
    const response = await this.request(
      this.http.get<BookUploadStatusDto>(this.base(bookId), {
        observe: 'response',
      }),
      signal,
    );
    return response.status === 204 ? null : (response.body as BookUploadStatusDto);
  }

  /** Creates a session, or returns the matching one that already exists. */
  create(
    bookId: string,
    request: BookUploadCreateRequestDto,
    signal?: AbortSignal,
  ): Promise<BookUploadStatusDto> {
    return this.request(this.http.post<BookUploadStatusDto>(this.base(bookId), request), signal);
  }

  async uploadPart(
    bookId: string,
    sessionId: string,
    partIndex: number,
    blob: Blob,
    sha256: string,
    offsetBytes: number,
    totalBytes: number,
    onProgress: (loaded: number, total: number) => void,
    signal?: AbortSignal,
  ): Promise<BookUploadPartResultDto> {
    return this.requestEvents(
      this.http.put<BookUploadPartResultDto>(
        `${this.base(bookId)}/${sessionId}/parts/${partIndex}`,
        blob,
        {
          headers: {
            'Content-Type': 'application/octet-stream',
            'Content-Range': `bytes ${offsetBytes}-${offsetBytes + blob.size - 1}/${totalBytes}`,
            'X-Nostos-Chunk-SHA256': sha256,
          },
          observe: 'events',
          reportProgress: true,
        },
      ),
      signal,
      onProgress,
    );
  }

  complete(bookId: string, sessionId: string, signal?: AbortSignal): Promise<BookUploadStatusDto> {
    return this.request(
      this.http.post<BookUploadStatusDto>(`${this.base(bookId)}/${sessionId}/complete`, null),
      signal,
    );
  }

  pause(bookId: string, sessionId: string, signal?: AbortSignal): Promise<BookUploadStatusDto> {
    return this.request(
      this.http.post<BookUploadStatusDto>(`${this.base(bookId)}/${sessionId}/pause`, null),
      signal,
    );
  }

  discard(bookId: string, sessionId: string, signal?: AbortSignal): Promise<void> {
    return this.request(this.http.delete<void>(`${this.base(bookId)}/${sessionId}`), signal);
  }

  private base(bookId: string): string {
    return `/api/books/${bookId}${BOOK_UPLOAD_BASE_SEGMENT}`;
  }

  private request<T>(source: Observable<T>, signal?: AbortSignal): Promise<T> {
    return this.awaitWithAbort(source, signal);
  }

  private requestEvents<T>(
    source: Observable<HttpEvent<T>>,
    signal: AbortSignal | undefined,
    onProgress: (loaded: number, total: number) => void,
  ): Promise<T> {
    return this.awaitWithAbort(this.withProgress(source, onProgress), signal);
  }

  /**
   * Reads one HTTP call as a promise, honouring the caller's `AbortSignal`.
   * Unsubscribing on abort also drops the underlying XHR, so a cancelled
   * upload stops moving bytes immediately.
   */
  private async awaitWithAbort<T>(
    events: Observable<T>,
    signal?: AbortSignal,
  ): Promise<T> {
    if (signal?.aborted) throw abortedError();

    try {
      return signal
        ? await firstValueFrom(events.pipe(takeUntil(fromEvent(signal, 'abort'))))
        : await firstValueFrom(events);
    } catch (error) {
      if (signal?.aborted) throw abortedError();
      throw mapBookUploadError(error);
    }
  }

  private withProgress<T>(
    source: Observable<HttpEvent<T>>,
    report: (loaded: number, total: number) => void,
  ): Observable<T> {
    return new Observable<T>((subscriber) => {
      const subscription = source.subscribe({
        next: (event) => {
          if (event.type === HttpEventType.UploadProgress) {
            report(event.loaded, event.total ?? 0);
          } else if (event.type === HttpEventType.Response) {
            subscriber.next(event.body as T);
          }
        },
        error: (error) => subscriber.error(error),
        complete: () => subscriber.complete(),
      });
      return () => subscription.unsubscribe();
    });
  }
}

/** A transport failure carrying the stable book-upload code and retry facts. */
export class BookUploadTransportError extends Error {
  constructor(
    readonly code: BookUploadErrorCode,
    readonly status: number,
    message: string,
    readonly retryAfterMs?: number,
    cause?: unknown,
  ) {
    super(message, cause === undefined ? undefined : { cause });
    this.name = 'BookUploadTransportError';
  }

  /** Mirrors the migration transport: only transient statuses may be retried. */
  get retryable(): boolean {
    if (this.code === 'request_aborted') return false;
    return isTransientStatus(this.status);
  }
}

export function abortedError(): BookUploadTransportError {
  return new BookUploadTransportError(
    'request_aborted',
    0,
    'The upload was cancelled.',
  );
}

export function mapBookUploadError(error: unknown): BookUploadTransportError {
  if (error instanceof BookUploadTransportError) return error;

  if (error instanceof HttpErrorResponse) {
    const body = readErrorBody(error.error);
    const code = (body?.error ??
      (error.status === 0 ? 'network_error' : 'unexpected_error')) as BookUploadErrorCode;
    return new BookUploadTransportError(
      code,
      error.status,
      body?.message ?? error.message,
      parseRetryAfterMs(error.headers.get('Retry-After')),
      error,
    );
  }

  return new BookUploadTransportError(
    'unexpected_error',
    0,
    error instanceof Error ? error.message : 'The upload failed unexpectedly.',
    undefined,
    error,
  );
}
