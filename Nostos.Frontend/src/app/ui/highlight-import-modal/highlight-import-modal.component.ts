import { editionChoices } from '../../book-detail/edition-switch-options';
import { Component, computed, effect, inject, signal, untracked, ChangeDetectionStrategy } from '@angular/core';
import { EmptyStateComponent } from '../empty-state/empty-state.component';
import { NgTemplateOutlet } from '@angular/common';
import { NostosDatePipe } from '../pipes/nostos-date.pipe';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { Subject, catchError, debounceTime, distinctUntilChanged, firstValueFrom, of, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  APPLE_BOOKS_FOLDER,
  appleBooksFiles,
  groupHighlightImportFiles,
  HIGHLIGHT_IMPORT_SOURCE_LABEL,
  HighlightImportBatch,
  HighlightImportCandidate,
  HighlightImportDecision,
  HighlightImportPreviewBook,
  HighlightImportReport,
  HighlightImportResultBook,
  HighlightImportService,
  HighlightImportSkipped,
  HighlightImportSource,
} from '../../core/services/highlight-import.service';
import { BooksService } from '../../core/services/books.service';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { DialogActionsComponent } from '../dialog-actions/dialog-actions.component';
import { ButtonComponent } from '../button/button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { InputDirective } from '../form-control/form-control.directive';
import { DropZoneComponent } from '../drop-zone/drop-zone.component';
import { BadgeComponent } from '../badge/badge.component';
import { FeedbackLinkService } from '../../core/services/feedback-link.service';
import { LoadingIndicatorComponent } from '../loading-indicator/loading-indicator.component';
import { SearchFieldComponent } from '../search-field/search-field.component';
import { BOOK_FORMAT_LABEL, bookFormatConcept, UNKNOWN_AUTHOR, type BookFormatConcept, UNKNOWN_BOOK } from '../../core/book-format';
import { ConfirmModal } from '../confirm-modal/confirm-modal.component';
import { CheckboxDirective } from '../check/check.directive';
import { ChoiceRowComponent } from '../choice-row/choice-row.component';
import { plural } from '../../core/format';
import { StarterDraftService } from '../../writing-studio/starter-draft/starter-draft.service';
import { answerStarterOffer } from '../../writing-studio/starter-draft/starter-draft';

type Stage = 'pick' | 'applebooks' | 'reading' | 'review' | 'importing' | 'result';

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
  /** Highlights the owner unticked, by index. */
  excluded: ReadonlySet<number>;
  /** Whether the row lists its highlights. */
  expanded: boolean;
}

interface FileFailure {
  fileName: string;
  message: string;
}

/** What a file holds that the import leaves out, and why. */
export interface UnimportableEntry extends HighlightImportSkipped {
  fileName: string;
}

