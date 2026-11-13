import { Sha256, sha256Hex, sha256LengthWords } from './sha256';

const encoder = new TextEncoder();

describe('Sha256', () => {
  it.each([
    ['', 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'],
    ['abc', 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'],
    [
      'abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq',
      '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1',
    ],
  ])('matches the known vector for %j', (input, expected) => {
    expect(sha256Hex(encoder.encode(input))).toBe(expected);
  });

  it('is identical whether data is fed once or in many updates', () => {
    const bytes = new Uint8Array(4096);
    for (let index = 0; index < bytes.length; index += 1) bytes[index] = (index * 31) % 256;

    const oneShot = new Sha256().update(bytes).hex();
    const incremental = new Sha256();
    for (let offset = 0; offset < bytes.length; offset += 7) {
      incremental.update(bytes.subarray(offset, offset + 7));
    }
    expect(incremental.hex()).toBe(oneShot);
  });

  it('matches WebCrypto across every padding boundary (55/56/63/64/65 bytes)', async () => {
    const digests = new Map<string, string>();
    for (const length of [0, 1, 55, 56, 63, 64, 65, 127, 128, 129]) {
      const bytes = new Uint8Array(length).fill(0x61);
      const digest = new Sha256().update(bytes).hex();
      const expected = new Uint8Array(
        await crypto.subtle.digest('SHA-256', bytes as unknown as BufferSource),
      );
      const expectedHex = [...expected].map((b) => b.toString(16).padStart(2, '0')).join('');
      expect(digest, `${length}-byte input`).toBe(expectedHex);
      digests.set(`${length}`, digest);
    }
    expect(new Set(digests.values()).size).toBe(digests.size);
  });

  it('hashes the one-million-a vector across many blocks', () => {
    const sha = new Sha256();
    const block = new Uint8Array(1000).fill(0x61);
    for (let index = 0; index < 1000; index += 1) sha.update(block);
    expect(sha.hex()).toBe(
      'cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0',
    );
  });

  it('matches WebCrypto over generated multi-megabyte data', async () => {
    const bytes = new Uint8Array(3 * 1024 * 1024 + 17);
    for (let index = 0; index < bytes.length; index += 1) bytes[index] = (index * 17) % 251;

    const expected = new Uint8Array(
      await crypto.subtle.digest('SHA-256', bytes as unknown as BufferSource),
    );
    const expectedHex = [...expected].map((b) => b.toString(16).padStart(2, '0')).join('');
    expect(sha256Hex(bytes)).toBe(expectedHex);
  });

  it('encodes the 64-bit length field correctly at the 2^29- and 2^32-byte boundaries', () => {
    // 55/56 are the padding transitions; 2^29 and 2^32 are where the high
    // 32-bit word of the bit length first becomes 1 and 8.
    expect(sha256LengthWords(55)).toEqual({ highBits: 0, lowBits: 440 });
    expect(sha256LengthWords(56)).toEqual({ highBits: 0, lowBits: 448 });
    expect(sha256LengthWords(0x20000000 - 1)).toEqual({
      highBits: 0,
      lowBits: (0x20000000 - 1) * 8,
    });
    expect(sha256LengthWords(0x20000000)).toEqual({ highBits: 1, lowBits: 0 });
    expect(sha256LengthWords(0x20000000 + 1)).toEqual({ highBits: 1, lowBits: 8 });
    expect(sha256LengthWords(0x100000000)).toEqual({ highBits: 8, lowBits: 0 });
    expect(sha256LengthWords(512 * 1024 * 1024 * 1024)).toEqual({
      highBits: 1024,
      lowBits: 0,
    });
  });

  it('finalises a synthetic byte count through the length seam at both boundaries', () => {
    const below = new Sha256().finalizeWithByteCount(0x20000000 - 1);
    const at = new Sha256().finalizeWithByteCount(0x20000000);
    const above = new Sha256().finalizeWithByteCount(0x20000000 + 1);
    const fourGiB = new Sha256().finalizeWithByteCount(0x100000000);

    for (const digest of [below, at, above, fourGiB]) expect(digest).toHaveLength(64);
    expect(new Set([below, at, above, fourGiB]).size).toBe(4);
    expect(() => new Sha256().update(encoder.encode('x')).finalizeWithByteCount(1)).toThrow();
  });

  it('returns an immutable copy from digest()', () => {
    const sha = new Sha256().update(encoder.encode('abc'));
    const first = sha.digest();
    first.fill(0);
    expect(sha.hex()).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  });

  it('rejects updates after finalization', () => {
    const sha = new Sha256().update(encoder.encode('abc'));
    sha.digest();
    expect(() => sha.update(encoder.encode('more'))).toThrow();
  });
});
