/**
 * What deleting a book says, wherever it is asked from (Library, Book detail).
 *
 * A book linked from an external media folder is only removed from Nostos: its
 * original files are not Nostos's to delete, so the copy must never promise
 * that they will be. Everything else is deleted for good.
 */
export interface BookDeleteCopy {
  heading: string;
  description: string;
  confirmLabel: string;
  busyLabel: string;
}

export function bookDeleteCopy(title: string | null | undefined, linked: boolean): BookDeleteCopy {
  const name = title?.trim();
  if (linked) {
    return {
      heading: name ? `Remove “${name}” from Nostos?` : 'Remove book from Nostos?',
      description:
        'This cannot be undone. The book, its notes and its reading progress are removed from Nostos. ' +
        'Your original files stay where they are and are never modified or deleted by Nostos.',
      confirmLabel: 'Remove from Nostos',
      busyLabel: 'Removing…',
    };
  }
  return {
    heading: name ? `Delete “${name}”?` : 'Delete book?',
    description:
      'This action cannot be undone. The book file, annotations, and all reading progress will be permanently deleted from your library.',
    confirmLabel: 'Delete Permanently',
    busyLabel: 'Deleting…',
  };
}

/** The success toast, worded like the Brain's topic toast (`Deleted “Name”`). */
export function bookDeletedToast(title: string | null | undefined, linked: boolean): string {
  const name = title?.trim();
  if (linked) return name ? `Removed “${name}” from Nostos` : 'Removed book from Nostos';
  return name ? `Deleted “${name}”` : 'Deleted book';
}
