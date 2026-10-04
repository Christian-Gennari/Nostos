/**
 * Bounded-memory chunk upload engine (plan §18-§23, §32; slice B3).
 *
 * A run uploads only the chunk indexes the server has not acknowledged,
 * hashes each `File.slice()` just before sending it, keeps at most
 * `concurrency` requests in flight, retries only transient failures with
 * exponential backoff plus jitter, and reports durable (receipt) progress
 * separately from in-flight bytes.
 *
 * Deliberately framework-light: the coordinator constructs one run per upload
 * and wires it to signals; tests construct it directly against the mock.
 */

import {
  BrowserMigrationChunk,
  MigrationSessionStatusDto,
  chunkLength,
} from '../models/migration-http.dtos';
import {
  DEFAULT_CHUNK_MAX_ATTEMPTS,
  DEFAULT_UPLOAD_CONCURRENCY,
  FileDigest,
  TransferCancelledError,
  TransferProgress,
} from '../models/library-transfer.models';
import {
  LibraryTransferTransport,
  MigrationTransportError,
} from './library-transfer-transport';
import { TransferRateEstimator } from './transfer-rate-estimator';

export interface ChunkUploadRequest {
  jobId: string;
  session: MigrationSessionStatusDto;
  file: Blob;
  concurrency?: number;
  maxAttempts?: number;
  signal?: AbortSignal;
  onProgress?: (progress: TransferProgress) => void;
}

export type ChunkUploadOutcome = { kind: 'completed' } | { kind: 'cancelled' };

export interface ChunkUploadEngineOptions {
  random?: () => number;
  now?: () => number;
}

const MAX_IMPLEMENTATION_CONCURRENCY = 4;

export class ChunkUploadEngine {
  private readonly random: () => number;
  private readonly now: () => number;

  constructor(
    private readonly transport: LibraryTransferTransport,
    private readonly digest: FileDigest,
    options: ChunkUploadEngineOptions = {},
  ) {
    this.random = options.random ?? (() => Math.random());
    this.now = options.now ?? (() => Date.now());
  }

  start(request: ChunkUploadRequest): ChunkUploadRun {
    return new ChunkUploadRun(this.transport, this.digest, this.random, this.now, request);
  }
}

export class ChunkUploadRun {
  private readonly missing: number[];
  private readonly received: Set<number>;
  private readonly estimator: TransferRateEstimator;
  private readonly abort = new AbortController();
  private readonly onExternalAbort = () => this.cancel();
  private readonly inFlight = new Map<number, number>();
  private readonly concurrency: number;
  private readonly maxAttempts: number;

  private cursor = 0;
  private paused = false;
  private cancelled = false;
  private fatal: unknown = null;
  private settled = false;
  private readonly settledPromise: Promise<ChunkUploadOutcome>;
  private resolveDone!: (outcome: ChunkUploadOutcome) => void;
  private rejectDone!: (error: unknown) => void;

  private durableBytes: number;
  private lastProgress: TransferProgress;

  constructor(
    private readonly transport: LibraryTransferTransport,
    private readonly digest: FileDigest,
    private readonly random: () => number,
    now: () => number,
    private readonly request: ChunkUploadRequest,
  ) {
    this.concurrency = clamp(
      request.concurrency ?? DEFAULT_UPLOAD_CONCURRENCY,
      1,
      MAX_IMPLEMENTATION_CONCURRENCY,
    );
    this.maxAttempts = Math.max(1, request.maxAttempts ?? DEFAULT_CHUNK_MAX_ATTEMPTS);

    const received = new Set(
      request.session.receivedChunks.filter(
        (index) => index >= 0 && index < request.session.totalChunks,
      ),
    );
    this.received = received;
    this.durableBytes = [...received].reduce(
      (total, index) =>
        total + chunkLength(index, request.session.totalBytes, request.session.chunkSize),
      0,
    );
    this.missing = [];
    for (let index = 0; index < request.session.totalChunks; index += 1) {
      if (!received.has(index)) this.missing.push(index);
    }

    this.estimator = new TransferRateEstimator({ now });
    this.lastProgress = this.snapshotProgress();
    this.settledPromise = new Promise<ChunkUploadOutcome>((resolve, reject) => {
      this.resolveDone = resolve;
      this.rejectDone = reject;
    });

    if (request.signal) {
      if (request.signal.aborted) {
        this.cancelled = true;
        this.resolveDone({ kind: 'cancelled' });
      } else {
        request.signal.addEventListener('abort', this.onExternalAbort, { once: true });
      }
    }

    if (!this.cancelled) {
      this.emit();
      this.pump();
    }
  }

  get done(): Promise<ChunkUploadOutcome> {
    return this.settledPromise;
  }

  get progress(): TransferProgress {
    return this.lastProgress;
  }

  /** Stops scheduling new chunks; in-flight requests are awaited, not aborted. */
  async pause(): Promise<void> {
    if (this.paused || this.cancelled || this.fatal) return;
    this.paused = true;
    this.emit();
    await Promise.allSettled(this.workers);
  }

  /** Continues a paused run; returns the same completion promise. */
  resume(): Promise<ChunkUploadOutcome> {
    if (this.fatal || this.cancelled || !this.paused) return this.settledPromise;
    this.paused = false;
    this.emit();
    this.pump();
    return this.settledPromise;
  }

  /** Aborts in-flight chunk requests and settles as cancelled. */
  cancel(): void {
    if (this.settled) return;
    this.cancelled = true;
    this.paused = false;
    this.abort.abort();
    this.settle({ kind: 'cancelled' });
  }

