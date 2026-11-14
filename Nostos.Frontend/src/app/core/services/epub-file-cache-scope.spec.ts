import { afterEach, describe, expect, it } from 'vitest';

import { NO_ACCOUNT_SCOPE, epubFileCacheName, retainEpubFileCaches } from './epub-file-cache-scope';

const PREFIX = 'nostos-epub-files-v1:';

/** A stand-in for the browser's Cache Storage, with the names a test seeds. */
function installCaches(names: string[]): { deleted: string[] } {
  const deleted: string[] = [];
  const store = new Map(names.map((name) => [name, {}]));
  (globalThis as { caches?: unknown }).caches = {
    keys: async () => [...store.keys()],
    delete: async (name: string) => {
      deleted.push(name);
      store.delete(name);
      return true;
    },
  };
  return { deleted };
}

function removeCaches(): void {
  delete (globalThis as { caches?: unknown }).caches;
}

describe('epub file cache scope', () => {
  afterEach(removeCaches);

  it('names one cache per account, under a versioned prefix', () => {
    expect(epubFileCacheName('account-1')).toBe(`${PREFIX}account-1`);
    expect(epubFileCacheName(NO_ACCOUNT_SCOPE)).toBe(`${PREFIX}local`);
    expect(NO_ACCOUNT_SCOPE).toBe('local');
  });

  it('keeps the current account’s files and removes every other account’s', async () => {
    const { deleted } = installCaches([
      `${PREFIX}account-1`,
      `${PREFIX}account-2`,
      'unrelated-cache',
    ]);

    await retainEpubFileCaches('account-1');

    expect(deleted).toEqual([`${PREFIX}account-2`]);
  });

  it('removes every account’s files on sign-out', async () => {
    const { deleted } = installCaches([`${PREFIX}local`, `${PREFIX}account-2`, 'unrelated-cache']);

    await retainEpubFileCaches(null);

    expect(deleted.sort()).toEqual([`${PREFIX}account-2`, `${PREFIX}local`]);
  });

  it('does nothing when the browser has no Cache Storage', async () => {
    removeCaches();

    await expect(retainEpubFileCaches('account-1')).resolves.toBeUndefined();
  });

  it('never rejects when storage refuses, because every cache is still its own', async () => {
    (globalThis as { caches?: unknown }).caches = {
      keys: async () => {
        throw new Error('denied');
      },
      delete: async () => true,
    };

    await expect(retainEpubFileCaches(null)).resolves.toBeUndefined();
  });
});
