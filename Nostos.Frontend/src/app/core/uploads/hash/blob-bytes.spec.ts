import { describe, expect, it } from 'vitest';

import { readBlobBytes, readBlobRange } from './blob-bytes';

describe('readBlobBytes', () => {
  it('reads every byte of a blob', async () => {
    const bytes = await readBlobBytes(new Blob([new Uint8Array([1, 2, 3, 4])]));

    expect([...bytes]).toEqual([1, 2, 3, 4]);
  });

  it('falls back to FileReader where the blob cannot read itself', async () => {
    const blob = new Blob([new Uint8Array([9, 8, 7])]);
    Object.defineProperty(blob, 'arrayBuffer', { value: undefined });

    const bytes = await readBlobBytes(blob);

    expect([...bytes]).toEqual([9, 8, 7]);
  });
});

describe('readBlobRange', () => {
  it('reads exactly the requested range', async () => {
    const blob = new Blob([new Uint8Array([0, 1, 2, 3, 4, 5])]);

    expect([...(await readBlobRange(blob, 2, 3))]).toEqual([2, 3, 4]);
    expect([...(await readBlobRange(blob, 0, 6))]).toEqual([0, 1, 2, 3, 4, 5]);
  });

  it.each([
    ['a negative offset', -1, 2],
    ['a negative length', 0, -1],
    ['a range past the end', 4, 4],
  ])('refuses %s rather than returning a short read', async (_case, offset, length) => {
    const blob = new Blob([new Uint8Array([0, 1, 2, 3])]);

    await expect(readBlobRange(blob, offset, length)).rejects.toBeInstanceOf(RangeError);
  });

  it('reads an empty range at the end', async () => {
    const blob = new Blob([new Uint8Array([1, 2])]);

    expect([...(await readBlobRange(blob, 2, 0))]).toEqual([]);
  });
});
