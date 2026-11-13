import { Injectable, inject } from '@angular/core';

import {
  BOOK_UPLOAD_LIMITS,
  BookUploadStatusDto,
} from '../dtos/book-upload.dtos';
import { UploadFailure, toUploadFailure } from '../uploads/upload-error';
import { canonicalUploadCode } from '../uploads/upload-error-copy';
import { FileDigestService } from '../uploads/file-digest.service';
import {
  ChunkPartTransport,
  ChunkUploadSession,
} from '../uploads/chunk-upload-engine';
import { TransferCancelledError } from '../uploads/upload.models';
import {
  UploadPhase,
  UploadProgress,
  uploadFinishedProgress,
} from '../uploads/upload-progress';
import { digestForUpload, uploadMissingParts } from '../uploads/upload-run';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';
import { BookUploadClient } from './book-upload.client';

/** Which visible phase a resumable book upload is in. */
export type BookUploadPhase = UploadPhase;

export type BookUploadProgress = UploadProgress;

export interface BookUploadOptions {
  signal?: AbortSignal;
  onProgress?: (progress: BookUploadProgress) => void;
  /** Must be true to upload over a file the book already has (409 `book_has_file`). */
  replace?: boolean;
}

/** One in-flight run, kept so a caller can stop it without owning the File. */
interface BookUploadRun {
  controller: AbortController;
  promise: Promise<BookUploadStatusDto>;
  /** The pause request scheduled by a cancellation or dropped connection. */
  getPause(): Promise<void>;
}

/**
 * Root-scoped owner of resumable book-file uploads (#1266).
 *
 * The run lives here, not in a modal or a detail page, so closing the Add book
 * sheet while a 300 MB file is still arriving does not cancel it (#1264). A
 * second caller asking for the same book joins the run already in flight.
 *
 * On a page reload the browser cannot keep the File object: the user picks the
 * same file again, and the server matches it by size + name + whole-file
 * SHA-256 and answers with the parts it already has. This service then sends
 * only the missing parts through the shared chunk engine, which owns the
 * bounded concurrency, backoff and per-part checksum retry.
 */
@Injectable({ providedIn: 'root' })
export class BookUploadService {
  private readonly client = inject(BookUploadClient);
  private readonly digest = inject(FileDigestService);
  private readonly capabilities = inject(DeploymentCapabilitiesService);

  private readonly runs = new Map<string, BookUploadRun>();

  /**
   * True only when the host advertised the resumable contract and bootstrap
   * already loaded that answer. An unknown answer falls back to the legacy
   * whole-request upload rather than blocking the click.
   */
  supportsResumable(): boolean {
    return this.capabilities.peek()?.supportsResumableBookUploads === true;
  }

  /**
   * Starts, resumes or joins the upload of one file for one book. Rejects with
   * an {@link UploadFailure} (or {@link TransferCancelledError} when the caller
   * aborts); the visible copy comes from `describeUploadFailure`.
   */
  upload(
    bookId: string,
    file: File,
    options: BookUploadOptions = {},
  ): Promise<BookUploadStatusDto> {
    const active = this.runs.get(bookId);
    if (active) return active.promise;

    const controller = new AbortController();
    const external = options.signal;
    const forwardAbort = () => controller.abort();
    if (external) {
      if (external.aborted) controller.abort();
      else external.addEventListener('abort', forwardAbort, { once: true });
    }

    // The run owns its own controller so a surface that did not start it (or no
    // longer holds its signal) can still stop it. A caller-owned signal is
    // forwarded rather than replaced, so existing callers keep that behaviour.
    const pauseState: { current: Promise<void> } = { current: Promise.resolve() };
    const record: BookUploadRun = {
      controller,
      getPause: () => pauseState.current,
      promise: this.start(
        bookId,
        file,
        { ...options, signal: controller.signal },
        (pause) => {
          pauseState.current = pause;
        },
      ),
    };
    record.promise = record.promise.finally(() => {
      external?.removeEventListener('abort', forwardAbort);
      if (this.runs.get(bookId) === record) this.runs.delete(bookId);
    });
    this.runs.set(bookId, record);
    return record.promise;
  }

  /**
   * Stops a run the caller can no longer see (#1275). In-flight parts are
   * aborted by the engine, the session is paused so the same file resumes, and
   * the promise resolves only once that pause attempt has finished. Safe to
   * call for a book with no active run.
   */
  async stop(bookId: string): Promise<void> {
    const run = this.runs.get(bookId);
    if (!run) return;

    run.controller.abort();
    try {
      await run.promise;
    } catch {
      // Stopping is the expected outcome; the caller already chose to end it.
    }
    await run.getPause();
  }

