/// <reference lib="webworker" />

/**
 * Web Worker entry for whole-file hashing. All logic lives in
 * `hash-worker.protocol.ts` and `file-hash.ts`; this file is only the worker
 * global wiring, so the algorithm stays unit-testable.
 */

import { createHashWorkerResponder, HashWorkerInbound } from './hash-worker.protocol';

const scope = self as unknown as DedicatedWorkerGlobalScope;
const respond = createHashWorkerResponder((event) => scope.postMessage(event));

scope.onmessage = (event: MessageEvent<HashWorkerInbound>) => respond(event.data);
