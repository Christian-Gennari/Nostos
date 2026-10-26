import { Injectable, inject } from '@angular/core';

import {
  BOOK_UPLOAD_LIMITS,
  BookUploadErrorCode,
  BookUploadStatusDto,
} from '../dtos/book-upload.dtos';
import { planLimitMessage } from '../plan-limit-message';
import { FileDigestService } from '../../library-transfer/services/file-digest.service';
import {
  ChunkPartTransport,
  ChunkUploadEngine,
  ChunkUploadSession,
} from '../../library-transfer/services/chunk-upload-engine.service';
import { TransferCancelledError } from '../../library-transfer/models/library-transfer.models';
import { DeploymentCapabilitiesService } from './deployment-capabilities.service';
import {
  BookUploadClient,
  BookUploadTransportError,
  abortedError,
} from './book-upload.client';

/** Which visible phase a resumable book upload is in. */
export type BookUploadPhase = 'preparing' | 'uploading' | 'finishing';

export interface BookUploadProgress {
  phase: BookUploadPhase;
  /** 0-100 for the phase the user is looking at. */
  percent: number;
  /** Bytes the server has durably receipted (0 while hashing). */
  uploadedBytes: number;
  totalBytes: number;
}

export interface BookUploadOptions {
  signal?: AbortSignal;
  onProgress?: (progress: BookUploadProgress) => void;
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

  private readonly runs = new Map<string, Promise<BookUploadStatusDto>>();

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
   * a {@link BookUploadTransportError} (or {@link TransferCancelledError} when
   * the caller aborts); the visible copy comes from
   * {@link describeBookUploadFailure}.
   */
  upload(
    bookId: string,
    file: File,
    options: BookUploadOptions = {},
  ): Promise<BookUploadStatusDto> {
    const active = this.runs.get(bookId);
    if (active) return active;

    let run: Promise<BookUploadStatusDto>;
    run = this.start(bookId, file, options).finally(() => {
      if (this.runs.get(bookId) === run) this.runs.delete(bookId);
    });
    this.runs.set(bookId, run);
    return run;
  }

  /** The server's current unfinished session, used by resume affordances. */
  current(bookId: string, signal?: AbortSignal): Promise<BookUploadStatusDto | null> {
    return this.client.getCurrent(bookId, signal);
  }

  private async start(
    bookId: string,
    file: File,
    options: BookUploadOptions,
  ): Promise<BookUploadStatusDto> {
    this.assertAcceptable(file);
    const signal = options.signal;
    if (signal?.aborted) throw abortedError();

    const report = options.onProgress ?? (() => {});
    report({ phase: 'preparing', percent: 0, uploadedBytes: 0, totalBytes: file.size });

    // Whole-file SHA-256 is streamed by the worker-backed digest service;
    // this call never materializes the file as one ArrayBuffer.
    let lastHashedPercent = 0;
    const sha256 = await this.digest.sha256(file, {
      signal,
      onProgress: (bytesRead, totalBytes) => {
        const percent = totalBytes > 0 ? Math.round((bytesRead / totalBytes) * 100) : 0;
        if (percent === lastHashedPercent) return;
        lastHashedPercent = percent;
        report({ phase: 'preparing', percent, uploadedBytes: 0, totalBytes });
      },
    });

    if (signal?.aborted) throw abortedError();

    const status = await this.client.create(
      bookId,
      {
        totalBytes: file.size,
        fileName: file.name,
        contentType: file.type || 'application/octet-stream',
        sha256,
      },
      signal,
    );

    if (status.state === 'Complete') {
      report({
        phase: 'finishing',
        percent: 100,
        uploadedBytes: status.totalBytes,
        totalBytes: status.totalBytes,
      });
      return status;
    }

    const session: ChunkUploadSession = {
      sessionId: status.sessionId,
      totalBytes: status.totalBytes,
      chunkSize: status.chunkSize,
      totalChunks: status.totalChunks,
      receivedChunks: status.receivedParts,
    };

    const engine = new ChunkUploadEngine(undefined, this.digest);
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

    const run = engine.start({
      session,
      file,
      partTransport,
      hashMismatchCode: 'book_upload_part_hash_mismatch',
      concurrency: 2,
      maxAttempts: 4,
      signal,
      onProgress: (progress) => {
        const moved = progress.uploadedBytes + progress.inFlightBytes;
        report({
          phase: 'uploading',
          percent:
            progress.totalBytes > 0
              ? Math.min(99, Math.round((moved / progress.totalBytes) * 100))
              : 0,
          uploadedBytes: progress.uploadedBytes,
          totalBytes: progress.totalBytes,
        });
      },
    });

    try {
      const outcome = await run.done;
      if (outcome.kind === 'cancelled') {
        void this.pauseInBackground(bookId, session.sessionId);
        throw new TransferCancelledError('The upload was cancelled.');
      }
    } catch (error) {
      if (error instanceof TransferCancelledError) throw error;

      // The parts already receipted stay useful: tell the server to mark the
      // session resumable before the page loses track of it.
      const failure = error as { retryable?: boolean; status?: number } | null;
      if (failure?.retryable || failure?.status === 0) {
        void this.pauseInBackground(bookId, session.sessionId);
      }
      throw error;
    }

    report({
      phase: 'finishing',
      percent: 100,
      uploadedBytes: status.totalBytes,
      totalBytes: status.totalBytes,
    });

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
      throw new BookUploadTransportError(
        'book_upload_invalid_request',
        400,
        'The selected file is empty.',
      );
    }
    if (file.size > BOOK_UPLOAD_LIMITS.maxBookBytes) {
      throw new BookUploadTransportError(
        'book_upload_invalid_request',
        413,
        'This file is larger than this server allows. Choose a smaller file to continue.',
      );
    }
  }
}