  /** The server's current unfinished session, used by resume affordances. */
  current(bookId: string, signal?: AbortSignal): Promise<BookUploadStatusDto | null> {
    return this.client.getCurrent(bookId, signal);
  }

  private async start(
    bookId: string,
    file: File,
    options: BookUploadOptions,
    onPauseScheduled: (pause: Promise<void>) => void,
  ): Promise<BookUploadStatusDto> {
    this.assertAcceptable(file);
    const signal = options.signal;
    if (signal?.aborted) throw cancelledError();

    const report = options.onProgress ?? (() => {});

    // Whole-file SHA-256 is streamed by the worker-backed digest service;
    // this call never materializes the file as one ArrayBuffer.
    const sha256 = await digestForUpload(this.digest, file, { signal, onProgress: report });

    const status = await this.client.create(
      bookId,
      {
        totalBytes: file.size,
        fileName: file.name,
        contentType: file.type || 'application/octet-stream',
        sha256,
      },
      signal,
      options.replace === true,
    );

    if (status.state === 'Complete') {
      report(uploadFinishedProgress(status.totalBytes));
      return status;
    }

    // A stop can land while the create request is in flight, before this side
    // has a session id. Now it does: pause it so the server does not keep a
    // session that looks busy to the next attempt.
    if (signal?.aborted) {
      onPauseScheduled(this.pauseInBackground(bookId, status.sessionId));
      throw cancelledError();
    }

    const session: ChunkUploadSession = {
      sessionId: status.sessionId,
      totalBytes: status.totalBytes,
      chunkSize: status.chunkSize,
      totalChunks: status.totalChunks,
      receivedChunks: status.receivedParts,
    };

    const partTransport: ChunkPartTransport = {
      uploadPart: (sessionId, chunk, onProgress, partSignal) =>
        this.client.uploadPart(
          bookId,
          sessionId,
          chunk.index,
          chunk.blob,
          chunk.sha256,
          chunk.offsetBytes,
          status.totalBytes,
          onProgress,
          partSignal,
        ),
    };

    try {
      await uploadMissingParts({
        digest: this.digest,
        file,
        session,
        partTransport,
        hashMismatchCode: 'book_upload_part_hash_mismatch',
        signal,
        onProgress: report,
      });
    } catch (error) {
      // The parts already receipted stay useful: tell the server to mark the
      // session resumable before the page loses track of it.
      const failure = toUploadFailure(error);
      if (failure.code === 'request_aborted' || failure.retryable || failure.status === 0) {
        onPauseScheduled(this.pauseInBackground(bookId, session.sessionId));
      }
      throw error;
    }

    report(uploadFinishedProgress(status.totalBytes));

    return this.client.complete(bookId, session.sessionId, signal);
  }

  /**
   * Best-effort pause after a dropped connection. The client is already
   * showing the resume copy; a pause that lands late is still better than a
   * session that looks busy to the next attempt. Never throws to the caller.
   */
  private async pauseInBackground(bookId: string, sessionId: string): Promise<void> {
    for (let attempt = 0; attempt < 3; attempt += 1) {
      try {
        await this.client.pause(bookId, sessionId);
        return;
      } catch {
        await delay(500 * 2 ** attempt);
      }
    }
  }

  private assertAcceptable(file: File): void {
    if (!file.name || file.size <= 0) {
      throw new UploadFailure(
        'book_upload_invalid_request',
        400,
        'The selected file is empty.',
      );
    }
    if (file.size > BOOK_UPLOAD_LIMITS.maxBookBytes) {
      throw new UploadFailure(
        'book_upload_invalid_request',
        413,
        'This file is larger than this server allows. Choose a smaller file to continue.',
      );
    }
  }
}

/**
 * The server's reason when it read the bytes and found they are not the kind of
 * file the name claims (an empty or damaged EPUB, say), else null. Works for a
 * resumable transport failure and for the whole-request upload's HTTP error.
 */
export function rejectedFileMessage(error: unknown): string | null {
  const failure = toUploadFailure(error);
  if (canonicalUploadCode(failure.code) !== 'invalid_file') return null;
  return failure.message || 'This file could not be read as a book.';
}

function cancelledError(): TransferCancelledError {
  return new TransferCancelledError('The upload was cancelled.');
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
