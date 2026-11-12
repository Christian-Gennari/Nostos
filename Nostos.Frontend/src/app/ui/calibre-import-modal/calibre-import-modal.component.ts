import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Subscription, firstValueFrom, switchMap, timer } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import {
  CalibreBatch,
  CalibreCustomColumn,
  CalibreFormatChoice,
  CalibreImportJobState,
  CalibreImportResultBook,
  CalibreImportService,
  CalibrePreview,
  CalibrePreviewBook,
  CalibrePreviewSummary,
  CalibreStatus,
} from '../../core/services/calibre-import.service';
import type { HighlightImportCandidate } from '../../core/services/highlight-import.service';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { DialogActionsComponent } from '../dialog-actions/dialog-actions.component';
import { ButtonComponent } from '../button/button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';
import { CheckboxDirective } from '../check/check.directive';
import { LoadingIndicatorComponent } from '../loading-indicator/loading-indicator.component';
import { ConfirmModal } from '../confirm-modal/confirm-modal.component';
import { NoticeComponent } from '../notice/notice.component';
import { NostosDatePipe } from '../pipes/nostos-date.pipe';
import { UNKNOWN_AUTHOR, UNKNOWN_BOOK } from '../../core/book-format';

type Stage = 'loading' | 'setup' | 'unavailable' | 'review' | 'importing' | 'result';

/** One Calibre book, with the owner's current answer. */
export interface CalibreRow {
  id: number;
  book: CalibrePreviewBook;
  /** The format keys the owner ticked. */
  selected: ReadonlySet<string>;
  /** An explicit library book chosen for a suggested row. */
  chosenBookId: string | null;
  chosenBookLabel: string | null;
}

const PAGE_POLL_MS = 700;

export const CALIBRE_PROMISE =
  'Your Calibre library is only ever read. Nothing in it is changed or deleted.';

/** The Docker mount the image expects, matching docs/calibre-import.md. */
export const CALIBRE_DOCKER_PATH = '/external/calibre';

/**
 * Import a Calibre library (issue #1527, SelfHosted only).
 *
 * The server reads its configured library, this dialog shows what it found and
 * where every book goes, and the import runs on the server so closing the
 * window loses nothing. Tags become collections only when the owner ticks the
 * one box; a Calibre book with several formats is a set of explicit choices.
 */
@Component({
  selector: 'app-calibre-import-modal',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    ConfirmModal,
    CheckboxDirective,
    NoticeComponent,
    NostosDatePipe,
    ModalShell,
    DialogActionsComponent,
    ButtonComponent,
    NostosIconComponent,
    LoadingIndicatorComponent,
  ],
  templateUrl: './calibre-import-modal.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './calibre-import-modal.component.css',
})
export class CalibreImportModal {
  protected readonly unknownBook = UNKNOWN_BOOK;
  protected readonly unknownAuthor = UNKNOWN_AUTHOR;
  protected readonly promise = CALIBRE_PROMISE;
  protected readonly dockerPath = CALIBRE_DOCKER_PATH;

  private readonly service = inject(CalibreImportService);
  private readonly destroyRef = inject(DestroyRef);

  readonly isOpen = this.service.isOpen;
  readonly stage = signal<Stage>('loading');
  readonly status = signal<CalibreStatus | null>(null);
  readonly preview = signal<CalibrePreview | null>(null);
  readonly rows = signal<CalibreRow[]>([]);
  readonly mapTags = signal(false);
  readonly error = signal<string | null>(null);
  readonly progress = signal<CalibreImportJobState | null>(null);
  readonly results = signal<CalibreImportResultBook[]>([]);
  readonly batchId = signal<string | null>(null);

  readonly undoBusy = signal(false);
  readonly undoError = signal<string | null>(null);
  readonly undone = signal<{ removed: number; kept: number } | null>(null);
  readonly pendingUndo = signal<CalibreBatch | { id: string; bookCount: number; changeCount: number } | null>(null);
  readonly recent = signal<CalibreBatch[]>([]);

  readonly summary = computed<CalibrePreviewSummary | null>(() => this.preview()?.summary ?? null);
  readonly customColumns = computed<CalibreCustomColumn[]>(() => this.preview()?.customColumns ?? []);
  readonly skipped = computed(() => this.preview()?.skipped ?? []);
  readonly warnings = computed(() => this.preview()?.warnings ?? []);

  readonly ready = computed(() =>
    this.rows().filter((row) => row.book.match !== 'suggested' && !this.alreadyHere(row) && this.hasSelection(row)),
  );
  readonly toCheck = computed(() => this.rows().filter((row) => row.book.match === 'suggested'));
  readonly alreadyHereRows = computed(() => this.rows().filter((row) => this.alreadyHere(row)));
  readonly selectedBooks = computed(() => this.rows().filter((row) => this.hasSelection(row)).length);
  readonly selectedFormats = computed(() =>
    this.rows().reduce((total, row) => total + row.selected.size, 0),
  );
  readonly hasTags = computed(() => this.rows().some((row) => row.book.tags.length > 0));

