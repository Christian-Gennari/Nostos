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
import { StarRatingComponent } from '../../ui/star-rating/star-rating.component';
import { NostosDatePipe } from '../../ui/pipes/nostos-date.pipe';
import { LibraryItemFailure } from '../library-item-failure/library-item-failure.component';
import { libraryBookLink } from '../library-book-link';
import {
  workFormatGlyphs,
  workFormatsTooltip,
  type WorkFormatGlyph,
} from '../library-work-format';
import { LibraryEmptyState } from '../library-empty-state/library-empty-state.component';

/**
 * The shelf as rows: title, author, rating, format and date, one book per line.
 *
 * WHY THIS EXISTS. The row is a table with its own column recipe, its own hover
 * cluster and its own themed tooltips — a surface unrelated to the toolbar, the
 * pager and the results stage that used to share its stylesheet. It is also the
 * one view where the icon-button tips live, so the whole `data-tip` recipe moved
 * here with the markup it decorates instead of staying in a stylesheet that also
 * painted a grid of covers.
 *
 * Like the grid, it reports intents and owns no book state: a favourite tap is an
 * output. It reads the live import feed itself (a per-book map lookup) and the
 * reader's saved edition for the row's link.
 *
 * The host IS the table and carries `.table-view`, so the results stage's enter
 * animation and any external class hooks keep matching.
 */
@Component({
  selector: 'app-library-book-list',
  standalone: true,
  imports: [
    RouterLink,
    CheckboxDirective,
    BookCoverComponent,
    NostosIconComponent,
    IconButtonComponent,
    ProgressComponent,
    StarRatingComponent,
    NostosDatePipe,
    LibraryItemFailure,
    LibraryEmptyState,
  ],
  templateUrl: './library-book-list.component.html',
  styleUrls: ['./library-book-list.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LibraryBookList {
  private readonly preferences = inject(LibraryPreferencesService);
  private readonly imports = inject(ImportService);

  protected readonly unknownAuthor = UNKNOWN_AUTHOR;

  readonly books = input.required<readonly Book[]>();
  readonly selecting = input(false);
  readonly selected = input.required<ReadonlySet<string>>();
  /** The search and filter state, so an empty shelf says which one emptied it. */
  readonly searchQuery = input('');
  readonly hasFilters = input(false);

  readonly favoriteToggled = output<Book>();
  readonly finishedToggled = output<Book>();
  readonly ratingChanged = output<{ book: Book; rating: number }>();
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

  /** The list's plain-text format: the one shared name, "Audiobook", "Ebook", "PDF", "Physical". */
  formatLabel(book: Book): string {
    return formatOf(book).format;
  }

  /** The format icon's concept for one edition, drawn the way its label reads. */
  formatConcept(book: Book): BookFormatConcept {
    return formatOf(book).concept;
  }

  /** The formats this work is available in, for the row's micro-glyphs. */
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

  /** Screen-reader text for the row's bar. */
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

  onRating(book: Book, rating: number): void {
    this.ratingChanged.emit({ book, rating });
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
