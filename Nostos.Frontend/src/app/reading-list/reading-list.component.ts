import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  OnInit,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NostosDatePipe } from '../ui/pipes/nostos-date.pipe';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable, finalize, switchMap, takeWhile, timer } from 'rxjs';
import { AddBookModal, AddBookPrefill } from '../add-book-modal/add-book-modal.component';
import { Collection } from '../core/dtos/collection.dtos';
import { ACQUISITION_FINISHED_STATES } from '../core/dtos/provider.dtos';
import { ReadingListEntry, SaveReadingListEntry } from '../core/dtos/reading-list.dtos';
import { BookLookupError, BooksService } from '../core/services/books.service';
import { CollectionsService } from '../core/services/collections.service';
import { ProvidersService } from '../core/services/providers.service';
import { ReadingListService } from '../core/services/reading-list.service';
import { ToastService } from '../core/services/toast.service';
import { ButtonComponent } from '../ui/button/button.component';
import { ConfirmModal } from '../ui/confirm-modal/confirm-modal.component';
import { DialogActionsComponent } from '../ui/dialog-actions/dialog-actions.component';
import { InputDirective, TextareaDirective } from '../ui/form-control/form-control.directive';
import { FormFieldComponent } from '../ui/form-field/form-field.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { NOSTOS_MENU } from '../ui/menu/menu.component';
import { ModalShell } from '../ui/modal-shell/modal-shell.component';
import { BookCoverComponent } from '../ui/book-cover/book-cover.component';

/** Muted cloth hues for the typeset covers: rust, ochre, olive, green, teal, blue, violet, plum. */
/** Digits (and a trailing X) only, 10 or 13 long: what an ISBN looks like once its dashes are gone. */
const ISBN_SHAPE = /^(?:\d{9}[\dXx]|\d{13})$/;

/**
 * What was typed, as a bare ISBN if it is one: an "ISBN" or "ISBN-13:" label,
 * spaces, dots and any kind of dash (phone keyboards and copied text bring
 * en dashes and non-breaking hyphens) are dropped. Null when it is not an ISBN.
 */
function asIsbn(typed: string): string | null {
  const bare = typed
    .replace(/^isbn(?:-1[03])?\s*:?/i, '')
    .replace(/[\s.\-\u2010-\u2015\u2212]/g, '');
  return ISBN_SHAPE.test(bare) ? bare.toUpperCase() : null;
}

/**
 * The reading list: books the user wants to read and does not have yet.
 *
 * Two tiers. "Next up" is a short shelf in reading order; "Someday" is
 * everything else, newest first, with no order to keep. Adding takes a title
 * and nothing more, and an entry only becomes a library book through
 * "I got it".
 */
@Component({
  selector: 'app-reading-list',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    BookCoverComponent,
    NostosDatePipe,
    FormsModule,
    RouterLink,
    AddBookModal,
    ButtonComponent,
    ConfirmModal,
    DialogActionsComponent,
    FormFieldComponent,
    IconButtonComponent,
    InputDirective,
    ModalShell,
    NostosIconComponent,
    ...NOSTOS_MENU,
    TextareaDirective,
  ],
  templateUrl: './reading-list.component.html',
  styleUrl: './reading-list.component.css',
})
export class ReadingList implements OnInit {
  private readonly list = inject(ReadingListService);
  private readonly books = inject(BooksService);
  private readonly collectionsService = inject(CollectionsService);
  private readonly providers = inject(ProvidersService);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  private readonly titleInput = viewChild<ElementRef<HTMLInputElement>>('titleInput');

  readonly next = this.list.next;
  readonly someday = this.list.someday;
  readonly count = this.list.count;
  readonly loaded = this.list.loaded;
  readonly loadFailed = signal(false);

  readonly summary = computed(() => {
    const count = this.count();
    if (count === 0) return 'Books you want to read. None of them has to be in your library.';
    const noun = count === 1 ? 'book' : 'books';
    return `${count} ${noun} you want to read. None of them has to be in your library.`;
  });

  // --- Quick add ---
  draftTitle = '';
  draftAuthor = '';
  readonly adding = signal(false);

  // --- Edit ---
  readonly editTarget = signal<ReadingListEntry | null>(null);
  editForm = { title: '', author: '', why: '', recommendedBy: '' };
  readonly saving = signal(false);

