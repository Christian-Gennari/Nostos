import {
  BrowserMigrationChunk,
  MigrationChunkUploadResultDto,
  MigrationJobStatusResponseDto,
  MigrationPreflightResponseDto,
  MigrationSessionStatusDto,
  MigrationUploadSessionResponseDto,
} from '../models/migration-http.dtos';
import { FileDigest, TransferProgress } from '../models/library-transfer.models';
import { MigrationTransportError } from './library-transfer-transport';
import { ChunkUploadEngine, ChunkUploadRun } from './chunk-upload-engine.service';
import { sha256ChunkHex } from './hash/chunk-digest';

const CHUNK = 4 * 1024 * 1024;

interface QueuedFailure {
  chunkIndex?: number;
  times: number;
  error: MigrationTransportError;
}

/** Scripted transport: the engine's counterpart to the server rules tested elsewhere. */
class EngineFakeTransport {
  readonly attempts = new Map<number, number>();
  readonly received = new Set<number>();
  readonly completedOrder: number[] = [];
  readonly requestedSlices: number[] = [];
  readonly abortedOperations = 0;
  delays = new Map<number, number>();
  failures: QueuedFailure[] = [];
  active = 0;
  maxActive = 0;
  digestCalls: Array<{ index: number; lengthBytes: number }> = [];

  get receivedIndexes(): number[] {
    return [...this.received].sort((a, b) => a - b);
  }

  async uploadChunk(
    _jobId: string,
    sessionId: string,
    chunk: BrowserMigrationChunk,
    onProgress: (loaded: number, total: number) => void,
    signal: AbortSignal,
  ): Promise<MigrationChunkUploadResultDto> {
    this.attempts.set(chunk.index, (this.attempts.get(chunk.index) ?? 0) + 1);
    this.requestedSlices.push(chunk.lengthBytes);
    this.active += 1;
    this.maxActive = Math.max(this.maxActive, this.active);

    try {
      await this.waitFor(this.delays.get(chunk.index) ?? 0, signal);
      const failure = this.takeFailure(chunk.index);
      if (failure) throw failure.error;

      onProgress(chunk.lengthBytes, chunk.lengthBytes);
      if (this.received.has(chunk.index)) {
        return { sessionId, chunkIndex: chunk.index, alreadyPresent: true };
      }
      this.received.add(chunk.index);
      this.completedOrder.push(chunk.index);
      return { sessionId, chunkIndex: chunk.index, alreadyPresent: false };
    } finally {
      this.active -= 1;
    }
  }

  preflight(): Promise<MigrationPreflightResponseDto> {
    throw new Error('not used');
  }

  createJob(): Promise<MigrationJobStatusResponseDto> {
    throw new Error('not used');
  }

  getJob(): Promise<MigrationJobStatusResponseDto> {
    throw new Error('not used');
  }

  cancelJob(): Promise<MigrationJobStatusResponseDto> {
    throw new Error('not used');
  }

  retryJob(): Promise<MigrationJobStatusResponseDto> {
    throw new Error('not used');
  }

  createUploadSession(): Promise<MigrationUploadSessionResponseDto> {
    throw new Error('not used');
  }

  getUploadSession(): Promise<MigrationUploadSessionResponseDto> {
    throw new Error('not used');
  }

  completeUpload(): Promise<MigrationSessionStatusDto> {
    throw new Error('not used');
  }

  getExportDownloadUrl(): string {
    throw new Error('not used');
  }

  private takeFailure(chunkIndex: number): QueuedFailure | null {
    const index = this.failures.findIndex(
      (failure) => failure.chunkIndex === undefined || failure.chunkIndex === chunkIndex,
    );
    if (index < 0) return null;
    const failure = this.failures[index];
    failure.times -= 1;
    if (failure.times <= 0) this.failures.splice(index, 1);
    return failure;
  }

  private waitFor(ms: number, signal: AbortSignal): Promise<void> {
    if (signal.aborted) {
      return Promise.reject(new MigrationTransportError('request_aborted', 0, 'aborted'));
    }
    if (ms <= 0) return Promise.resolve();

    return new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        signal.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
      const onAbort = () => {
        clearTimeout(timer);
        reject(new MigrationTransportError('request_aborted', 0, 'aborted'));
      };
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }
}

