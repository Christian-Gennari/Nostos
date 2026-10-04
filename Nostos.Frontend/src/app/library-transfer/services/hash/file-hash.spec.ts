import { TransferCancelledError } from '../../models/library-transfer.models';
import { hashBlobIncrementally } from './file-hash';

const encoder = new TextEncoder();

function blobOf(bytes: Uint8Array): Blob {
  return new Blob([bytes as unknown as BlobPart]);
}

function generated(size: number, seed = 17): Uint8Array {
  const bytes = new Uint8Array(size);
  for (let index = 0; index < size; index += 1) bytes[index] = (index * seed + 3) % 251;
  return bytes;
}

/**
 * A File double that records every `slice()` and refuses whole-file reads.
 * Bounded-memory behaviour is asserted against this, not by reading source.
 */
class RecordingBlob {
  readonly requested: Array<{ start: number; end: number }> = [];
  readonly sliceCount = () => this.requested.length;

  constructor(private readonly bytes: Uint8Array) {}

  get size(): number {
    return this.bytes.length;
  }

  slice(start: number, end: number): Blob {
    this.requested.push({ start, end });
    return blobOf(this.bytes.subarray(start, end));
  }

  arrayBuffer(): Promise<ArrayBuffer> {
    throw new Error('The whole file must never be read at once.');
  }
}

describe('hashBlobIncrementally', () => {
  it('hashes the empty file', async () => {
    await expect(hashBlobIncrementally(blobOf(new Uint8Array(0)))).resolves.toBe(
      'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
    );
  });

  it('hashes "abc"', async () => {
    await expect(hashBlobIncrementally(blobOf(encoder.encode('abc')))).resolves.toBe(
      'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
    );
  });

  it.each([
    [16, 64],
    [16, 63],
    [16, 65],
    [64, 64],
    [64, 128],
    [1000, 64],
    [1000, 1000],
  ])('matches WebCrypto for %i bytes read in %i-byte blocks', async (size, block) => {
    const bytes = generated(size);
    const file = blobOf(bytes);
    const expected = new Uint8Array(
      await crypto.subtle.digest('SHA-256', bytes as unknown as BufferSource),
    );
    const expectedHex = [...expected].map((b) => b.toString(16).padStart(2, '0')).join('');
    await expect(hashBlobIncrementally(file, { readBlockBytes: block })).resolves.toBe(expectedHex);
  });

  it('never requests more than one bounded slice and never the whole file', async () => {
    const size = 5 * 1024 * 1024 + 123;
    const recording = new RecordingBlob(generated(size));
    const digest = await hashBlobIncrementally(recording as unknown as Blob, {
      readBlockBytes: 1024 * 1024,
    });

    expect(digest).toHaveLength(64);
    expect(recording.requested.length).toBe(6);
    let expectedStart = 0;
    for (const { start, end } of recording.requested) {
      expect(start).toBe(expectedStart);
      expect(end - start).toBeLessThanOrEqual(1024 * 1024);
      expectedStart = end;
    }
    expect(expectedStart).toBe(size);
  });

  it('equals a one-shot digest for a multi-megabyte generated file', async () => {
    const bytes = generated(5 * 1024 * 1024 + 7, 29);
    const expected = new Uint8Array(
      await crypto.subtle.digest('SHA-256', bytes as unknown as BufferSource),
    );
    const expectedHex = [...expected].map((b) => b.toString(16).padStart(2, '0')).join('');
    await expect(
      hashBlobIncrementally(blobOf(bytes), { readBlockBytes: 512 * 1024 }),
    ).resolves.toBe(expectedHex);
  });

  it('reports monotonic progress ending at the total size', async () => {
    const size = 300;
    const reads: number[] = [];
    await hashBlobIncrementally(blobOf(generated(size)), {
      readBlockBytes: 64,
      onProgress: (bytesRead, totalBytes) => {
        expect(totalBytes).toBe(size);
        reads.push(bytesRead);
      },
    });

    expect(reads.at(-1)).toBe(size);
    expect([...reads].sort((a, b) => a - b)).toEqual(reads);
  });

  it('cancels before reading when the signal is already aborted', async () => {
    const controller = new AbortController();
    controller.abort();
    const recording = new RecordingBlob(generated(128));
    await expect(
      hashBlobIncrementally(recording as unknown as Blob, { signal: controller.signal }),
    ).rejects.toBeInstanceOf(TransferCancelledError);
    expect(recording.requested).toHaveLength(0);
  });

  it('cancels mid-read and stops requesting slices', async () => {
    const controller = new AbortController();
    const recording = new RecordingBlob(generated(1024));
    const promise = hashBlobIncrementally(recording as unknown as Blob, {
      readBlockBytes: 64,
      signal: controller.signal,
      onProgress: (bytesRead) => {
        if (bytesRead >= 128) controller.abort();
      },
    });

    await expect(promise).rejects.toBeInstanceOf(TransferCancelledError);
    expect(recording.requested.length).toBeLessThanOrEqual(3);
  });
});
