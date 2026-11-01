import { Injectable, InjectionToken, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';

import { BookFileVersionDto, BooksService } from '../../core/services/books.service';
import { CloudAuthService } from '../../core/services/cloud-auth.service';
import { NO_ACCOUNT_SCOPE, epubFileCacheName } from '../../core/services/epub-file-cache-scope';

export const EPUB_FILE_CACHE_LIMITS = new InjectionToken('EPUB file cache limits', {
  factory: () => ({
    /** The last few books read. A reader moves between a handful at most. */
    books: 5,
    /** What the browser may hold in all, and so the largest book worth keeping. */
    bytes: 300 * 1024 * 1024,
  }),
});

/** A synthetic path: nothing else answers it, and it names no signed URL. */
const ENTRY_ROOT = '/__nostos-epub-files';
const INDEX_URL = `${ENTRY_ROOT}/index.json`;

interface CachedFile {
  bookId: string;
  version: string;
  bytes: number;
  usedAt: number;
}

export interface EpubFile {
  bytes: ArrayBuffer;
  /** True when these bytes came from the cache, so a failure to open them means a bad copy. */
  fromCache: boolean;
}

/**
 * Keeps the EPUB files of the last few books so a reopen does not download them.
 *
 * A file is kept under its version, and the version is asked from the server on
 * every open, so a replaced file is a miss and its old bytes are never shown. The
 * URL cannot do this job: on Cloud it redirects to a new signed URL every time.
 *
 * Everything here is an optimisation. Cache Storage is missing on plain HTTP and
 * refused in some private windows, a disk can be full, and any of that ends in the
 * ordinary download.
 */
@Injectable({ providedIn: 'root' })
export class EpubFileCacheService {
  private readonly books = inject(BooksService);
  private readonly limits = inject(EPUB_FILE_CACHE_LIMITS);

  private accountId: string | null = null;
  /** Index writes in order, so two opens in one tab cannot lose each other's entry. */
  private writes: Promise<unknown> = Promise.resolve();

  constructor() {
    inject(CloudAuthService)
      .sessionChanges$.pipe(takeUntilDestroyed())
      .subscribe((session) => (this.accountId = session.accountId));
  }

  /** The book's file: the kept copy if it is still current, else a download. */
  async load(bookId: string): Promise<EpubFile> {
    const scope = this.scope();
    const version = await this.readVersion(bookId);

    if (version) {
      const kept = await this.read(scope, bookId, version);
      if (kept) return { bytes: kept, fromCache: true };
    }

    const bytes = await this.download(bookId);
    if (version?.bytes === bytes.byteLength) this.keepWhenIdle(scope, bookId, version, bytes);
    return { bytes, fromCache: false };
  }

  /** Drops a book's copy, for one that turned out not to open. */
  forget(bookId: string): Promise<void> {
    const scope = this.scope();
    return this.write(async () => {
      const cache = await this.open(scope);
      if (!cache) return;
      const entries = await readIndex(cache);
      const file = entries.find((entry) => entry.bookId === bookId);
      if (file) await cache.delete(entryUrl(file));
      await writeIndex(
        cache,
        entries.filter((entry) => entry.bookId !== bookId),
      );
    });
  }

  private scope(): string {
    return this.accountId ?? NO_ACCOUNT_SCOPE;
  }

  private async readVersion(bookId: string): Promise<BookFileVersionDto | null> {
    try {
      return await firstValueFrom(this.books.getFileVersion(bookId));
    } catch {
      return null;
    }
  }

  private async download(bookId: string): Promise<ArrayBuffer> {
    // no-cache, because `max-age` on this URL would otherwise hand back a file
    // that was replaced minutes ago. Where the URL is stable that costs a 304.
    const response = await fetch(`/api/books/${bookId}/file`, { cache: 'no-cache' });
    if (!response.ok) throw new Error(`The book file answered ${response.status}.`);
    return response.arrayBuffer();
  }

  private async read(
    scope: string,
    bookId: string,
    version: BookFileVersionDto,
  ): Promise<ArrayBuffer | null> {
    try {
      const cache = await this.open(scope);
      if (!cache) return null;
      const url = entryUrl({ bookId, version: version.version });
      const response = await cache.match(url);
      if (!response) return null;

      const bytes = await response.arrayBuffer();
      if (bytes.byteLength !== version.bytes) {
        await this.forget(bookId);
        return null;
      }
      void this.write(() => this.touch(cache, bookId));
      return bytes;
    } catch {
      return null;
    }
  }

  private async touch(cache: Cache, bookId: string): Promise<void> {
    const entries = await readIndex(cache);
    await writeIndex(
      cache,
      entries.map((entry) =>
        entry.bookId === bookId ? { ...entry, usedAt: Date.now() } : entry,
      ),
    );
  }

  /** Building the stored copy costs the main thread a copy of the whole file, so not now. */
  private keepWhenIdle(
    scope: string,
    bookId: string,
    version: BookFileVersionDto,
    bytes: ArrayBuffer,
  ): void {
    if (bytes.byteLength > this.limits.bytes) return;
    whenIdle(() => {
      // The session ended while this waited: it is no longer this account's to keep.
      if (this.scope() !== scope) return;
      void this.write(() => this.keep(scope, bookId, version.version, bytes));
    });
  }

  private async keep(
    scope: string,
    bookId: string,
    version: string,
    bytes: ArrayBuffer,
  ): Promise<void> {
    const cache = await this.open(scope);
    if (!cache) return;

    const file: CachedFile = { bookId, version, bytes: bytes.byteLength, usedAt: Date.now() };
    let entries = await removeUnlisted(cache, await readIndex(cache));
    for (const stale of entries.filter((entry) => entry.bookId === bookId)) {
      await cache.delete(entryUrl(stale));
    }
    entries = entries.filter((entry) => entry.bookId !== bookId);
    entries = await evictFor(cache, entries, file.bytes, this.limits);

    // Out of room: give the oldest books up to fit this one, then stop trying.
    for (;;) {
      try {
        await cache.put(entryUrl(file), fileResponse(bytes));
        break;
      } catch {
        const oldest = oldestFirst(entries)[0];
        if (!oldest) {
          await writeIndex(cache, entries);
          return;
        }
        await cache.delete(entryUrl(oldest));
        entries = entries.filter((entry) => entry !== oldest);
      }
    }
    await writeIndex(cache, [...entries, file]);
  }

  private async open(scope: string): Promise<Cache | null> {
    try {
      if (typeof caches === 'undefined') return null;
      return await caches.open(epubFileCacheName(scope));
    } catch {
      return null;
    }
  }

  private write(step: () => Promise<void>): Promise<void> {
    const done = this.writes.then(step).catch(() => undefined);
    this.writes = done;
    return done;
  }
}

function entryUrl(file: Pick<CachedFile, 'bookId' | 'version'>): string {
  return `${ENTRY_ROOT}/${file.bookId}/${file.version}`;
}

function fileResponse(bytes: ArrayBuffer): Response {
  return new Response(bytes, { headers: { 'Content-Type': 'application/epub+zip' } });
}

function oldestFirst(entries: CachedFile[]): CachedFile[] {
  return [...entries].sort((a, b) => a.usedAt - b.usedAt);
}

async function readIndex(cache: Cache): Promise<CachedFile[]> {
  try {
    const entries: unknown = await (await cache.match(INDEX_URL))?.json();
    return Array.isArray(entries) ? (entries as CachedFile[]) : [];
  } catch {
    return [];
  }
}

function writeIndex(cache: Cache, entries: CachedFile[]): Promise<void> {
  return cache.put(INDEX_URL, new Response(JSON.stringify(entries)));
}

/**
 * The index and the cache can disagree: another tab writes the index too. A file
 * the index has lost could never be evicted, and an entry with no file holds a
 * place in the cap, so both go.
 */
async function removeUnlisted(cache: Cache, entries: CachedFile[]): Promise<CachedFile[]> {
  const present = new Set(
    (await cache.keys()).map((request) => new URL(request.url).pathname),
  );
  const listed = new Set(entries.map(entryUrl));
  await Promise.all(
    [...present]
      .filter((path) => path !== INDEX_URL && !listed.has(path))
      .map((path) => cache.delete(path)),
  );
  return entries.filter((entry) => present.has(entryUrl(entry)));
}

/** Makes room for one more file of `incoming` bytes, oldest first. */
async function evictFor(
  cache: Cache,
  entries: CachedFile[],
  incoming: number,
  limits: { books: number; bytes: number },
): Promise<CachedFile[]> {
  const kept = oldestFirst(entries);
  let total = kept.reduce((sum, entry) => sum + entry.bytes, 0) + incoming;
  while (kept.length >= limits.books || (kept.length > 0 && total > limits.bytes)) {
    const oldest = kept.shift()!;
    await cache.delete(entryUrl(oldest));
    total -= oldest.bytes;
  }
  return kept;
}

function whenIdle(work: () => void): void {
  if (typeof requestIdleCallback === 'function') {
    requestIdleCallback(work, { timeout: 5000 });
  } else {
    setTimeout(work, 1000);
  }
}
