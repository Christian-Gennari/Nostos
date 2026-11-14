/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { computed, signal } from '@angular/core';
import { Observable, catchError, from, map, mergeMap, of, tap, toArray } from 'rxjs';
import type { Book } from '../core/dtos/book.dtos';
import type { Collection } from '../core/dtos/collection.dtos';
import { plural } from '../core/format';
import { BooksService } from '../core/services/books.service';
import { ToastService } from '../core/services/toast.service';
import { buildFlatTree, type FlatTreeNode } from '../ui/flat-tree/flat-tree.helper';
import { BOOKS_DRAG_TYPE } from './library-drag';

/**
 * The taps the shelf applies to the book on screen itself, so the response is not
 * a reason to re-read the page.
 */
const HANDLED = { handledLocally: true } as const;

/** One book's whole membership set, as it was and as it should become. */
interface MembershipChange {
  id: string;
  before: string[];
  after: string[];
}

/**
 * What the selection controller needs from the shelf it is attached to.
 *
 * The controller owns the SELECTION (what is ticked, the batch writes, the bar's
 * copy) but not the list: reading the visible books and replacing them stays with
 * `Library`, which owns pagination, the query and the cache. The port is the seam
 * between the two, and it is small enough to stub in a test.
 */
export interface LibrarySelectionPort {
  /** The books on screen right now. */
  books(): Book[];
  /** Every collection, for the "Add to collection" menu and its names. */
  collections(): Collection[];
  /** The collection the shelf is filtered to, if any. */
  activeCollectionId(): string | null;
  /** True while the shelf shows Unsorted, which membership writes can change. */
  unsortedActive(): boolean;
  /** Whether this deployment links books from external media folders. */
  supportsExternalMedia(): boolean;
  /** Replaces the visible books (identical references for untouched ones). */
  updateBooks(update: (books: Book[]) => Book[]): void;
  /** Adjusts the total the pager counts against. */
  updateTotal(update: (total: number) => number): void;
  /** Applies a favourite/finished/rating response, honouring the active filter. */
  syncToggledBook(book: Book, refreshCounts?: boolean): void;
  /** Re-reads the sidebar's status counts. */
  refreshStatusCounts(): void;
  /** Re-reads the sidebar's collection counts. */
  refreshCollectionCounts(): void;
  /** A membership write may have changed which books the shelf holds. */
  refreshAfterMembershipChange(): void;
}

/**
 * Select mode, on the Library: tick books, then act on all of them from one bar.
 * A drag onto a sidebar collection is the same "add to collection" with the
 * pointer. Membership is the full set per book, so each book's new set is worked
 * out here and written with PUT /api/books/{id} `collectionIds`.
 *
 * WHY THIS IS NOT IN THE COMPONENT. These are ten writes over a selection, each
 * with its own partial-failure and undo handling, and they used to sit in the same
 * 1,400-line class as pagination, the filters and the import feed. The selection
 * is a coherent unit: it has its own state (what is ticked), its own copy (the
 * bar and the dialog) and its own failure rule (one book failing does not undo
 * the others). Keeping it here also means the batch write can be exercised by a
 * unit test without a rendered shelf.
 */
export class LibrarySelectionController {
  readonly selecting = signal(false);
  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly bulkDeleteOpen = signal(false);
  readonly bulkDeleting = signal(false);

  /** The ticked books that are still in the list. */
  readonly selectedBooks = computed(() => this.port.books().filter((b) => this.selected().has(b.id)));

  /** Every collection in tree order with its depth, for the "Add to collection" menu. */
  readonly collectionChoices = computed<FlatTreeNode<Collection>[]>(() => {
    const all = this.port.collections();
    return buildFlatTree(all, new Set(all.map((c) => c.id)), true);
  });

  readonly bulkDeleteHeading = computed(() => `Delete ${plural(this.selectedBooks().length, 'book')}?`);
  readonly bulkDeleteDescription = computed(
    () =>
      'This action cannot be undone. The book files, annotations, and all reading progress will be permanently deleted from your library.' +
      (this.port.supportsExternalMedia()
        ? ' Books linked from an external media folder are only removed from Nostos; their original files stay.'
        : ''),
  );

  constructor(
    private readonly booksService: BooksService,
    private readonly toast: ToastService,
    private readonly port: LibrarySelectionPort,
  ) {}

  /** A book still being imported has nothing to act on yet. */
  isSelectable(book: Book): boolean {
    return book.status !== 1 && book.status !== 2;
  }

  toggleSelecting(): void {
    if (this.selecting()) this.leaveSelection();
    else this.selecting.set(true);
  }

  leaveSelection(): void {
    this.selecting.set(false);
    this.selected.set(new Set());
  }

  setSelected(bookId: string, on: boolean): void {
    this.selected.update((current) => {
      const next = new Set(current);
      if (on) next.add(bookId);
      else next.delete(bookId);
      return next;
    });
  }

  /** In Select mode a press on the card ticks it instead of opening the book. */
  onBookPress(book: Book): void {
    if (!this.selecting() || !this.isSelectable(book)) return;
    this.setSelected(book.id, !this.selected().has(book.id));
  }

  onBookDragStart(book: Book, event: DragEvent): void {
    if (!this.isSelectable(book) || !event.dataTransfer) {
      event.preventDefault();
      return;
    }
    // Dragging a ticked book carries every ticked book; any other carries itself.
    const ids =
      this.selecting() && this.selected().has(book.id)
        ? this.selectedBooks().map((b) => b.id)
        : [book.id];
    event.dataTransfer.setData(BOOKS_DRAG_TYPE, JSON.stringify(ids));
    event.dataTransfer.effectAllowed = 'copy';
  }

