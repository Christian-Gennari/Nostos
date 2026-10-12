import { computed, signal, type Signal } from '@angular/core';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import type { NotesService } from '../core/services/notes.service';
import type { ToastService } from '../core/services/toast.service';
import type { TopicDto } from '../core/services/topics.service';
import {
  REVIEW_PAGE_SIZE,
  declaresTopic,
  newTopicNameFor,
  rankTopics,
  withTopicReference,
  type IndexSort,
} from './second-brain.helpers';

/** What the queue needs from the Brain, and what it reports back. */
export interface BrainNoteReviewHost {
  readonly topics: Signal<TopicDto[]>;
  readonly indexSort: Signal<IndexSort>;
  isReviewing(): boolean;
  /** A note was saved or linked: the topic index and its counts may have changed. */
  noteSaved(): void;
  /** The note in focus changed; a session that remembers it should record it. */
  focusChanged(): void;
}

/** Which notes a review walks through. Empty means every note with no topic. */
export interface BrainNoteReviewFilters {
  query: string;
  bookId: string | null;
  oldestFirst: boolean;
}

/**
 * The unlinked-note review queue (issue #256): the notes with no topic, one
 * in focus at a time, and what can be done to it: skip it, edit it, or link
 * it to a topic.
 *
 * Deliberately empty until the user asks for it. Nothing here is loaded on a
 * normal Brain visit: unlinked notes are a review task, not the rail's second
 * content type, so opening the Brain must not fetch them at all.
 *
 * Entering and leaving review, and what the Brain shows afterwards, are the
 * Brain's business. This class only knows the queue.
 */
export class BrainNoteReview {
  /** Notes fetched so far and not yet resolved, in the server's order. */
  queue = signal<NoteSearchHit[]>([]);

  /** How many are still waiting, including rows not fetched yet. */
  total = signal(0);

  loading = signal(false);
  loaded = signal(false);
  error = signal(false);

  /** The note under review, chosen explicitly. `null` focuses the first row. */
  focusId = signal<string | null>(null);

  saving = signal(false);
  editing = signal(false);
  editContent = signal('');

  pickerOpen = signal(false);
  pickerQuery = signal('');
  pickerTopicId = signal<string | null>(null);
  newTopicName = computed(() => newTopicNameFor(this.pickerQuery(), this.host.topics()));

  /** Bumped to disown a request that is still in flight. */
  private seq = 0;

  /** True once a note was saved or linked: the surface review returns to is stale. */
  mutated = false;

  /** The note a restored session wants back in focus, until its page arrives. */
  readonly restoringFocusId = signal<string | null>(null);

  private filterQuery = '';
  private filterBookId: string | null = null;
  private filterOldestFirst = false;

  /** The note under review: the chosen one, or the head of the queue. */
  note = computed<NoteSearchHit | null>(() => {
    const queue = this.queue();
    if (!queue.length) return null;

    const restoringId = this.restoringFocusId();
    if (restoringId) {
      return queue.find((row) => row.id === restoringId) ?? null;
    }

    const id = this.focusId();
    return (id ? queue.find((row) => row.id === id) : undefined) ?? queue[0];
  });

  position = computed(() => {
    const note = this.note();
    if (!note) return null;
    const index = this.queue().findIndex((row) => row.id === note.id);
    return index < 0 ? null : index + 1;
  });

  /**
   * Whether the queue still holds rows this browser has not fetched. The local
   * queue is exactly the rows fetched so far that are still unresolved, so the
   * gap to the total IS the unfetched remainder.
   */
  hasMore = computed(() => this.queue().length < this.total());

  canGoBack = computed(() => this.queue().findIndex(
    (row) => row.id === this.note()?.id) > 0);

  canAdvance = computed(() => {
    const index = this.queue().findIndex((row) => row.id === this.note()?.id);
    return index >= 0 && (index < this.queue().length - 1 || this.hasMore());
  });

  /** The topic the user picked to link the reviewed note to. */
  pickerTopic = computed(() => {
    const id = this.pickerTopicId();
    return id ? this.host.topics().find((topic) => topic.id === id) ?? null : null;
  });

  pickerCandidates = computed(() => rankTopics(this.host.topics(), this.pickerQuery(), null, this.host.indexSort()));

  constructor(
    private readonly notesService: NotesService,
    private readonly toast: ToastService,
    private readonly host: BrainNoteReviewHost,
  ) {}

