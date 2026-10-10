import { Injectable, inject, signal, computed, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ToastService } from '../core/services/toast.service';
import { HttpEventType } from '@angular/common/http';
import { finalize } from 'rxjs';

// DTOs & Services
import { Book, LinkableBookDto } from '../core/dtos/book.dtos';
import { Note } from '../core/dtos/note.dtos';
import { Collection } from '../core/dtos/collection.dtos';
import { TopicDto } from '../core/services/topics.service';
import { BooksService } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { CollectionsService } from '../core/services/collections.service';
import { TopicsService } from '../core/services/topics.service';
import { TopicAutocompleteService } from '../ui/topic-autocomplete-panel/topic-autocomplete.service';
import { ImportService } from '../core/services/import.service';
import { isImportInFlight, importStageLabel } from '../core/dtos/import.dtos';

@Injectable()
export class BookDetailStore {
  // Dependencies
  private booksService = inject(BooksService);
  private notesService = inject(NotesService);
  private collectionsService = inject(CollectionsService);
  private topicsService = inject(TopicsService);
  private autocompleteService = inject(TopicAutocompleteService);
  private toast = inject(ToastService);
  private imports = inject(ImportService);
  private destroyRef = inject(DestroyRef);
  readonly importConnectionState = this.imports.connectionState;

  // --- STATE ---
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly book = signal<Book | null>(null);
  readonly notes = signal<Note[]>([]);
  readonly collections = signal<Collection[]>([]);
  readonly topicMap = signal<Map<string, TopicDto>>(new Map());

  /** The same per-book import feed that supplies the Library cover and row. */
  readonly importActivity = computed(() => {
    const book = this.book();
    return book ? this.imports.progressByBookId().get(book.id) : undefined;
  });
  readonly isImporting = computed(() => {
    const book = this.book();
    const activity = this.importActivity();
    return !!book && (book.status === 1 || book.status === 2 ||
      (!!activity && isImportInFlight(activity)));
  });
  readonly importStage = computed(() => {
    const activity = this.importActivity();
    if (activity && isImportInFlight(activity)) return importStageLabel(activity);
    return this.book()?.status === 2 ? 'Transcoding' : 'Downloading';
  });
  readonly importPercent = computed(() => {
    const activity = this.importActivity();
    return activity && isImportInFlight(activity) ? activity.percent : null;
  });
  readonly uploadingFile = signal(false);

  constructor() {
    // Terminal import updates are already fetched by ImportService. Apply the
    // resulting single-book patch here as well as in Library, without a reload.
    this.imports.bookPatched.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((book) => {
      if (this.book()?.id === book.id) this.book.set(book);
    });
  }

  /** True while a reset-progress command is in flight (duplicate-click guard). */
  readonly resettingProgress = signal(false);

  /** True while a work link/unlink command is in flight (duplicate-click guard). */
  readonly linkingWork = signal(false);

  /** Books offered by the advanced "link to…" picker. */
  readonly linkCandidates = signal<LinkableBookDto[]>([]);
  readonly candidatesLoading = signal(false);

  // --- ACTIONS ---

  loadAllData(id: string) {
    // Reset state for new book
    if (this.book()?.id !== id) {
      this.book.set(null);
      this.notes.set([]);
      this.error.set(null);
      this.loading.set(true);
    }

    this.loadBook(id);
    this.loadNotes(id);
    this.loadCollections();
    this.loadTopics();
  }