  addToCollection(bookIds: string[], collectionId: string): void {
    const ids = new Set(bookIds);
    const name = this.port.collections().find((c) => c.id === collectionId)?.name ?? 'the collection';
    const books = this.port.books().filter((b) => ids.has(b.id));
    const changes = books
      .filter((b) => !(b.collectionIds ?? []).includes(collectionId))
      .map((b) => ({
        id: b.id,
        before: b.collectionIds ?? [],
        after: [...(b.collectionIds ?? []), collectionId],
      }));
    if (!changes.length) {
      this.toast.info(
        books.length === 1 ? `Already in “${name}”` : `All of them are already in “${name}”`,
      );
      return;
    }
    this.changeMemberships(changes, (n) => `Added ${plural(n, 'book')} to “${name}”`);
  }

  removeSelectionFromCollection(): void {
    const collectionId = this.port.activeCollectionId();
    if (!collectionId) return;
    const name = this.port.collections().find((c) => c.id === collectionId)?.name ?? 'the collection';
    const changes = this.selectedBooks()
      .filter((b) => (b.collectionIds ?? []).includes(collectionId))
      .map((b) => ({
        id: b.id,
        before: b.collectionIds ?? [],
        after: (b.collectionIds ?? []).filter((id) => id !== collectionId),
      }));
    if (!changes.length) {
      // The collection list also shows books that sit in a sub-collection.
      this.toast.info(`These books are not directly in “${name}”`);
      return;
    }
    this.changeMemberships(changes, (n) => `Removed ${plural(n, 'book')} from “${name}”`);
  }

  markSelectionFinished(): void {
    const pending = this.selectedBooks().filter((b) => !b.finishedAt);
    if (!pending.length) {
      this.toast.info('They are all finished already');
      return;
    }
    from(pending)
      .pipe(
        mergeMap(
          (book) =>
            this.booksService.update(book.id, { isFinished: true }, HANDLED).pipe(
              catchError(() => of(null)),
            ),
          4,
        ),
        toArray(),
      )
      .subscribe((results) => {
        const updated = results.filter((b): b is Book => b !== null);
        updated.forEach((book) => this.port.syncToggledBook(book, false));
        this.port.refreshStatusCounts();
        if (updated.length < pending.length) {
          this.toast.error(`Could not update ${plural(pending.length - updated.length, 'book')}`);
        }
        if (updated.length) this.toast.success(`Marked ${plural(updated.length, 'book')} as finished`);
        this.selected.set(new Set());
      });
  }

  askDeleteSelection(): void {
    if (this.selectedBooks().length) this.bulkDeleteOpen.set(true);
  }

  cancelBulkDelete(): void {
    if (!this.bulkDeleting()) this.bulkDeleteOpen.set(false);
  }

  confirmBulkDelete(): void {
    const books = this.selectedBooks();
    if (!books.length || this.bulkDeleting()) return;
    this.bulkDeleting.set(true);
    from(books)
      .pipe(
        mergeMap(
          (book) =>
            this.booksService.delete(book.id).pipe(
              map(() => book.id),
              catchError(() => of(null)),
            ),
          4,
        ),
        toArray(),
      )
      .subscribe((results) => {
        const gone = new Set(results.filter((id): id is string => id !== null));
        this.port.updateBooks((current) => current.filter((b) => !gone.has(b.id)));
        this.port.updateTotal((c) => Math.max(0, c - gone.size));
        this.port.refreshStatusCounts();
        this.port.refreshCollectionCounts();
        this.bulkDeleting.set(false);
        this.bulkDeleteOpen.set(false);
        this.selected.set(new Set());
        if (gone.size < books.length) {
          this.toast.error(`Could not delete ${plural(books.length - gone.size, 'book')}`);
        }
        if (gone.size) this.toast.success(`Deleted ${plural(gone.size, 'book')}`);
      });
  }

  private changeMemberships(changes: MembershipChange[], message: (count: number) => string): void {
    this.writeMemberships(changes, 'after').subscribe((done) => {
      if (done.length < changes.length) {
        this.toast.error(`Could not update ${plural(changes.length - done.length, 'book')}`);
      }
      if (!done.length) return;
      this.selected.set(new Set());
      this.toast.success(message(done.length), {
        label: 'Undo',
        run: () => this.writeMemberships(done, 'before').subscribe(),
      });
    });
  }

  /** Writes each book's whole membership set; resolves with the changes that were saved. */
  private writeMemberships(
    changes: MembershipChange[],
    which: 'before' | 'after',
  ): Observable<MembershipChange[]> {
    return from(changes).pipe(
      mergeMap(
        (change) =>
          this.booksService.update(change.id, { collectionIds: change[which] }, HANDLED).pipe(
            map(() => change),
            catchError(() => of(null)),
          ),
        4,
      ),
      toArray(),
      map((results) => results.filter((c): c is MembershipChange => c !== null)),
      tap((done) => {
        const next = new Map(done.map((c) => [c.id, c[which]]));
        this.port.updateBooks((books) =>
          books.map((b) => (next.has(b.id) ? { ...b, collectionIds: next.get(b.id)! } : b)),
        );
        this.port.refreshCollectionCounts();
        this.port.refreshStatusCounts();
        // A collection or Unsorted view may now hold different books.
        if (this.port.activeCollectionId() || this.port.unsortedActive()) {
          this.port.refreshAfterMembershipChange();
        }
      }),
    );
  }
}
