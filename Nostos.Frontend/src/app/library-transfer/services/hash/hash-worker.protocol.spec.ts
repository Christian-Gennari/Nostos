import { TransferCancelledError } from '../../models/library-transfer.models';
import {
  HashWorkerInbound,
  HashWorkerOutbound,
  createHashWorkerResponder,
} from './hash-worker.protocol';

const encoder = new TextEncoder();
const ABC_SHA256 = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad';

function blobOf(text: string): Blob {
  return new Blob([encoder.encode(text) as unknown as BlobPart]);
}

function collect(): { events: HashWorkerOutbound[]; post: (event: HashWorkerOutbound) => void } {
  const events: HashWorkerOutbound[] = [];
  return { events, post: (event) => events.push(event) };
}

async function waitFor(predicate: () => boolean, timeoutMs = 1_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error('Timed out waiting for worker events.');
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
}

describe('hash worker protocol', () => {
  it('posts progress and the final digest for a request', async () => {
    const { events, post } = collect();
    const progressHash = async (
      _blob: Blob,
      options: { onProgress?: (read: number, total: number) => void },
    ): Promise<string> => {
      options.onProgress?.(1, 3);
      options.onProgress?.(2, 3);
      options.onProgress?.(3, 3);
      return ABC_SHA256;
    };
    const respond = createHashWorkerResponder(post, progressHash as never);
    respond({ type: 'hash', requestId: 7, blob: blobOf('abc'), readBlockBytes: 1 });

    await waitFor(() => events.some((event) => event.type === 'result'));

    const progress = events.filter((event) => event.type === 'progress');
    expect(progress).toHaveLength(3);
    expect(progress.at(-1)).toMatchObject({ bytesRead: 3, totalBytes: 3 });
    expect(events.at(-1)).toEqual({ type: 'result', requestId: 7, sha256: ABC_SHA256 });
  });

  it('cancels only the requested hash and reports it as cancelled', async () => {
    const { events, post } = collect();
    const slowHash = async (
      _blob: Blob,
      options: { signal?: AbortSignal; onProgress?: (read: number, total: number) => void },
    ): Promise<string> => {
      options.onProgress?.(1, 10);
      await new Promise((resolve) => setTimeout(resolve, 20));
      if (options.signal?.aborted) throw new TransferCancelledError('cancelled');
      return 'ff'.repeat(32);
    };

    const respond = createHashWorkerResponder(post, slowHash as never);
    respond({ type: 'hash', requestId: 1, blob: blobOf('abc'), readBlockBytes: 1 });
    respond({ type: 'hash', requestId: 2, blob: blobOf('abc'), readBlockBytes: 1 });
    await waitFor(() => events.filter((event) => event.type === 'progress').length >= 2);
    respond({ type: 'cancel', requestId: 2 });

    await waitFor(
      () =>
        events.some((event) => event.type === 'result' && event.requestId === 1) &&
        events.some(
          (event) => event.type === 'error' && event.requestId === 2 && event.cancelled,
        ),
    );
  });

  it('reports non-cancellation failures as errors', async () => {
    const { events, post } = collect();
    const failingHash = async (): Promise<string> => {
      throw new Error('disk exploded');
    };
    const respond = createHashWorkerResponder(post, failingHash as never);
    respond({ type: 'hash', requestId: 3, blob: blobOf('abc'), readBlockBytes: 1 });
    await waitFor(() => events.length > 0);

    expect(events.at(-1)).toEqual({
      type: 'error',
      requestId: 3,
      cancelled: false,
      message: 'disk exploded',
    });
  });

  it('ignores cancels for unknown requests', () => {
    const { events, post } = collect();
    const respond = createHashWorkerResponder(post);
    const cancel: HashWorkerInbound = { type: 'cancel', requestId: 404 };
    expect(() => respond(cancel)).not.toThrow();
    expect(events).toHaveLength(0);
  });
});
