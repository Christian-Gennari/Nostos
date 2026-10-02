import { Component, computed, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  HighlightImportBook,
  HighlightImportService,
} from '../../core/services/highlight-import.service';
import { ModalShell } from '../modal-shell/modal-shell.component';
import { DialogActionsComponent } from '../dialog-actions/dialog-actions.component';
import { ButtonComponent } from '../button/button.component';
import { IconButtonComponent } from '../icon-button/icon-button.component';
import { NostosIconComponent } from '../icon/nostos-icon.component';

type Stage = 'pick' | 'importing' | 'result';

interface FileFailure {
  fileName: string;
  message: string;
}

/**
 * Import highlights and notes from an e-reader (issue #656): pick or drop the
 * files, wait, read what happened per book.
 *
 * There is no "which device?" step — the file name says it — and no preview
 * step: an import only adds notes and never duplicates one it has already
 * brought in, so a dry run would be a click that protects nothing.
 *
 * Hosted once at the app root and opened through `HighlightImportService`, so
 * Settings and the command palette share it.
 */
@Component({
  selector: 'app-highlight-import-modal',
  standalone: true,
  imports: [ModalShell, DialogActionsComponent, ButtonComponent, IconButtonComponent, NostosIconComponent],
  templateUrl: './highlight-import-modal.component.html',
  styleUrl: './highlight-import-modal.component.css',
})
export class HighlightImportModal {
  private readonly service = inject(HighlightImportService);
  private readonly router = inject(Router);

  readonly isOpen = this.service.isOpen;
  readonly stage = signal<Stage>('pick');
  readonly dragActive = signal(false);

  /** Progress through the selected files; the upload itself reports none. */
  readonly currentFileName = signal('');
  readonly currentIndex = signal(0);
  readonly fileCount = signal(0);

  readonly books = signal<HighlightImportBook[]>([]);
  readonly failures = signal<FileFailure[]>([]);

  readonly matched = computed(() => this.books().filter((book) => book.status === 'matched'));
  readonly unplaced = computed(() => this.books().filter((book) => book.status !== 'matched'));
  readonly importedCount = computed(() => this.sum(this.matched(), 'importedCount'));
  readonly duplicateCount = computed(() => this.sum(this.matched(), 'duplicateCount'));
  readonly unplacedHighlightCount = computed(() => this.sum(this.unplaced(), 'annotationCount'));

  readonly headline = computed(() => {
    const imported = this.importedCount();
    if (imported > 0) return `${this.count(imported, 'highlight')} imported`;
    if (this.duplicateCount() > 0) return 'Already up to date';
    if (this.books().length > 0) return 'No highlights imported';
    return this.failures().length > 0 ? 'Import failed' : 'No highlights found';
  });

  readonly summary = computed(() => {
    const imported = this.importedCount();
    const duplicates = this.duplicateCount();
    if (imported > 0) {
      const from = `From ${this.count(this.matched().filter((b) => b.importedCount > 0).length, 'book')}.`;
      return duplicates > 0 ? `${from} ${duplicates} more were already in Nostos.` : from;
    }
    if (duplicates > 0) {
      return duplicates === 1
        ? 'The 1 highlight found is already in Nostos.'
        : `All ${duplicates} highlights found are already in Nostos.`;
    }
    if (this.books().length > 0) return 'None of these books are in your library yet.';
    return this.failures().length > 0 ? '' : 'The file holds no highlights or notes.';
  });

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
    void this.importFiles(Array.from(event.dataTransfer?.files ?? []));
  }

  onFilesSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    // Reset so choosing the same file again still fires `change`.
    input.value = '';
    void this.importFiles(files);
  }

  async importFiles(files: File[]): Promise<void> {
    if (files.length === 0 || this.stage() === 'importing') return;

    this.books.set([]);
    this.failures.set([]);
    this.fileCount.set(files.length);
    this.stage.set('importing');

    for (const [index, file] of files.entries()) {
      this.currentIndex.set(index + 1);
      this.currentFileName.set(file.name);

      const source = this.service.detectSource(file.name);
      if (source === null) {
        this.fail(file, 'Not a Kobo database or a KOReader metadata file.');
        continue;
      }

      try {
        const books = await firstValueFrom(this.service.import(file, source));
        this.books.update((all) => [...all, ...books]);
      } catch (error) {
        this.fail(file, this.failureMessage(error));
      }
    }

    this.stage.set('result');
  }

  importAnother(): void {
    this.stage.set('pick');
  }

  openBook(book: HighlightImportBook): void {
    if (!book.bookId) return;
    this.close();
    void this.router.navigate(['/library', book.bookId]);
  }

  close(): void {
    if (this.stage() === 'importing') return;
    this.service.close();
    this.stage.set('pick');
    this.books.set([]);
    this.failures.set([]);
  }

  /** "3 new · 2 already imported" for a matched book's trailing slot. */
  outcome(book: HighlightImportBook): string {
    const parts: string[] = [];
    if (book.importedCount > 0) parts.push(`${book.importedCount} new`);
    if (book.duplicateCount > 0) parts.push(`${book.duplicateCount} already imported`);
    return parts.length > 0 ? parts.join(' · ') : 'Nothing to import';
  }

  count(value: number, noun: string): string {
    return `${value} ${noun}${value === 1 ? '' : 's'}`;
  }

  private sum(
    books: HighlightImportBook[],
    key: 'importedCount' | 'duplicateCount' | 'annotationCount',
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