  /**
   * Start a review from nothing. A previous request may still be in flight;
   * its response belongs to the previous review, including its filters, so it
   * is disowned here.
   */
  begin(filters: BrainNoteReviewFilters): void {
    this.mutated = false;
    this.setFilters(filters);
    this.seq++;
    this.loading.set(false);
    this.loaded.set(false);
    this.error.set(false);
    this.queue.set([]);
    this.total.set(0);
    this.focusId.set(null);
    this.editing.set(false);
    this.closePicker();
  }

  /** Drop the queue and disown any request in flight, without starting over. */
  abandon(): void {
    ++this.seq;
    this.loading.set(false);
    this.queue.set([]);
    this.total.set(0);
  }

  /** Resume a remembered review: its filters, and the note that was in focus. */
  resume(filters: BrainNoteReviewFilters, focusId: string | null): void {
    this.setFilters(filters);
    this.focusId.set(focusId);
    this.restoringFocusId.set(focusId);
  }

  private setFilters(filters: BrainNoteReviewFilters): void {
    this.filterQuery = filters.query;
    this.filterBookId = filters.bookId;
    this.filterOldestFirst = filters.oldestFirst;
  }

  /** Blocks leaving the note while an edit is unsaved, and says why. */
  hasUnsavedEdit(): boolean {
    if (!this.host.isReviewing()) return false;
    if (this.saving() ||
      (this.editing() && this.editContent() !== this.note()?.content)) {
      this.toast.error('Save or cancel the note edit before leaving it');
      return true;
    }
    return false;
  }

  /**
   * Fetch the next page of the queue.
   *
   * The offset is the number of rows still held, not the number fetched: a note
   * that has been resolved is gone from the server's set too, so the rows this
   * browser holds are exactly the first N of the server's current order.
   */
  loadPage(advanceFromId: string | null = null): void {
    if (this.loading()) return;
    const seq = this.seq;
    this.loading.set(true);
    this.error.set(false);
    const pageRequest = this.filterQuery || this.filterBookId || this.filterOldestFirst
      ? this.notesService.browse({
          query: this.filterQuery,
          bookId: this.filterBookId ?? undefined,
          withoutTopics: true,
          oldestFirst: this.filterOldestFirst,
          limit: REVIEW_PAGE_SIZE,
          offset: this.queue().length,
        })
      : this.notesService.unlinkedPage(REVIEW_PAGE_SIZE, this.queue().length);
    pageRequest.subscribe({
      next: (page) => {
        if (seq !== this.seq) return;
        const held = new Set(this.queue().map((row) => row.id));
        const fresh = (page.items ?? []).filter((row) => !held.has(row.id));
        this.queue.set([...this.queue(), ...fresh]);
        this.total.set(page.totalCount ?? this.queue().length);
        if (advanceFromId && this.note()?.id === advanceFromId && fresh.length) {
          this.focusId.set(fresh[0].id);
          this.host.focusChanged();
        }
        this.loading.set(false);
        this.loaded.set(true);

        const restoringId = this.restoringFocusId();
        if (restoringId) {
          if (this.queue().some((note) => note.id === restoringId)) {
            this.restoringFocusId.set(null);
          } else if (fresh.length > 0 && this.hasMore()) {
            this.loadPage();
          } else {
            this.restoringFocusId.set(null);
            this.focusId.set(null);
            this.host.focusChanged();
            this.toast.info('The previously selected note is no longer in this review queue');
          }
        }
      },
      error: () => {
        if (seq !== this.seq) return;
        this.loading.set(false);
        this.error.set(true);
        this.toast.error('Notes with no topic could not be loaded');
      },
    });
  }

  /** Leave the focused note (mobile's way back to the queue). Focus decides nothing. */
  clearFocus(): void {
    if (this.hasUnsavedEdit()) return;
    this.editing.set(false);
    this.closePicker();
    this.restoringFocusId.set(null);
    this.focusId.set(null);
    this.host.focusChanged();
  }

  /** Focus a queued note. Focusing decides nothing — it only moves the review on. */
  focusNote(id: string): void {
    if (this.hasUnsavedEdit()) return;
    this.editing.set(false);
    this.closePicker();
    this.restoringFocusId.set(null);
    this.focusId.set(id);
    this.host.focusChanged();
  }

  /**
   * Move to the next queued note without touching it.
   *
   * Skipping is not a decision: the note is neither linked nor edited, so it
   * stays at the full count and the user can leave the mode and find it still
   * waiting.
   */
  skip(): void {
    if (this.hasUnsavedEdit()) return;
    const queue = this.queue();
    const current = this.note();
    if (!current) return;
    const index = current ? queue.findIndex((row) => row.id === current.id) : -1;
    const next = queue[index + 1];
    if (next) this.focusNote(next.id);
    else if (this.hasMore()) this.loadPage(current.id);
  }