  private workers: Promise<void>[] = [];

  private pump(): void {
    const workers: Promise<void>[] = [];
    for (let index = 0; index < this.concurrency; index += 1) workers.push(this.worker());
    this.workers = workers;

    void Promise.allSettled(workers).then((results) => {
      if (this.settled) return;
      const failed = results.find(
        (result): result is PromiseRejectedResult => result.status === 'rejected',
      );
      if (failed) {
        this.fail(failed.reason);
        return;
      }
      if (this.cancelled || this.fatal) return;
      if (this.paused && this.cursor < this.missing.length) return;
      if (this.cursor >= this.missing.length) this.settle({ kind: 'completed' });
    });
  }

  private async worker(): Promise<void> {
    while (!this.cancelled && !this.fatal && !this.paused) {
      const position = this.cursor;
      this.cursor += 1;
      if (position >= this.missing.length) return;
      await this.uploadOne(this.missing[position]);
    }
  }

  private async uploadOne(chunkIndex: number): Promise<void> {
    const { session, jobId } = this.request;
    const offsetBytes = chunkIndex * session.chunkSize;
    const lengthBytes = chunkLength(chunkIndex, session.totalBytes, session.chunkSize);
    const blob = this.request.file.slice(offsetBytes, offsetBytes + lengthBytes);

    let sha256 = await this.digest.sha256Chunk(blob);
    this.throwIfCancelled();
    let recomputedHash = false;

    for (let attempt = 1; ; attempt += 1) {
      this.throwIfCancelled();
      this.inFlight.set(chunkIndex, 0);
      this.emit();

      try {
        const chunk: BrowserMigrationChunk = {
          index: chunkIndex,
          offsetBytes,
          lengthBytes,
          sha256,
          blob,
        };
        await this.transport.uploadChunk(
          jobId,
          session.sessionId,
          chunk,
          (loaded) => {
            this.inFlight.set(chunkIndex, loaded);
            this.emit();
          },
          this.abort.signal,
        );

        this.inFlight.delete(chunkIndex);
        if (!this.received.has(chunkIndex)) {
          this.received.add(chunkIndex);
          this.durableBytes += lengthBytes;
        }
        this.emit();
        return;
      } catch (error) {
        this.inFlight.delete(chunkIndex);
        this.emit();
        if (this.cancelled) throw new TransferCancelledError('Upload was cancelled.');

        const transportError = asTransportError(error);
        if (transportError.code === 'migration_chunk_hash_mismatch' && !recomputedHash) {
          recomputedHash = true;
          sha256 = await this.digest.sha256Chunk(blob);
          continue;
        }
        if (!transportError.retryable || attempt >= this.maxAttempts) throw transportError;

        await this.sleep(this.backoffMs(attempt, transportError.retryAfterMs));
      }
    }
  }

  private backoffMs(attempt: number, retryAfterMs?: number): number {
    const base = Math.min(500 * 2 ** (attempt - 1), 8_000);
    const jitter = 0.8 + 0.4 * this.random();
    return Math.max(Math.round(base * jitter), retryAfterMs ?? 0);
  }

  private sleep(ms: number): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.abort.signal.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
      const onAbort = () => {
        clearTimeout(timer);
        reject(new TransferCancelledError('Upload was cancelled.'));
      };
      this.abort.signal.addEventListener('abort', onAbort, { once: true });
    });
  }

  private throwIfCancelled(): void {
    if (this.cancelled || this.abort.signal.aborted) {
      throw new TransferCancelledError('Upload was cancelled.');
    }
    if (this.fatal) throw this.fatal;
  }

  private emit(): void {
    this.lastProgress = this.snapshotProgress();
    this.request.onProgress?.(this.lastProgress);
  }

  private snapshotProgress(): TransferProgress {
    const inFlightBytes = [...this.inFlight.values()].reduce(
      (total, loaded) => total + loaded,
      0,
    );
    this.estimator.sample(this.durableBytes + inFlightBytes);
    const remaining = this.request.session.totalBytes - this.durableBytes;
    return {
      uploadedBytes: this.durableBytes,
      totalBytes: this.request.session.totalBytes,
      completedChunks: this.received.size,
      totalChunks: this.request.session.totalChunks,
      inFlightBytes,
      rateBytesPerSecond: this.estimator.rateBytesPerSecond(),
      etaSeconds: this.estimator.etaSeconds(remaining),
      paused: this.paused,
    };
  }

  private fail(error: unknown): void {
    if (this.settled) return;
    this.fatal = error;
    this.abort.abort();
    this.request.signal?.removeEventListener('abort', this.onExternalAbort);
    this.settled = true;
    this.rejectDone(error);
  }

  private settle(outcome: ChunkUploadOutcome): void {
    if (this.settled) return;
    this.settled = true;
    this.abort.abort();
    this.request.signal?.removeEventListener('abort', this.onExternalAbort);
    this.resolveDone(outcome);
  }
}

function clamp(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min;
  return Math.min(max, Math.max(min, Math.floor(value)));
}

function asTransportError(error: unknown): MigrationTransportError {
  if (error instanceof MigrationTransportError) return error;
  if (error instanceof TransferCancelledError) {
    return new MigrationTransportError('request_aborted', 0, error.message);
  }
  return new MigrationTransportError(
    'network_error',
    0,
    error instanceof Error ? error.message : 'Chunk upload failed.',
    { cause: error },
  );
}
