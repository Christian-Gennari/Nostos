import { signal } from '@angular/core';
import { of, throwError } from 'rxjs';

import {
  LibrarySelectionController,
  type LibrarySelectionPort,
} from './library-selection.controller';
import type { Book } from '../core/dtos/book.dtos';
import type { Collection } from '../core/dtos/collection.dtos';
import type { BooksService } from '../core/services/books.service';
import type { ToastService } from '../core/services/toast.service';
import { BOOKS_DRAG_TYPE } from './library-drag';

function book(id: string, collectionIds: string[] = [], overrides: Partial<Book> = {}): Book {
  return {
    id,
    title: `Book ${id}`,
    type: 'ebook',
    collectionIds,
    status: 0,
    finishedAt: null,
    ...overrides,
  } as unknown as Book;
}

describe('LibrarySelectionController', () => {
  let books: ReturnType<typeof signal<Book[]>>;
  let collections: ReturnType<typeof signal<Collection[]>>;
  let collectionFilter: string | null;
  let unsorted: boolean;
  let update: ReturnType<typeof vi.fn>;
  let remove: ReturnType<typeof vi.fn>;
  let toast: { info: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn>; success: ReturnType<typeof vi.fn> };
  let synced: Book[];
  let reseeded: number;
  let controller: LibrarySelectionController;

  const port: () => LibrarySelectionPort = () => ({
    books: () => books(),
    collections: () => collections(),
    activeCollectionId: () => collectionFilter,
    unsortedActive: () => unsorted,
    supportsExternalMedia: () => false,
    updateBooks: (fn) => books.update(fn),
    updateTotal: () => undefined,
    syncToggledBook: (b) => synced.push(b),
    refreshStatusCounts: () => undefined,
    refreshCollectionCounts: () => undefined,
    refreshAfterMembershipChange: () => {
      reseeded += 1;
    },
  });

  beforeEach(() => {
    localStorage.clear();
    books = signal([book('a', ['c2']), book('b'), book('c', ['c1'])]);
    collections = signal([
      { id: 'c1', name: 'Classics', parentId: null },
      { id: 'c2', name: 'Poetry', parentId: 'c1' },
    ] as unknown as Collection[]);
    collectionFilter = null;
    unsorted = false;
    synced = [];
    reseeded = 0;
    update = vi.fn((id: string, dto: { collectionIds?: string[] }) => of({ id, ...dto }));
    remove = vi.fn(() => of(undefined));
    toast = { info: vi.fn(), error: vi.fn(), success: vi.fn() };
    controller = new LibrarySelectionController(
      { update, delete: remove } as unknown as BooksService,
      toast as unknown as ToastService,
      port(),
    );
  });

  it('tracks the ticked set and the books it still holds', () => {
    controller.toggleSelecting();
    controller.setSelected('a', true);
    controller.setSelected('gone', true);

    expect(controller.selecting()).toBe(true);
    expect(controller.selectedBooks().map((b) => b.id)).toEqual(['a']);

    controller.leaveSelection();
    expect(controller.selected().size).toBe(0);
    expect(controller.selecting()).toBe(false);
  });

  it('ticks a book on press only while selecting, and never one still importing', () => {
    controller.onBookPress(books()[0]);
    expect(controller.selected().size).toBe(0);

    controller.toggleSelecting();
    controller.onBookPress(books()[0]);
    expect([...controller.selected()]).toEqual(['a']);

    books.set([book('i', [], { status: 1 })]);
    controller.onBookPress(books()[0]);
    expect([...controller.selected()]).toEqual(['a']);
  });

  it('sends each book its whole new set, skipping books already in the collection', () => {
    controller.addToCollection(['a', 'b', 'c'], 'c1');

    // 'c' already sits in Classics, so only 'a' and 'b' are written.
    expect(update).toHaveBeenCalledTimes(2);
    expect(update).toHaveBeenCalledWith('a', { collectionIds: ['c2', 'c1'] }, { handledLocally: true });
    expect(update).toHaveBeenCalledWith('b', { collectionIds: ['c1'] }, { handledLocally: true });
    expect(books().find((b) => b.id === 'a')!.collectionIds).toEqual(['c2', 'c1']);
    expect(books().find((b) => b.id === 'b')!.collectionIds).toEqual(['c1']);
    expect(toast.success).toHaveBeenCalledWith('Added 2 books to “Classics”', expect.anything());
  });

  it('says nothing changed when every book is already in the collection', () => {
    controller.addToCollection(['c'], 'c1');

    expect(update).not.toHaveBeenCalled();
    expect(toast.info).toHaveBeenCalledWith('Already in “Classics”');
  });

  it('offers Undo that puts every book back to the set it had', () => {
    controller.addToCollection(['b'], 'c1');

    const action = toast.success.mock.calls[0][1] as { label: string; run: () => void };
    expect(action.label).toBe('Undo');
    update.mockClear();
    action.run();

    expect(update).toHaveBeenCalledWith('b', { collectionIds: [] }, { handledLocally: true });
    expect(books().find((b) => b.id === 'b')!.collectionIds).toEqual([]);
  });

  it('removes only the open collection from the selected books', () => {
    collectionFilter = 'c1';
    controller.setSelected('a', true);
    controller.setSelected('c', true);

    controller.removeSelectionFromCollection();

    // 'a' is only in the SUB-collection, so there is nothing to remove from c1.
    expect(update).toHaveBeenCalledTimes(1);
    expect(update).toHaveBeenCalledWith('c', { collectionIds: [] }, { handledLocally: true });
    expect(toast.success).toHaveBeenCalledWith('Removed 1 book from “Classics”', expect.anything());
  });

  it('re-reads a collection or Unsorted shelf after a membership write', () => {
    controller.addToCollection(['b'], 'c1');
    expect(reseeded).toBe(0);

    collectionFilter = 'c1';
    controller.addToCollection(['a'], 'c1');
    expect(reseeded).toBe(1);
  });

  it('marks the unfinished selected books finished, and says how many it could not', () => {
    controller.setSelected('a', true);
    controller.setSelected('c', true);
    update.mockImplementation((id: string) =>
      id === 'c' ? throwError(() => new Error('no')) : of({ ...books()[0], id, finishedAt: 'now' }),
    );

    controller.markSelectionFinished();

    expect(synced.map((b) => b.id)).toEqual(['a']);
    expect(toast.success).toHaveBeenCalledWith('Marked 1 book as finished');
    expect(toast.error).toHaveBeenCalledWith('Could not update 1 book');
  });

  it('deletes every selected book after confirmation, and reports what survived', () => {
    controller.setSelected('a', true);
    controller.setSelected('b', true);
    remove.mockImplementation((id: string) =>
      id === 'b' ? throwError(() => new Error('no')) : of(undefined),
    );

    controller.askDeleteSelection();
    expect(controller.bulkDeleteOpen()).toBe(true);

    controller.confirmBulkDelete();

    expect(controller.bulkDeleteOpen()).toBe(false);
    expect(controller.bulkDeleting()).toBe(false);
    expect(books().map((b) => b.id)).toEqual(['b', 'c']);
    expect(toast.success).toHaveBeenCalledWith('Deleted 1 book');
    expect(toast.error).toHaveBeenCalledWith('Could not delete 1 book');
  });

  it('carries every ticked book when one of them is dragged, and only itself otherwise', () => {
    const data = new Map<string, string>();
    const event = {
      dataTransfer: { setData: (type: string, value: string) => data.set(type, value), effectAllowed: '' },
      preventDefault: vi.fn(),
    } as unknown as DragEvent;

    controller.onBookDragStart(books()[1], event);
    expect(JSON.parse(data.get(BOOKS_DRAG_TYPE)!)).toEqual(['b']);

    controller.toggleSelecting();
    controller.setSelected('a', true);
    controller.setSelected('b', true);
    controller.onBookDragStart(books()[0], event);
    expect(JSON.parse(data.get(BOOKS_DRAG_TYPE)!)).toEqual(['a', 'b']);
  });

  it('refuses to drag a book that is still importing', () => {
    const event = {
      dataTransfer: { setData: vi.fn(), effectAllowed: '' },
      preventDefault: vi.fn(),
    } as unknown as DragEvent;

    controller.onBookDragStart(book('i', [], { status: 2 }), event);

    expect(event.preventDefault).toHaveBeenCalled();
    expect((event.dataTransfer!.setData as ReturnType<typeof vi.fn>)).not.toHaveBeenCalled();
  });

  it('describes the bulk delete with the deployment’s own promise about external media', () => {
    controller.setSelected('a', true);
    expect(controller.bulkDeleteHeading()).toBe('Delete 1 book?');
    expect(controller.bulkDeleteDescription()).not.toContain('external media');

    controller = new LibrarySelectionController(
      { update, delete: remove } as unknown as BooksService,
      toast as unknown as ToastService,
      { ...port(), supportsExternalMedia: () => true },
    );
    expect(controller.bulkDeleteDescription()).toContain('external media folder');
  });
});
