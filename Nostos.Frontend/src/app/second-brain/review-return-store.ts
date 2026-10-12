import type { NoteSort } from './second-brain.helpers';

const STORAGE_PREFIX = 'nostos.brain.reviewReturn.v1';

/**
 * Where the Brain was when review was entered, so leaving review (or
 * reloading the page during one) can put it back.
 */
export interface ReviewReturnState {
  returnMode: 'list' | 'notes';
  searchQuery: string;
  selectedId: string | null;
  noteSearchQuery: string;
  sourceFilter: string;
  noteSort: NoteSort;
  browseQuery: string;
  browseBookId: string | null;
  browseBookTitle: string;
  browseWithoutTopics: boolean;
  browseOldestFirst: boolean;
  focusedNoteId: string | null;
  reviewId: string | null;
}

/** What changed when the store was told who is using the Brain. */
export interface ReviewReturnIdentityChange {
  changed: boolean;
  /** Someone else's state was held before, and has been discarded. */
  hadPrevious: boolean;
  /** There is an identity now, so state can be kept. */
  hasCurrent: boolean;
}

/**
 * Keeps one person's review return state in session storage.
 *
 * The key carries who it belongs to (the SelfHosted owner, or a Cloud
 * account), and nothing is kept until that is known, so one account never
 * resumes another's review. Storage that is missing or refuses is not an
 * error: review still works within the visit.
 */
export class ReviewReturnStore {
  private key: string | null = null;

  /** Say whose state this is. A different identity discards the previous one's. */
  setIdentity(identity: string | null): ReviewReturnIdentityChange {
    const nextKey = identity ? `${STORAGE_PREFIX}:${encodeURIComponent(identity)}` : null;
    if (nextKey === this.key) {
      return { changed: false, hadPrevious: this.key !== null, hasCurrent: this.key !== null };
    }

    const previousKey = this.key;
    this.key = nextKey;

    if (previousKey) {
      try {
        sessionStorage.removeItem(previousKey);
      } catch {
        // Storage unavailable.
      }
    }

    return { changed: true, hadPrevious: previousKey !== null, hasCurrent: nextKey !== null };
  }

  /** The stored state, or null. Anything that is not exactly the expected shape is discarded. */
  read(): ReviewReturnState | null {
    if (!this.key) return null;

    try {
      const raw = sessionStorage.getItem(this.key);
      if (!raw) return null;

      const value: unknown = JSON.parse(raw);
      if (!value || typeof value !== 'object') return null;

      const state = value as Partial<ReviewReturnState>;
      if (
        (state.returnMode !== 'list' && state.returnMode !== 'notes') ||
        typeof state.searchQuery !== 'string' ||
        typeof state.noteSearchQuery !== 'string' ||
        typeof state.sourceFilter !== 'string' ||
        !['newest', 'oldest', 'source'].includes(state.noteSort ?? '') ||
        typeof state.browseQuery !== 'string' ||
        typeof state.browseBookTitle !== 'string' ||
        typeof state.browseWithoutTopics !== 'boolean' ||
        typeof state.browseOldestFirst !== 'boolean' ||
        (state.selectedId != null && typeof state.selectedId !== 'string') ||
        (state.browseBookId != null && typeof state.browseBookId !== 'string') ||
        (state.focusedNoteId != null && typeof state.focusedNoteId !== 'string') ||
        (state.reviewId != null && typeof state.reviewId !== 'string')
      ) {
        sessionStorage.removeItem(this.key);
        return null;
      }

      return state as ReviewReturnState;
    } catch {
      return null;
    }
  }

  write(state: ReviewReturnState): void {
    if (!this.key) return;
    try {
      sessionStorage.setItem(this.key, JSON.stringify(state));
    } catch {
      // Storage unavailable: review remains functional within this visit.
    }
  }

  clear(): void {
    if (!this.key) return;
    try {
      sessionStorage.removeItem(this.key);
    } catch {
      // Storage unavailable.
    }
  }
}
