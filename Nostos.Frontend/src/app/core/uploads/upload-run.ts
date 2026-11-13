/**
 * The shared upload run both resumable flows execute (#1646): hash the whole
 * file with visible progress, then send only the parts the server has not
 * acknowledged.
 *
 * The book upload and the Calibre upload each own their create/complete
 * endpoints and their pause policy; everything between (digest progress,
 * percent maths, engine construction, cancellation) lives here once.
 */

import { ChunkPartTransport, ChunkUploadEngine, ChunkUploadSession } from './chunk-upload-engine';
import {
  FileDigest,
  TransferCancelledError,
  TransferProgress,
} from './upload.models';
import {
  UploadProgress,
  UploadProgressReporter,
  uploadStartingProgress,
} from './upload-progress';

export interface UploadRunRequest {
  digest: FileDigest;
  file: Blob;
  session: ChunkUploadSession;
  partTransport: ChunkPartTransport;
  /** Server code meaning "recompute and resend this part once". */
  hashMismatchCode: string;
  signal?: AbortSignal;
  onProgress?: UploadProgressReporter;
}

/** Bounded pipelining and a few more tries than the migration default allows. */
const UPLOAD_CONCURRENCY = 2;
const UPLOAD_MAX_ATTEMPTS = 4;

/**
 * Streams the whole-file SHA-256, reporting `preparing` from 0 to 100. Throws
 * {@link TransferCancelledError} when the caller stopped while hashing.
 */
export async function digestForUpload(
  digest: FileDigest,
  file: Blob,
  options: { signal?: AbortSignal; onProgress?: UploadProgressReporter } = {},
): Promise<string> {
  const { signal, onProgress } = options;
  onProgress?.(uploadStartingProgress(file.size));

  let lastPercent = 0;
  const sha256 = await digest.sha256(file, {
    signal,
    onProgress: (bytesRead, totalBytes) => {
      const percent = totalBytes > 0 ? Math.round((bytesRead / totalBytes) * 100) : 0;
      if (percent === lastPercent) return;
      lastPercent = percent;
      onProgress?.({
        phase: 'preparing',
        percent,
        uploadedBytes: 0,
        movedBytes: bytesRead,
        totalBytes,
      });
    },
  });

  if (signal?.aborted) throw new TransferCancelledError('The upload was cancelled.');
  return sha256;
}

/**
 * Sends the parts the server has not receipted, reporting `uploading` and
 * holding at 99% until the caller finalises the session.
 */
export async function uploadMissingParts(request: UploadRunRequest): Promise<void> {
  const { digest, file, session, partTransport, hashMismatchCode, signal, onProgress } = request;
  const engine = new ChunkUploadEngine(undefined, digest);
  const run = engine.start({
    session,
    file,
    partTransport,
    hashMismatchCode,
    concurrency: UPLOAD_CONCURRENCY,
    maxAttempts: UPLOAD_MAX_ATTEMPTS,
    signal,
    onProgress: (progress) => onProgress?.(uploadingProgress(progress)),
  });

  const outcome = await run.done;
  if (outcome.kind === 'cancelled') {
    throw new TransferCancelledError('The upload was cancelled.');
  }
}

function uploadingProgress(progress: TransferProgress): UploadProgress {
  const movedBytes = progress.uploadedBytes + progress.inFlightBytes;
  return {
    phase: 'uploading',
    percent:
      progress.totalBytes > 0
        ? Math.min(99, Math.round((movedBytes / progress.totalBytes) * 100))
        : 0,
    uploadedBytes: progress.uploadedBytes,
    movedBytes,
    totalBytes: progress.totalBytes,
  };
}
