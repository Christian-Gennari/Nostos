/**
 * Small incremental SHA-256 (FIPS 180-4) used for the whole-file archive
 * digest, where WebCrypto's one-shot `digest()` cannot work: the archive may
 * be hundreds of GiB and must be hashed over bounded read blocks.
 *
 * This is deliberately dependency-free. No shipped package in this repo
 * provides an incremental SHA-256, and plan §11.3 requires the algorithm to be
 * audited against known vectors plus WebCrypto parity for manageable fixtures
 * (see `sha256.spec.ts`).
 */

const K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

const INITIAL_STATE = new Uint32Array([
  0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
]);

function rotr(value: number, bits: number): number {
  return (value >>> bits) | (value << (32 - bits));
}

/**
 * Bit length of the message as the two big-endian words SHA-256 appends.
 *
 * `bytesHashed < 2^53` (the migration contract caps archives at 512 GiB), so
 * splitting on 2^29 bytes keeps both words exact in 32-bit math. Exported so
 * the 2^29/2^32 length boundaries can be asserted without hashing gigabytes.
 */
export function sha256LengthWords(bytesHashed: number): { highBits: number; lowBits: number } {
  return {
    highBits: Math.floor(bytesHashed / 0x20000000),
    lowBits: (bytesHashed % 0x20000000) * 8,
  };
}

export class Sha256 {
  private readonly state = Uint32Array.from(INITIAL_STATE);
  private readonly buffer = new Uint8Array(64);
  private readonly schedule = new Uint32Array(64);
  private bufferLength = 0;
  private bytesHashed = 0;
  private finalized: Uint8Array | null = null;

  /** Number of bytes fed so far. */
  get bytesRead(): number {
    return this.bytesHashed;
  }

  update(data: Uint8Array): this {
    if (this.finalized) throw new Error('SHA-256 digest is finalized.');

    this.bytesHashed += data.length;
    let offset = 0;

    if (this.bufferLength > 0) {
      const take = Math.min(64 - this.bufferLength, data.length);
      this.buffer.set(data.subarray(0, take), this.bufferLength);
      this.bufferLength += take;
      offset = take;
      if (this.bufferLength === 64) {
        this.processBlock(this.buffer, 0);
        this.bufferLength = 0;
      }
    }

    const fullEnd = data.length - ((data.length - offset) % 64);
    for (let index = offset; index < fullEnd; index += 64) this.processBlock(data, index);

    const remainder = data.length - fullEnd;
    if (remainder > 0) {
      this.buffer.set(data.subarray(fullEnd), 0);
      this.bufferLength = remainder;
    }

    return this;
  }

  /** Finalises and returns the 32-byte digest. Idempotent; callers get a copy. */
  digest(): Uint8Array {
    if (this.finalized) return Uint8Array.from(this.finalized);

    const { highBits, lowBits } = sha256LengthWords(this.bytesHashed);

    const padLength = this.bufferLength < 56 ? 64 : 128;
    const padding = new Uint8Array(padLength);
    padding.set(this.buffer.subarray(0, this.bufferLength));
    padding[this.bufferLength] = 0x80;
    padding[padLength - 8] = (highBits >>> 24) & 0xff;
    padding[padLength - 7] = (highBits >>> 16) & 0xff;
    padding[padLength - 6] = (highBits >>> 8) & 0xff;
    padding[padLength - 5] = highBits & 0xff;
    padding[padLength - 4] = (lowBits >>> 24) & 0xff;
    padding[padLength - 3] = (lowBits >>> 16) & 0xff;
    padding[padLength - 2] = (lowBits >>> 8) & 0xff;
    padding[padLength - 1] = lowBits & 0xff;

    for (let index = 0; index < padLength; index += 64) this.processBlock(padding, index);

    const digest = new Uint8Array(32);
    for (let index = 0; index < 8; index += 1) {
      digest[index * 4] = (this.state[index] >>> 24) & 0xff;
      digest[index * 4 + 1] = (this.state[index] >>> 16) & 0xff;
      digest[index * 4 + 2] = (this.state[index] >>> 8) & 0xff;
      digest[index * 4 + 3] = this.state[index] & 0xff;
    }

    this.finalized = digest;
    return Uint8Array.from(digest);
  }

  hex(): string {
    return toHex(this.digest());
  }

  /**
   * Test seam: finalises a fresh instance as if `byteCount` bytes had been fed,
   * without running any rounds. Used to prove the 64-bit length field at the
   * 2^29- and 2^32-byte boundaries without allocating gigabytes.
   */
  finalizeWithByteCount(byteCount: number): string {
    if (this.bytesHashed !== 0 || this.bufferLength !== 0 || this.finalized) {
      throw new Error('finalizeWithByteCount requires a fresh, unfinalized Sha256.');
    }
    this.bytesHashed = byteCount;
    return this.hex();
  }

  private processBlock(data: Uint8Array, offset: number): void {
    const w = this.schedule;
    for (let index = 0; index < 16; index += 1) {
      const base = offset + index * 4;
      w[index] =
        ((data[base] << 24) | (data[base + 1] << 16) | (data[base + 2] << 8) | data[base + 3]) >>> 0;
    }
    for (let index = 16; index < 64; index += 1) {
      const w15 = w[index - 15];
      const w2 = w[index - 2];
      const s0 = rotr(w15, 7) ^ rotr(w15, 18) ^ (w15 >>> 3);
      const s1 = rotr(w2, 17) ^ rotr(w2, 19) ^ (w2 >>> 10);
      w[index] = (w[index - 16] + s0 + w[index - 7] + s1) | 0;
    }

    let a = this.state[0];
    let b = this.state[1];
    let c = this.state[2];
    let d = this.state[3];
    let e = this.state[4];
    let f = this.state[5];
    let g = this.state[6];
    let h = this.state[7];

    for (let index = 0; index < 64; index += 1) {
      const s1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
      const ch = (e & f) ^ (~e & g);
      const temp1 = (h + s1 + ch + K[index] + w[index]) | 0;
      const s0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
      const maj = (a & b) ^ (a & c) ^ (b & c);
      const temp2 = (s0 + maj) | 0;

      h = g;
      g = f;
      f = e;
      e = (d + temp1) | 0;
      d = c;
      c = b;
      b = a;
      a = (temp1 + temp2) | 0;
    }

    this.state[0] = (this.state[0] + a) | 0;
    this.state[1] = (this.state[1] + b) | 0;
    this.state[2] = (this.state[2] + c) | 0;
    this.state[3] = (this.state[3] + d) | 0;
    this.state[4] = (this.state[4] + e) | 0;
    this.state[5] = (this.state[5] + f) | 0;
    this.state[6] = (this.state[6] + g) | 0;
    this.state[7] = (this.state[7] + h) | 0;
  }
}

export function toHex(bytes: Uint8Array): string {
  let hex = '';
  for (const byte of bytes) hex += byte.toString(16).padStart(2, '0');
  return hex;
}

/** One-shot convenience over a bounded buffer. */
export function sha256Hex(bytes: Uint8Array): string {
  return new Sha256().update(bytes).hex();
}
