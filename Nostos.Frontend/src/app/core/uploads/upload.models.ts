/**
 * Framework-neutral primitives shared by every chunked upload: the engine, the
 * digest service, the library transfer and both the book and Calibre clients.
 *
 * They live in `core/` so a `core/services/` upload client never has to reach
 * into a feature folder for them (#1646).
 */

/** Read block for whole-file hashing: bounded regardless of file size (plan §11.3). */
export const HASH_READ_BLOCK_BYTES = 4 * 1024 * 1024;

/** Default upload concurrency: enough pipelining, bounded memory (plan §19). */
export const DEFAULT_UPLOAD_CONCURRENCY = 2;

/** Default total attempts per chunk, including the first (plan §22). */
export const DEFAULT_CHUNK_MAX_ATTEMPTS = 8;

export class TransferCancelledError extends Error {
  constructor(message = 'The operation was cancelled.') {
    super(message);
    this.name = 'TransferCancelledError';
  }
}

/** Transient progress of a single in-flight chunk request. */
export interface ChunkProgress {
  chunkIndex: number;
  loadedBytes: number;
  totalBytes: number;
}

/** Aggregated transfer progress suitable for a progress component (plan §23). */
export interface TransferProgress {
  /** Server-confirmed completed bytes; never decreases. */
  uploadedBytes: number;
  totalBytes: number;
  completedChunks: number;
  totalChunks: number;
  /** Bytes of in-flight requests currently known to be on the wire. */
  inFlightBytes: number;
  /** Rolling transfer rate, null until the estimator has enough samples. */
  rateBytesPerSecond: number | null;
  /** Seconds remaining, null while the rate is unreliable. */
  etaSeconds: number | null;
  paused: boolean;
}

export interface FileDigest {
  /** Whole-file streaming SHA-256; bounded reads, progress, cancellable. */
  sha256(
    file: Blob,
    options?: {
      signal?: AbortSignal;
      readBlockBytes?: number;
      onProgress?: (bytesRead: number, totalBytes: number) => void;
    },
  ): Promise<string>;

  /** One-shot SHA-256 of a single bounded chunk. */
  sha256Chunk(blob: Blob): Promise<string>;

  /** Versioned cheap fingerprint over size + head + tail (plan §14). */
  fingerprint(file: Blob): Promise<string>;
}

/** One bounded part of a file, addressed by its byte range and its digest. */
export interface UploadChunk {
  index: number;
  offsetBytes: number;
  lengthBytes: number;
  sha256: string;
  blob: Blob;
}

/** Byte length of one part; the last part is shorter than `chunkSize`. */
export function chunkLength(index: number, totalBytes: number, chunkSize: number): number {
  const offset = index * chunkSize;
  return Math.max(0, Math.min(chunkSize, totalBytes - offset));
}