  // --- Remove ---
  readonly removeTarget = signal<ReadingListEntry | null>(null);
  readonly removing = signal(false);
  readonly removeHeading = computed(() => {
    const entry = this.removeTarget();
    return entry ? `Remove “${entry.title}” from your reading list?` : '';
  });

  // --- I got it ---
  readonly acquireTarget = signal<ReadingListEntry | null>(null);
  readonly acquirePrefill = computed<AddBookPrefill | null>(() => {
    const entry = this.acquireTarget();
    return entry
      ? {
          title: entry.title,
          author: entry.author,
          isbn: entry.isbn,
          publishedDate: entry.publishedDate,
        }
      : null;
  });
  readonly collections = signal<Collection[]>([]);
  private collectionsLoaded = false;
  /** The entry the open sheet is for, kept until the sheet reports what it made. */
  private pendingAcquire: ReadingListEntry | null = null;

  /** Ids with a shelf change in flight, so a double click cannot send two orders. */
  readonly busyIds = signal<ReadonlySet<string>>(new Set());

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.loadFailed.set(false);
    this.list
      .load()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ error: () => this.loadFailed.set(true) });
  }

  // ---------------------------------------------------------------- add

  add(event?: Event): void {
    event?.preventDefault();
    const title = this.draftTitle.trim();
    if (!title || this.adding()) return;

    const author = this.draftAuthor.trim();
    const isbn = asIsbn(title);

    this.adding.set(true);
    if (isbn) {
      this.addByIsbn(isbn, author || null);
      return;
    }

    this.create({ title, author: author || null });
  }

  /**
   * A typed ISBN is looked up so the entry carries the book's real title, not
   * a number. An author typed alongside it wins over the looked-up one. A
   * lookup without a title counts as nothing found: the number is never saved
   * as the title.
   */
  private addByIsbn(isbn: string, author: string | null): void {
    this.books.lookup(isbn).subscribe({
      next: (found) => {
        const title = found.title?.trim();
        if (!title) {
          this.isbnNotFound();
          return;
        }
        this.create({
          title,
          author: author ?? found.author ?? null,
          isbn,
          publishedDate: found.publishedDate ?? null,
        });
      },
      error: (error: unknown) => {
        if (error instanceof BookLookupError && error.reason === 'unavailable') {
          this.adding.set(false);
          this.toast.error('Book lookup is not answering right now. Try again, or type the title.');
          return;
        }
        this.isbnNotFound();
      },
    });
  }

  private isbnNotFound(): void {
    this.adding.set(false);
    this.toast.error('No book was found for that ISBN. Type the title instead.');
  }

  private create(entry: SaveReadingListEntry): void {
    this.list
      .add(entry)
      .pipe(finalize(() => this.adding.set(false)))
      .subscribe({
        next: () => {
          this.draftTitle = '';
          this.draftAuthor = '';
          // Back to the title field: the next book is one keystroke away.
          setTimeout(() => this.titleInput()?.nativeElement.focus(), 0);
        },
        error: (error) => this.toast.error(this.describe(error, 'The book could not be added.')),
      });
  }

  // ---------------------------------------------------------------- shelf

  isBusy(entry: ReadingListEntry): boolean {
    return this.busyIds().has(entry.id);
  }

  promote(entry: ReadingListEntry): void {
    this.setShelf(entry, [...this.next().map((e) => e.id), entry.id]);
  }

  demote(entry: ReadingListEntry): void {
    this.setShelf(
      entry,
      this.next()
        .map((e) => e.id)
        .filter((id) => id !== entry.id),
    );
  }

  /** Moves a shelf entry one place earlier (-1) or later (+1). */
  move(entry: ReadingListEntry, offset: -1 | 1): void {
    const ids = this.next().map((e) => e.id);
    const from = ids.indexOf(entry.id);
    const to = from + offset;
    if (from < 0 || to < 0 || to >= ids.length) return;

    [ids[from], ids[to]] = [ids[to], ids[from]];
    this.setShelf(entry, ids);
  }

  private setShelf(entry: ReadingListEntry, ids: string[]): void {
    if (this.isBusy(entry)) return;

    this.track(entry.id, this.list.setNext(ids)).subscribe({
      error: (error) => {
        this.toast.error(this.describe(error, 'The order could not be saved.'));
        this.reload();
      },
    });
  }

  private track<T>(id: string, work: Observable<T>): Observable<T> {
    this.busyIds.update((ids) => new Set(ids).add(id));
    return work.pipe(
      finalize(() =>
        this.busyIds.update((ids) => {
          const rest = new Set(ids);
          rest.delete(id);
          return rest;
        }),
      ),
    );
  }

  // ---------------------------------------------------------------- edit

  openEdit(entry: ReadingListEntry): void {
    this.editForm = {
      title: entry.title,
      author: entry.author ?? '',
      why: entry.why ?? '',
      recommendedBy: entry.recommendedBy ?? '',
    };
    this.editTarget.set(entry);
  }

  closeEdit(): void {
    if (!this.saving()) this.editTarget.set(null);
  }

  saveEdit(event?: Event): void {
    event?.preventDefault();
    const entry = this.editTarget();
    const title = this.editForm.title.trim();
    if (!entry || !title || this.saving()) return;

    this.saving.set(true);
    this.list
      .update(entry.id, {
        title,
        author: this.editForm.author.trim() || null,
        isbn: entry.isbn,
        publishedDate: entry.publishedDate,
        why: this.editForm.why.trim() || null,
        recommendedBy: this.editForm.recommendedBy.trim() || null,
      })
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: () => this.editTarget.set(null),
        error: (error) => this.toast.error(this.describe(error, 'The changes could not be saved.')),
      });
  }

  // ---------------------------------------------------------------- remove

  confirmRemove(): void {
    const entry = this.removeTarget();
    if (!entry || this.removing()) return;

    this.removing.set(true);
    this.list
      .remove(entry.id)
      .pipe(finalize(() => this.removing.set(false)))
      .subscribe({
        next: () => this.removeTarget.set(null),
        error: () => this.toast.error('The book could not be removed.'),
      });
  }

  // ---------------------------------------------------------------- I got it

  gotIt(entry: ReadingListEntry): void {
    this.pendingAcquire = entry;
    this.acquireTarget.set(entry);

    if (!this.collectionsLoaded) {
      this.collectionsLoaded = true;
      this.collectionsService.list().subscribe({
        next: (collections) => this.collections.set(collections),
        error: () => (this.collectionsLoaded = false),
      });
    }
  }

  closeAcquire(): void {
    this.acquireTarget.set(null);
  }

  /** The sheet produced a library book: the entry has done its job. */
  onBookCreated(bookId: string): void {
    const entry = this.pendingAcquire;
    this.pendingAcquire = null;
    if (entry) this.finish(entry, bookId);
  }

  /**
   * An import from a free source was queued. Its book row appears a moment
   * later, so the job is followed until it names one. Leaving the page stops
   * the follow; the entry then shows "It's in your library" on the next visit.
   */
  onImportQueued(jobId: string): void {
    const entry = this.pendingAcquire;
    this.pendingAcquire = null;
    if (!entry) return;

    timer(1000, 2000)
      .pipe(
        switchMap(() => this.providers.job(jobId)),
        takeWhile((job) => !job.bookId && !ACQUISITION_FINISHED_STATES.has(job.state), true),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (job) => {
          if (job.bookId) this.finish(entry, job.bookId, { quiet: true });
        },
        error: () => undefined,
      });
  }

  /** The entry is already a library book (added some other way): finish the same move. */
  finishOwned(entry: ReadingListEntry): void {
    if (entry.ownedBookId) this.finish(entry, entry.ownedBookId);
  }

  private finish(entry: ReadingListEntry, bookId: string, options?: { quiet: boolean }): void {
    if (this.isBusy(entry)) return;

    this.track(entry.id, this.list.markAcquired(entry.id, bookId)).subscribe({
      next: () => {
        if (!options?.quiet) this.toast.success(`“${entry.title}” is in your library.`);
      },
      error: () =>
        this.toast.error(`“${entry.title}” is in your library, but it could not leave this list.`),
    });
  }

  private describe(error: unknown, fallback: string): string {
    const body = (error as { error?: { detail?: string } })?.error;
    return body?.detail || fallback;
  }
}
