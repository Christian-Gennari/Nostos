/**
 * Worker construction seam.
 *
 * The default factory returns null where `Worker` is unavailable (SSR, jsdom
 * unit tests); the digest service then falls back to in-process bounded
 * hashing with identical semantics. Tests inject `HASH_WORKER_FACTORY` to
 * drive the real protocol without a worker thread.
 */

import { InjectionToken } from '@angular/core';

export type HashWorkerFactory = () => Worker | null;

export function createBrowserHashWorker(): Worker | null {
  if (typeof Worker === 'undefined') return null;
  return new Worker(new URL('./file-hash.worker', import.meta.url), { type: 'module' });
}

export const HASH_WORKER_FACTORY = new InjectionToken<HashWorkerFactory>(
  'NOSTOS_HASH_WORKER_FACTORY',
  { providedIn: 'root', factory: () => createBrowserHashWorker },
);