  readonly importing = computed(() => this.stage() === 'importing');
  readonly busy = computed(() => this.stage() === 'loading' || this.stage() === 'importing');
  readonly canImport = computed(() => this.selectedFormats() > 0 && !this.busy());

  readonly imported = computed(() => this.results().filter((book) => book.status === 'imported'));
  readonly unchanged = computed(() => this.results().filter((book) => book.status === 'unchanged'));
  readonly failed = computed(() => this.results().filter((book) => book.status === 'failed' || book.status === 'skipped'));

  readonly headline = computed(() => {
    if (this.undone()) return 'Import undone';
    if (this.error()) return 'The library could not be read';
    const imported = this.imported().length;
    if (imported > 0) return `${this.count(imported, 'book')} brought in`;
    if (this.unchanged().length > 0) return 'Already up to date';
    return this.failed().length > 0 ? 'Nothing was imported' : 'Nothing was imported';
  });

  readonly summaryLine = computed(() => {
    const undone = this.undone();
    if (undone) {
      const kept = undone.kept > 0
        ? ` ${this.count(undone.kept, 'book')} stayed because you have read or written in them.`
        : '';
      return `${this.count(undone.removed, 'row')} taken back out of your library.${kept}`;
    }
    const imported = this.imported().length;
    const files = this.imported().reduce((total, book) => total + book.fileCount, 0);
    if (imported > 0) return `Into ${this.count(imported, 'book')}, ${this.count(files, 'file')}.`;
    if (this.unchanged().length > 0) return 'Every file this selection would bring is already in Nostos.';
    return '';
  });

  readonly progressPercent = computed(() => {
    const state = this.progress();
    if (!state || state.total <= 0) return 0;
    return Math.min(100, Math.round((state.completed / state.total) * 100));
  });

  readonly statusLine = computed(() => {
    const state = this.progress();
    if (!state) return '';
    if (state.total <= 0) return 'Reading the library…';
    return `${state.completed} of ${state.total} books`;
  });

  private nextRowId = 0;
  private poll?: Subscription;

  constructor() {
    effect(() => {
      if (!this.isOpen()) return;
      untracked(() => this.load());
    });
  }

  // --- Reading -----------------------------------------------------------

  async load(): Promise<void> {
    this.stopPolling();
    this.stage.set('loading');
    this.error.set(null);
    this.undone.set(null);
    this.undoError.set(null);
    this.results.set([]);
    this.batchId.set(null);
    this.preview.set(null);
    this.rows.set([]);
    this.mapTags.set(false);

    try {
      const status = await firstValueFrom(this.service.status());
      this.status.set(status);

      if (!status.configured) {
        this.stage.set('setup');
        this.loadRecent();
        return;
      }

      if (!status.available) {
        this.stage.set('unavailable');
        this.loadRecent();
        return;
      }

      if (status.import.status === 'importing') {
        this.progress.set(status.import);
        this.stage.set('importing');
        this.startPolling();
        return;
      }

      const preview = await firstValueFrom(this.service.preview());
      this.preview.set(preview);
      this.rows.set(preview.books.map((book) => this.toRow(book)));
      this.stage.set('review');
      this.loadRecent();
    } catch (error) {
      this.error.set(this.failureMessage(error));
      this.stage.set('unavailable');
    }
  }

  refresh(): void {
    void this.load();
  }

  private toRow(book: CalibrePreviewBook): CalibreRow {
    const selected = book.match === 'suggested'
      ? new Set<string>()
      : new Set(book.formats
          .filter((format) => format.selectedByDefault && !format.alreadyHere)
          .map((format) => format.key));
    return {
      id: this.nextRowId++,
      book,
      selected,
      chosenBookId: null,
      chosenBookLabel: null,
    };
  }

  // --- Answering ---------------------------------------------------------

  toggleFormat(row: CalibreRow, format: CalibreFormatChoice): void {
    if (format.alreadyHere || this.busy()) return;
    const selected = new Set(row.selected);
    if (selected.has(format.key)) selected.delete(format.key);
    else selected.add(format.key);
    this.patch(row, { selected });
  }

  chooseCandidate(row: CalibreRow, candidate: HighlightImportCandidate): void {
    const selected = new Set(row.selected);
    for (const format of row.book.formats) {
      if (!format.alreadyHere && format.selectedByDefault) selected.add(format.key);
    }
    this.patch(row, {
      chosenBookId: candidate.bookId,
      chosenBookLabel: `${candidate.title}${candidate.author ? ` — ${candidate.author}` : ''}`,
      selected,
    });
  }

