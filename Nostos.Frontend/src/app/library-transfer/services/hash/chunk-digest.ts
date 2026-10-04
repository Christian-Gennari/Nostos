/**
 * Per-chunk SHA-256 (plan §12): a chunk is at most the session chunk size
 * (≤ 64 MiB), so a one-shot WebCrypto digest over the slice's buffer is
 * bounded and preferred; the incremental fallback keeps the result identical
 * where SubtleCrypto is unavailable.
 */

import { readBlobBytes } from './blob-bytes';
import { Sha256, toHex } from './sha256';

export async function sha256ChunkHex(blob: Blob): Promise<string> {
  const bytes = await readBlobBytes(blob);
  const subtle = (globalThis.crypto as Crypto | undefined)?.subtle;
  if (subtle?.digest) {
    const digest = await subtle.digest('SHA-256', bytes as unknown as BufferSource);
    return toHex(new Uint8Array(digest));
  }
  return new Sha256().update(bytes).hex();
}