function stubDigest(): FileDigest {
  return {
    sha256: async () => 'f'.repeat(64),
    // No crypto here: the transport fake does not verify hashes, and native
    // SubtleCrypto promises do not advance under fake timers.
    sha256Chunk: async (blob) => `stub-${blob.size}-hash`,
    fingerprint: async () => 'nostos-fp-v1:stub',
  };
}

function fileOfSize(size: number): File {
  const bytes = new Uint8Array(size);
  for (let index = 0; index < size; index += 1) bytes[index] = (index * 31) % 256;
  return new File([bytes as unknown as BlobPart], 'library.nostos');
}

function sessionFor(
  size: number,
  options: { chunkSize?: number; received?: number[] } = {},
): MigrationSessionStatusDto {
  const chunkSize = options.chunkSize ?? CHUNK;
  const totalChunks = Math.max(1, Math.ceil(size / chunkSize));
  const received = options.received ?? [];
  return {
    sessionId: 'session-1',
    purpose: 'Import',
    state: received.length > 0 ? 'Receiving' : 'Created',
    totalBytes: size,
    chunkSize,
    totalChunks,
    fileIdentity: { totalSizeBytes: size, sha256Checksum: 'a'.repeat(64) },
    receivedChunks: received,
    receivedChunkCount: received.length,
    createdAtUtc: new Date(0).toISOString(),
    expiresAtUtc: new Date(60_000).toISOString(),
  };
}

function engineFor(
  transport: EngineFakeTransport,
  options: { random?: () => number } = {},
): ChunkUploadEngine {
  return new ChunkUploadEngine(transport as never, stubDigest(), {
    random: options.random ?? (() => 0.5),
  });
}

function runFor(
  transport: EngineFakeTransport,
  size: number,
  options: {
    concurrency?: number;
    maxAttempts?: number;
    received?: number[];
    signal?: AbortSignal;
    onProgress?: (progress: TransferProgress) => void;
    random?: () => number;
  } = {},
): ChunkUploadRun {
  return engineFor(transport, { random: options.random }).start({
    jobId: 'job-1',
    session: sessionFor(size, { received: options.received }),
    file: fileOfSize(size),
    concurrency: options.concurrency,
    maxAttempts: options.maxAttempts,
    signal: options.signal,
    onProgress: options.onProgress,
  });
}

describe('ChunkUploadEngine — chunk selection and completion', () => {
  it('uploads a file smaller than one chunk as a single short final chunk', async () => {
    const transport = new EngineFakeTransport();
    const run = runFor(transport, 100);

    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(transport.receivedIndexes).toEqual([0]);
    expect(transport.requestedSlices).toEqual([100]);
  });

  it('uploads an exact multiple of the chunk size', async () => {
    const transport = new EngineFakeTransport();
    const run = runFor(transport, 2 * CHUNK);

    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(transport.receivedIndexes).toEqual([0, 1]);
    expect(transport.requestedSlices).toEqual([CHUNK, CHUNK]);
  });

  it('uploads a short final chunk after exact full chunks', async () => {
    const transport = new EngineFakeTransport();
    const run = runFor(transport, 2 * CHUNK + 100);

    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(transport.receivedIndexes).toEqual([0, 1, 2]);
    expect(transport.requestedSlices).toEqual([CHUNK, CHUNK, 100]);
  });

  it('uploads only chunks without a server receipt', async () => {
    const transport = new EngineFakeTransport();
    transport.received.add(0);
    transport.received.add(2);
    const run = runFor(transport, 3 * CHUNK, { received: [0, 2] });

    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect([...transport.attempts.keys()]).toEqual([1]);
    expect(transport.receivedIndexes).toEqual([0, 1, 2]);
  });

  it('treats an already-present chunk receipt as success', async () => {
    const transport = new EngineFakeTransport();
    transport.received.add(0);
    const run = runFor(transport, 2 * CHUNK, { received: [] });

    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(transport.receivedIndexes).toEqual([0, 1]);
  });
});

