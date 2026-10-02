import { Component, computed, effect, inject, signal } from '@angular/core';
import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { Subject, debounceTime, distinctUntilChanged, firstValueFrom, of, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  HighlightImportBatch,
  HighlightImportCandidate,
  HighlightImportDecision,
  HighlightImportPreviewBook,
  HighlightImportResultBook,
  HighlightImportService,
} from '../../core/services/highlight-import.service';
import { BooksService } from '../../core/services/books.service';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { DialogActionsComponent } from '../dialog-actions/dialog-actions.component';
import { ButtonComponent } from '../button/button.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { InputDirective } from '../form-control/form-control.directive';

type Stage = 'pick' | 'reading' | 'review' | 'importing' | 'result';

/** Where one device book's highlights will go. `null` on a row means "leave out". */
export type ImportTarget =
  | { kind: 'book'; bookId: string; title: string; author: string | null }
  | { kind: 'create' };

/** One book found on the device, with the owner's current answer for it. */
export interface ReviewRow {
  id: number;
  file: File;
  book: HighlightImportPreviewBook;
  /** The server's best guess, shown as a question until answered. */
  suggestion: HighlightImportCandidate | null;
  target: ImportTarget | null;
}

interface FileFailure {
  fileName: string;
  message: string;
}

/**
 * Import highlights and notes from an e-reader (issue #656).
 *
 * Nothing is written until the owner has seen where every book is going.
 * Books whose identity is certain arrive already decided; a book that only
 * resembles a library book is a one-tap question, never an assumption — a
 * wrong guess would file hundreds of notes under another author. Each answer
 * is remembered by the server, so the question is asked once per book.
 *
 * Hosted once at the app root and opened through `HighlightImportService`, so
 * Settings and the command palette share it.
 */
@Component({
  selector: 'app-highlight-import-modal',
  standalone: true,
  imports: [
    DatePipe,
    NgTemplateOutlet,
    ModalShell,
    DialogActionsComponent,
    ButtonComponent,
    IconButtonComponent,
    NostosIconComponent,
    InputDirective,
  ],
  templateUrl: './highlight-import-modal.component.html',
  styleUrl: './highlight-import-modal.component.css',
})
export class HighlightImportModal {
  private readonly service = inject(HighlightImportService);
  private readonly booksService = inject(BooksService);
  private readonly router = inject(Router);

  readonly isOpen = this.service.isOpen;
  readonly stage = signal<Stage>('pick');
  readonly dragActive = signal(false);
  readonly busy = computed(() => this.stage() === 'reading' || this.stage() === 'importing');

  /** Progress through the selected files; the upload itself reports none. */
  readonly currentFileName = signal('');
  readonly currentIndex = signal(0);
  readonly fileCount = signal(0);

  readonly rows = signal<ReviewRow[]>([]);
  readonly failures = signal<FileFailure[]>([]);

  /** Grouped by what the server found, so a row never jumps as it is answered. */
  readonly ready = computed(() =>
    this.rows().filter((row) => row.book.match === 'exact' || row.book.match === 'remembered'),
  );
  readonly toCheck = computed(() => this.rows().filter((row) => row.book.match === 'suggested'));
  readonly missing = computed(() => this.rows().filter((row) => row.book.match === 'none'));

  readonly included = computed(() => this.rows().filter((row) => row.target !== null));
  readonly unanswered = computed(
    () => this.toCheck().filter((row) => row.target === null && row.suggestion !== null).length,
  );

  /** The row whose book picker is open, and that picker's search. */
  readonly pickingRowId = signal<number | null>(null);
  readonly searchQuery = signal('');
  readonly searchResults = signal<HighlightImportCandidate[]>([]);
  readonly searching = signal(false);
  private readonly searchSubject = new Subject<string>();

  readonly results = signal<HighlightImportResultBook[]>([]);
  readonly batchIds = signal<string[]>([]);
  readonly undoing = signal(false);
  readonly undone = signal<number | null>(null);

  readonly imported = computed(() => this.results().filter((book) => book.status === 'imported'));
  readonly skipped = computed(() => this.results().filter((book) => book.status === 'skipped'));
  readonly importedCount = computed(() => this.sum(this.imported(), 'importedCount'));
  readonly duplicateCount = computed(() => this.sum(this.imported(), 'duplicateCount'));

  readonly recent = signal<HighlightImportBatch[]>([]);

  readonly headline = computed(() => {
    const removed = this.undone();
    if (removed !== null) return 'Import undone';
    const imported = this.importedCount();
    if (imported > 0) return `${this.count(imported, 'highlight')} imported`;
    if (this.duplicateCount() > 0) return 'Already up to date';
    return this.failures().length > 0 && this.results().length === 0
      ? 'Import failed'
      : 'No highlights imported';
  });

