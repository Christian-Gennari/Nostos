import { describe, expect, it, vi } from 'vitest';

import { HERO_PREFETCH_INTENT_MS, LibraryCoverPrefetcher } from './library-cover-prefetch';
import type { Book } from '../core/dtos/book.dtos';

const covered = { id: 'b1', title: 'T', type: 'ebook', coverUrl: '/api/books/b1/cover' } as unknown as Book;

describe('LibraryCoverPrefetcher', () => {
  let requested: string[];
  let hover: boolean;

  beforeEach(() => {
    requested = [];
    hover = true;
    vi.useFakeTimers();
    vi.stubGlobal('matchMedia', (q: string) => ({ matches: q.includes('hover: hover') ? hover : false }));
    class FakeImage {
      decoding = '';
      addEventListener(): void {}
      set src(value: string) {
        requested.push(value);
      }
    }
    vi.stubGlobal('Image', FakeImage);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('waits for hover intent before downloading', () => {
    new LibraryCoverPrefetcher().prefetch(covered);

    vi.advanceTimersByTime(HERO_PREFETCH_INTENT_MS - 1);
    expect(requested).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(requested).toEqual(['/api/books/b1/cover/thumbnail?width=640']);
  });

  it('downloads nothing for a cover the pointer only swept across', () => {
    const prefetcher = new LibraryCoverPrefetcher();
    prefetcher.prefetch(covered);
    vi.advanceTimersByTime(60);
    prefetcher.cancel();
    vi.advanceTimersByTime(500);

    expect(requested).toEqual([]);
  });

  it('fetches at once where there is no hover (a tap)', () => {
    hover = false;
    new LibraryCoverPrefetcher().prefetch(covered);

    expect(requested).toEqual(['/api/books/b1/cover/thumbnail?width=640']);
  });

  it('does not re-request a cover it is already fetching', () => {
    const prefetcher = new LibraryCoverPrefetcher();
    prefetcher.prefetch(covered);
    vi.advanceTimersByTime(HERO_PREFETCH_INTENT_MS);
    prefetcher.prefetch(covered);
    vi.advanceTimersByTime(HERO_PREFETCH_INTENT_MS);

    expect(requested).toHaveLength(1);
  });

  it('drops the pending fetch when the shelf goes away', () => {
    const prefetcher = new LibraryCoverPrefetcher();
    prefetcher.prefetch(covered);
    prefetcher.destroy();
    vi.advanceTimersByTime(500);

    expect(requested).toEqual([]);
  });

  it('has nothing to fetch for a book with no cover', () => {
    new LibraryCoverPrefetcher().prefetch({ ...covered, coverUrl: null } as unknown as Book);
    vi.advanceTimersByTime(500);

    expect(requested).toEqual([]);
  });
});
