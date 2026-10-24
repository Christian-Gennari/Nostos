import { bookDeleteCopy, bookDeletedToast } from './book-delete-copy';

describe('bookDeleteCopy', () => {
  it('promises a permanent delete for a book Nostos owns', () => {
    const copy = bookDeleteCopy('Dune', false);
    expect(copy.heading).toBe('Delete “Dune”?');
    expect(copy.description).toContain('permanently deleted');
    expect(copy.confirmLabel).toBe('Delete Permanently');
    expect(copy.busyLabel).toBe('Deleting…');
  });

  it('says a linked book is only removed from Nostos and its files stay', () => {
    const copy = bookDeleteCopy('Dune', true);
    expect(copy.heading).toBe('Remove “Dune” from Nostos?');
    expect(copy.description).toContain('Your original files stay where they are');
    expect(copy.description).not.toContain('permanently deleted');
    expect(copy.confirmLabel).toBe('Remove from Nostos');
    expect(copy.busyLabel).toBe('Removing…');
  });

  it('falls back to a generic heading without a title', () => {
    expect(bookDeleteCopy(null, false).heading).toBe('Delete book?');
    expect(bookDeleteCopy('  ', true).heading).toBe('Remove book from Nostos?');
  });
});

describe('bookDeletedToast', () => {
  it('names the book the way the topic toast does', () => {
    expect(bookDeletedToast('Dune', false)).toBe('Deleted “Dune”');
    expect(bookDeletedToast('Dune', true)).toBe('Removed “Dune” from Nostos');
    expect(bookDeletedToast(undefined, false)).toBe('Deleted book');
  });
});
