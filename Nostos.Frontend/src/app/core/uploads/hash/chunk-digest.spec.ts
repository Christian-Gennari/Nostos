import { afterEach, describe, expect, it, vi } from 'vitest';

import { sha256ChunkHex } from './chunk-digest';

const ABC = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad';
const EMPTY = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';

describe('sha256ChunkHex', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('hashes a chunk to lower-case hex', async () => {
    await expect(sha256ChunkHex(new Blob(['abc']))).resolves.toBe(ABC);
  });

  it('hashes an empty chunk', async () => {
    await expect(sha256ChunkHex(new Blob([]))).resolves.toBe(EMPTY);
  });

  it('gives the same answer where SubtleCrypto is unavailable', async () => {
    vi.stubGlobal('crypto', {});

    await expect(sha256ChunkHex(new Blob(['abc']))).resolves.toBe(ABC);
  });
});
