/**
 * Copyright (C) 2026 Christian Gennari
 * SPDX-License-Identifier: AGPL-3.0-or-later
 */
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { ButtonComponent } from '../../ui/button/button.component';
import { EmptyStateComponent } from '../../ui/empty-state/empty-state.component';

/**
 * What the shelf says when it has no books to show (issue #1202): the search
 * found nothing, the filters did not, or the library is genuinely empty — each
 * with the one action that resolves it.
 *
 * One empty state for both views, extracted so the wording and the "which one
 * emptied it" decision live in one place instead of inside two `@for … @empty`
 * blocks. The caller only says what it knows: the search text and whether any
 * filter is on.
 */
@Component({
  selector: 'app-library-empty-state',
  standalone: true,
  imports: [EmptyStateComponent, ButtonComponent],
  template: `
    @if (search()) {
      <nostos-empty-state icon="book" [message]="searchMessage()">
        <button appButton variant="secondary" size="sm" type="button" (click)="clearSearch.emit()">Clear search</button>
      </nostos-empty-state>
    } @else if (hasFilters()) {
      <nostos-empty-state icon="book" message="Nothing matches these filters">
        <button appButton variant="secondary" size="sm" type="button" (click)="clearFilters.emit()">Clear filters</button>
      </nostos-empty-state>
    } @else {
      <nostos-empty-state icon="book" message="Your library is empty">
        <button appButton variant="primary" type="button" (click)="addBook.emit()">Add your first book</button>
      </nostos-empty-state>
    }
  `,
  styles: [':host { display: block; }'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'books-empty-state' },
})
export class LibraryEmptyState {
  /** The search text, trimmed: empty when there is none. */
  readonly search = input('', { transform: (value: string) => value.trim() });
  /** Whether a status, format or collection filter is on. */
  readonly hasFilters = input(false);

  readonly clearSearch = output<void>();
  readonly clearFilters = output<void>();
  readonly addBook = output<void>();

  protected readonly searchMessage = computed(() => `No books match "${this.search()}"`);
}
