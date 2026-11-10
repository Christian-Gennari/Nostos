import { ChangeDetectionStrategy, Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, catchError, debounceTime, distinctUntilChanged, firstValueFrom, of, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import {
  READING_HISTORY_SOURCE_LABEL,
  ReadingHistoryImportBatch,
  ReadingHistoryImportDecision,
  ReadingHistoryImportResultBook,
  ReadingHistoryImportService,
  ReadingHistoryImportSource,
  ReadingHistoryPreviewBook,
  ReadingHistorySkipped,
} from '../../core/services/reading-history-import.service';
import type { HighlightImportCandidate } from '../../core/services/highlight-import.service';
import { BooksService } from '../../core/services/books.service';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { DialogActionsComponent } from '../dialog-actions/dialog-actions.component';
import { ButtonComponent } from '../button/button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { InputDirective } from '../form-control/form-control.directive';
import { DropZoneComponent } from '../drop-zone/drop-zone.component';
import { LoadingIndicatorComponent } from '../loading-indicator/loading-indicator.component';
import { SearchFieldComponent } from '../search-field/search-field.component';
import { ConfirmModal } from '../confirm-modal/confirm-modal.component';
import { CheckboxDirective } from '../check/check.directive';
import { ChoiceRowComponent } from '../choice-row/choice-row.component';
import { EmptyStateComponent } from '../empty-state/empty-state.component';
import { NostosDatePipe } from '../pipes/nostos-date.pipe';
import { BOOK_FORMAT_LABEL, bookFormatConcept, UNKNOWN_AUTHOR, UNKNOWN_BOOK, type BookFormatConcept } from '../../core/book-format';
import { TO_READ_LABEL } from '../../core/reading-status';

type Stage = 'pick' | 'reading' | 'review' | 'importing' | 'result';

/** Where one source row will go. `null` on a row means "leave out". */
export type ReadingHistoryTarget =
  | { kind: 'book'; bookId: string; title: string; author: string | null }
  | { kind: 'create' };

/** One row of the export, with the owner's current answer for it. */
export interface HistoryRow {
  id: number;
  book: ReadingHistoryPreviewBook;
  /** The server's best guess, shown as a question until answered. */
  suggestion: HighlightImportCandidate | null;
  target: ReadingHistoryTarget | null;
}

interface FileFailure {
  fileName: string;
  message: string;
}

/**
 * Import a reading history from Goodreads or StoryGraph (issue #1521): what
 * was read and when, the rating, the review and the shelves. Nothing is
 * written until the owner has seen where every row goes; shelves become
 * collections only when the owner asks. The server tells the two exports
 * apart by their header row.
 */
@Component({
  selector: 'app-reading-history-import-modal',
  standalone: true,
  imports: [
    EmptyStateComponent,
    ConfirmModal,
    CheckboxDirective,
    ChoiceRowComponent,
    SearchFieldComponent,
    LoadingIndicatorComponent,
    DropZoneComponent,
    NostosDatePipe,
    NgTemplateOutlet,
    ModalShell,
    DialogActionsComponent,
    ButtonComponent,
    NostosIconComponent,
    InputDirective,
  ],
  templateUrl: './reading-history-import-modal.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './reading-history-import-modal.component.css',
})
export class ReadingHistoryImportModal {
  protected readonly unknownBook = UNKNOWN_BOOK;
  protected readonly unknownAuthor = UNKNOWN_AUTHOR;
  protected readonly toReadLabel = TO_READ_LABEL;

  private readonly service = inject(ReadingHistoryImportService);
  private readonly booksService = inject(BooksService);

  readonly isOpen = this.service.isOpen;
  readonly stage = signal<Stage>('pick');
  readonly busy = computed(() => this.stage() === 'reading' || this.stage() === 'importing');

  readonly file = signal<File | null>(null);
  readonly rows = signal<HistoryRow[]>([]);
  readonly unimportable = signal<ReadingHistorySkipped[]>([]);
  readonly failures = signal<FileFailure[]>([]);
  readonly mapShelves = signal(false);

  readonly source = signal<ReadingHistoryImportSource | null>(null);
  readonly sourceName = computed(() => {
    const name = this.source();
    return name ? READING_HISTORY_SOURCE_LABEL[name] : 'Goodreads or StoryGraph';
  });

  sourceNameFor(source: ReadingHistoryImportSource): string {
    return READING_HISTORY_SOURCE_LABEL[source];
  }

  /** Grouped by what the server found, so a row never jumps as it is answered. */
  readonly ready = computed(() => this.rows().filter((row) => row.book.match === 'exact'));
  readonly toCheck = computed(() => this.rows().filter((row) => row.book.match === 'suggested'));
  readonly missing = computed(() => this.rows().filter((row) => row.book.match === 'none'));

  readonly included = computed(() => this.rows().filter((row) => row.target !== null));
  readonly unanswered = computed(
    () => this.toCheck().filter((row) => row.target === null && row.suggestion !== null).length,
  );
  readonly matchedCount = computed(() => this.ready().length);
  readonly newCount = computed(() => this.missing().filter((row) => row.book.title !== null).length);
  readonly skippedCount = computed(() => this.unimportable().reduce((total, entry) => total + entry.count, 0));
  readonly hasShelves = computed(() => this.rows().some((row) => row.book.values.shelves.length > 0));

  /** The row whose book picker is open, and that picker's search. */
  readonly pickingRowId = signal<number | null>(null);
  readonly searchQuery = signal('');
  readonly searchResults = signal<HighlightImportCandidate[]>([]);
  readonly searching = signal(false);
  readonly searchError = signal(false);
  private readonly searchSubject = new Subject<string>();

  readonly results = signal<ReadingHistoryImportResultBook[]>([]);
  readonly batchId = signal<string | null>(null);
  readonly undoing = signal(false);
  readonly undoError = signal<string | null>(null);
  readonly undone = signal<number | null>(null);
  readonly pendingUndo = signal<ReadingHistoryImportBatch | { id: string; bookCount: number; changeCount: number } | null>(null);

  readonly imported = computed(() => this.results().filter((book) => book.status === 'imported'));
  readonly unchanged = computed(() => this.results().filter((book) => book.status === 'unchanged'));
  readonly leftOut = computed(() => this.results().filter((book) => book.status === 'skipped'));
  readonly changeTotal = computed(() => this.imported().reduce((total, book) => total + book.changeCount, 0));

  readonly recent = signal<ReadingHistoryImportBatch[]>([]);

  readonly headline = computed(() => {
    if (this.undone() !== null) return 'Import undone';
    const imported = this.imported().length;
    if (imported > 0) return `${this.count(imported, 'book')} brought in`;
    if (this.unchanged().length > 0) return 'Already up to date';
    return this.failures().length > 0 ? 'Import failed' : 'Nothing was imported';
  });

  readonly summary = computed(() => {
    const removed = this.undone();
    if (removed !== null) return `${this.count(removed, 'row')} taken back out of your library.`;
    const imported = this.imported().length;
    const changes = this.changeTotal();
    if (imported > 0) {
      const values = changes > 0 ? ` and ${this.count(changes, 'value')}` : '';
      return `Into ${this.count(imported, 'book')}${values}.`;
    }
    if (this.unchanged().length > 0) return 'Every value the file holds is already in Nostos.';
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
          this.searchError.set(false);
          return this.booksService.list({ search: q, page: 1, pageSize: 6 }).pipe(
            catchError(() => {
              this.searchError.set(true);
              return of(null);
            }),
          );
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
              format: bookFormatConcept(book.type, book.fileName),
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
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    const file = event.dataTransfer?.files?.[0];
    if (file) void this.readFile(file);
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    input.value = '';
    if (file) void this.readFile(file);
  }

  /** Step one: ask the server what the file holds. Nothing is written. */
  async readFile(file: File): Promise<void> {
    if (this.busy()) return;

    this.file.set(file);
    this.rows.set([]);
    this.unimportable.set([]);
    this.failures.set([]);
    this.results.set([]);
    this.batchId.set(null);
    this.undone.set(null);
    this.undoError.set(null);
    this.mapShelves.set(false);
    this.stage.set('reading');

    try {
      const preview = await firstValueFrom(this.service.preview(file));
      this.source.set(preview.source);
      this.rows.set(preview.books.map((book) => this.toRow(book)));
      this.unimportable.set(preview.skipped ?? []);
      this.stage.set('review');
    } catch (error) {
      this.fail(file, this.failureMessage(error));
      this.stage.set('result');
    }
  }

  // --- Review -------------------------------------------------------------

  confirm(row: HistoryRow): void {
    if (row.suggestion) this.setTarget(row, this.bookTarget(row.suggestion));
  }

  choose(row: HistoryRow, candidate: HighlightImportCandidate): void {
    this.setTarget(row, this.bookTarget(candidate));
    this.closePicker();
  }

  add(row: HistoryRow): void {
    this.setTarget(row, { kind: 'create' });
  }

  skip(row: HistoryRow): void {
    this.patch(row, { target: null, suggestion: null });
    this.closePicker();
  }

  openPicker(row: HistoryRow): void {
    this.pickingRowId.set(row.id);
    this.searchQuery.set('');
    this.searchResults.set([]);
    this.searchError.set(false);
    this.searchSubject.next('');
  }

  closePicker(): void {
    this.pickingRowId.set(null);
  }

  onSearch(value: string): void {
    this.searchQuery.set(value);
    this.searchSubject.next(value);
  }

  onMapShelves(event: Event): void {
    this.mapShelves.set((event.target as HTMLInputElement).checked);
  }

  /** What the row's shelf/tag count promises when mapping is off. */
  shelfLabel(row: HistoryRow): string {
    const shelves = row.book.values.shelves;
    return shelves.length === 1 ? shelves[0] : `${shelves.length} shelves`;
  }

  ratingLabel(row: HistoryRow): string | null {
    const rating = row.book.values.rating;
    return rating > 0 ? `${rating} ${rating === 1 ? 'star' : 'stars'}` : null;
  }

  readLabel(row: HistoryRow): string | null {
    const finished = row.book.values.finishedAt;
    return finished ? `Read ${finished.slice(0, 10)}` : null;
  }

  statusLabel(row: HistoryRow): string | null {
    switch (row.book.values.readStatus) {
      case 'currently-reading':
        return 'Currently reading';
      case 'did-not-finish':
        return 'Did not finish';
      case 'paused':
        return 'Paused';
      case 'to-read':
        return TO_READ_LABEL;
      default:
        return null;
    }
  }

  countLabel(value: number, noun: string): string {
    return this.count(value, noun);
  }

  // --- Commit -------------------------------------------------------------

  async importReviewed(): Promise<void> {
    const file = this.file();
    if (!file || this.included().length === 0 || this.busy()) return;

    const mapShelves = this.mapShelves();
    const decisions: ReadingHistoryImportDecision[] = this.included().map((row) =>
      row.target?.kind === 'create'
        ? { sourceKey: row.book.sourceKey, create: true, mapShelves }
        : { sourceKey: row.book.sourceKey, bookId: row.target?.bookId, mapShelves },
    );

    this.stage.set('importing');
    this.undoError.set(null);
    try {
      const result = await firstValueFrom(this.service.commit(file, decisions));
      this.results.set(result.books);
      this.batchId.set(result.batchId);
      this.undone.set(null);
      this.stage.set('result');
    } catch (error) {
      this.fail(file, this.failureMessage(error));
      this.stage.set('review');
    }
  }

  backToPick(): void {
    this.stage.set('pick');
  }

  importAnother(): void {
    this.file.set(null);
    this.rows.set([]);
    this.unimportable.set([]);
    this.failures.set([]);
    this.results.set([]);
    this.batchId.set(null);
    this.undone.set(null);
    this.undoError.set(null);
    this.stage.set('pick');
  }

  close(): void {
    this.service.close();
    this.importAnother();
  }

  // --- Undo ---------------------------------------------------------------

  askUndoImport(): void {
    const id = this.batchId();
    if (id) this.pendingUndo.set({ id, bookCount: this.imported().length, changeCount: this.changeTotal() });
  }

  askUndoRecent(batch: ReadingHistoryImportBatch): void {
    this.pendingUndo.set(batch);
  }

  cancelUndo(): void {
    this.pendingUndo.set(null);
  }

  async confirmUndo(): Promise<void> {
    const pending = this.pendingUndo();
    if (!pending || this.undoing()) return;

    this.undoing.set(true);
    this.undoError.set(null);
    try {
      const result = await firstValueFrom(this.service.undo(pending.id));
      this.undone.set(result.removed);
      this.pendingUndo.set(null);
      this.batchId.set(null);
      this.stage.set('result');
      this.loadRecent();
    } catch {
      this.undoError.set('That import could not be undone. Try again.');
    } finally {
      this.undoing.set(false);
    }
  }

  undoDescription(): string {
    const pending = this.pendingUndo();
    if (!pending) return '';
    return `The ${this.count(pending.changeCount, 'value')} this import wrote will be taken back. Values you changed since are kept.`;
  }

  // --- Helpers ------------------------------------------------------------

  formatName(format: BookFormatConcept): string {
    return BOOK_FORMAT_LABEL[format];
  }

  outcome(book: ReadingHistoryImportResultBook): string {
    if (book.message) return book.message;
    if (book.created) return book.bookId ? 'Added as a new book' : 'Added to your reading list';
    if (book.changeCount === 0) return 'Already in Nostos';
    return `${this.count(book.changeCount, 'value')} updated`;
  }

  private loadRecent(): void {
    untracked(() => {
      this.service.recent().subscribe({
        next: (batches) => this.recent.set(batches),
        error: () => this.recent.set([]),
      });
    });
  }

  private toRow(book: ReadingHistoryPreviewBook): HistoryRow {
    const suggestion = book.candidates[0] ?? null;
    const exact = book.match === 'exact' ? book.bookId : null;
    const target = exact
      ? this.bookTarget({
          bookId: exact,
          title: book.candidates.find((candidate) => candidate.bookId === exact)?.title ?? book.existingTitle ?? book.title ?? UNKNOWN_BOOK,
          author: book.candidates.find((candidate) => candidate.bookId === exact)?.author ?? book.author,
          reason: '',
          format: book.candidates.find((candidate) => candidate.bookId === exact)?.format ?? 'ebook',
        })
      : null;
    return { id: this.nextRowId++, book, suggestion, target };
  }

  private bookTarget(candidate: HighlightImportCandidate): ReadingHistoryTarget {
    return { kind: 'book', bookId: candidate.bookId, title: candidate.title, author: candidate.author };
  }

  private setTarget(row: HistoryRow, target: ReadingHistoryTarget): void {
    this.patch(row, { target });
  }

  private patch(row: HistoryRow, changes: Partial<HistoryRow>): void {
    this.rows.update((rows) => rows.map((candidate) => (candidate.id === row.id ? { ...candidate, ...changes } : candidate)));
  }

  private fail(file: File, message: string): void {
    this.failures.update((failures) => [...failures, { fileName: file.name, message }]);
  }

  private failureMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      return (error.error as { error?: string } | null)?.error ?? 'That file could not be read.';
    }
    return 'That file could not be read.';
  }

  private count(value: number, noun: string): string {
    return `${value} ${noun}${value === 1 ? '' : 's'}`;
  }
}
