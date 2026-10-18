import { Injectable, computed, inject, signal } from '@angular/core';
import { catchError, forkJoin, map, of } from 'rxjs';

import type { Note, NoteSearchHit } from '../core/dtos/note.dtos';
import { NotesService } from '../core/services/notes.service';
import { ToastService } from '../core/services/toast.service';
import { TopicsService } from '../core/services/topics.service';
import type { NoteItemEdit } from '../ui/note-item/note-item.component';
import { BrainStateService } from './brain-state.service';

/** What a page tells the actions about its notes, and how it hears back. */
export interface BrainNoteHost {
  /** A note changed (topics, words): show this version. */
  replace(note: NoteSearchHit): void;
  /** A note is gone. */
  remove(noteId: string): void;
}

/**
 * Everything a reader can do to notes on a Brain page: add or remove a topic
 * (one note or many, with Undo), edit, delete, keep with a writing. Provided
 * per page; the page says how to show the result.
 */
@Injectable()
export class BrainNoteActionsService {
  private readonly notes = inject(NotesService);
  private readonly topics = inject(TopicsService);
  private readonly toast = inject(ToastService);
  private readonly state = inject(BrainStateService);

  private host: BrainNoteHost | null = null;

  /** Notes with a change in flight; their topic controls wait. */
  readonly busy = signal<ReadonlySet<string>>(new Set());

  // Confirmations and dialogs the page renders.
  readonly deleteIds = signal<string[]>([]);
  readonly deleting = signal(false);
  readonly handoffIds = signal<string[]>([]);

  readonly deleteHeading = computed(() => {
    const count = this.deleteIds().length;
    return count === 1 ? 'Delete this note?' : `Delete ${count} notes?`;
  });

  attach(host: BrainNoteHost): void {
    this.host = host;
  }

  isBusy(noteId: string): boolean {
    return this.busy().has(noteId);
  }

  /** Adds `topic` to one note. */
  addTopic(note: NoteSearchHit, topic: string): void {
    this.link([topic], [note.id], null);
  }

  /** Removes `topic` from one note, with Undo. */
  removeTopic(note: NoteSearchHit, topic: string): void {
    this.change('unlink', [topic], [note.id], {
      message: () => `Removed “${topic}”`,
      undo: (changed) => this.link([topic], changed, null),
    });
  }

  /** Adds `topic` to several notes, with Undo for exactly the notes it changed. */
  addTopicToMany(noteIds: string[], topic: string): void {
    this.link([topic], noteIds, (changed) =>
      changed.length === 0
        ? `Every selected note already has “${topic}”`
        : `Added “${topic}” to ${changed.length === 1 ? '1 note' : `${changed.length} notes`}`);
  }

  private link(topics: string[], noteIds: string[], message: ((changed: string[]) => string) | null): void {
    this.change('link', topics, noteIds, message
      ? { message, undo: (changed) => this.change('unlink', topics, changed, null) }
      : null);
  }

  private change(
    kind: 'link' | 'unlink',
    topics: string[],
    noteIds: string[],
    feedback: { message: (changed: string[]) => string; undo: (changed: string[]) => void } | null,
  ): void {
    if (noteIds.length === 0) return;
    this.setBusy(noteIds, true);
    const request = kind === 'link' ? this.topics.link(topics, noteIds) : this.topics.unlink(topics, noteIds);
    request.subscribe({
      next: (result) => {
        this.setBusy(noteIds, false);
        result.notes.forEach((note) => this.host?.replace(note));
        if (result.changedNoteIds.length) this.state.refresh();
        if (!feedback) return;
        const changed = result.changedNoteIds;
        this.toast.success(feedback.message(changed), changed.length
          ? { label: 'Undo', run: () => feedback.undo(changed) }
          : undefined);
      },
      error: () => {
        this.setBusy(noteIds, false);
        this.toast.error(kind === 'link' ? 'Could not add the topic. Try again.' : 'Could not remove the topic. Try again.');
      },
    });
  }

  /** Saves an edit, then shows the note as the server now has it. */
  edit(edit: NoteItemEdit): void {
    this.setBusy([edit.id], true);
    const { id, ...dto } = edit;
    this.notes.update(id, dto).subscribe({
      next: () => this.reload(id, 'Note saved'),
      error: () => {
        this.setBusy([id], false);
        this.toast.error('Could not save the note. Try again.');
      },
    });
  }

  /** The note's original wording was restored (capture details): show it. */
  restored(note: Note): void {
    this.reload(note.id, null);
  }

  private reload(id: string, message: string | null): void {
    this.notes.get(id).subscribe({
      next: (note) => {
        this.setBusy([id], false);
        this.host?.replace(note);
        this.state.refresh();
        if (message) this.toast.success(message);
      },
      error: () => {
        this.setBusy([id], false);
        if (message) this.toast.success(message);
      },
    });
  }

  askDelete(noteIds: string[]): void {
    this.deleteIds.set(noteIds);
  }

  cancelDelete(): void {
    if (!this.deleting()) this.deleteIds.set([]);
  }

  /** Deletes the notes asked about; reports any that failed. */
  confirmDelete(onDone?: (deleted: string[]) => void): void {
    const ids = this.deleteIds();
    if (!ids.length || this.deleting()) return;
    this.deleting.set(true);
    forkJoin(ids.map((id) => this.notes.delete(id).pipe(map(() => id), catchError(() => of(null)))))
      .subscribe((results) => {
        const deleted = results.filter((id): id is string => id !== null);
        deleted.forEach((id) => this.host?.remove(id));
        this.deleting.set(false);
        this.deleteIds.set([]);
        this.state.refresh();
        onDone?.(deleted);
        if (deleted.length === ids.length) {
          this.toast.success(deleted.length === 1 ? 'Note deleted' : `${deleted.length} notes deleted`);
        } else {
          this.toast.error(`${ids.length - deleted.length} of ${ids.length} notes could not be deleted.`);
        }
      });
  }

  keepWithWriting(noteIds: string[]): void {
    this.handoffIds.set(noteIds);
  }

  closeHandoff(): void {
    this.handoffIds.set([]);
  }

  private setBusy(ids: string[], busy: boolean): void {
    this.busy.update((current) => {
      const next = new Set(current);
      ids.forEach((id) => (busy ? next.add(id) : next.delete(id)));
      return next;
    });
  }
}
