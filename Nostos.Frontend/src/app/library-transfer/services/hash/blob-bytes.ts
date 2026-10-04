/**
 * Bounded blob reading that works in browsers and in the jsdom test
 * environment.
 *
 * `Blob.arrayBuffer()` is the fast path; jsdom (and some older WebKit) does not
 * implement it, so `FileReader` — which exists in both windows and workers —
 * is the portable fallback. The caller decides the slice size, so this helper
 * never materialises more than one bounded buffer.
 */

export async function readBlobBytes(blob: Blob): Promise<Uint8Array> {
  const native = (blob as { arrayBuffer?: () => Promise<ArrayBuffer> }).arrayBuffer;
  if (typeof native === 'function') {
    return new Uint8Array(await native.call(blob));
  }

  return new Promise<Uint8Array>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(new Uint8Array(reader.result as ArrayBuffer));
    reader.onerror = () => reject(reader.error ?? new Error('Failed to read blob bytes.'));
    reader.onabort = () => reject(new Error('Blob read was aborted.'));
    reader.readAsArrayBuffer(blob);
  });
}

/** Reads an exact range and fails when the blob is too short. */
export async function readBlobRange(
  blob: Blob,
  offset: number,
  length: number,
): Promise<Uint8Array> {
  if (offset < 0 || length < 0 || offset + length > blob.size) {
    throw new RangeError(
      `Requested blob range ${offset}+${length} is outside a ${blob.size}-byte blob.`,
    );
  }
  const bytes = await readBlobBytes(blob.slice(offset, offset + length));
  if (bytes.length !== length) {
    throw new Error(`Expected ${length} bytes at ${offset}, read ${bytes.length}.`);
  }
  return bytes;
}
