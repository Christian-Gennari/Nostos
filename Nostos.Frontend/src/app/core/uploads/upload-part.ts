/**
 * The abortable HTTP half of a chunked upload (#1646).
 *
 * Every resumable upload sends the same part: a bounded `Blob` with a
 * `Content-Range`, an `X-Nostos-Chunk-SHA256` digest and upload-progress
 * events, dropped the moment the caller's `AbortSignal` fires. Book and
 * Calibre uploads used to carry two copies of that XHR plumbing; this is the
 * one implementation, and it reports every failure as one {@link UploadFailure}.
 */

import { HttpClient, HttpEventType } from '@angular/common/http';
import { firstValueFrom, fromEvent, Observable, takeUntil } from 'rxjs';

import { toUploadFailure, uploadCancelled } from './upload-error';

export interface UploadPartRequest {
  /** Absolute API path for one part of one session. */
  url: string;
  blob: Blob;
  offsetBytes: number;
  totalBytes: number;
  sha256: string;
  onProgress?: (loaded: number, total: number) => void;
  signal?: AbortSignal;
}

/** The headers every part carries; the server owns the part size, not the client. */
export function uploadPartHeaders(
  offsetBytes: number,
  blobBytes: number,
  totalBytes: number,
  sha256: string,
): Record<string, string> {
  return {
    'Content-Type': 'application/octet-stream',
    'Content-Range': `bytes ${offsetBytes}-${offsetBytes + blobBytes - 1}/${totalBytes}`,
    'X-Nostos-Chunk-SHA256': sha256,
  };
}

/**
 * Sends one part and resolves with the server's receipt. A caller abort drops
 * the request immediately and rejects with a cancelled failure rather than
 * leaving the promise pending.
 */
export function putUploadPart<T>(http: HttpClient, request: UploadPartRequest): Promise<T> {
  const { url, blob, offsetBytes, totalBytes, sha256, onProgress, signal } = request;
  if (signal?.aborted) return Promise.reject(uploadCancelled());

  return new Promise<T>((resolve, reject) => {
    let settled = false;
    const settle = (finish: () => void) => {
      if (settled) return;
      settled = true;
      signal?.removeEventListener('abort', onAbort);
      finish();
    };

    const onAbort = () => {
      subscription.unsubscribe();
      settle(() => reject(uploadCancelled()));
    };

    const subscription = http
      .put<T>(url, blob, {
        headers: uploadPartHeaders(offsetBytes, blob.size, totalBytes, sha256),
        observe: 'events',
        reportProgress: true,
      })
      .subscribe({
        next: (event) => {
          if (event.type === HttpEventType.UploadProgress) {
            onProgress?.(event.loaded, event.total ?? blob.size);
          } else if (event.type === HttpEventType.Response) {
            settle(() => resolve(event.body as T));
          }
        },
        error: (error) =>
          settle(() =>
            reject(signal?.aborted ? uploadCancelled() : toUploadFailure(error)),
          ),
        complete: () =>
          settle(() =>
            reject(toUploadFailure(new Error('The upload ended without a response.'))),
          ),
      });

    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

/**
 * Reads one upload HTTP call as a promise, honouring the caller's signal.
 * Unsubscribing on abort also drops the underlying XHR, so a cancelled upload
 * stops moving bytes immediately.
 */
export async function sendUploadRequest<T>(
  source: Observable<T>,
  signal?: AbortSignal,
): Promise<T> {
  if (signal?.aborted) throw uploadCancelled();

  try {
    return signal
      ? await firstValueFrom(source.pipe(takeUntil(fromEvent(signal, 'abort'))))
      : await firstValueFrom(source);
  } catch (error) {
    if (signal?.aborted) throw uploadCancelled();
    throw toUploadFailure(error);
  }
}