/**
 * Reader-facing copy for a failed resumable upload (#1266). The codes are
 * stable; the words say what happened and what the reader can do next.
 */
export function describeBookUploadFailure(
  error: unknown,
  options: { bookSaved?: boolean } = {},
): string {
  const limit = planLimitMessage(error) ?? planLimitMessage(asCause(error));
  if (limit) return limit;

  if (error instanceof TransferCancelledError) {
    return 'The upload was cancelled.';
  }

  const failure = error as
    | { code?: string; status?: number; cause?: unknown }
    | null
    | undefined;
  const resume = options.bookSaved
    ? 'Your book is saved; choose the same file to resume.'
    : 'Choose the same file to resume.';

  switch (failure?.code as BookUploadErrorCode | undefined) {
    case 'book_upload_expired':
      return `This upload expired. ${resume}`;
    case 'book_upload_part_hash_mismatch':
      return 'A part of the file did not match its checksum. The upload will retry that part.';
    case 'book_upload_file_hash_mismatch':
      return `The file changed while it was uploading. ${resume}`;
    case 'book_upload_storage_exhausted':
      return 'There is not enough free storage for this file.';
    case 'book_upload_unsupported_type':
      return 'This file type cannot be uploaded as a book.';
    case 'book_upload_in_progress':
      return 'This book is already importing or uploading a file. Wait until it finishes.';
    case 'book_upload_not_found':
    case 'book_upload_book_not_found':
      return `The book or its upload is no longer available. ${resume}`;
    case 'book_upload_part_range_invalid':
    case 'book_upload_part_conflict':
      return `The server rejected a part of the file. ${resume}`;
    case 'book_upload_publish_failed':
      return 'The file was received but could not be added to the book. Try the upload again.';
    case 'book_upload_invalid_request':
      return failure?.status === 413
        ? 'This file is larger than this server allows. Choose a smaller file to continue.'
        : 'The server could not accept this file. Check the file and try again.';
    case 'request_aborted':
      return 'The upload was cancelled.';
  }

  const status = failure?.status;
  if (status === 0 || status == null) {
    return `The connection was interrupted. ${resume}`;
  }
  if (status === 408 || status === 504) {
    return `The upload timed out. ${resume}`;
  }
  if (status === 413) {
    return 'This file is larger than this server allows. Choose a smaller file to continue.';
  }
  if (status === 429) {
    return `Too many uploads are running right now. ${resume}`;
  }
  if (status >= 500) {
    return `The upload was interrupted. ${resume}`;
  }
  return 'The server did not accept this file. Check the file and try again.';
}

function asCause(error: unknown): unknown {
  return (error as { cause?: unknown } | null | undefined)?.cause;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