  clearCandidate(row: CalibreRow): void {
    this.patch(row, { chosenBookId: null, chosenBookLabel: null, selected: new Set<string>() });
  }

  selectAllReady(): void {
    this.rows.update((rows) => rows.map((row) => {
      if (row.book.match === 'suggested' || this.alreadyHere(row) || row.selected.size > 0) return row;
      return {
        ...row,
        selected: new Set(row.book.formats
          .filter((format) => format.selectedByDefault && !format.alreadyHere)
          .map((format) => format.key)),
      };
    }));
  }

  onMapTags(event: Event): void {
    this.mapTags.set((event.target as HTMLInputElement).checked);
  }

  // --- Importing ---------------------------------------------------------

  async importNow(): Promise<void> {
    if (!this.canImport()) return;

    this.stage.set('importing');
    this.error.set(null);
    this.progress.set(null);
    const decisions = this.rows()
      .filter((row) => row.selected.size > 0)
      .map((row) => ({
        sourceKey: row.book.sourceKey,
        formats: [...row.selected],
        bookId: row.chosenBookId ?? undefined,
        createCollections: this.mapTags(),
      }));

    try {
      this.progress.set(await firstValueFrom(this.service.start(decisions)));
      this.startPolling();
    } catch (error) {
      this.error.set(this.failureMessage(error));
      this.stage.set('review');
    }
  }

  private startPolling(): void {
    this.stopPolling();
    this.poll = timer(0, PAGE_POLL_MS)
      .pipe(
        switchMap(() => this.service.progress()),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (state) => {
          this.progress.set(state);
          if (state.status === 'importing') return;

          this.stopPolling();
          this.results.set(state.results);
          this.batchId.set(state.batchId);
          if (state.status === 'failed' && state.error) this.error.set(state.error);
          this.stage.set('result');
          this.loadRecent();
        },
        error: () => {
          this.stopPolling();
          this.error.set('The import could not be followed. Reopen Settings to see how far it got.');
          this.stage.set('result');
        },
      });
  }

  private stopPolling(): void {
    this.poll?.unsubscribe();
    this.poll = undefined;
  }

  // --- Undo --------------------------------------------------------------

  askUndoImport(): void {
    const id = this.batchId();
    if (!id) return;
    this.pendingUndo.set({
      id,
      bookCount: this.imported().length,
      changeCount: this.imported().reduce((total, book) => total + book.fileCount, 0),
    });
  }

  askUndoRecent(batch: CalibreBatch): void {
    this.pendingUndo.set(batch);
  }

  cancelUndo(): void {
    this.pendingUndo.set(null);
  }

  async confirmUndo(): Promise<void> {
    const pending = this.pendingUndo();
    if (!pending || this.undoBusy()) return;

    this.undoBusy.set(true);
    this.undoError.set(null);
    try {
      const result = await firstValueFrom(this.service.undo(pending.id));
      this.undone.set(result);
      this.pendingUndo.set(null);
      this.batchId.set(null);
      this.stage.set('result');
      this.loadRecent();
    } catch {
      this.undoError.set('That import could not be undone. Try again.');
    } finally {
      this.undoBusy.set(false);
    }
  }

  undoDescription(): string {
    const pending = this.pendingUndo();
    if (!pending) return '';
    return `The files this import copied and the collections it created will be taken back. Books you have read or written in stay.`;
  }

  // --- Result and helper -------------------------------------------------

  importAgain(): void {
    this.results.set([]);
    this.batchId.set(null);
    this.undone.set(null);
    this.error.set(null);
    this.load();
  }

  close(): void {
    this.stopPolling();
    this.service.close();
    this.stage.set('loading');
  }

  countLabel(value: number, noun: string): string {
    return this.count(value, noun);
  }

  formatSize(bytes: number): string {
    if (bytes <= 0) return '';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  trackFormat(_: number, format: CalibreFormatChoice): string {
    return format.key;
  }

  hasSelection(row: CalibreRow): boolean {
    return row.selected.size > 0;
  }

  alreadyHere(row: CalibreRow): boolean {
    return row.book.formats.length > 0 && row.book.formats.every((format) => format.alreadyHere);
  }

  private patch(row: CalibreRow, changes: Partial<CalibreRow>): void {
    this.rows.update((rows) => rows.map((candidate) => (candidate.id === row.id ? { ...candidate, ...changes } : candidate)));
  }

  private loadRecent(): void {
    this.service.recent().subscribe({
      next: (batches) => this.recent.set(batches),
      error: () => this.recent.set([]),
    });
  }

  private failureMessage(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      const body = error.error as { error?: string; message?: string } | null;
      return body?.error ?? body?.message ?? 'The Calibre library could not be read.';
    }
    return 'The Calibre library could not be read.';
  }

  private count(value: number, noun: string): string {
    return value === 1 ? `1 ${noun}` : `${value} ${noun}s`;
  }
}
