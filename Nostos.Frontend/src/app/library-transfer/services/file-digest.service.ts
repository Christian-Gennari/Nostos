import { Injectable, inject } from '@angular/core';

import {
  FileDigest,
  HASH_READ_BLOCK_BYTES,
  TransferCancelledError,
} from '../models/library-transfer.models';
import { readBlobBytes } from './hash/blob-bytes';
import { sha256ChunkHex } from './hash/chunk-digest';
import { hashBlobIncrementally } from './hash/file-hash';
import { HASH_WORKER_FACTORY } from './hash/hash-worker';
import {
  HashWorkerCancelRequest,
  HashWorkerOutbound,
  HashWorkerStartRequest,
} from './hash/hash-worker.protocol';
import { Sha256 } from './hash/sha256';

/** Per request, the main-thread half of the worker conversation. */
interface PendingHashRequest {
  resolve: (sha256: string) => void;
  reject: (error: unknown) => void;
  onProgress?: (bytesRead: number, totalBytes: number) => void;
}

const FINGERPRINT_SAMPLE_BYTES = 64 * 1024;

/**
 * Whole-file streaming SHA-256 over a Web Worker, with a bounded in-process
 * fallback where `Worker` is unavailable. The main thread never reads file
 * bytes; per-chunk digests stay one-shot over a single bounded slice.
 */
@Injectable({ providedIn: 'root' })
export class FileDigestService implements FileDigest {
  private readonly workerFactory = inject(HASH_WORKER_FACTORY);

  private worker: Worker | null | undefined;
  private nextRequestId = 1;
  private readonly pending = new Map<number, PendingHashRequest>();

  async sha256(
    file: Blob,
    options: {
      signal?: AbortSignal;
      readBlockBytes?: number;
      onProgress?: (bytesRead: number, totalBytes: number) => void;
    } = {},
  ): Promise<string> {
    if (options.signal?.aborted) throw new TransferCancelledError('Hashing was cancelled.');

    const worker = this.ensureWorker();
    if (!worker) return hashBlobIncrementally(file, options);

    try {
      return await this.hashViaWorker(worker, file, options);
    } catch (error) {
      if (error instanceof TransferCancelledError) throw error;

      // A broken worker must not take the transfer down: reset it and finish
      // in-process, still bounded and incremental.
      this.disposeWorker();
      return hashBlobIncrementally(file, options);
    }
  }

  async sha256Chunk(blob: Blob): Promise<string> {
    return sha256ChunkHex(blob);
  }

  /**
   * Cheap rejection fingerprint (plan §14): `nostos-fp-v1:<hex>` over the exact
   * byte length plus the first and last 64 KiB. It can never substitute for
   * size + full SHA-256.
   */
  async fingerprint(file: Blob): Promise<string> {
    const sha = new Sha256();
    const sizeBytes = new Uint8Array(8);
    new DataView(sizeBytes.buffer).setBigUint64(0, BigInt(file.size), false);
    sha.update(sizeBytes);

    const sample = Math.min(FINGERPRINT_SAMPLE_BYTES, file.size);
    if (sample > 0) {
      sha.update(await readBlobBytes(file.slice(0, sample)));
      const tailStart = Math.max(sample, file.size - sample);
      if (tailStart >= sample) {
        sha.update(await readBlobBytes(file.slice(tailStart, file.size)));
      }
    }

    return `nostos-fp-v1:${sha.hex()}`;
  }

  /** Releases the worker; the next hash re-creates it. */
  terminate(): void {
    this.disposeWorker();
  }

  private ensureWorker(): Worker | null {
    if (this.worker !== undefined) return this.worker;

    try {
      this.worker = this.workerFactory();
    } catch {
      this.worker = null;
    }

    if (this.worker) {
      this.worker.onmessage = (event: MessageEvent<HashWorkerOutbound>) =>
        this.handleWorkerMessage(event.data);
      this.worker.onerror = () => this.failWorker();
    }

    return this.worker ?? null;
  }

  private disposeWorker(): void {
    this.worker?.terminate();
    this.worker = undefined;
    this.failAllPending(new Error('The hashing worker was terminated.'));
  }

  private failWorker(): void {
    this.worker = undefined;
    this.failAllPending(new Error('The hashing worker failed.'));
  }

  private failAllPending(error: Error): void {
    const pending = [...this.pending.values()];
    this.pending.clear();
    for (const request of pending) request.reject(error);
  }

  private handleWorkerMessage(message: HashWorkerOutbound): void {
    const request = this.pending.get(message.requestId);
    if (!request) return;

    switch (message.type) {
      case 'progress':
        request.onProgress?.(message.bytesRead, message.totalBytes);
        break;
      case 'result':
        this.pending.delete(message.requestId);
        request.resolve(message.sha256);
        break;
      case 'error':
        this.pending.delete(message.requestId);
        request.reject(
          message.cancelled
            ? new TransferCancelledError(message.message)
            : new Error(message.message),
        );
        break;
    }
  }

  private hashViaWorker(
    worker: Worker,
    file: Blob,
    options: {
      signal?: AbortSignal;
      readBlockBytes?: number;
      onProgress?: (bytesRead: number, totalBytes: number) => void;
    },
  ): Promise<string> {
    const requestId = this.nextRequestId;
    this.nextRequestId += 1;

    return new Promise<string>((resolve, reject) => {
      const cleanup = () => {
        options.signal?.removeEventListener('abort', onAbort);
        this.pending.delete(requestId);
      };

      const onAbort = () => {
        cleanup();
        const cancel: HashWorkerCancelRequest = { type: 'cancel', requestId };
        worker.postMessage(cancel);
        reject(new TransferCancelledError('Hashing was cancelled.'));
      };

      this.pending.set(requestId, {
        resolve: (sha256) => {
          cleanup();
          resolve(sha256);
        },
        reject: (error) => {
          cleanup();
          reject(error);
        },
        onProgress: options.onProgress,
      });

      options.signal?.addEventListener('abort', onAbort, { once: true });

      const request: HashWorkerStartRequest = {
        type: 'hash',
        requestId,
        blob: file,
        readBlockBytes: options.readBlockBytes ?? HASH_READ_BLOCK_BYTES,
      };

      try {
        worker.postMessage(request);
      } catch (error) {
        cleanup();
        reject(error);
      }
    });
  }
}