/**
 * Import highlights and notes from an e-reader (issue #656), a Kindle's
 * My Clippings.txt or a Readwise CSV export (issue #1219), or Apple Books'
 * two databases from a Mac, chosen together (issue #1357).
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
    EmptyStateComponent,
    ConfirmModal,
    CheckboxDirective,
    ChoiceRowComponent,
    SearchFieldComponent,
    LoadingIndicatorComponent,
    DropZoneComponent,
    BadgeComponent,
    NostosDatePipe,
    NgTemplateOutlet,
    ModalShell,
    DialogActionsComponent,
    ButtonComponent,
    NostosIconComponent,
    InputDirective,
  ],
  templateUrl: './highlight-import-modal.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './highlight-import-modal.component.css',
})
export class HighlightImportModal {
  protected readonly unknownBook = UNKNOWN_BOOK;
  protected formatName(format: BookFormatConcept): string {
    return BOOK_FORMAT_LABEL[format];
  }
  protected readonly unknownAuthor = UNKNOWN_AUTHOR;
  private readonly service = inject(HighlightImportService);
  private readonly booksService = inject(BooksService);
  private readonly router = inject(Router);
  private readonly starter = inject(StarterDraftService);

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
  /** Bookmarks, web articles and the like: shown, never imported. */
  readonly unimportable = signal<UnimportableEntry[]>([]);
  readonly unimportableCount = computed(() =>
    this.unimportable().reduce((total, entry) => total + entry.count, 0),
  );

  /** Grouped by what the server found, so a row never jumps as it is answered. */
  readonly ready = computed(() =>
    this.rows().filter((row) => row.book.match === 'exact' || row.book.match === 'remembered'),
  );
  readonly toCheck = computed(() => this.rows().filter((row) => row.book.match === 'suggested'));
  readonly missing = computed(() => this.rows().filter((row) => row.book.match === 'none'));

  // A book whose every highlight is unticked has nothing left to import.
  readonly included = computed(() =>
    this.rows().filter((row) => row.target !== null && !this.allExcluded(row)),
  );
  readonly unanswered = computed(
    () => this.toCheck().filter((row) => row.target === null && row.suggestion !== null).length,
  );

  /** The row whose book picker is open, and that picker's search. */
  readonly pickingRowId = signal<number | null>(null);
  readonly searchQuery = signal('');
  readonly searchResults = signal<HighlightImportCandidate[]>([]);
  readonly searching = signal(false);
  readonly searchError = signal(false);
  private readonly searchSubject = new Subject<string>();

  readonly results = signal<HighlightImportResultBook[]>([]);
  readonly batchIds = signal<string[]>([]);
  readonly undoing = signal(false);
  readonly undoError = signal<string | null>(null);
  readonly undone = signal<number | null>(null);

  readonly imported = computed(() => this.results().filter((book) => book.status === 'imported'));
  readonly skipped = computed(() => this.results().filter((book) => book.status === 'skipped'));
  readonly importedCount = computed(() => this.sum(this.imported(), 'importedCount'));
  readonly duplicateCount = computed(() => this.sum(this.imported(), 'duplicateCount'));

  readonly recent = signal<HighlightImportBatch[]>([]);

  /** A starter draft from what was just imported (issue #1220). */
  readonly startingDraft = signal(false);
  readonly draftError = signal<string | null>(null);
  readonly canStartDraft = computed(
    () => this.importedCount() > 0 && this.batchIds().length > 0 && this.undone() === null,
  );

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
      const books = new Set(this.imported().filter((book) => book.importedCount > 0).map((book) => book.bookId));
      const from = `Into ${this.count(books.size, 'book')}.`;
      return duplicates > 0 ? `${from} ${duplicates} more were already in Nostos.` : from;
    }
    if (duplicates > 0) {
      return duplicates === 1
        ? 'The 1 highlight found is already in Nostos.'
        : `All ${duplicates} highlights found are already in Nostos.`;
    }
    return '';
  });

  /**
   * Apple Books is in beta until it has run on more Macs: its database was
   * checked on a real macOS, the folder window only on a few. A Cloud owner
   * reaches the feedback page; a self-hosted one the issue on GitHub.
   */
  private readonly feedbackLink = inject(FeedbackLinkService);
  readonly appleBooksFeedbackUrl = computed(
    () => this.feedbackLink.url() ?? 'https://github.com/Christian-Gennari/Nostos/issues/1357',
  );

  /** Apple Books' own steps (issue #1357): the owner points at one folder, Nostos does the rest. */
  readonly appleBooksFolder = APPLE_BOOKS_FOLDER;
  readonly folderCopied = signal<'idle' | 'copied' | 'failed'>('idle');
  /** The import started from Apple Books' steps, so Back and "again" return there. */
  readonly fromAppleBooks = signal(false);

  private nextRowId = 0;
  /** Files that went up with a main file, so the commit sends the same set. */
  private readonly companions = new Map<File, File[]>();

  constructor() {
    effect(() => {
      if (this.isOpen() && this.stage() === 'pick') this.loadRecent();
    });

    // Settings' Apple Books row opens straight on Apple Books' steps.
    effect(() => {
      if (!this.isOpen() || this.service.startOn() !== 'applebooks') return;
      untracked(() => {
        if (this.stage() === 'pick') this.showAppleBooks();
        this.service.startOn.set('all');
      });
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
          // A book once, then its editions below: highlights are placed in one copy.
          return this.booksService.list({ search: q, page: 1, pageSize: 6, groupByWork: true }).pipe(
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
            (page?.items ?? []).flatMap((book) => {
              // Highlights are placed in one copy, so a book with several
              // editions offers each, labelled as the book page labels them.
              const editions = editionChoices(book);
              if (!editions.length) {
                return [{
                  bookId: book.id,
                  title: book.title,
                  author: book.author ?? null,
                  reason: '',
                  format: bookFormatConcept(book.type, book.fileName),
                }];
              }
              return editions.map((edition) => ({
                bookId: edition.id,
                title: book.title,
                author: book.author ?? null,
                reason: edition.detail ?? '',
                format: edition.concept,
              }));
            }),
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

  /**
   * The Books folder on a Mac holds the two databases in separate folders,
   * which one file window cannot select together. The whole folder is
   * chosen instead, and only Apple Books' own files are sent.
   */
  onAppleBooksFolderSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const chosen = Array.from(input.files ?? []);
    const files = appleBooksFiles(chosen);
    input.value = '';
    if (chosen.length === 0) return;
    if (files.length === 0) {
      this.appleBooksError.set(true);
      return;
    }
    this.appleBooksError.set(false);
    void this.readFiles(files);
  }

  /** The chosen folder held no Apple Books highlights. */
  readonly appleBooksError = signal(false);

  showAppleBooks(): void {
    this.fromAppleBooks.set(true);
    this.appleBooksError.set(false);
    this.folderCopied.set('idle');
    this.stage.set('applebooks');
  }

  showAllSources(): void {
    this.fromAppleBooks.set(false);
    this.stage.set('pick');
  }

  async copyAppleBooksFolder(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.appleBooksFolder);
      this.folderCopied.set('copied');
    } catch {
      // Without clipboard access the location is shown to copy by hand.
      this.folderCopied.set('failed');
    }
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
    this.unimportable.set([]);
    this.results.set([]);
    this.batchIds.set([]);
    this.undone.set(null);
    this.undoError.set(null);
    this.draftError.set(null);
    const uploads = groupHighlightImportFiles(files);
    this.companions.clear();
    for (const upload of uploads) this.companions.set(upload.file, upload.companions);
    this.fileCount.set(uploads.length);
    this.stage.set('reading');

    for (const [index, { file, companions }] of uploads.entries()) {
      this.currentIndex.set(index + 1);
      this.currentFileName.set(file.name);
      try {
        const preview = await firstValueFrom(this.service.preview(file, companions));
        this.rows.update((rows) => [...rows, ...preview.books.map((book) => this.toRow(file, book))]);
        const skipped = (preview.skipped ?? []).map((entry) => ({ ...entry, fileName: file.name }));
        if (skipped.length > 0) this.unimportable.update((all) => [...all, ...skipped]);
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
    const isProposed = row.target?.kind === 'book' && row.target.bookId === proposed;
    const highlights = row.book.highlights;
    if (!highlights?.length) return isProposed ? row.book.newCount : row.book.annotationCount;
    return highlights.filter((h) => !row.excluded.has(h.index) && (!isProposed || h.isNew)).length;
  }

  /** What the row's heading says: all of them, or "2 of 6" once some are left out. */
  highlightLabel(row: ReviewRow): string {
    const total = row.book.annotationCount;
    const kept = total - row.excluded.size;
    return row.excluded.size === 0 ? this.count(total, 'highlight') : `${kept} of ${total} highlights`;
  }

  toggleExpanded(row: ReviewRow): void {
    this.patch(row, { expanded: !row.expanded });
  }

  /** Ticks or unticks one highlight of a book. */
  setHighlight(row: ReviewRow, index: number, keep: boolean): void {
    const excluded = new Set(this.rows().find((r) => r.id === row.id)?.excluded ?? row.excluded);
    if (keep) excluded.delete(index);
    else excluded.add(index);
    this.patch(row, { excluded });
  }

  private allExcluded(row: ReviewRow): boolean {
    const total = row.book.highlights?.length ?? 0;
    return total > 0 && row.excluded.size >= total;
  }

  backToPick(): void {
    this.stage.set(this.fromAppleBooks() ? 'applebooks' : 'pick');
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
        .filter((row) => !this.allExcluded(row))
        .map((row): HighlightImportDecision => {
          const decision: HighlightImportDecision =
            row.target!.kind === 'create'
              ? { sourceKey: row.book.sourceKey, create: true }
              : { sourceKey: row.book.sourceKey, bookId: row.target!.bookId };
          if (row.excluded.size > 0) decision.excludedHighlights = [...row.excluded].sort((a, b) => a - b);
          return decision;
        });
      if (decisions.length === 0) continue;

      try {
        const result = await firstValueFrom(
          this.service.commit(file, decisions, this.batchIds()[0] ?? null, this.companions.get(file) ?? []),
        );
        this.results.update((all) => [...all, ...result.books]);
        if (result.batchId && !this.batchIds().includes(result.batchId)) {
          this.batchIds.update((ids) => [...ids, result.batchId!]);
        }
      } catch (error) {
        this.fail(file, this.failureMessage(error));
      }
    }

    this.stage.set('result');
  }

  // --- Result -------------------------------------------------------------

  /** What the confirmation is about, while it is open: this import, or one from the list. */
  readonly pendingUndo = signal<{ batch: HighlightImportBatch | null } | null>(null);
  readonly undoDescription = computed(() => {
    const batch = this.pendingUndo()?.batch;
    const noun = batch ? this.count(batch.noteCount, 'highlight') : this.count(this.importedCount(), 'highlight');
    return (
      `${noun} will be removed. A book this import added to your library is removed too, ` +
      'unless it has other notes or a file.'
    );
  });

  askUndoImport(): void {
    if (!this.undoing() && this.batchIds().length > 0) this.pendingUndo.set({ batch: null });
  }

  askUndoRecent(batch: HighlightImportBatch): void {
    if (!this.undoing()) this.pendingUndo.set({ batch });
  }

  cancelUndo(): void {
    this.pendingUndo.set(null);
  }

  async confirmUndo(): Promise<void> {
    const pending = this.pendingUndo();
    if (!pending) return;
    if (pending.batch) await this.undoRecent(pending.batch);
    else await this.undoImport();
    this.pendingUndo.set(null);
  }

  /** Takes back everything this import just added. */
  async undoImport(): Promise<void> {
    if (this.undoing() || this.batchIds().length === 0) return;
    this.undoing.set(true);
    this.undoError.set(null);
    let removed = 0;
    const pending: string[] = [];
    for (const id of this.batchIds()) {
      try {
        removed += (await firstValueFrom(this.service.undo(id))).removed;
      } catch (error) {
        if (!(error instanceof HttpErrorResponse && error.status === 404)) pending.push(id);
      }
    }
    this.batchIds.set(pending);
    if (pending.length === 0) {
      this.undone.set(removed);
    } else {
      this.undoError.set('The import could not be undone. Try again.');
    }
    this.undoing.set(false);
  }

  async undoRecent(batch: HighlightImportBatch): Promise<void> {
    if (this.undoing()) return;
    this.undoing.set(true);
    this.undoError.set(null);
    try {
      await firstValueFrom(this.service.undo(batch.id));
    } catch (error) {
      if (!(error instanceof HttpErrorResponse && error.status === 404)) {
        this.undoError.set('The import could not be undone. Try again.');
      }
    }
    this.undoing.set(false);
    this.loadRecent();
  }

  /**
   * Opens the Studio on a new draft that quotes three of the passages just
   * imported, with their sources (issue #1220).
   */
  async startDraft(): Promise<void> {
    if (this.startingDraft() || this.undoing() || !this.canStartDraft()) return;
    this.startingDraft.set(true);
    this.draftError.set(null);
    let writingId: string | null = null;
    try {
      writingId = await this.starter.create(await this.starter.passages(this.batchIds()));
      if (!writingId) this.draftError.set('None of these highlights has words to quote.');
    } catch {
      this.draftError.set('The draft could not be started. Try again.');
    }
    this.startingDraft.set(false);
    if (!writingId) return;
    // The empty Studio need not offer it again.
    answerStarterOffer('taken');
    this.close();
    void this.router.navigate(['/studio'], { queryParams: { writingId } });
  }

  importAnother(): void {
    this.stage.set(this.fromAppleBooks() ? 'applebooks' : 'pick');
  }

  openBook(book: HighlightImportResultBook): void {
    if (!book.bookId) return;
    this.close();
    void this.router.navigate(['/library', book.bookId]);
  }

  close(): void {
    if (this.busy() || this.undoing() || this.startingDraft()) return;
    this.service.close();
    this.stage.set('pick');
    this.fromAppleBooks.set(false);
    this.appleBooksError.set(false);
    this.rows.set([]);
    this.results.set([]);
    this.failures.set([]);
    this.unimportable.set([]);
    this.undoError.set(null);
    this.draftError.set(null);
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

  sourceName(source: HighlightImportSource): string {
    return HIGHLIGHT_IMPORT_SOURCE_LABEL[source] ?? source;
  }

  /**
   * One line for a batch's report (issue #1522): what the file held, and the
   * three places the rest went. Empty outcomes are left out, as the issue's
   * example does; found and imported always show.
   */
  reportLine(report: HighlightImportReport): string {
    const parts = [`${report.found} found`, `${report.imported} imported`];
    if (report.duplicates > 0) parts.push(`${report.duplicates} already here`);
    if (report.skipped > 0) parts.push(`${report.skipped} skipped`);
    if (report.leftOut > 0) parts.push(`${report.leftOut} left out`);
    return parts.join(' · ');
  }

  /** "2 books matched · 1 added · 4 left out" for the batch's sub-line. */
  reportBooks(report: HighlightImportReport): string {
    const parts = [`${this.count(report.booksMatched, 'book')} matched`];
    if (report.booksCreated > 0) parts.push(`${report.booksCreated} added`);
    if (report.booksLeftOut > 0) parts.push(`${report.booksLeftOut} left out`);
    return parts.join(' · ');
  }

  count(value: number, noun: string): string {
    return plural(value, noun);
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
      excluded: new Set<number>(),
      expanded: false,
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
