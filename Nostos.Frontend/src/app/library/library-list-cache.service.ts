import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';

import type { Collection } from '../core/dtos/collection.dtos';
import type { Book } from '../core/dtos/book.dtos';

/** The list as it was left: what was loaded, and how far down the reader was. */
export interface LibraryListSnapshot {
  /** The query this list answers. It fits no other: see `Library.listKey`. */
  key: string;
  /** Held here because the search box, unlike the filters, lives and dies with the page. */
  search: string;
  books: Book[];
  total: number;
  /** Pages in `books`: a restored list goes on paging from here, and re-reads this many. */
  page: number;
  collections: Collection[];
  scrollTop: number;
}

/**
 * Router state for a link that means Back without being a history step. The book
 * page's "Library" link is that link, and on an installed phone app it is the
 * only Back there is.
 */
export const LIBRARY_BACK_STATE = { libraryBack: true } as const;

/**
 * Brings the Library back as it was when the reader comes Back to it, instead of
 * rebuilding it from the top. A Library opened any other way loads fresh.
 *
 * There is one list, so there is one snapshot. It is memory only: sign-out and
 * every other change of account are full page loads, which empty it.
 */
@Injectable({ providedIn: 'root' })
export class LibraryListCacheService {
  private readonly router = inject(Router);
  private snapshot: LibraryListSnapshot | null = null;

  /** Keeps the list left behind; null when what was on screen is not worth keeping. */
  save(snapshot: LibraryListSnapshot | null): void {
    this.snapshot = snapshot;
  }

  /** The list as it was, when the navigation under way is a step Back or forward. */
  restore(): LibraryListSnapshot | null {
    const navigation = this.router.currentNavigation();
    const isBack =
      navigation?.trigger === 'popstate' ||
      navigation?.extras.state?.['libraryBack'] === LIBRARY_BACK_STATE.libraryBack;
    return isBack ? this.snapshot : null;
  }
}