  readonly summary = computed(() => {
    const removed = this.undone();
    if (removed !== null) return `${this.count(removed, 'highlight')} removed from your library.`;
    const imported = this.importedCount();
    const duplicates = this.duplicateCount();
    if (imported > 0) {
      const from = `Into ${this.count(this.imported().filter((b) => b.importedCount > 0).length, 'book')}.`;
      return duplicates > 0 ? `${from} ${duplicates} more were already in Nostos.` : from;
    }
    if (duplicates > 0) {
      return duplicates === 1
        ? 'The 1 highlight found is already in Nostos.'
        : `All ${duplicates} highlights found are already in Nostos.`;
    }
    return '';
  });

  private nextRowId = 0;

  constructor() {
    effect(() => {
      if (this.isOpen() && this.stage() === 'pick') this.loadRecent();
    });

    this.searchSubject
      .pipe(
        debounceTime(200),
        distinctUntilChanged(),
        switchMap((query) => {
          const q = query.trim();
          if (!q) return of(null);
          this.searching.set(true);
          return this.booksService.list({ search: q, page: 1, pageSize: 6 });
        }),
        takeUntilDestroyed(),
      )
      .subscribe({
        next: (page) => {
          this.searchResults.set(
            (page?.items ?? []).map((book) => ({
              bookId: book.id,
              title: book.title,
              author: book.author ?? null,
              reason: '',
            })),
          );
          this.searching.set(false);
        },
        error: () => this.searching.set(false),
      });
  }

