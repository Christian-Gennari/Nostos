import { afterEach, describe, expect, it, vi } from 'vitest';

import { createBrowserHashWorker, HASH_WORKER_FACTORY } from './hash-worker';
import { TestBed } from '@angular/core/testing';

describe('hash worker seam', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('has no worker where the environment has none, so hashing stays in process', () => {
    vi.stubGlobal('Worker', undefined);

    expect(createBrowserHashWorker()).toBeNull();
  });

  it('opens a module worker where the environment has one', () => {
    const created: Array<{ url: unknown; options: unknown }> = [];
    class FakeWorker {
      constructor(url: unknown, options: unknown) {
        created.push({ url, options });
      }
    }
    vi.stubGlobal('Worker', FakeWorker);

    const worker = createBrowserHashWorker();

    expect(worker).toBeInstanceOf(FakeWorker);
    expect(created).toHaveLength(1);
    expect(created[0].options).toEqual({ type: 'module' });
  });

  it('offers the same factory through the injection token', () => {
    TestBed.configureTestingModule({});

    expect(TestBed.inject(HASH_WORKER_FACTORY)).toBe(createBrowserHashWorker);
  });
});
