import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';

import {
  BOOK_UPLOAD_BASE_SEGMENT,
  BookUploadCreateRequestDto,
  BookUploadPartResultDto,
  BookUploadStatusDto,
} from '../dtos/book-upload.dtos';
import { putUploadPart, sendUploadRequest } from '../../core/uploads/upload-part';

/**
 * HTTP adapter for the resumable book-file contract (#1266).
 *
 * One request per part, carrying the same `Content-Range` and
 * `X-Nostos-Chunk-SHA256` metadata every chunked upload uses (the shared
 * transport lives in `core/uploads/upload-part.ts`). A part body is always a
 * bounded `Blob` slice supplied by the caller; this adapter never reads the
 * whole file into memory.
 */
@Injectable({ providedIn: 'root' })
export class BookUploadClient {
  private readonly http = inject(HttpClient);

  /** The owner's current unfinished session for a book, or null. */
  async getCurrent(bookId: string, signal?: AbortSignal): Promise<BookUploadStatusDto | null> {
    const response = await sendUploadRequest(
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
    replace = false,
  ): Promise<BookUploadStatusDto> {
    const url = replace ? `${this.base(bookId)}?replace=true` : this.base(bookId);
    return sendUploadRequest(this.http.post<BookUploadStatusDto>(url, request), signal);
  }

  uploadPart(
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
    return putUploadPart<BookUploadPartResultDto>(this.http, {
      url: `${this.base(bookId)}/${sessionId}/parts/${partIndex}`,
      blob,
      sha256,
      offsetBytes,
      totalBytes,
      onProgress,
      signal,
    });
  }

  complete(bookId: string, sessionId: string, signal?: AbortSignal): Promise<BookUploadStatusDto> {
    return sendUploadRequest(
      this.http.post<BookUploadStatusDto>(`${this.base(bookId)}/${sessionId}/complete`, null),
      signal,
    );
  }

  pause(bookId: string, sessionId: string, signal?: AbortSignal): Promise<BookUploadStatusDto> {
    return sendUploadRequest(
      this.http.post<BookUploadStatusDto>(`${this.base(bookId)}/${sessionId}/pause`, null),
      signal,
    );
  }

  discard(bookId: string, sessionId: string, signal?: AbortSignal): Promise<void> {
    return sendUploadRequest(this.http.delete<void>(`${this.base(bookId)}/${sessionId}`), signal);
  }

  private base(bookId: string): string {
    return `/api/books/${bookId}${BOOK_UPLOAD_BASE_SEGMENT}`;
  }
}