  loadBook(id: string, options: { forceImageRefresh?: boolean; background?: boolean } = {}) {
    if (!options.background) this.loading.set(true);

    this.booksService
      .get(id)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (data) => {
          if (options.forceImageRefresh && data.coverUrl) {
            data.coverUrl += `?t=${Date.now()}`;
          }
          this.book.set(data);
          // Direct navigation and refresh must discover the import even when
          // the Library has never mounted in this browser session.
          if (data.status === 1 || data.status === 2) this.imports.ensureConnected();
        },
        error: () => {
          this.error.set('Book not found');
          this.toast.error('Book not found');
        },
      });
  }

  loadNotes(id: string) {
    this.notesService.list(id).subscribe({
      next: (data) => this.notes.set(data),
    });
  }

  loadCollections() {
    this.collectionsService.list().subscribe({
      next: (data) => this.collections.set(data),
    });
  }

  loadTopics() {
    this.topicsService.list().subscribe({
      next: (topics) => {
        const map = new Map<string, TopicDto>();
        topics.forEach((c) => map.set(c.name.trim().toLowerCase(), c));
        this.topicMap.set(map);
        this.autocompleteService.setTopics(topics);
      },
    });
  }

  // --- OPTIMISTIC UPDATES ---

  toggleFavorite() {
    const b = this.book();
    if (!b) return;

    const newStatus = !b.isFavorite;

    // 1. Optimistic Update
    this.book.update((curr) => (curr ? { ...curr, isFavorite: newStatus } : null));

    // 2. API Call
    this.booksService.update(b.id, { isFavorite: newStatus } as any).subscribe({
      error: () => {
        // Revert on error
        this.book.update((curr) => (curr ? { ...curr, isFavorite: !newStatus } : null));
        this.toast.error('Failed to update favorite');
      },
    });
  }

  toggleFinished() {
    const b = this.book();
    if (!b) return;
    this.setFinished(!b.finishedAt);
  }

  setFinished(isFinished: boolean) {
    const b = this.book();
    if (!b) return;

    const newDate = isFinished ? new Date().toISOString() : null;
    const newProgress = isFinished ? 100 : (b.progressPercent === 100 ? 0 : b.progressPercent);

    // 1. Optimistic
    this.book.update((curr) =>
      curr
        ? {
            ...curr,
            finishedAt: newDate,
            progressPercent: newProgress,
          }
        : null,
    );

    // 2. API
    this.booksService.update(b.id, { isFinished }).subscribe({
      next: (updated) => {
        this.book.set(updated);
        this.toast.success(isFinished ? 'Marked as finished' : 'Marked as in progress');
      },
      error: () => {
        this.loadBook(b.id, { background: true });
        this.toast.error('Failed to update status');
      },
    });
  }

  rate(rating: number) {
    const b = this.book();
    if (!b) return;

    this.book.update((curr) => (curr ? { ...curr, rating } : null));

    this.booksService.update(b.id, { rating } as any).subscribe({
      error: () => this.toast.error('Failed to update rating'),
    });
  }

  /**
   * Resets the book's reading progress to the canonical "not started" state.
   * Local pending signal prevents duplicate submissions; on success the book
   * is refetched from the backend (the server owns the reset state) and the
   * user is told the next reader opening starts at the beginning.
   */
  resetProgress() {
    const b = this.book();
    if (!b || this.resettingProgress()) return;

    this.resettingProgress.set(true);
    this.booksService
      .resetProgress(b.id)
      .pipe(finalize(() => this.resettingProgress.set(false)))
      .subscribe({
        next: () => {
          this.loadBook(b.id, { background: true });
          this.toast.success('Reading progress reset');
        },
        error: () => this.toast.error('Failed to reset progress'),
      });
  }

  // --- MANUAL WORK MEMBERSHIP (issue #143) ---

  /**
   * Merge this book into another book's work.
   *
   * The mutation is owned by the domain service, so the client never writes a
   * WorkId: it asks for the link and then re-reads the book, which is what makes
   * the displayed editions reflect the server's decision (including the merge of
   * whole groups, which the client cannot predict from two book ids).
   */
  linkToWork(targetBookId: string) {
    const b = this.book();
    if (!b || this.linkingWork()) return;

    this.linkingWork.set(true);
    this.booksService
      .linkWork(b.id, targetBookId)
      .pipe(finalize(() => this.linkingWork.set(false)))
      .subscribe({
        next: () => {
          this.loadBook(b.id, { background: true });
          this.toast.success('Linked as editions');
        },
        error: () => this.toast.error('Could not link these books'),
      });
  }

  /** Split a book out into its own work. Defaults to the book being viewed. */
  unlinkFromWork(bookId?: string) {
    const b = this.book();
    if (!b || this.linkingWork()) return;
    const id = bookId ?? b.id;

    this.linkingWork.set(true);
    this.booksService
      .unlinkWork(id)
      .pipe(finalize(() => this.linkingWork.set(false)))
      .subscribe({
        next: () => {
          this.loadBook(b.id, { background: true });
          this.toast.success('Unlinked into its own edition set');
        },
        error: () => this.toast.error('Could not unlink this edition'),
      });
  }

  /**
   * Books that could be linked to the current one, for the advanced picker.
   *
   * Candidates are deduplicated BY WORK: linking is a group-level operation, so
   * a work with three editions would otherwise offer the same choice three times
   * under the same title (measured on a real library: "Justice" appeared twice).
   * The work's size travels in `editionCount`, which is what the merge warning
   * needs anyway.
   *
   * The current book and anything already in its work are excluded: linking
   * those is a no-op the user cannot see the reason for.
   */
  loadLinkCandidates(search: string): void {
    this.candidatesLoading.set(true);
    this.booksService
      .list({ search, pageSize: 20, groupByWork: false })
      .pipe(finalize(() => this.candidatesLoading.set(false)))
      .subscribe({
        next: (page) => {
          const current = this.book();
          const currentWorkId = current?.workId;

          const byWork = new Map<string, LinkableBookDto>();
          for (const candidate of page.items) {
            if (candidate.id === current?.id) continue;
            if (candidate.workId && candidate.workId === currentWorkId) continue;

            // A book with no work id cannot be deduped by work, so it stands
            // alone under its own id rather than being dropped.
            const key = candidate.workId ?? `book:${candidate.id}`;
            if (byWork.has(key)) continue;

            byWork.set(key, {
              id: candidate.id,
              title: candidate.title,
              author: candidate.author,
              workId: candidate.workId,
              editionCount: candidate.editionCount,
            });
          }

          this.linkCandidates.set([...byWork.values()]);
        },
        error: () => {
          this.linkCandidates.set([]);
          this.toast.error('Could not load library books');
        },
      });
  }

  // --- NOTES ---

  addNote(content: string) {
    const b = this.book();
    if (!b || !content.trim()) return;

    this.notesService.create(b.id, { content }).subscribe({
      next: () => {
        this.loadNotes(b.id);
        this.loadTopics(); // Refresh topics as note might have added new ones
      },
    });
  }

  updateNote(id: string, content: string, selectedText?: string) {
    const b = this.book();
    if (!b) return;

    this.notesService.update(id, { content, selectedText }).subscribe({
      next: () => {
        this.loadNotes(b.id);
        this.loadTopics();
      },
    });
  }

  deleteNote(noteId: string) {
    // Confirmed by the caller through ConfirmModal (book-detail.component);
    // the store only performs the delete.
    const b = this.book();
    if (!b) return;

    this.notesService.delete(noteId).subscribe({
      next: () => {
        this.loadNotes(b.id);
        this.loadTopics();
      },
    });
  }

  // --- FILES ---

  uploadCover(file: File) {
    const b = this.book();
    if (!b) return;

    this.booksService.uploadCover(b.id, file).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.Response) {
          this.loadBook(b.id, { forceImageRefresh: true, background: true });
        }
      },
      error: () => this.toast.error('Failed to upload cover'),
    });
  }

  deleteCover() {
    // Confirmed by the caller through ConfirmModal (book-detail.component).
    const b = this.book();
    if (!b) return;

    this.booksService.deleteCover(b.id).subscribe({
      next: () => this.loadBook(b.id, { background: true }),
    });
  }

  uploadFile(file: File) {
    const b = this.book();
    if (!b || this.isImporting() || this.uploadingFile()) return;

    this.uploadingFile.set(true);
    this.booksService.uploadFile(b.id, file).pipe(
      finalize(() => this.uploadingFile.set(false)),
    ).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.Response) {
          this.loadBook(b.id, { background: true });
        }
      },
      error: (error: unknown) => this.toast.error(this.describeUploadFailure(error)),
    });
  }

  private describeUploadFailure(error: unknown): string {
    const status = (error as { status?: number } | null)?.status;
    if (status === 0) return 'The connection was interrupted. Try the upload again.';
    if (status === 408 || status === 504) return 'The upload timed out. Try again.';
    if (status === 409) return 'This book is already importing or uploading a file. Wait until it finishes.';
    if (status === 413) return 'This file is larger than this server allows.';
    if (status === 429) return 'Too many uploads are running right now. Try again in a moment.';
    if (status != null && status >= 400 && status < 500) {
      return 'The server could not accept this file. Check the file and try again.';
    }
    return 'The file could not be uploaded. Try again.';
  }
}