describe('ChunkUploadEngine — concurrency and ordering', () => {
  it('never exceeds the configured concurrency and completes out of order', async () => {
    vi.useFakeTimers();
    try {
      const transport = new EngineFakeTransport();
      transport.delays.set(0, 500);
      transport.delays.set(1, 100);
      transport.delays.set(2, 10);
      const run = runFor(transport, 3 * CHUNK, { concurrency: 2 });

      await vi.advanceTimersByTimeAsync(2_000);
      await expect(run.done).resolves.toEqual({ kind: 'completed' });

      expect(transport.maxActive).toBe(2);
      expect(transport.completedOrder).toEqual([1, 2, 0]);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('ChunkUploadEngine — retries and failures', () => {
  afterEach(() => vi.useRealTimers());

  it('retries only the affected chunk after a transient failure', async () => {
    vi.useFakeTimers();
    const transport = new EngineFakeTransport();
    transport.failures.push({
      chunkIndex: 1,
      times: 1,
      error: new MigrationTransportError('network_error', 503, 'unavailable'),
    });
    const run = runFor(transport, 2 * CHUNK, { concurrency: 1 });

    await vi.advanceTimersByTimeAsync(5_000);
    await expect(run.done).resolves.toEqual({ kind: 'completed' });

    expect(transport.attempts.get(0)).toBe(1);
    expect(transport.attempts.get(1)).toBe(2);
  });

  it('waits at least the Retry-After duration before retrying', async () => {
    vi.useFakeTimers();
    const transport = new EngineFakeTransport();
    transport.failures.push({
      chunkIndex: 0,
      times: 1,
      error: new MigrationTransportError('network_error', 429, 'slow down', {
        retryAfterMs: 3_000,
      }),
    });
    const run = runFor(transport, 100, { maxAttempts: 2 });

    await vi.advanceTimersByTimeAsync(2_999);
    expect(transport.attempts.get(0)).toBe(1);

    await vi.advanceTimersByTimeAsync(2);
    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(transport.attempts.get(0)).toBe(2);
  });

  it('keeps the default retry window open for a minute and caps waits at thirty seconds', async () => {
    vi.useFakeTimers();
    const transport = new EngineFakeTransport();
    transport.failures.push({
      chunkIndex: 0,
      times: 7,
      error: new MigrationTransportError('network_error', 502, 'bad gateway'),
    });
    const run = runFor(transport, 100, { random: () => 0.5 });

    // Seven failures: 500 + 1000 + 2000 + 4000 + 8000 + 16000 + 30000.
    await vi.advanceTimersByTimeAsync(61_499);
    expect(transport.attempts.get(0)).toBe(7);

    await vi.advanceTimersByTimeAsync(2);
    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(transport.attempts.get(0)).toBe(8);
  });

  it('fails permanently after exhausting attempts on a transient error', async () => {
    vi.useFakeTimers();
    const transport = new EngineFakeTransport();
    transport.failures.push({
      chunkIndex: 0,
      times: 10,
      error: new MigrationTransportError('network_error', 503, 'unavailable'),
    });
    const run = runFor(transport, 100, { maxAttempts: 3 });
    const expectation = expect(run.done).rejects.toMatchObject({ code: 'network_error' });

    await vi.advanceTimersByTimeAsync(60_000);
    await expectation;
    expect(transport.attempts.get(0)).toBe(3);
  });

  it('never retries a permanent failure', async () => {
    const transport = new EngineFakeTransport();
    transport.failures.push({
      chunkIndex: 0,
      times: 1,
      error: new MigrationTransportError('migration_chunk_conflict', 409, 'conflict'),
    });
    const run = runFor(transport, 100);

    await expect(run.done).rejects.toMatchObject({ code: 'migration_chunk_conflict' });
    expect(transport.attempts.get(0)).toBe(1);
  });

  it('does not retry a hash mismatch without recomputing the digest once', async () => {
    const digestCalls: string[] = [];
    const digest: FileDigest = {
      sha256: async () => 'f'.repeat(64),
      sha256Chunk: async (blob) => {
        digestCalls.push(`${blob.size}`);
        return sha256ChunkHex(blob);
      },
      fingerprint: async () => 'nostos-fp-v1:stub',
    };
    const transport = new EngineFakeTransport();
    transport.failures.push({
      chunkIndex: 0,
      times: 1,
      error: new MigrationTransportError('migration_chunk_hash_mismatch', 422, 'mismatch'),
    });
    const engine = new ChunkUploadEngine(transport as never, digest, { random: () => 0.5 });
    const run = engine.start({ jobId: 'job-1', session: sessionFor(100), file: fileOfSize(100) });

    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(digestCalls).toEqual(['100', '100']);
    expect(transport.attempts.get(0)).toBe(2);
  });
});

describe('ChunkUploadEngine — cancellation, pause and progress', () => {
  afterEach(() => vi.useRealTimers());

  it('cancels in-flight uploads and resolves cancelled', async () => {
    vi.useFakeTimers();
    const transport = new EngineFakeTransport();
    transport.delays.set(0, 1_000);
    transport.delays.set(1, 1_000);
    const run = runFor(transport, 3 * CHUNK, { concurrency: 2 });

    await vi.advanceTimersByTimeAsync(10);
    run.cancel();

    await expect(run.done).resolves.toEqual({ kind: 'cancelled' });
    expect(transport.receivedIndexes).toEqual([]);
  });

  it('pauses scheduling, then resumes and finishes the remaining chunks', async () => {
    vi.useFakeTimers();
    const transport = new EngineFakeTransport();
    transport.delays.set(0, 100);
    transport.delays.set(1, 100);
    transport.delays.set(2, 100);
    const run = runFor(transport, 3 * CHUNK, { concurrency: 1 });

    await vi.advanceTimersByTimeAsync(100);
    const pausing = run.pause();
    await vi.advanceTimersByTimeAsync(200);
    await pausing;
    const pausedAt = transport.receivedIndexes.length;
    await vi.advanceTimersByTimeAsync(1_000);
    expect(transport.receivedIndexes.length).toBe(pausedAt);

    const done = run.resume();
    await vi.advanceTimersByTimeAsync(1_000);
    await expect(done).resolves.toEqual({ kind: 'completed' });
    expect(transport.receivedIndexes).toEqual([0, 1, 2]);
  });

  it('starts progress at previously received bytes and never goes backwards', async () => {
    const transport = new EngineFakeTransport();
    const seen: TransferProgress[] = [];
    const run = runFor(transport, 3 * CHUNK, {
      received: [0, 1],
      concurrency: 1,
      onProgress: (progress) => seen.push({ ...progress }),
    });

    await expect(run.done).resolves.toEqual({ kind: 'completed' });
    expect(seen[0].uploadedBytes).toBe(2 * CHUNK);
    expect(seen.at(-1)?.uploadedBytes).toBe(3 * CHUNK);

    const uploaded = seen.map((progress) => progress.uploadedBytes);
    expect([...uploaded].sort((a, b) => a - b)).toEqual(uploaded);
    expect(seen.at(-1)?.completedChunks).toBe(3);
  });

  it('reports the per-chunk digests it computed over the exact slice bytes', async () => {
    const transport = new EngineFakeTransport();
    const digest: FileDigest = {
      sha256: async () => 'f'.repeat(64),
      sha256Chunk: (blob) => sha256ChunkHex(blob),
      fingerprint: async () => 'nostos-fp-v1:stub',
    };
    const seen: BrowserMigrationChunk[] = [];
    const original = transport.uploadChunk.bind(transport);
    transport.uploadChunk = (jobId, sessionId, chunk, onProgress, signal) => {
      seen.push(chunk);
      return original(jobId, sessionId, chunk, onProgress, signal);
    };

    const engine = new ChunkUploadEngine(transport as never, digest, { random: () => 0.5 });
    const run = engine.start({
      jobId: 'job-1',
      session: sessionFor(2 * CHUNK + 100),
      file: fileOfSize(2 * CHUNK + 100),
      concurrency: 1,
    });
    await expect(run.done).resolves.toEqual({ kind: 'completed' });

    for (const chunk of seen) {
      expect(chunk.sha256).toBe(await sha256ChunkHex(chunk.blob));
      expect(chunk.blob.size).toBe(chunk.lengthBytes);
    }
    expect(seen.map((chunk) => chunk.lengthBytes)).toEqual([CHUNK, CHUNK, 100]);
  });
});
