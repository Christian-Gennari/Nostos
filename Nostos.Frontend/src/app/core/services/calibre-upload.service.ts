import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpEventType } from '@angular/common/http';
import { firstValueFrom, fromEvent, Observable, takeUntil } from 'rxjs';

import { FileDigestService } from '../../library-transfer/services/file-digest.service';
import {
  ChunkPartTransport,
  ChunkUploadEngine,
  ChunkUploadSession,
} from '../../library-transfer/services/chunk-upload-engine.service';
import { TransferCancelledError } from '../../library-transfer/models/library-transfer.models';
import {
  CalibreImportService,
  CalibreUploadPartReceipt,
  CalibreUploadStatus,
} from './calibre-import.service';

/** Phase of a resumable Calibre zip upload, for the dialog's progress line. */
export type CalibreUploadPhase = 'preparing' | 'uploading' | 'finishing';

export interface CalibreUploadProgress {
  phase: CalibreUploadPhase;
  /** 0-100 for the phase the owner is looking at. */
  percent: number;
  /** Bytes the server has durably receipted. */
  uploadedBytes: number;
  /** Bytes moved, including bodies still in flight. */
  movedBytes: number;
  totalBytes: number;
}

export interface CalibreUploadOptions {
  signal?: AbortSignal;
  onProgress?: (progress: CalibreUploadProgress) => void;
  /** The cap the server advertised; a larger file is refused before hashing. */
  maxBytes?: number | null;
}

/** A stable, owner-readable failure from the upload contract. */
export class CalibreUploadError extends Error {
  constructor(
    readonly code: string,
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'CalibreUploadError';
  }
}

/**
 * The browser half of the uploaded Calibre library (#1572).
 *
 * The parts and their receipts are the same contract the library transfer
 * uses: the server owns the part size, each part carries its SHA-256, and a
 * page reload or a dropped connection leaves the received parts on the
 * server. Selecting the same zip again resumes; selecting a different one
 * replaces the scratch session. The chunk engine (bounded concurrency,
 * backoff, per-part checksum retry) is shared, not copied.
 */
@Injectable({ providedIn: 'root' })
export class CalibreUploadService {
  private readonly http = inject(HttpClient);
  private readonly digest = inject(FileDigestService);
  private readonly api = inject(CalibreImportService);

  async upload(file: File, options: CalibreUploadOptions = {}): Promise<CalibreUploadStatus> {
    if (!file.name || file.size <= 0) {
      throw new CalibreUploadError('calibre_upload_invalid_request', 400, 'The selected file is empty.');
    }
    if (options.maxBytes && file.size > options.maxBytes) {
      throw new CalibreUploadError(
        'calibre_upload_too_large',
        413,
        `This library is ${formatBytes(file.size)}; the limit is ${formatBytes(options.maxBytes)}. ` +
          'Remove some formats or split the library into smaller uploads.',
      );
    }

    const report = options.onProgress ?? (() => {});
    const signal = options.signal;
    if (signal?.aborted) throw new TransferCancelledError('The upload was cancelled.');

    report({ phase: 'preparing', percent: 0, uploadedBytes: 0, movedBytes: 0, totalBytes: file.size });
    let lastHashed = 0;
    const sha256 = await this.digest.sha256(file, {
      signal,
      onProgress: (read, total) => {
        const percent = total > 0 ? Math.round((read / total) * 100) : 0;
        if (percent === lastHashed) return;
        lastHashed = percent;
        report({
          phase: 'preparing',
          percent,
          uploadedBytes: 0,
          movedBytes: read,
          totalBytes: total,
        });
      },
    });

    if (signal?.aborted) throw new TransferCancelledError('The upload was cancelled.');

    const created = await this.create(file, sha256, signal);
    if (created.state === 'staged') {
      report({
        phase: 'finishing',
        percent: 100,
        uploadedBytes: created.totalBytes,
        movedBytes: created.totalBytes,
        totalBytes: created.totalBytes,
      });
      return created;
    }

    const session: ChunkUploadSession = {
      sessionId: created.sessionId,
      totalBytes: created.totalBytes,
      chunkSize: created.partBytes,
      totalChunks: created.totalParts,
      receivedChunks: created.receivedParts,
    };
    const engine = new ChunkUploadEngine(undefined, this.digest);
    const partTransport: ChunkPartTransport = {
      uploadPart: (sessionId, chunk, onProgress, partSignal) =>
        this.sendPart(
          sessionId,
          chunk.index,
          chunk.blob,
          chunk.sha256,
          chunk.offsetBytes,
          session.totalBytes,
          onProgress,
          partSignal,
        ),
    };

    const run = engine.start({
      session,
      file,
      partTransport,
      hashMismatchCode: 'calibre_upload_part_hash_mismatch',
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
          movedBytes: moved,
          totalBytes: progress.totalBytes,
        });
      },
    });

    const outcome = await run.done;
    if (outcome.kind === 'cancelled') {
      throw new TransferCancelledError('The upload was cancelled.');
    }

    report({
      phase: 'finishing',
      percent: 100,
      uploadedBytes: session.totalBytes,
      movedBytes: session.totalBytes,
      totalBytes: session.totalBytes,
    });
    return this.complete(session.sessionId, signal);
  }

  /** Removes a partial or staged session and its scratch bytes. */
  async cancel(sessionId: string): Promise<void> {
    try {
      await firstValueFrom(this.http.delete<void>(`/api/calibre/upload/${sessionId}`));
    } catch (error) {
      const failure = toCalibreUploadError(error);
      if (failure.status === 404) return;
      throw failure;
    }
  }

  /** The owner's unfinished or staged session, for resuming after a reload. */
  async current(signal?: AbortSignal): Promise<CalibreUploadStatus | null> {
    try {
      const response = await firstValueFrom(
        this.abortable(
          this.http.get<CalibreUploadStatus>('/api/calibre/upload', { observe: 'response' }),
          signal,
        ),
      );
      return response.status === 204 ? null : (response.body ?? null);
    } catch (error) {
      if (signal?.aborted) throw new TransferCancelledError('The upload was cancelled.');
      throw toCalibreUploadError(error);
    }
  }

  private async create(
    file: File,
    sha256: string,
    signal?: AbortSignal,
  ): Promise<CalibreUploadStatus> {
    try {
      return await firstValueFrom(
        this.abortable(
          this.http.post<CalibreUploadStatus>('/api/calibre/upload', {
            totalBytes: file.size,
            fileName: file.name,
            sha256,
          }),
          signal,
        ),
      );
    } catch (error) {
      if (signal?.aborted) throw new TransferCancelledError('The upload was cancelled.');
      throw toCalibreUploadError(error);
    }
  }

  private async complete(sessionId: string, signal?: AbortSignal): Promise<CalibreUploadStatus> {
    try {
      return await firstValueFrom(this.abortable(this.api.completeUpload(sessionId), signal));
    } catch (error) {
      if (signal?.aborted) throw new TransferCancelledError('The upload was cancelled.');
      throw toCalibreUploadError(error);
    }
  }

  private sendPart(
    sessionId: string,
    partIndex: number,
    blob: Blob,
    sha256: string,
    offsetBytes: number,
    totalBytes: number,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<CalibreUploadPartReceipt> {
    return new Promise((resolve, reject) => {
      this.http
        .put<CalibreUploadPartReceipt>(
          `/api/calibre/upload/${sessionId}/parts/${partIndex}`,
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
        )
        .pipe(takeUntil(fromEvent(signal, 'abort')))
        .subscribe({
          next: (event) => {
            if (event.type === HttpEventType.UploadProgress) {
              onProgress(event.loaded, event.total ?? blob.size);
            } else if (event.type === HttpEventType.Response) {
              resolve(event.body as CalibreUploadPartReceipt);
            }
          },
          error: (error) => {
            reject(
              signal.aborted
                ? new TransferCancelledError('The upload was cancelled.')
                : toCalibreUploadError(error),
            );
          },
        });
    });
  }

  private abortable<T>(source: Observable<T>, signal?: AbortSignal): Observable<T> {
    return signal ? source.pipe(takeUntil(fromEvent(signal, 'abort'))) : source;
  }
}

