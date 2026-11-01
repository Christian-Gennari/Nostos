/**
 * Where a browser keeps the EPUB files it has downloaded: one Cache Storage
 * cache per account, so one account's books can never be read by the next on a
 * shared browser. This half is on the sign-in path and stays small; the rest of
 * the cache lives with the reader (EpubFileCacheService).
 */
const EPUB_FILE_CACHE_PREFIX = 'nostos-epub-files-v1:';

/** The scope of a host with no accounts, which is all of self-hosting. */
export const NO_ACCOUNT_SCOPE = 'local';

export function epubFileCacheName(scope: string): string {
  return EPUB_FILE_CACHE_PREFIX + scope;
}

/**
 * Removes every account's files but the one given; null removes them all. Never
 * rejects: without Cache Storage (plain HTTP, some private windows) there is
 * nothing to remove.
 */
export async function retainEpubFileCaches(accountId: string | null): Promise<void> {
  try {
    if (typeof caches === 'undefined') return;
    const keep = accountId === null ? null : epubFileCacheName(accountId);
    const names = await caches.keys();
    await Promise.all(
      names
        .filter((name) => name.startsWith(EPUB_FILE_CACHE_PREFIX) && name !== keep)
        .map((name) => caches.delete(name)),
    );
  } catch {
    // Storage refused. Each account's cache is still its own.
  }
}
