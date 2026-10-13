/**
 * A book the user wants to read and does not have yet. Not a library book: it
 * has no format, no file and no reading state.
 */
export interface ReadingListEntry {
  id: string;
  title: string;
  author: string | null;
  isbn: string | null;
  publishedDate: string | null;
  /** The user's own reason for wanting to read it. */
  why: string | null;
  /** Who or what recommended it, in the user's words. */
  recommendedBy: string | null;
  /** Slot on the ordered "Next up" shelf, lowest first; null means "Someday". */
  nextPosition: number | null;
  createdAt: string;
  /** A library book that looks like the same title, when there is one. */
  ownedBookId?: string | null;
}

export interface SaveReadingListEntry {
  title: string;
  author?: string | null;
  isbn?: string | null;
  publishedDate?: string | null;
  why?: string | null;
  recommendedBy?: string | null;
  /** Create only: put the new entry at the end of "Next up". */
  readNext?: boolean;
}
