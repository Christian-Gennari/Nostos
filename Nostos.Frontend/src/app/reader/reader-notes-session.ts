import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NotesService } from '../core/services/notes.service';
import { Book } from '../core/dtos/book.dtos';
import { Note, NoteSearchHit } from '../core/dtos/note.dtos';
import { formatOf, formatOfConcept } from '../core/book-format';
import { noteAsItem } from '../ui/note-item/note-item.helpers';

export type ReaderNoteRow = { note: Note; item: NoteSearchHit };

/**
 * The open book's notes for the reader: the list the page readers draw from,
 * each note's topics, and the notes made in the book's other editions.
 *
 * These were ~150 lines of the shell that mixed the HTTP calls, the paging of
 * the topic listing, a generation counter, and the shape the drawer renders
 * (issue #1607). They are one shell-scoped session: every request ends with the
 * reader through `takeUntilDestroyed`, and a reload is guarded by the same
 * generation the list was read with, so a late answer cannot replace a newer
 * book's notes.
 */
@Injectable()
export class ReaderNotesSession {
  private readonly notesService = inject(NotesService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly book = signal<Book | null>(null);

  /** The book's notes, newest first. */
  readonly notes = signal<Note[]>([]);
  /** The book's notes have been read once; null until then for the page. */
  readonly loaded = signal(false);
  /** The book's notes could not be read; the panel offers Try again. */
  readonly failed = signal(false);
  /** Each note's topics, by note id (the reader's own list does not carry them). */
  readonly topicNames = signal<ReadonlyMap<string, readonly string[]>>(new Map());
  /** The notes made in the book's other editions, never painted on this page. */
  readonly otherEditionNotes = signal<NoteSearchHit[]>([]);
  readonly otherNotesOpen = signal(false);

  /** What the page readers draw from: null until the book's notes are in. */
  readonly readerNotes = computed(() => (this.loaded() ? this.notes() : null));

  /** The notes panel's rows: each note, and the same note as its item shows it. */
  readonly rows = computed<ReaderNoteRow[]>(() => {
    const topics = this.topicNames();
    return this.notes().map((note) => ({ note, item: noteAsItem(note, topics.get(note.id)) }));
  });

  /** "4 from the audiobook", or "6 from other editions"; null when there are none. */
  readonly otherNotesLabel = computed(() => {
    const notes = this.otherEditionNotes();
    if (!notes.length) return null;
    const editions = new Set(notes.map((note) => note.bookId.toLowerCase()));
    const format = formatOfConcept(notes[0].bookFormat);
    const book = this.book();
    const own = book ? formatOf(book).format : null;
    const kind =
      editions.size === 1 && format && format.format !== own
        ? ` the ${
            format.concept === 'physical'
              ? 'physical copy'
              : format.format === 'PDF'
                ? 'PDF'
                : format.format.toLowerCase()
          }`
        : null;
    return `${notes.length} from${kind ?? (editions.size === 1 ? ' another edition' : ' other editions')}`;
  });

  private topicsSeq = 0;

  /** A different book opened: nothing of the last one may show through. */
  reset(): void {
    this.topicsSeq++;
    this.notes.set([]);
    this.loaded.set(false);
    this.failed.set(false);
    this.topicNames.set(new Map());
    this.otherEditionNotes.set([]);
  }

  /** Reads the book's notes and, beside them, their topics. */
  load(book: Book): void {
    this.book.set(book);
    this.failed.set(false);
    this.notesService
      .list(book.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (notes) => {
          this.notes.set(notes.reverse());
          this.loaded.set(true);
        },
        error: () => this.failed.set(true),
      });
    this.loadTopics(book);
  }

  /**
   * A note this reader just wrote joins the list where it was made, with no
   * second read of the book's notes. The save already answered with the note;
   * only its topics (a [[link]] in its text) are asked for, and only when it
   * has text to link from.
   */
  addCreated(bookId: string, created: Note): void {
    if (this.book()?.id !== bookId) return;
    this.notes.update((notes) => (notes.some((n) => n.id === created.id) ? notes : [created, ...notes]));
    if (created.content?.trim()) this.refreshTopics(created.id);
  }

  /** A quote Ask Nostos saved to this book joins the list like a note made here. */
  addCaptured(bookId: string, noteId: string): void {
    const same = (a: string | null | undefined, b: string) => a?.toLowerCase() === b.toLowerCase();
    if (this.notes().some((note) => same(note.id, noteId))) return;
    this.notesService
      .get(noteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (hit) => {
          if (!same(hit.bookId, bookId)) return;
          this.addCreated(bookId, {
            ...hit,
            bookTitle: hit.bookTitle ?? undefined,
            selectedText: hit.selectedText ?? undefined,
            cfiRange: hit.cfiRange ?? undefined,
          });
        },
        // The note was saved; it is in the list at the next opening.
        error: () => undefined,
      });
  }

  /**
   * Every note's topics in this book, read 200 at a time until all are in. A
   * book with several editions is read as the whole work in the same pass: the
   * other editions' notes are kept apart, for the collapsed group below the list.
   */
  private loadTopics(book: Book): void {
    const seq = ++this.topicsSeq;
    const manyEditions = !!book.workId && (book.otherEditions?.length ?? 0) > 0;
    const filter = manyEditions ? { workId: book.workId } : { bookId: book.id };
    const others: NoteSearchHit[] = [];
    const page = (held: Map<string, readonly string[]>, offset: number) =>
      this.notesService
        .browse({ ...filter, limit: 200, offset })
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: (result) => {
            if (seq !== this.topicsSeq) return;
            for (const hit of result.items) {
              if (!manyEditions || hit.bookId?.toLowerCase() === book.id.toLowerCase()) {
                held.set(hit.id, hit.topicNames ?? []);
              } else {
                others.push(hit);
              }
            }
            const read = offset + result.items.length;
            if (result.items.length && read < result.totalCount) page(held, read);
            else {
              this.topicNames.set(held);
              this.otherEditionNotes.set(others);
            }
          },
        });
    if (!manyEditions) this.otherEditionNotes.set([]);
    page(new Map(), 0);
  }

  /** A note as the Brain's actions now have it: its topics and its words. */
  replaceFromHit(hit: NoteSearchHit): void {
    this.topicNames.update((current) => new Map(current).set(hit.id, hit.topicNames ?? []));
    this.notes.update((notes) =>
      notes.map((note) => (note.id === hit.id ? { ...note, content: hit.content } : note)),
    );
  }

  /** A note that was edited or recoloured: swap the row and re-read its topics. */
  replace(updated: Note): void {
    this.notes.update((notes) => notes.map((note) => (note.id === updated.id ? updated : note)));
    this.refreshTopics(updated.id);
  }

  /** A [[link]] written in a note links a topic: show the note's topics as saved. */
  refreshTopics(noteId: string): void {
    this.notesService
      .get(noteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (hit) =>
          this.topicNames.update((current) => new Map(current).set(hit.id, hit.topicNames ?? [])),
      });
  }

  /** The reader found a note's text in the book; keep the list in step. */
  noteLocated(located: Note): void {
    this.notes.update((notes) =>
      notes.map((note) => (note.id === located.id ? { ...note, cfiRange: located.cfiRange } : note)),
    );
  }

  /** A note is gone: drop it from the list and say which one it was. */
  remove(noteId: string): Note | undefined {
    const removed = this.notes().find((note) => note.id === noteId);
    this.notes.update((notes) => notes.filter((note) => note.id !== noteId));
    return removed;
  }
}
