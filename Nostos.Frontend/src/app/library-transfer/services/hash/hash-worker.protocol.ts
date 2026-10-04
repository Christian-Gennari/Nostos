/**
 * Message protocol between the main thread and the hashing Web Worker.
 *
 * The worker owns the whole-file read loop, so the main thread never holds
 * more than one request descriptor and receives only progress/result events.
 * `createHashWorkerResponder` is exported separately from the worker entry so
 * its behaviour can be exercised in unit tests without a real worker thread.
 */

import { TransferCancelledError } from '../../models/library-transfer.models';
import { hashBlobIncrementally } from './file-hash';

export interface HashWorkerStartRequest {
  type: 'hash';
  requestId: number;
  blob: Blob;
  readBlockBytes: number;
}

export interface HashWorkerCancelRequest {
  type: 'cancel';
  requestId: number;
}

export type HashWorkerInbound = HashWorkerStartRequest | HashWorkerCancelRequest;

export interface HashWorkerProgressEvent {
  type: 'progress';
  requestId: number;
  bytesRead: number;
  totalBytes: number;
}

export interface HashWorkerResultEvent {
  type: 'result';
  requestId: number;
  sha256: string;
}

export interface HashWorkerErrorEvent {
  type: 'error';
  requestId: number;
  cancelled: boolean;
  message: string;
}

export type HashWorkerOutbound =
  | HashWorkerProgressEvent
  | HashWorkerResultEvent
  | HashWorkerErrorEvent;

export type HashWorkerPost = (event: HashWorkerOutbound) => void;

export function createHashWorkerResponder(
  post: HashWorkerPost,
  hash: typeof hashBlobIncrementally = hashBlobIncrementally,
): (message: HashWorkerInbound) => void {
  const inFlight = new Map<number, AbortController>();

  return (message: HashWorkerInbound): void => {
    if (message.type === 'cancel') {
      inFlight.get(message.requestId)?.abort();
      return;
    }

    const controller = new AbortController();
    inFlight.set(message.requestId, controller);

    void hash(message.blob, {
      signal: controller.signal,
      readBlockBytes: message.readBlockBytes,
      onProgress: (bytesRead, totalBytes) =>
        post({ type: 'progress', requestId: message.requestId, bytesRead, totalBytes }),
    })
      .then((sha256) => post({ type: 'result', requestId: message.requestId, sha256 }))
      .catch((error: unknown) => {
        post({
          type: 'error',
          requestId: message.requestId,
          cancelled: error instanceof TransferCancelledError,
          message: error instanceof Error ? error.message : 'Hashing failed.',
        });
      })
      .finally(() => inFlight.delete(message.requestId));
  };
}
