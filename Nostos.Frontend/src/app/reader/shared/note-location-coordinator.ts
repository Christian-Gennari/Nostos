import { HttpErrorResponse } from '@angular/common/http';
import { Observable, firstValueFrom } from 'rxjs';

import { Note } from '../../core/dtos/note.dtos';
import { readUnlocatedNoteIds, rememberUnlocatedNoteId } from '../passage/note-locating';

/** What became of a note whose words the reader tried to find in the book. */
export type NoteLocationOutcome = 'saved' | 'deleted' | 'refused' | 'failed' | 'stale';

/**
 * The notes that joined the list since the reader last looked: each one is
 * recorded as seen, so a re-render or a note removed from the list cannot make
 * the same note arrive twice. Both readers drew saved marks this way.
 */
export function newNotesSince(seen: Set<string>, notes: readonly Note[]): Note[] {
  const arrived = notes.filter((note) => !seen.has(note.id));
  for (const note of arrived) seen.add(note.id);
  return arrived;
}

export interface NoteLocationCoordinatorOptions {
  /** True while the lookup still belongs to the book on screen. */
  isCurrent: () => boolean;
  /** The notes the reader holds, to tell a saved note from one deleted meanwhile. */
  notes: () => Note[] | null;
  /** The reader's own service call, with its own request teardown. */
  setLocation: (noteId: string, location: string) => Observable<Note>;
  /** Draw the note now that it has a location; the reader's own painting. */
  onLocated: (note: Note) => void;
  /** What the note is, for the log line, e.g. "a note in the book". */
  what: string;
}

/**
 * The bookkeeping both readers share once they have found a note's words: look
 * for the ones not tried yet, save the location once, and stop asking for the
 * ones never found or refused.
 *
 * Finding the words is format work and stays with each reader (EPUB searches
 * its spine text, PDF looks on a page's text layer). What happens next is the
 * same, and used to be written twice with subtly different rules: which notes
 * are worth another whole-book read, what a 400 means, when a note was deleted
 * while its location was being saved (issue #1610).
 */
export class NoteLocationCoordinator {
  constructor(private readonly options: NoteLocationCoordinatorOptions) {}

  /**
   * The notes still worth looking for: not already found missing on an earlier
   * opening, and eligible by the format's own rule.
   */
  candidates(notes: readonly Note[], eligible: (note: Note) => boolean): Note[] {
    const missing = readUnlocatedNoteIds();
    return notes.filter((note) => !missing.has(note.id) && eligible(note));
  }

  /**
   * The words are not in this copy of the book. Remember it: a whole-book read
   * at every opening is not worth repeating for a page bookmark or a highlight
   * from another edition. The note's own jump still looks.
   */
  rememberMissing(noteId: string): void {
    rememberUnlocatedNoteId(noteId);
  }

  /**
   * Save one found location and draw the note. A 400 is the server refusing the
   * note for good, so it is remembered like a miss; any other failure is logged
   * and retried on the next opening.
   */
  async save(note: Note, location: string): Promise<NoteLocationOutcome> {
    try {
      const saved = await firstValueFrom(this.options.setLocation(note.id, location));
      // The book may have been closed or replaced while the save was in flight.
      if (!this.options.isCurrent()) return 'stale';
      // Deleted while it was being saved: nothing to draw.
      if (!saved.cfiRange || !this.options.notes()?.some((held) => held.id === note.id)) {
        return 'deleted';
      }
      this.options.onLocated(saved);
      return 'saved';
    } catch (error) {
      if (!this.options.isCurrent()) return 'stale';
      if (error instanceof HttpErrorResponse && error.status === 400) {
        this.rememberMissing(note.id);
        return 'refused';
      }
      console.warn(`Could not save the location of ${this.options.what}:`, error);
      return 'failed';
    }
  }
}
