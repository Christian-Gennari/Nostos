import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';

import { BooksService } from '../../core/services/books.service';
import { CloudAuthService } from '../../core/services/cloud-auth.service';
import {
  NO_ACCOUNT_SCOPE,
  epubFileCacheName,
  retainEpubFileCaches,
} from '../../core/services/epub-file-cache-scope';
import { HostedBrowserSession } from '../../core/dtos/hosted-browser-integration.dtos';
import { EPUB_FILE_CACHE_LIMITS, EpubFileCacheService } from './epub-file-cache.service';

const LIMITS = { books: 3, bytes: 1000 };

/** Cache Storage as the browser has it, for what this service touches. */
class FakeCache {
  readonly stored = new Map<string, { body: ArrayBuffer | string }>();
  failPut: (url: string) => boolean = () => false;

  async match(url: string): Promise<Response | undefined> {
    const entry = this.stored.get(url);
    return entry && new Response(entry.body);
  }

  async put(url: string, response: Response): Promise<void> {
    if (this.failPut(url)) throw new DOMException('Full', 'QuotaExceededError');
    const body = url.endsWith('index.json') ? await response.text() : await response.arrayBuffer();
    this.stored.set(url, { body });
  }

  async delete(url: string): Promise<boolean> {
    return this.stored.delete(url);
  }

  async keys(): Promise<{ url: string }[]> {
    return [...this.stored.keys()].map((url) => ({ url: `http://nostos.test${url}` }));
  }

  /** The books held, by path, without the index. */
  files(): string[] {
    return [...this.stored.keys()].filter((url) => !url.endsWith('index.json'));
  }
}

class FakeCacheStorage {
  readonly byName = new Map<string, FakeCache>();

  async open(name: string): Promise<FakeCache> {
    if (!this.byName.has(name)) this.byName.set(name, new FakeCache());
    return this.byName.get(name)!;
  }

  async keys(): Promise<string[]> {
    return [...this.byName.keys()];
  }

  async delete(name: string): Promise<boolean> {
    return this.byName.delete(name);
  }
}

const bytesOf = (length: number, fill = 1) => new Uint8Array(length).fill(fill).buffer;
const versionOf = (bookId: string, version: string, bytes: number) => [bookId, { version, bytes }] as const;