  // --- Pick ---------------------------------------------------------------

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.dragActive.set(true);
  }

  onDragLeave(): void {
    this.dragActive.set(false);
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragActive.set(false);
    void this.readFiles(Array.from(event.dataTransfer?.files ?? []));
  }

  onFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    // Reset so choosing the same file again still fires `change`.
    input.value = '';
    void this.readFiles(files);
  }

  /** Step one: ask the server what each file holds. Nothing is written. */
  async readFiles(files: File[]): Promise<void> {
    if (files.length === 0 || this.busy()) return;

    this.rows.set([]);
    this.failures.set([]);
    this.results.set([]);
    this.batchIds.set([]);
    this.undone.set(null);
    this.fileCount.set(files.length);
    this.stage.set('reading');

    for (const [index, file] of files.entries()) {
      this.currentIndex.set(index + 1);
      this.currentFileName.set(file.name);
      try {
        const preview = await firstValueFrom(this.service.preview(file));
        this.rows.update((rows) => [...rows, ...preview.books.map((book) => this.toRow(file, book))]);
      } catch (error) {
        this.fail(file, this.failureMessage(error));
      }
    }

    // With nothing to review there is nothing to decide: show why.
    this.stage.set(this.rows().length > 0 ? 'review' : 'result');
  }

  // --- Review -------------------------------------------------------------

  /** "Yes, that is the book." */
  confirm(row: ReviewRow): void {
    if (row.suggestion) this.setTarget(row, this.bookTarget(row.suggestion));
  }

  choose(row: ReviewRow, candidate: HighlightImportCandidate): void {
    this.setTarget(row, this.bookTarget(candidate));
  }

  addToLibrary(row: ReviewRow): void {
    this.setTarget(row, { kind: 'create' });
  }

  /** Leave this book out, and stop asking about the suggestion. */
  skip(row: ReviewRow): void {
    this.patch(row, { target: null, suggestion: null });
    this.closePicker();
  }

  openPicker(row: ReviewRow): void {
    this.pickingRowId.set(row.id);
    this.searchQuery.set('');
    this.searchResults.set([]);
    this.searchSubject.next('');
  }

  closePicker(): void {
    this.pickingRowId.set(null);
  }

  onSearch(value: string): void {
    this.searchQuery.set(value);
    this.searchSubject.next(value);
  }

  /** How many highlights the footer promises. */
  readonly includedHighlightCount = computed(() =>
    this.included().reduce((total, row) => total + this.rowCount(row), 0),
  );

  /**
   * The server counted new highlights against its own proposed book. For any
   * other destination the honest number is everything the device holds.
   */
  rowCount(row: ReviewRow): number {
    const proposed = row.book.bookId ?? row.book.candidates[0]?.bookId ?? null;
    return row.target?.kind === 'book' && row.target.bookId === proposed
      ? row.book.newCount
      : row.book.annotationCount;
  }

  backToPick(): void {
    this.stage.set('pick');
    this.closePicker();
  }

  /** Step two: import exactly what the review shows. */
  async importReviewed(): Promise<void> {
    if (this.busy() || this.included().length === 0) return;

    const files = [...new Set(this.rows().map((row) => row.file))];
    this.fileCount.set(files.length);
    this.closePicker();
    this.stage.set('importing');

    for (const [index, file] of files.entries()) {
      this.currentIndex.set(index + 1);
      this.currentFileName.set(file.name);

      const decisions: HighlightImportDecision[] = this.rows()
        .filter((row) => row.file === file && row.target !== null)
        .map((row) =>
          row.target!.kind === 'create'
            ? { sourceKey: row.book.sourceKey, create: true }
            : { sourceKey: row.book.sourceKey, bookId: row.target!.bookId },
        );

      try {
        const result = await firstValueFrom(this.service.commit(file, decisions));
        this.results.update((all) => [...all, ...result.books]);
        if (result.batchId) this.batchIds.update((ids) => [...ids, result.batchId!]);
      } catch (error) {
        this.fail(file, this.failureMessage(error));
      }
    }

    this.stage.set('result');
  }

  // --- Result -------------------------------------------------------------

  /** Takes back everything this import just added. */
  async undoImport(): Promise<void> {
    if (this.undoing() || this.batchIds().length === 0) return;
    this.undoing.set(true);
    let removed = 0;
    for (const id of this.batchIds()) {
      try {
        removed += (await firstValueFrom(this.service.undo(id))).removed;
      } catch {
        // Already gone, or unreachable: the count below says what was undone.
      }
    }
    this.batchIds.set([]);
    this.undone.set(removed);
    this.undoing.set(false);
  }

  async undoRecent(batch: HighlightImportBatch): Promise<void> {
    if (this.undoing()) return;
    this.undoing.set(true);
    try {
      await firstValueFrom(this.service.undo(batch.id));
    } catch {
      // Reloading the list below shows the truth either way.
    }
    this.undoing.set(false);
    this.loadRecent();
  }

  importAnother(): void {
    this.stage.set('pick');
  }

  openBook(book: HighlightImportResultBook): void {
    if (!book.bookId) return;
    this.close();
    void this.router.navigate(['/library', book.bookId]);
  }

  close(): void {
    if (this.busy()) return;
    this.service.close();
    this.stage.set('pick');
    this.rows.set([]);
    this.results.set([]);
    this.failures.set([]);
    this.closePicker();
  }

  /** "3 new · 2 already imported" for an imported book. */
  outcome(book: HighlightImportResultBook): string {
    const parts: string[] = [];
    if (book.created) parts.push('Added to your library');
    if (book.importedCount > 0) parts.push(`${book.importedCount} new`);
    if (book.duplicateCount > 0) parts.push(`${book.duplicateCount} already imported`);
    return parts.length > 0 ? parts.join(' · ') : 'Nothing to import';
  }

  count(value: number, noun: string): string {
    return `${value} ${noun}${value === 1 ? '' : 's'}`;
  }

  private toRow(file: File, book: HighlightImportPreviewBook): ReviewRow {
    const decided = book.match === 'exact' || book.match === 'remembered';
    const best = book.candidates[0] ?? null;
    return {
      id: this.nextRowId++,
      file,
      book,
      suggestion: decided ? null : best,
      target: decided && best ? this.bookTarget(best) : null,
    };
  }

  private bookTarget(candidate: HighlightImportCandidate): ImportTarget {
    return {
      kind: 'book',
      bookId: candidate.bookId,
      title: candidate.title,
      author: candidate.author,
    };
  }

  private setTarget(row: ReviewRow, target: ImportTarget): void {
    this.patch(row, { target });
    this.closePicker();
  }

  private patch(row: ReviewRow, change: Partial<ReviewRow>): void {
    this.rows.update((rows) => rows.map((r) => (r.id === row.id ? { ...r, ...change } : r)));
  }

  private loadRecent(): void {
    this.service.recent().subscribe({
      next: (batches) => this.recent.set(batches),
      error: () => this.recent.set([]),
    });
  }

  private sum(
    books: HighlightImportResultBook[],
    key: 'importedCount' | 'duplicateCount',
  ): number {
    return books.reduce((total, book) => total + book[key], 0);
  }

  private fail(file: File, message: string): void {
    this.failures.update((all) => [...all, { fileName: file.name, message }]);
  }

  private failureMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      if (typeof error.error?.error === 'string') return error.error.error;
      if (error.status === 413) return 'This file is too large to import.';
      if (error.status === 0) return 'Nostos could not be reached. Check your connection and try again.';
    }
    return 'The import did not finish. Try again.';
  }
}