/** Normalises an HTTP failure into the stable code and a safe sentence. */
export function toCalibreUploadError(error: unknown): CalibreUploadError {
  if (error instanceof CalibreUploadError) return error;
  if (error instanceof TransferCancelledError) {
    return new CalibreUploadError('request_aborted', 0, error.message);
  }
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { code?: string; message?: string } | string | null;
    const parsed = typeof body === 'string' ? safeJson(body) : body;
    return new CalibreUploadError(
      parsed?.code ?? 'calibre_upload_failed',
      error.status,
      parsed?.message ?? messageFor(error.status),
    );
  }
  return new CalibreUploadError(
    'calibre_upload_failed',
    0,
    error instanceof Error ? error.message : 'The upload failed unexpectedly.',
  );
}

/** Reader-facing copy for a Calibre upload failure. */
export function describeCalibreUploadFailure(error: unknown): string {
  const failure = toCalibreUploadError(error);
  switch (failure.code) {
    case 'calibre_upload_too_large':
    case 'calibre_upload_extracted_too_large':
      return failure.message;
    case 'calibre_upload_expired':
      return 'This upload expired. Choose the zip again to start over.';
    case 'calibre_upload_part_hash_mismatch':
      return 'A part of the file did not match its checksum. The upload will retry that part.';
    case 'calibre_upload_file_hash_mismatch':
      return 'The file changed while it was uploading. Choose the same zip again.';
    case 'calibre_upload_storage_exhausted':
      return 'There is not enough scratch space for this library.';
    case 'calibre_upload_invalid_archive':
    case 'calibre_upload_unsafe_entry':
    case 'calibre_upload_too_many_files':
    case 'calibre_upload_metadata_missing':
    case 'calibre_upload_not_calibre':
      return failure.message;
    case 'calibre_upload_incomplete':
      return 'The upload is missing part of the file. Choose the same zip to resume.';
    case 'request_aborted':
      return 'The upload was cancelled.';
  }

  if (failure.status === 0) {
    return 'The connection was interrupted. Choose the same zip to resume.';
  }
  if (failure.status === 413) {
    return failure.message || 'This library is larger than this server allows.';
  }
  if (failure.status === 507) {
    return 'There is not enough scratch space for this library.';
  }
  if (failure.status >= 500) {
    return 'The upload was interrupted. Choose the same zip to resume.';
  }
  return failure.message || 'The server did not accept this library.';
}

function messageFor(status: number): string {
  if (status === 0) return 'The connection was interrupted.';
  if (status === 413) return 'This library is larger than this server allows.';
  if (status === 507) return 'There is not enough scratch space for this library.';
  return 'The server did not accept this library.';
}

function safeJson(value: string): { code?: string; message?: string } | null {
  try {
    return JSON.parse(value) as { code?: string; message?: string };
  } catch {
    return null;
  }
}

function formatBytes(bytes: number): string {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} bytes`;
}