  previous(): void {
    if (this.hasUnsavedEdit()) return;
    const index = this.queue().findIndex((row) => row.id === this.note()?.id);
    if (index > 0) this.focusNote(this.queue()[index - 1].id);
  }

  startEdit(): void {
    const note = this.note();
    if (!note) return;
    this.editContent.set(note.content);
    this.closePicker();
    this.editing.set(true);
  }

  cancelEdit(): void {
    this.editing.set(false);
    this.editContent.set('');
  }

  /**
   * Save the reviewed note's text.
   *
   * This is the canonical note edit path, unchanged: the server re-reads the
   * `[[Topic]]` links from the body on save. What review mode adds is the
   * consequence — a note that now declares a topic has been resolved, so it
   * leaves the queue immediately instead of waiting for a reload.
   */
  saveEdit(): void {
    const note = this.note();
    if (!note || this.saving()) return;

    const content = this.editContent();
    if (content === note.content) {
      this.cancelEdit();
      return;
    }

    this.saving.set(true);
    this.notesService.update(note.id, { content, selectedText: note.selectedText ?? undefined }).subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(false);
        this.mutated = true;
        this.host.noteSaved();

        if (declaresTopic(content)) {
          this.remove(note.id);
          this.toast.success('Note linked to a topic');
        } else {
          // Still belongs to no topic. Keep it queued, showing what was just
          // written, rather than pretending the edit resolved anything.
          this.queue.update((rows) =>
            rows.map((row) => (row.id === note.id ? { ...row, content } : row))
          );
          this.toast.success('Note saved');
        }
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Failed to update note');
      },
    });
  }

  openPicker(): void {
    this.editing.set(false);
    this.pickerQuery.set('');
    this.pickerTopicId.set(null);
    this.pickerOpen.set(true);
  }

  closePicker(): void {
    this.pickerOpen.set(false);
    this.pickerQuery.set('');
    this.pickerTopicId.set(null);
  }

  chooseTopic(id: string): void {
    this.pickerTopicId.set(id);
  }

  /**
   * Link the reviewed note to an existing topic.
   *
   * The association written here is the canonical one this codebase has: the note
   * body gains an explicit `[[Topic]]` reference and the server rebuilds the
   * note's topic links from it. Nothing is invented — the topic must already
   * exist, it is chosen by the user, and no prose is rewritten beyond appending
   * the reference. Membership is never stored as a link the next note save would
   * silently drop.
   */
  confirmLink(): void {
    const note = this.note();
    const topic = this.pickerTopic();
    if (!note || !topic || this.saving()) return;

    this.saveLink(note, topic.name);
  }

  createTopic(): void {
    const note = this.note();
    const name = this.newTopicName();
    if (!note || !name || this.saving()) return;
    this.saveLink(note, name);
  }

  private saveLink(note: NoteSearchHit, name: string): void {
    const content = withTopicReference(note.content, name);
    this.saving.set(true);
    this.notesService.update(note.id, { content, selectedText: note.selectedText ?? undefined }).subscribe({
      next: () => {
        this.saving.set(false);
        this.mutated = true;
        this.closePicker();
        this.host.noteSaved();
        this.remove(note.id);
        this.toast.success(`Linked to “${name}”`);
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Failed to link the note');
      },
    });
  }

  /**
   * Take a resolved note out of the queue and off the count, in place.
   *
   * Resolving is a deliberate act that only ever shrinks the waiting set, so the
   * row can go immediately — a refetch would make the user wait to see the effect
   * of their own decision. Focus moves to the row that took its place so the
   * queue keeps flowing.
   */
  remove(noteId: string): void {
    const queue = this.queue();
    const index = queue.findIndex((row) => row.id === noteId);
    if (index < 0) return;

    const remaining = queue.filter((row) => row.id !== noteId);
    this.queue.set(remaining);
    this.total.update((total) => Math.max(0, total - 1));
    this.editing.set(false);

    if (this.focusId() === noteId) {
      const following = remaining[index] ?? remaining[index - 1] ?? null;
      this.focusId.set(following ? following.id : null);
      this.host.focusChanged();
    }
    if (remaining.length === 0 && this.total() > 0) this.loadPage();
  }
}
