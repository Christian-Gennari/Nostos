import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';

import { formatFileSize } from '../format';
import { FileDigestService } from '../uploads/file-digest.service';
import {
  ChunkPartTransport,
  ChunkUploadSession,
} from '../uploads/chunk-upload-engine';
import { TransferCancelledError } from '../uploads/upload.models';
import { UploadFailure, toUploadFailure } from '../uploads/upload-error';
import {
  UploadPhase,
  UploadProgress,
  uploadFinishedProgress,
} from '../uploads/upload-progress';
import { putUploadPart, sendUploadRequest } from '../uploads/upload-part';
import { digestForUpload, uploadMissingParts } from '../uploads/upload-run';
import {
  CalibreImportService,
  CalibreUploadPartReceipt,
  CalibreUploadStatus,
} from './calibre-import.service';

/** Phase of a resumable Calibre zip upload, for the dialog's progress line. */
export type CalibreUploadPhase = UploadPhase;

export type CalibreUploadProgress = UploadProgress;

export interface CalibreUploadOptions {
  signal?: AbortSignal;
  onProgress?: (progress: CalibreUploadProgress) => void;
  /** The cap the server advertised; a larger file is refused before hashing. */
  maxBytes?: number | null;
}

/**
 * The browser half of the uploaded Calibre library (#1572).
 *
 * The parts and their receipts are the same contract the library transfer
 * uses: the server owns the part size, each part carries its SHA-256, and a
 * page reload or a dropped connection leaves the received parts on the
 * server. Selecting the same zip again resumes; selecting a different one
 * replaces the scratch session. The digest, part transport and retry policy
 * are the shared upload engine, not a copy (#1646).
 */
@Injectable({ providedIn: 'root' })
export class CalibreUploadService {
  private readonly http = inject(HttpClient);
  private readonly digest = inject(FileDigestService);
  private readonly api = inject(CalibreImportService);

  async upload(file: File, options: CalibreUploadOptions = {}): Promise<CalibreUploadStatus> {
    if (!file.name || file.size <= 0) {
      throw new UploadFailure('calibre_upload_invalid_request', 400, 'The selected file is empty.');
    }
    if (options.maxBytes && file.size > options.maxBytes) {
      throw new UploadFailure(
        'calibre_upload_too_large',
        413,
        `This library is ${formatFileSize(file.size)}; the limit is ${formatFileSize(options.maxBytes)}. ` +
          'Remove some formats or split the library into smaller uploads.',
      );
    }

    const report = options.onProgress ?? (() => {});
    const signal = options.signal;
    if (signal?.aborted) throw new TransferCancelledError('The upload was cancelled.');

    const sha256 = await digestForUpload(this.digest, file, { signal, onProgress: report });

    const created = await this.create(file, sha256, signal);
    if (created.state === 'staged') {
      report(uploadFinishedProgress(created.totalBytes));
      return created;
    }

    const session: ChunkUploadSession = {
      sessionId: created.sessionId,
      totalBytes: created.totalBytes,
      chunkSize: created.partBytes,
      totalChunks: created.totalParts,
      receivedChunks: created.receivedParts,
    };
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

    await uploadMissingParts({
      digest: this.digest,
      file,
      session,
      partTransport,
      hashMismatchCode: 'calibre_upload_part_hash_mismatch',
      signal,
      onProgress: report,
    });

    report(uploadFinishedProgress(session.totalBytes));
    return this.complete(session.sessionId, signal);
  }

  /** Removes a partial or staged session and its scratch bytes. */
  async cancel(sessionId: string): Promise<void> {
    try {
      await sendUploadRequest(this.http.delete<void>(`/api/calibre/upload/${sessionId}`));
    } catch (error) {
      const failure = toUploadFailure(error);
      if (failure.status === 404) return;
      throw failure;
    }
  }

  /** The owner's unfinished or staged session, for resuming after a reload. */
  async current(signal?: AbortSignal): Promise<CalibreUploadStatus | null> {
    const response = await sendUploadRequest(
      this.http.get<CalibreUploadStatus>('/api/calibre/upload', { observe: 'response' }),
      signal,
    );
    return response.status === 204 ? null : (response.body ?? null);
  }

  private create(
    file: File,
    sha256: string,
    signal?: AbortSignal,
  ): Promise<CalibreUploadStatus> {
    return sendUploadRequest(
      this.http.post<CalibreUploadStatus>('/api/calibre/upload', {
        totalBytes: file.size,
        fileName: file.name,
        sha256,
      }),
      signal,
    );
  }

  private complete(sessionId: string, signal?: AbortSignal): Promise<CalibreUploadStatus> {
    return sendUploadRequest(this.api.completeUpload(sessionId), signal);
  }

  private sendPart(
    sessionId: string,
    partIndex: number,
    blob: Blob,
    sha256: string,
    offsetBytes: number,
    totalBytes: number,
    onProgress: (loaded: number, total: number) => void,
    signal?: AbortSignal,
  ): Promise<CalibreUploadPartReceipt> {
    return putUploadPart<CalibreUploadPartReceipt>(this.http, {
      url: `/api/calibre/upload/${sessionId}/parts/${partIndex}`,
      blob,
      sha256,
      offsetBytes,
      totalBytes,
      onProgress,
      signal,
    });
  }
}
