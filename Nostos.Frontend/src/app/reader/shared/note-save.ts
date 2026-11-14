import { Observable, Subscription } from 'rxjs';

import { Note } from '../../core/dtos/note.dtos';

/** What became of a note the reader tried to save. */
export type NoteSaveOutcome = 'saved' | 'failed' | 'stale';

export interface NoteSaveOptions {
  request: Observable<Note>;
  /**
   * Whether the answer may still be applied. Both readers check their own
   * book/opening generation: a save that resolves after another book was opened
   * must be dropped, not drawn into the new one.
   */
  isCurrent?: () => boolean;
  onSaved: (note: Note) => void;
  /** The server refused the save; the reader decides what stays on the page. */
  onFailed: (error: unknown) => void;
  /** The answer belongs to a book that is no longer open. */
  onStale?: () => void;
}

/**
 * Subscribes to a note save and tells saved, failed and stale apart in one
 * place. The readers draw their marks differently but must react to the answer
 * the same way, and both used to spell the three branches out themselves.
 */
export function saveNote(options: NoteSaveOptions): Subscription {
  const isCurrent = options.isCurrent ?? (() => true);
  return options.request.subscribe({
    next: (note) => {
      if (!isCurrent()) {
        options.onStale?.();
        return;
      }
      options.onSaved(note);
    },
    error: (error) => {
      if (!isCurrent()) {
        options.onStale?.();
        return;
      }
      options.onFailed(error);
    },
  });
}

/** Everything a note save can come to, including nothing on offer to save. */
export type NoteCommitOutcome = NoteSaveOutcome | 'nothing';

export interface NoteCommitOptions<TPending> {
  /** The passage on offer, or null when there is nothing to save. */
  pending: () => TPending | null;
  request: (pending: TPending) => Observable<Note>;
  /**
   * Draw the mark the save will make, before the request. Returns how to take
   * it back if the save fails; a format whose passage is already on the page
   * (EPUB) draws nothing and rolls nothing back.
   */
  draw?: (pending: TPending) => (() => void) | void;
  /** The save stuck: the optimistic mark becomes the saved one. */
  settle: (pending: TPending, note: Note) => void;
  /** The save failed and the reader can offer the same passage again. */
  onFailed: (pending: TPending) => void;
  /** Nothing was on offer; the shell must not be left waiting on a save. */
  onNothing?: () => void;
  isCurrent?: () => boolean;
}

/**
 * The optimistic highlight both readers commit with: draw the mark now, send
 * the note, and on failure take the mark back off the page so what stays on
 * screen is what the server actually holds.
 *
 * The formats differ in what "take it back" means — EPUB's passage is painted
 * by epub.js and is deliberately kept so Save can retry it, PDF's mark is a
 * rectangle list of its own and is removed — so the drawing is passed in. What
 * is shared is the order: draw, send, settle or roll back, never a second save
 * while one is in flight, and an answer for a closed book changes nothing
 * (issue #1610).
 */
export function commitNote<TPending>(
  options: NoteCommitOptions<TPending>,
): Promise<NoteCommitOutcome> {
  const pending = options.pending();
  if (pending === null) {
    options.onNothing?.();
    return Promise.resolve('nothing');
  }

  const rollback = options.draw?.(pending);
  return new Promise<NoteCommitOutcome>((resolve) => {
    saveNote({
      request: options.request(pending),
      isCurrent: options.isCurrent,
      onSaved: (note) => {
        options.settle(pending, note);
        resolve('saved');
      },
      onFailed: () => {
        rollback?.();
        options.onFailed(pending);
        resolve('failed');
      },
      onStale: () => resolve('stale'),
    });
  });
}
