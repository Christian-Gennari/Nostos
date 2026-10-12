import { computed, signal, type Signal, type WritableSignal } from '@angular/core';

import type { NoteSearchHit } from '../core/dtos/note.dtos';
import type { NotesService } from '../core/services/notes.service';
import type { ToastService } from '../core/services/toast.service';
import type { TopicDto } from '../core/services/topics.service';
import {
  NOTE_SEARCH_DEBOUNCE_MS,
  REVIEW_PAGE_SIZE,
  declaredTopicNames,
  newTopicNameFor,
  rankTopics,
  withTopicReference,
  type IndexSort,
} from './second-brain.helpers';

/** What notes browsing needs from the Brain, and what it reports back. */
export interface BrainNoteBrowseHost {
  readonly topics: Signal<TopicDto[]>;
  readonly indexSort: Signal<IndexSort>;
  /** The note open in the inspector. The Brain shares it with search results. */
  readonly panelNote: WritableSignal<NoteSearchHit | null>;
  isBrowsingNotes(): boolean;
  /** A note was saved or linked: the topic index and its counts may have changed. */
  noteSaved(): void;
}

/**
 * Notes as a way into the Brain: a filtered, paged list of saved notes, and
 * editing or linking the one that is open.
 *
 * The server returns bounded pages. Review remains a deliberate,
 * non-persisted subtask that starts from this list's filters.
 */
export class BrainNoteBrowse {
  /** The page of notes fetched so far, and how many match in all. */
  readonly notes = signal<NoteSearchHit[]>([]);
  readonly total = signal(0);

  // The filters. Changing one starts the list again from its first page.
  readonly query = signal('');
  readonly bookId = signal<string | null>(null);
  readonly bookTitle = signal('');
  readonly withoutTopics = signal(false);
  readonly oldestFirst = signal(false);

  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly error = signal(false);

  // Editing the open note, and linking it to a topic.
  readonly editing = signal(false);
  readonly editContent = signal('');
  readonly saving = signal(false);
  readonly pickerOpen = signal(false);
  readonly pickerQuery = signal('');
  readonly pickerId = signal<string | null>(null);

  readonly pickerCandidates = computed(() =>
    rankTopics(this.host.topics(), this.pickerQuery(), null, this.host.indexSort()),
  );

  readonly newTopicName = computed(() => newTopicNameFor(this.pickerQuery(), this.host.topics()));

  /** Bumped to disown a request that is still in flight. */
  private seq = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly notesService: NotesService,
    private readonly toast: ToastService,
    private readonly host: BrainNoteBrowseHost,
  ) {}

  /** A pending search debounce must not outlive the Brain. */
  dispose(): void {
    if (this.timer !== null) clearTimeout(this.timer);
  }

  setQuery(value: string): void {
    if (this.hasUnsavedEdit()) return;
    this.query.set(value);
    if (this.timer !== null) clearTimeout(this.timer);
    this.seq++;
    this.timer = setTimeout(() => {
      this.timer = null;
      this.reload();
    }, NOTE_SEARCH_DEBOUNCE_MS);
  }

  setWithoutTopics(value: boolean): void {
    if (this.hasUnsavedEdit()) return;
    this.withoutTopics.set(value);
    this.reload();
  }

  setBook(bookId: string | null, title = ''): void {
    if (this.hasUnsavedEdit()) return;
    this.bookId.set(bookId);
    this.bookTitle.set(bookId ? title : '');
    this.reload();
  }

  setOldestFirst(value: boolean): void {
    if (this.hasUnsavedEdit()) return;
    this.oldestFirst.set(value);
    this.reload();
  }

  /** Start the list again from its first page, closing the open note. */
  reload(): void {
    this.seq++;
    this.notes.set([]);
    this.total.set(0);
    this.loaded.set(false);
    this.host.panelNote.set(null);
    this.loadPage();
  }

  /** Blocks leaving the open note while an edit is unsaved, and says why. */
  hasUnsavedEdit(): boolean {
    if (!this.host.isBrowsingNotes()) return false;
    if (this.saving() ||
      (this.editing() && this.editContent() !== this.host.panelNote()?.content)) {
      this.toast.error('Save or cancel the note edit before leaving it');
      return true;
    }
    return false;
  }

  startEdit(): void {
    const note = this.host.panelNote();
    if (!note) return;
    this.pickerOpen.set(false);
    this.editContent.set(note.content);
    this.editing.set(true);
  }

  cancelEdit(): void {
    if (this.saving()) return;
    this.editing.set(false);
  }

  saveEdit(): void {
    const note = this.host.panelNote();
    if (!note || this.saving()) return;
    const content = this.editContent();
    if (content === note.content) { this.cancelEdit(); return; }
    this.saveNote(note, content);
  }

  openPicker(): void {
    this.pickerQuery.set('');
    this.pickerId.set(null);
    this.pickerOpen.set(true);
  }

  linkTopic(): void {
    const note = this.host.panelNote();
    const topic = this.host.topics().find((item) => item.id === this.pickerId());
    if (!note || !topic || this.saving()) return;
    const content = withTopicReference(note.content, topic.name);
    if (content === note.content) { this.pickerOpen.set(false); return; }
    this.saveNote(note, content);
  }

  createTopic(): void {
    const note = this.host.panelNote();
    const name = this.newTopicName();
    if (!note || !name || this.saving()) return;
    // The existing processor creates the topic from this same canonical link.
    this.saveNote(note, withTopicReference(note.content, name));
  }

  private saveNote(note: NoteSearchHit, content: string): void {
    this.saving.set(true);
    this.notesService.update(note.id, { content, selectedText: note.selectedText ?? undefined }).subscribe({
      next: () => {
        const saved: NoteSearchHit = { ...note, content, topicNames: declaredTopicNames(content),
          snippet: note.selectedText || content.slice(0, 160) };
        this.notes.update((items) => this.withoutTopics() && saved.topicNames.length
          ? items.filter((item) => item.id !== note.id)
          : items.map((item) => item.id === note.id ? saved : item));
        if (this.withoutTopics() && saved.topicNames.length) {
          this.total.update((total) => Math.max(0, total - 1));
        }
        if (this.host.panelNote()?.id === note.id) this.host.panelNote.set(saved);
        this.saving.set(false);
        this.editing.set(false);
        this.pickerOpen.set(false);
        this.host.noteSaved();
        this.toast.success('Note saved');
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Could not save the note');
      },
    });
  }

  loadPage(): void {
    if (this.loading() && this.loaded()) return;
    const seq = ++this.seq;
    const offset = this.notes().length;
    this.loading.set(true);
    this.error.set(false);
    this.notesService.browse({
      query: this.query(),
      bookId: this.bookId() ?? undefined,
      withoutTopics: this.withoutTopics(),
      oldestFirst: this.oldestFirst(),
      limit: REVIEW_PAGE_SIZE,
      offset,
    }).subscribe({
      next: (page) => {
        if (seq !== this.seq) return;
        const held = new Set(this.notes().map((note) => note.id));
        this.notes.update((notes) => [
          ...notes,
          ...(page.items ?? []).filter((note) => !held.has(note.id)),
        ]);
        this.total.set(page.totalCount);
        this.loading.set(false);
        this.loaded.set(true);
      },
      error: () => {
        if (seq !== this.seq) return;
        this.loading.set(false);
        this.error.set(true);
      },
    });
  }
}
