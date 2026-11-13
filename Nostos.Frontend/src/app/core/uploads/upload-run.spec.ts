import { ChunkPartTransport } from './chunk-upload-engine';
import { FileDigest, TransferCancelledError } from './upload.models';
import { digestForUpload, uploadMissingParts } from './upload-run';
import { UploadProgress } from './upload-progress';

function digestOf(sha256: FileDigest['sha256'] = async () => 'a'.repeat(64)): FileDigest {
  return {
    sha256,
    sha256Chunk: async () => 'b'.repeat(64),
    fingerprint: async () => 'nostos-fp-v1:x',
  };
}

describe('digestForUpload', () => {
  it('reports preparing progress once per whole percent', async () => {
    const frames: UploadProgress[] = [];
    const digest = digestOf(async (_file, options) => {
      options?.onProgress?.(512, 1024);
      options?.onProgress?.(512, 1024);
      options?.onProgress?.(1024, 1024);
      return 'a'.repeat(64);
    });

    const sha256 = await digestForUpload(digest, new Blob([new Uint8Array(8)]), {
      onProgress: (progress) => frames.push(progress),
    });

    expect(sha256).toBe('a'.repeat(64));
    expect(frames.map((frame) => frame.phase)).toEqual(['preparing', 'preparing', 'preparing']);
    expect(frames.map((frame) => frame.percent)).toEqual([0, 50, 100]);
  });

  it('stops rather than creating a session after the caller aborted', async () => {
    const controller = new AbortController();
    controller.abort();

    await expect(
      digestForUpload(digestOf(), new Blob([new Uint8Array(8)]), { signal: controller.signal }),
    ).rejects.toBeInstanceOf(TransferCancelledError);
  });
});

describe('uploadMissingParts', () => {
  const file = new Blob([new Uint8Array(8)]);
  const session = {
    sessionId: 'session-1',
    totalBytes: 8,
    chunkSize: 4,
    totalChunks: 2,
    receivedChunks: [0],
  };

  it('sends only the missing part and holds at 99%', async () => {
    const sent: number[] = [];
    const frames: UploadProgress[] = [];
    const partTransport: ChunkPartTransport = {
      async uploadPart(_sessionId, chunk, onProgress) {
        sent.push(chunk.index);
        onProgress(chunk.lengthBytes, chunk.lengthBytes);
        return { partIndex: chunk.index };
      },
    };

    await uploadMissingParts({
      digest: digestOf(),
      file,
      session,
      partTransport,
      hashMismatchCode: 'book_upload_part_hash_mismatch',
      onProgress: (progress) => frames.push(progress),
    });

    expect(sent).toEqual([1]);
    const last = frames.at(-1)!;
    expect(last).toMatchObject({
      phase: 'uploading',
      percent: 99,
      uploadedBytes: 8,
      movedBytes: 8,
      totalBytes: 8,
    });
  });

  it('stops as a cancellation', async () => {
    const controller = new AbortController();
    controller.abort();
    const partTransport: ChunkPartTransport = {
      uploadPart: async () => ({}),
    };

    await expect(
      uploadMissingParts({
        digest: digestOf(),
        file,
        session,
        partTransport,
        hashMismatchCode: 'book_upload_part_hash_mismatch',
        signal: controller.signal,
      }),
    ).rejects.toBeInstanceOf(TransferCancelledError);
  });
});
