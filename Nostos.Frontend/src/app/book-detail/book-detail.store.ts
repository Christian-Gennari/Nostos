import { Injectable, inject, signal, computed, DestroyRef } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ToastService } from '../core/services/toast.service';
import { HttpEventType } from '@angular/common/http';
import { finalize } from 'rxjs';

// DTOs & Services
import { Book, LinkableBookDto } from '../core/dtos/book.dtos';
import type { NoteSearchHit } from '../core/dtos/note.dtos';
import { Collection } from '../core/dtos/collection.dtos';
import { BooksService } from '../core/services/books.service';
import { NotesService } from '../core/services/notes.service';
import { CollectionsService } from '../core/services/collections.service';
import { ImportService } from '../core/services/import.service';
import { isImportInFlight, importStageLabel } from '../core/dtos/import.dtos';
import { clampPaperPage, paperPagePercent, writePaperPage } from './paper-page-progress';
import { planLimitMessage } from '../core/plan-limit-message';

@Injectable()
export class BookDetailStore {
  // Dependencies
  private booksService = inject(BooksService);
  private notesService = inject(NotesService);
  private collectionsService = inject(CollectionsService);
  private toast = inject(ToastService);
  private imports = inject(ImportService);
  private destroyRef = inject(DestroyRef);
  readonly importConnectionState = this.imports.connectionState;

