import { TestBed } from '@angular/core/testing';

import { TransferCancelledError } from '../models/library-transfer.models';
import { FileDigestService } from './file-digest.service';
import { HASH_WORKER_FACTORY } from './hash/hash-worker';
import {
  HashWorkerInbound,
  HashWorkerOutbound,
  createHashWorkerResponder,
} from './hash/hash-worker.protocol';

const encoder = new TextEncoder();
const ABC_SHA256 = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad';

function blobOf(bytes: Uint8Array): Blob {
  return new Blob([bytes as unknown as BlobPart]);
}

/** In-process stand-in for a real Worker, running the real protocol responder. */
function createFakeWorker(): {
  worker: Worker;
  terminated: () => number;
  posted: () => number;
} {
  let terminated = 0;
  let posted = 0;
  const worker = {
    onmessage: null as ((event: MessageEvent) => void) | null,
    onerror: null as ((event: unknown) => void) | null,
    postMessage: (message: HashWorkerInbound) => {
      posted += 1;
      const deliver = (event: HashWorkerOutbound) => {
        queueMicrotask(() => worker.onmessage?.({ data: event } as MessageEvent));
      };
      const respond = createHashWorkerResponder(deliver);
      queueMicrotask(() => respond(message));
    },
    terminate: () => {
      terminated += 1;
    },
  };
  return {
    worker: worker as unknown as Worker,
    terminated: () => terminated,
    posted: () => posted,
  };
}

function configure(factory: () => Worker | null): FileDigestService {
  TestBed.configureTestingModule({
    providers: [{ provide: HASH_WORKER_FACTORY, useValue: factory }],
  });
  return TestBed.inject(FileDigestService);
}

describe('FileDigestService', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('hashes through the worker and reports progress', async () => {
    const fake = createFakeWorker();
    const service = configure(() => fake.worker);
    const progress: number[] = [];

    const digest = await service.sha256(blobOf(encoder.encode('abc')), {
      readBlockBytes: 1,
      onProgress: (bytesRead) => progress.push(bytesRead),
    });

    expect(digest).toBe(ABC_SHA256);
    expect(fake.posted()).toBe(1);
    expect(progress.at(-1)).toBe(3);
  });

  it('reuses one worker across hashes and terminates it on demand', async () => {
    const fake = createFakeWorker();
    const service = configure(() => fake.worker);

    await service.sha256(blobOf(encoder.encode('abc')));
    await service.sha256(blobOf(encoder.encode('abc')));
    expect(fake.posted()).toBe(2);

    service.terminate();
    expect(fake.terminated()).toBe(1);
  });

  it('cancels a worker request through the signal', async () => {
    const fake = createFakeWorker();
    const service = configure(() => fake.worker);
    const controller = new AbortController();

    const promise = service.sha256(blobOf(new Uint8Array(4096).fill(1)), {
      readBlockBytes: 1,
      signal: controller.signal,
      onProgress: () => controller.abort(),
    });

    await expect(promise).rejects.toBeInstanceOf(TransferCancelledError);
  });

  it('falls back to bounded in-process hashing when no worker exists', async () => {
    const service = configure(() => null);
    await expect(service.sha256(blobOf(encoder.encode('abc')))).resolves.toBe(ABC_SHA256);
  });

  it('falls back in-process when the worker postMessage fails', async () => {
    const broken = {
      onmessage: null,
      onerror: null,
      postMessage: () => {
        throw new Error('not cloneable');
      },
      terminate: () => undefined,
    } as unknown as Worker;
    const service = configure(() => broken);

    await expect(service.sha256(blobOf(encoder.encode('abc')))).resolves.toBe(ABC_SHA256);
  });

  it('matches WebCrypto for per-chunk digests', async () => {
    const service = configure(() => null);
    const bytes = new Uint8Array(256 * 1024 + 5);
    for (let index = 0; index < bytes.length; index += 1) bytes[index] = index % 256;

    const expected = new Uint8Array(
      await crypto.subtle.digest('SHA-256', bytes as unknown as BufferSource),
    );
    const expectedHex = [...expected].map((b) => b.toString(16).padStart(2, '0')).join('');
    await expect(service.sha256Chunk(blobOf(bytes))).resolves.toBe(expectedHex);
  });

  it('produces a stable versioned fingerprint that changes with content', async () => {
    const service = configure(() => null);
    const first = await service.fingerprint(blobOf(encoder.encode('nostos archive A')));
    const again = await service.fingerprint(blobOf(encoder.encode('nostos archive A')));
    const different = await service.fingerprint(blobOf(encoder.encode('nostos archive B')));

    expect(first).toMatch(/^nostos-fp-v1:[0-9a-f]{64}$/);
    expect(again).toBe(first);
    expect(different).not.toBe(first);
  });

  it('fingerprints a file larger than its 64 KiB samples without reading it whole', async () => {
    const service = configure(() => null);
    const requests: Array<{ start: number; end: number }> = [];
    const inner = new Uint8Array(200 * 1024).fill(7);
    const sparse = {
      size: inner.length,
      slice: (start: number, end: number) => {
        requests.push({ start, end });
        return blobOf(inner.subarray(start, end));
      },
    } as unknown as Blob;

    const fingerprint = await service.fingerprint(sparse);
    expect(fingerprint).toMatch(/^nostos-fp-v1:[0-9a-f]{64}$/);
    expect(requests).toEqual([
      { start: 0, end: 64 * 1024 },
      { start: inner.length - 64 * 1024, end: inner.length },
    ]);
  });
});