describe('EpubFileCacheService (#1316)', () => {
  let storage: FakeCacheStorage;
  let versions: Map<string, { version: string; bytes: number }>;
  let served: Map<string, ArrayBuffer>;
  let download: ReturnType<typeof vi.fn>;
  let session: Subject<HostedBrowserSession>;
  let service: EpubFileCacheService;

  const localCache = () => storage.byName.get(epubFileCacheName(NO_ACCOUNT_SCOPE));

  function serve(bookId: string, version: string, bytes: ArrayBuffer): void {
    versions.set(...versionOf(bookId, version, bytes.byteLength));
    served.set(bookId, bytes);
  }

  /** Lets the idle wait pass and the background write finish. */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await (service as unknown as { writes: Promise<unknown> }).writes;
  }

  async function open(bookId: string) {
    const file = await service.load(bookId);
    await settle();
    return file;
  }

  beforeEach(() => {
    let clock = 0;
    vi.spyOn(Date, 'now').mockImplementation(() => ++clock);
    vi.stubGlobal('requestIdleCallback', (work: () => void) => setTimeout(work, 0));
    storage = new FakeCacheStorage();
    vi.stubGlobal('caches', storage);
    versions = new Map();
    served = new Map();
    download = vi.fn(async (url: string) => {
      const bookId = url.split('/')[3];
      const bytes = served.get(bookId);
      return bytes ? new Response(bytes) : new Response(null, { status: 404 });
    });
    vi.stubGlobal('fetch', download);
    session = new Subject();

    TestBed.configureTestingModule({
      providers: [
        {
          provide: BooksService,
          useValue: {
            getFileVersion: (id: string) => {
              const version = versions.get(id);
              return version ? of(version) : throwError(() => new Error('404'));
            },
          },
        },
        { provide: CloudAuthService, useValue: { sessionChanges$: session.asObservable() } },
        { provide: EPUB_FILE_CACHE_LIMITS, useValue: LIMITS },
      ],
    });
    service = TestBed.inject(EpubFileCacheService);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('downloads a book the first time and not the second', async () => {
    serve('book-1', 'v1', bytesOf(100));

    const first = await open('book-1');
    const second = await open('book-1');

    expect(first.fromCache).toBe(false);
    expect(second.fromCache).toBe(true);
    expect(second.bytes.byteLength).toBe(100);
    expect(download).toHaveBeenCalledTimes(1);
  });

  it('never lets the HTTP cache answer the download', async () => {
    serve('book-1', 'v1', bytesOf(100));

    await open('book-1');

    expect(download).toHaveBeenCalledWith('/api/books/book-1/file', { cache: 'no-cache' });
  });

  it('shows the new bytes after the file was replaced, and drops the old ones', async () => {
    serve('book-1', 'v1', bytesOf(100, 1));
    await open('book-1');

    serve('book-1', 'v2', bytesOf(100, 2));
    const replaced = await open('book-1');

    expect(replaced.fromCache).toBe(false);
    expect(new Uint8Array(replaced.bytes)[0]).toBe(2);
    expect(localCache()!.files()).toEqual(['/__nostos-epub-files/book-1/v2']);
    expect((await open('book-1')).fromCache).toBe(true);
  });

  it('never serves a kept copy when the version cannot be read', async () => {
    serve('book-1', 'v1', bytesOf(100));
    await open('book-1');
    versions.delete('book-1');

    const file = await open('book-1');

    expect(file.fromCache).toBe(false);
    expect(download).toHaveBeenCalledTimes(2);
  });

  it('downloads a copy of the wrong length again', async () => {
    serve('book-1', 'v1', bytesOf(100));
    await open('book-1');
    localCache()!.stored.set('/__nostos-epub-files/book-1/v1', { body: bytesOf(10) });

    const file = await open('book-1');

    expect(file.fromCache).toBe(false);
    expect(file.bytes.byteLength).toBe(100);
  });

  it('keeps nothing when the download is not the size the version promised', async () => {
    versions.set('book-1', { version: 'v1', bytes: 999 });
    served.set('book-1', bytesOf(100));

    await open('book-1');

    expect(localCache()!.files()).toEqual([]);
  });

  it('rejects when the download fails, so the reader can show it', async () => {
    versions.set('book-1', { version: 'v1', bytes: 100 });

    await expect(service.load('book-1')).rejects.toThrow('404');
  });

  it('forgets a copy on request', async () => {
    serve('book-1', 'v1', bytesOf(100));
    await open('book-1');

    await service.forget('book-1');

    expect((await open('book-1')).fromCache).toBe(false);
  });

  describe('bounds', () => {
    it('keeps the last few books, evicting the oldest', async () => {
      for (let n = 0; n <= LIMITS.books; n++) {
        serve(`book-${n}`, 'v1', bytesOf(10));
        await open(`book-${n}`);
      }

      expect(localCache()!.files()).toHaveLength(LIMITS.books);
      expect(localCache()!.files()).not.toContain('/__nostos-epub-files/book-0/v1');
    });

    it('counts a reopen as use, so the book read most recently stays', async () => {
      for (let n = 0; n < LIMITS.books; n++) {
        serve(`book-${n}`, 'v1', bytesOf(10));
        await open(`book-${n}`);
      }
      await open('book-0'); // Now book-1 is the oldest.

      serve('book-new', 'v1', bytesOf(10));
      await open('book-new');

      expect(localCache()!.files()).toContain('/__nostos-epub-files/book-0/v1');
      expect(localCache()!.files()).not.toContain('/__nostos-epub-files/book-1/v1');
    });

    it('holds the total under the size cap, evicting as many as it takes', async () => {
      serve('book-a', 'v1', bytesOf(400));
      serve('book-b', 'v1', bytesOf(400));
      serve('book-c', 'v1', bytesOf(400));

      await open('book-a');
      await open('book-b');
      await open('book-c');

      expect(localCache()!.files()).toEqual([
        '/__nostos-epub-files/book-b/v1',
        '/__nostos-epub-files/book-c/v1',
      ]);
    });

    it('does not keep a book larger than the whole cache', async () => {
      serve('book-1', 'v1', bytesOf(LIMITS.bytes + 1));

      const file = await open('book-1');

      expect(file.bytes.byteLength).toBe(LIMITS.bytes + 1);
      expect(localCache()!.files()).toEqual([]);
    });

    it('removes a file the index has lost, so it cannot sit outside the cap', async () => {
      serve('book-1', 'v1', bytesOf(10));
      await open('book-1');
      const cache = localCache()!;
      cache.stored.set('/__nostos-epub-files/orphan/v1', { body: bytesOf(10) });

      serve('book-2', 'v1', bytesOf(10));
      await open('book-2');

      expect(cache.files()).not.toContain('/__nostos-epub-files/orphan/v1');
    });

    it('survives an unreadable index', async () => {
      serve('book-1', 'v1', bytesOf(10));
      await open('book-1');
      localCache()!.stored.set('/__nostos-epub-files/index.json', { body: 'not json' });

      serve('book-2', 'v1', bytesOf(10));
      await open('book-2');

      expect((await open('book-2')).fromCache).toBe(true);
    });
  });

  describe('when storage says no', () => {
    it('gives up the oldest books to fit a new one when out of quota', async () => {
      serve('book-1', 'v1', bytesOf(10));
      serve('book-2', 'v1', bytesOf(10));
      await open('book-1');
      await open('book-2');
      const cache = localCache()!;
      let refused = 0;
      cache.failPut = (url) => url.includes('book-3') && refused++ === 0;

      serve('book-3', 'v1', bytesOf(10));
      await open('book-3');

      expect(cache.files()).toEqual([
        '/__nostos-epub-files/book-2/v1',
        '/__nostos-epub-files/book-3/v1',
      ]);
    });

    it('still reads the book when nothing can be stored at all', async () => {
      serve('book-1', 'v1', bytesOf(10));
      await open('book-1');
      localCache()!.failPut = () => true;

      serve('book-2', 'v1', bytesOf(10));
      const file = await open('book-2');

      expect(file.bytes.byteLength).toBe(10);
      expect(file.fromCache).toBe(false);
    });

    it('falls back to a download without Cache Storage (plain HTTP)', async () => {
      vi.stubGlobal('caches', undefined);
      serve('book-1', 'v1', bytesOf(10));

      expect((await open('book-1')).fromCache).toBe(false);
      expect((await open('book-1')).fromCache).toBe(false);
      expect(download).toHaveBeenCalledTimes(2);
    });

    it('falls back to a download when Cache Storage refuses (private window)', async () => {
      vi.stubGlobal('caches', {
        open: () => Promise.reject(new DOMException('Denied', 'SecurityError')),
      });
      serve('book-1', 'v1', bytesOf(10));

      expect((await open('book-1')).fromCache).toBe(false);
      await expect(service.forget('book-1')).resolves.toBeUndefined();
    });

    it('falls back to a download when reading the copy fails', async () => {
      serve('book-1', 'v1', bytesOf(10));
      await open('book-1');
      localCache()!.match = () => Promise.reject(new Error('Disk'));

      expect((await open('book-1')).fromCache).toBe(false);
    });
  });

  describe('accounts on a shared browser', () => {
    const signIn = (accountId: string) => session.next({ authenticated: true, accountId });

    it('keeps each account in its own cache and shows nobody else its files', async () => {
      signIn('account-a');
      serve('book-1', 'v1', bytesOf(10));
      await open('book-1');

      signIn('account-b');

      expect((await open('book-1')).fromCache).toBe(false);
    });

    it('drops every copy when the session ends', async () => {
      signIn('account-a');
      serve('book-1', 'v1', bytesOf(10));
      await open('book-1');

      await retainEpubFileCaches(null);

      expect(storage.byName.size).toBe(0);
    });

    it('removes the last account\'s books once another signs in', async () => {
      signIn('account-a');
      serve('book-1', 'v1', bytesOf(10));
      await open('book-1');

      await retainEpubFileCaches('account-b');

      expect(storage.byName.size).toBe(0);
      expect(await storage.keys()).toEqual([]);
    });

    it('keeps the signed-in account\'s books and leaves other caches alone', async () => {
      signIn('account-a');
      serve('book-1', 'v1', bytesOf(10));
      await open('book-1');
      await storage.open('ngsw:app-shell');

      await retainEpubFileCaches('account-a');

      expect(await storage.keys()).toEqual([epubFileCacheName('account-a'), 'ngsw:app-shell']);
    });

    it('does not store a book whose session ended during the idle wait', async () => {
      signIn('account-a');
      serve('book-1', 'v1', bytesOf(10));
      await service.load('book-1');

      session.next({ authenticated: false, accountId: null });
      await settle();

      expect(storage.byName.get(epubFileCacheName('account-a'))!.files()).toEqual([]);
    });
  });

  it('clearing needs no Cache Storage to exist', async () => {
    vi.stubGlobal('caches', undefined);

    await expect(retainEpubFileCaches(null)).resolves.toBeUndefined();
  });
});