  // --- STATE ---
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly book = signal<Book | null>(null);
  readonly notes = signal<NoteSearchHit[]>([]);
  readonly collections = signal<Collection[]>([]);

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
    // A page set just before leaving is still saved.
    this.destroyRef.onDestroy(() => this.flushPaperPage());
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
  }

  /** Reads the book again after it failed to load. */
  retry(id: string) {
    this.error.set(null);
    this.loadBook(id);
    this.loadNotes(id);
  }

  loadBook(id: string, options: { background?: boolean } = {}) {
    if (!options.background) this.loading.set(true);

    this.booksService
      .get(id)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (data) => {
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

  /**
   * Re-reads the open book, its notes and the collections after the library
   * changed elsewhere. Quiet on purpose: no loading state, and a failed read
   * keeps what is on screen instead of replacing the page with "not found".
   */
  refreshInBackground(id: string) {
    this.booksService.get(id).subscribe({
      next: (data) => {
        if (this.book()?.id === id) this.book.set(data);
      },
      error: () => undefined,
    });
    this.loadNotes(id);
    this.loadCollections();
  }

  private notesSeq = 0;

  /**
   * Every note of the book, oldest first, as the Brain's cards show them (with
   * their topics); read 200 at a time until all are in.
   */
  loadNotes(id: string) {
    const seq = ++this.notesSeq;
    const page = (held: NoteSearchHit[]) =>
      this.notesService.browse({ bookId: id, order: 'oldest', limit: 200, offset: held.length }).subscribe({
        next: (result) => {
          if (seq !== this.notesSeq) return;
          const all = [...held, ...result.items];
          if (result.items.length && all.length < result.totalCount) page(all);
          else this.notes.set(all);
        },
      });
    page([]);
  }

  loadCollections() {
    this.collectionsService.list().subscribe({
      next: (data) => this.collections.set(data),
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
        this.toast.error('Could not update the favorite');
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
        this.toast.success(isFinished ? 'Marked as finished' : 'Marked as reading');
      },
      error: () => {
        this.loadBook(b.id, { background: true });
        this.toast.error('Could not update the status');
      },
    });
  }

  // --- PAPER BOOK PAGE (issue #1091) ---

  /** A run of −/+ taps or keystrokes is one save, sent this long after the last. */
  static readonly PAPER_PAGE_SAVE_DELAY_MS = 600;
  private paperPageSave: { bookId: string; location: string; percent: number } | null = null;
  private paperPageTimer: ReturnType<typeof setTimeout> | null = null;

  /**
   * Sets the page a paper book is open at. The page shows at once; the save goes
   * through the readers' progress endpoint, so Last Read, the Reading filter and
   * Finished (at the last page) behave as for EPUB and PDF.
   */
  setPaperPage(page: number) {
    const b = this.book();
    if (!b || b.type !== 'physical' || !Number.isFinite(page)) return;

    const clamped = clampPaperPage(page, b.pageCount);
    const percent = paperPagePercent(clamped, b.pageCount);
    const location = writePaperPage(clamped);
    const finishedAt = percent >= 100 ? (b.finishedAt ?? new Date().toISOString()) : null;

    this.book.update((curr) =>
      curr
        ? {
            ...curr,
            lastLocation: location,
            progressPercent: percent,
            lastReadAt: new Date().toISOString(),
            finishedAt,
          }
        : null,
    );

    this.paperPageSave = { bookId: b.id, location, percent };
    if (this.paperPageTimer !== null) clearTimeout(this.paperPageTimer);
    this.paperPageTimer = setTimeout(
      () => this.flushPaperPage(),
      BookDetailStore.PAPER_PAGE_SAVE_DELAY_MS,
    );
  }

  /** Sends the pending page now: after the pause, and when the page is left. */
  flushPaperPage() {
    if (this.paperPageTimer !== null) {
      clearTimeout(this.paperPageTimer);
      this.paperPageTimer = null;
    }
    const pending = this.paperPageSave;
    this.paperPageSave = null;
    if (!pending) return;

    this.booksService.updateProgress(pending.bookId, pending.location, pending.percent).subscribe({
      error: () => {
        if (this.book()?.id === pending.bookId) this.loadBook(pending.bookId, { background: true });
        this.toast.error('Could not save the page');
      },
    });
  }

  rate(rating: number) {
    const b = this.book();
    if (!b) return;

    this.book.update((curr) => (curr ? { ...curr, rating } : null));

    this.booksService.update(b.id, { rating } as any).subscribe({
      error: () => this.toast.error('Could not update the rating'),
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
        error: () => this.toast.error('Could not reset progress'),
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
              type: candidate.type,
              fileName: candidate.fileName,
            });
          }

          this.linkCandidates.set([...byWork.values()]);
        },
        error: () => {
          this.linkCandidates.set([]);
          this.toast.error('Could not load the library’s books');
        },
      });
  }

  // --- NOTES ---

  /** Saves a new note; `saved` runs once it exists (new [[topics]] included). */
  addNote(content: string, saved?: () => void) {
    const b = this.book();
    if (!b || !content.trim()) return;

    this.notesService.create(b.id, { content }).subscribe({
      next: () => {
        this.loadNotes(b.id);
        saved?.();
      },
    });
  }

  /** A note changed elsewhere on the page (topics, words): show this version. */
  replaceNote(note: NoteSearchHit) {
    this.notes.update((notes) => notes.map((held) => (held.id === note.id ? note : held)));
  }

  /** A note was deleted from the page. */
  removeNote(noteId: string) {
    this.notes.update((notes) => notes.filter((held) => held.id !== noteId));
  }

  // --- FILES ---

  uploadCover(file: File) {
    const b = this.book();
    if (!b) return;

    this.booksService.uploadCover(b.id, file).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.Response) {
          // The re-read book carries the new cover version, so every cover
          // URL changes with it and no cached copy of the old cover is used.
          this.loadBook(b.id, { background: true });
        }
      },
      error: () => this.toast.error('Could not upload the cover'),
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
    const limit = planLimitMessage(error);
    if (limit) return limit;
    const status = (error as { status?: number } | null)?.status;
    if (status === 0) return 'The connection was interrupted. Try the upload again.';
    if (status === 408 || status === 504) return 'The upload timed out. Try again.';
    if (status === 409) return 'This book is already importing or uploading a file. Wait until it finishes.';
    if (status === 413) return 'This file is larger than this server allows';
    if (status === 429) return 'Too many uploads are running right now. Try again in a moment.';
    if (status != null && status >= 400 && status < 500) {
      return 'The server did not accept this file. Check the file and try again.';
    }
    return 'Could not upload the file. Try again.';
  }
}
