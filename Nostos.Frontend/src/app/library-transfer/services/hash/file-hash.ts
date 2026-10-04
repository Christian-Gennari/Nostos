/**
 * Bounded whole-file hashing (plan §11.3, §52).
 *
 * The file is read sequentially in fixed-size slices; only one slice buffer is
 * alive at a time and `File`/`Blob` itself is never materialised. The digest is
 * computed incrementally, so a 100 GiB archive costs one read block of memory,
 * not one archive.
 */

import { TransferCancelledError } from '../../models/library-transfer.models';
import { HASH_READ_BLOCK_BYTES } from '../../models/library-transfer.models';
import { readBlobBytes } from './blob-bytes';
import { Sha256 } from './sha256';

export interface HashBlobOptions {
  signal?: AbortSignal;
  readBlockBytes?: number;
  onProgress?: (bytesRead: number, totalBytes: number) => void;
}

export async function hashBlobIncrementally(
  blob: Blob,
  options: HashBlobOptions = {},
): Promise<string> {
  const totalBytes = blob.size;
  const readBlockBytes = Math.max(1, options.readBlockBytes ?? HASH_READ_BLOCK_BYTES);
  const sha = new Sha256();

  for (let offset = 0; offset < totalBytes; offset += readBlockBytes) {
    if (options.signal?.aborted) throw new TransferCancelledError('Hashing was cancelled.');

    const end = Math.min(offset + readBlockBytes, totalBytes);
    const bytes = await readBlobBytes(blob.slice(offset, end));
    sha.update(bytes);
    options.onProgress?.(end, totalBytes);
  }

  if (options.signal?.aborted) throw new TransferCancelledError('Hashing was cancelled.');

  options.onProgress?.(totalBytes, totalBytes);
  return sha.hex();
}
