/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { Book } from '../../core/dtos/book.dtos';
import type { ImportActivity } from '../../core/dtos/import.dtos';
import { importStageLabel } from '../../core/dtos/import.dtos';
import { LibraryPreferencesService } from '../../core/services/library-preferences.service';
import { ImportService } from '../../core/services/import.service';
import { CheckboxDirective } from '../../ui/check/check.directive';
import {
  BookCoverComponent,
  bookCoverFormat,
  type BookCoverFormat,
} from '../../ui/book-cover/book-cover.component';
import { coverImageUrl } from '../../core/cover-url';
import { formatOf, UNKNOWN_AUTHOR, type BookFormatConcept } from '../../core/book-format';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { ProgressComponent } from '../../ui/progress/progress.component';
import { LibraryItemFailure } from '../library-item-failure/library-item-failure.component';
import { libraryBookLink } from '../library-book-link';
import {
  workFormatGlyphs,
  workFormatsTooltip,
  type WorkFormatGlyph,
} from '../library-work-format';
import { LibraryEmptyState } from '../library-empty-state/library-empty-state.component';

/**
 * The shelf as covers: one card per book, each linking to its detail page.
 *
 * WHY THIS EXISTS. The card is a self-contained surface — cover, format badge,
 * hover discs, the import overlay drawn on the art — and its stylesheet (the
 * cover frame, the bloom, the badge's frosted capsule, the reveal rules) has no
 * business in the page that also owns the toolbar, the sidebar and pagination.
 * Extracting it is also the only way the grid's CSS can sit under Angular's
 * `anyComponentStyle` budget as the shelf grows.
 *
 * The card reports intents and the shelf decides: this component owns no book
 * state, so a favourite tap is an output, not a service call. The two things it
 * does read itself are per-item lookups that would otherwise be piped through
 * the parent for no reason — the live import progress for a book (a map lookup)
 * and the reader's saved edition, which is what the card's link targets.
 *
 * The host IS the grid container and carries `.book-grid`, so the global rules
 * that address the shelf by that class, and the results stage's enter animation
 * (which targets a direct child), keep matching exactly as before.
 */
@Component({
  selector: 'app-library-book-grid',
  standalone: true,
  imports: [
    RouterLink,
    CheckboxDirective,
    BookCoverComponent,
    NostosIconComponent,
    IconButtonComponent,
    ProgressComponent,
    LibraryItemFailure,
    LibraryEmptyState,
  ],
  templateUrl: './library-book-grid.component.html',
  styleUrls: ['./library-book-grid.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LibraryBookGrid {
  protected readonly unknownAuthor = UNKNOWN_AUTHOR;

  private readonly preferences = inject(LibraryPreferencesService);
  private readonly imports = inject(ImportService);

  readonly books = input.required<readonly Book[]>();
  readonly selecting = input(false);
  readonly selected = input.required<ReadonlySet<string>>();
  /** The search and filter state, so an empty shelf says which one emptied it. */
  readonly searchQuery = input('');
  readonly hasFilters = input(false);

  readonly favoriteToggled = output<Book>();
  readonly finishedToggled = output<Book>();
  readonly editRequested = output<Book>();
  readonly deleteRequested = output<Book>();
  readonly bookPressed = output<Book>();
  readonly selectionChanged = output<{ id: string; on: boolean }>();
  readonly dragStarted = output<{ book: Book; event: DragEvent }>();
  readonly hoverStarted = output<Book>();
  readonly hoverEnded = output<void>();
  readonly clearSearch = output<void>();
  readonly clearFilters = output<void>();
  readonly addBook = output<void>();

  /** A book still being imported has nothing to act on yet. */
  isSelectable(book: Book): boolean {
    return book.status !== 1 && book.status !== 2;
  }

  /** The book's route, or none while Select mode owns the press. */
  bookLink(book: Book): string[] | null {
    return libraryBookLink(book, this.selecting(), this.preferences);
  }

  coverUrl(book: Book): string | null {
    return coverImageUrl(book, 320);
  }

  /** The format a cover-less book's placeholder names. */
  coverFormat(book: Book): BookCoverFormat | null {
    return bookCoverFormat(book);
  }

  /** The grid badge sets the shared format name in capitals. */
  formatLabel(book: Book): string {
    return formatOf(book).format.toUpperCase();
  }

  formatConcept(book: Book): BookFormatConcept {
    return formatOf(book).concept;
  }

  /** The formats this work is available in, for the badge. */
  formatGlyphs(book: Book): WorkFormatGlyph[] {
    return workFormatGlyphs(book);
  }

  formatsTooltip(book: Book): string {
    return workFormatsTooltip(book);
  }

  /** The import driving this book, or undefined when it is not importing. */
  bookProgress(book: Book): ImportActivity | undefined {
    return this.imports.progressByBookId().get(book.id);
  }

  /** The stage word for an in-flight book; the book's own status until the feed reports. */
  bookStageLabel(book: Book): string {
    const progress = this.bookProgress(book);
    return progress ? importStageLabel(progress) : book.status === 2 ? 'Transcoding' : 'Downloading';
  }

  /** Screen-reader text for the item's bar. */
  bookProgressLabel(book: Book): string {
    const progress = this.bookProgress(book);
    const stage = this.bookStageLabel(book);
    return progress ? `${stage}, ${progress.percent} percent` : stage;
  }

  onFavorite(book: Book, event: Event): void {
    event.stopPropagation();
    this.favoriteToggled.emit(book);
  }

  onFinished(book: Book, event: Event): void {
    event.stopPropagation();
    this.finishedToggled.emit(book);
  }

  onEdit(book: Book, event: Event): void {
    event.stopPropagation();
    this.editRequested.emit(book);
  }

  onDelete(book: Book, event: Event): void {
    event.stopPropagation();
    this.deleteRequested.emit(book);
  }

  onSelection(book: Book, on: boolean): void {
    this.selectionChanged.emit({ id: book.id, on });
  }
}
