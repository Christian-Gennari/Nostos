import { Component, computed, input, output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { LinkableBookDto } from '../../core/dtos/book.dtos';
import { ModalShell } from '../../ui/modal-shell/modal-shell.component';
import { NostosIconComponent } from '../../ui/icon/nostos-icon.component';
import { IconButtonComponent } from '../../ui/icon-button/icon-button.component';
import { InputDirective } from '../../ui/form-control/form-control.directive';

/** One book in the current work, as the membership list needs it. */
export interface WorkMember {
  id: string;
  title: string;
  author: string | null;
}

/**
 * The advanced work-membership surface: which books are editions of the same
 * topicual work, and the explicit override for when automatic grouping got it
 * wrong.
 *
 * It is a modal rather than a panel in the details rail because the job needs
 * room the rail does not have (a search field, a result list, per-candidate
 * disambiguation) and because link/unlink restructure a relationship across
 * books rather than editing this book's own fields. Keeping it out of the
 * Edit Book form matters for a concrete reason: that form is a deferred save
 * (`Save Changes` fires the one PUT, `Cancel` discards), while these mutations
 * apply immediately — an unlink confirmed inside that form would survive a
 * Cancel that the user believed undid everything.
 *
 * Presentation only: it owns no data and calls no service. The caller supplies
 * the membership and candidate lists and handles the two intents, so all state
 * and every mutation stay with the page that already owns them.
 */
@Component({
  selector: 'app-editions-modal',
  standalone: true,
  imports: [CommonModule, FormsModule, NostosIconComponent, IconButtonComponent, InputDirective, ModalShell],
  templateUrl: './editions-modal.component.html',
  styleUrl: './editions-modal.component.css',
})
export class EditionsModal {
  isOpen = input.required<boolean>();

  /** Every book in the current work, the current book included. */
  members = input<WorkMember[]>([]);

  /** Books that could be linked in, already deduplicated by work. */
  candidates = input<LinkableBookDto[]>([]);

  /** The book being viewed: named in the copy and marked in the list. */
  currentBookId = input<string | null>(null);
  bookTitle = input<string>('');

  /** True while a membership mutation is in flight. */
  busy = input<boolean>(false);

  /** True while the candidate search is in flight. */
  searching = input<boolean>(false);

  /** The search text, owned by the caller so it survives a re-open. */
  query = input<string>('');

  close = output<void>();
  linkRequest = output<LinkableBookDto>();
  unlinkRequest = output<WorkMember>();
  queryChange = output<string>();

  /**
   * How many candidates the resting list shows before it asks for a search.
   *
   * The caller's first page is the 20 newest books, which is 1469px of rows
   * inside a 640px dialog: the opening state was a scroll, so "search to narrow"
   * was advice the user never saw the need for. Typing is not capped — a query is
   * already a deliberate act, and its result set is the one the user asked for.
   */
  private readonly RESTING_CANDIDATE_LIMIT = 5;

  /** The candidates actually rendered. */
  readonly shownCandidates = computed(() => {
    const all = this.candidates();
    return this.query().trim() ? all : all.slice(0, this.RESTING_CANDIDATE_LIMIT);
  });

  /** True when the resting list is shorter than what the caller supplied. */
  readonly candidatesTruncated = computed(
    () => !this.query().trim() && this.candidates().length > this.shownCandidates().length
  );

  // Escape and the backdrop now belong to `app-modal-shell`, which emits
  // `closed`; both were hand-rolled here before, including the `busy` guard
  // that stopped a save being dismissed mid-flight.
  onQueryInput(value: string): void {
    this.queryChange.emit(value);
  }
}
