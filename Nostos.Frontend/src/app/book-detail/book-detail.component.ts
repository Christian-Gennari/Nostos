import { afterNextRender, Component, effect, untracked, inject, Injector, OnDestroy, OnInit, signal, model, computed, ViewChild, ElementRef, ChangeDetectionStrategy } from '@angular/core';
import { TopicChipComponent } from '../ui/topic-chip/topic-chip.component';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { READING_STATUS_LABEL, bookReadingStatusOf } from '../core/reading-status';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

// Store
import { BookDetailStore } from './book-detail.store';

// DTOs
import { Book, EditionSummaryDto, LinkableBookDto } from '../core/dtos/book.dtos';
import { formatBytes } from '../library-transfer/library-transfer.copy';

/**
 * How many RENDERED LINES a review may occupy before Book Details opens it as a
 * collapsed preview with a "Show more" control (issue #159).
 *
 * Why lines and not characters: the identical review is 16 lines in the 718px
 * desktop column and 49 lines on a 320px phone (16px/1.6 at every width), so a
 * character budget is not a height budget. A line is the same amount of reading
 * everywhere, and it is what the page actually has to spend vertically.
 *
 * Why 50: the calibration anchor in the issue is the current "Devils" review
 * (1240 chars), which must stay expanded at EVERY width — and at 320px, the
 * narrowest viewport the app serves, it already renders 49 lines. Anything at or
 * below that collapses the anchor review on a phone, so the threshold sits just
 * above it. On the desktop column 50 lines is ~1280px of review, i.e. a review
 * has to run past a full screen before it takes the page over; a normal review
 * needs no interaction and sees no control at all.
 *
 * `--review-preview-lines` in the component stylesheet sets how much of a
 * collapsed review stays visible.
 */
export const REVIEW_COLLAPSE_LINES = 50;

/** How many of the latest notes a collapsed feed shows (issue #1062). */
export const NOTES_COLLAPSED_COUNT = 10;

/** A work-membership action awaiting confirmation. */
interface PendingWorkAction {
  kind: 'link' | 'unlink';
  bookId: string;
  label: string;
  /** True when the action joins two multi-edition groups, not just two books. */
  merge: boolean;
}

// UI Components
import { AddBookModal } from '../add-book-modal/add-book-modal.component';
import { EditionsModal, LinkCandidate, WorkMember } from './editions-modal/editions-modal.component';
import { PhotoQuote, PhotoQuoteModal } from './photo-quote/photo-quote-modal.component';
import { tesseractLanguage } from './photo-quote/page-ocr';
import { ProgressComponent } from '../ui/progress/progress.component';
import { BookCoverComponent, bookCoverFormat } from '../ui/book-cover/book-cover.component';
import { ConfirmModal, type ConfirmTone } from '../ui/confirm-modal/confirm-modal.component';
import { TopicInputComponent } from '../ui/topic-input.component/topic-input.component';
import { NoteItemComponent } from '../ui/note-item/note-item.component';
import { BrainNoteActionsService } from '../brain/brain-note-actions.service';
import { BrainNoteDialogsComponent } from '../brain/brain-note-dialogs.component';
import { BrainStateService } from '../brain/brain-state.service';
import { TopicAutocompleteService } from '../ui/topic-autocomplete-panel/topic-autocomplete.service';
import { BrainTopicSuggestionsService } from '../brain/brain-topic-suggestions.service';
import { StarRatingComponent } from '../ui/star-rating/star-rating.component';
import { LibraryPreferencesService } from '../core/services/library-preferences.service';
import { BooksService } from '../core/services/books.service';
import { ToastService } from '../core/services/toast.service';
import { LibraryChangesService } from '../core/services/library-changes.service';
import { CloudEntryService } from '../core/services/cloud-entry.service';
import { bookDeleteCopy, bookDeletedToast } from '../core/book-delete-copy';
import { ExternalMediaBookStatus, ExternalMediaService } from '../core/services/external-media.service';
import { NostosIconComponent } from '../ui/icon/nostos-icon.component';
import { LoadingIndicatorComponent } from '../ui/loading-indicator/loading-indicator.component';
import { EmptyStateComponent } from '../ui/empty-state/empty-state.component';
import { ButtonComponent } from '../ui/button/button.component';
import { IconButtonComponent } from '../ui/icon-button/icon-button.component';
import { BadgeComponent } from '../ui/badge/badge.component';
import { NOSTOS_MENU } from '../ui/menu/menu.component';
import { DropdownComponent, type DropdownOption } from '../ui/dropdown/dropdown.component';
import { EditionSwitcherComponent, type EditionRatingAction } from '../ui/edition-switcher/edition-switcher.component';
import { buildEditionSwitchOptions, editionRatingNoun } from './edition-switch-options';
import { coverImageUrl } from '../core/cover-url';
import { readPaperPage } from './paper-page-progress';
import { UNKNOWN_AUTHOR, UNKNOWN_BOOK, formatOf, formatOfConcept, type BookFormat } from '../core/book-format';

/** An edition as the notes filter and the switcher name it: "Ebook · 2nd edition". */
function editionOptionName(option: { format: string; detail: string | null }): string {
  return option.detail ? `${option.format} · ${option.detail}` : option.format;
}

interface StatusConfirmCopy {
  heading: string;
  description: string;
  confirmLabel: string;
  tone: ConfirmTone;
}

@Component({
  standalone: true,
  selector: 'app-book-detail',
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    NostosIconComponent,
    LoadingIndicatorComponent,
    AddBookModal,
    EditionsModal,
    PhotoQuoteModal,
    ConfirmModal,
    ProgressComponent,
    BookCoverComponent,
    TopicInputComponent,
    NoteItemComponent,
    BrainNoteDialogsComponent,
    StarRatingComponent,
    ButtonComponent,
    IconButtonComponent,
    BadgeComponent,
    ...NOSTOS_MENU,
    DropdownComponent,
    EditionSwitcherComponent,
    EmptyStateComponent,
    TopicChipComponent,
  ],
  providers: [BookDetailStore, BrainNoteActionsService, BrainTopicSuggestionsService],
  templateUrl: './book-detail.component.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrls: ['./book-detail.component.css'],
})
export class BookDetail implements OnInit, OnDestroy {
  protected readonly unknownAuthor = UNKNOWN_AUTHOR;
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private injector = inject(Injector);

  // Inject the Store
  readonly store = inject(BookDetailStore);
  /** The book's notes are the Brain's cards, with the Brain's actions. */
  readonly brain = inject(BrainStateService);
  readonly noteActions = inject(BrainNoteActionsService);
  readonly suggestions = inject(BrainTopicSuggestionsService);
  private readonly topicAutocomplete = inject(TopicAutocompleteService);
  private preferences = inject(LibraryPreferencesService);
  private booksService = inject(BooksService);
  private toast = inject(ToastService);
  private cloudEntry = inject(CloudEntryService);
  private externalMediaApi = inject(ExternalMediaService);
  private libraryChanges = inject(LibraryChangesService);

  /**
   * A library change announced while Edit Book is open. The modal re-seeds its
   * form whenever the book it was given changes, so re-reading the book under
   * it would discard what the user is typing; the re-read waits for it to close.
   */
  private readonly changePending = signal(false);

  /**
   * Set when this book's media is read from the server's linked folder (#843)
   * rather than stored by Nostos. Only asked for on a server that reports the
   * feature, so every other deployment makes no request.
   */
  readonly externalMedia = signal<ExternalMediaBookStatus | null>(null);
  readonly isLinkedExternally = computed(() => this.externalMedia()?.linked === true);
  readonly externalFileUnavailable = computed(() => {
    const state = this.externalMedia();
    return !!state && state.linked && state.unavailableFiles > 0;
  });
  // Local UI State
  isDescriptionExpanded = signal(false);

  /**
   * A long notes feed shows only its latest notes until asked (issue #1062):
   * the feed had no limit, so the most-annotated books were the longest to
   * scroll. Oldest-first order is kept, so the hidden notes are the earlier ones
   * and the reveal sits above the feed. Resets per book.
   */
  readonly notesExpanded = signal(false);

  /**
   * The edition whose notes the feed shows, or 'all'. The feed holds every
   * edition's notes (see the store); this narrows it. It holds across a switch
   * to another edition of the same book, since the feed does too.
   */
  readonly notesEdition = signal<string>('all');

  /**
   * The editions that have notes, named as the edition switcher names them,
   * with how many. A single edition (or notes from one only) gives nothing to
   * filter by.
   */
  readonly noteEditions = computed(() => {
    const counts = new Map<string, number>();
    for (const note of this.store.notes()) counts.set(note.bookId, (counts.get(note.bookId) ?? 0) + 1);
    if (counts.size < 2) return [];
    const names = new Map(this.editionSwitchOptions().map((option) => [option.id, editionOptionName(option)]));
    const formats = new Map(this.store.notes().map((note) => [note.bookId, formatOfConcept(note.bookFormat)]));
    return [...counts].map(([id, count]) => ({
      id,
      count,
      name: names.get(id) ?? formats.get(id)?.format ?? 'Edition',
    }));
  });

  /** "All editions", then "Ebook · 12", "Audiobook · 4"; shown only when notes span editions. */
  readonly noteEditionOptions = computed<DropdownOption[]>(() => [
    { value: 'all', label: 'All editions' },
    ...this.noteEditions().map((edition) => ({ value: edition.id, label: `${edition.name} · ${edition.count}` })),
  ]);

  /** The filter in effect: a chosen edition that has no notes left falls back to all. */
  readonly notesEditionValue = computed(() => {
    const chosen = this.notesEdition();
    return this.noteEditions().some((edition) => edition.id === chosen) ? chosen : 'all';
  });

  /** The notes the feed shows, and the badge counts. */
  readonly filteredNotes = computed(() => {
    const edition = this.notesEditionValue();
    const notes = this.store.notes();
    return edition === 'all' ? notes : notes.filter((note) => note.bookId === edition);
  });

  /** Each note names its edition when the book has several. */
  readonly showNoteFormat = computed(() => this.editionSwitchOptions().length > 0);

  readonly visibleNotes = computed(() => {
    const notes = this.filteredNotes();
    return this.notesExpanded() || notes.length <= NOTES_COLLAPSED_COUNT
      ? notes
      : notes.slice(-NOTES_COLLAPSED_COUNT);
  });
  readonly hiddenNoteCount = computed(() => this.filteredNotes().length - this.visibleNotes().length);
  showMetadataModal = signal(false);
  showDeleteConfirm = signal(false);
  pendingStatus = signal<'notstarted' | 'reading' | 'finished' | null>(null);
  deleting = signal(false);

  /**
   * A long review (issue #159). Null until the review has been MEASURED — see
   * `measureReviewOverflow`. Never derived from the review text itself.
   */
  reviewOverflows = signal<boolean | null>(null);

  /** True once the reader has opened this long review on this page visit. */
  isReviewExpanded = signal(false);

  /** The rendered review paragraph, measured to decide whether to collapse it. */
  @ViewChild('reviewText') reviewText?: ElementRef<HTMLElement>;

  private reviewResizeObserver?: ResizeObserver;
  /** Reviews are keyed by id so switching book measures the new one, not the old. */
  private measuredReviewKey?: string;

  /**
   * Collapse decision for a long review (issue #159).
   *
   * This measures the rendered paragraph instead of counting characters, because
   * the same review is 16 lines at 718px and 49 lines at 320px. A ResizeObserver
   * re-runs it when the column changes width, so a review that fits on desktop
   * still collapses when the window narrows — and expands again when it grows.
   */
  constructor() {
    this.brain.ensureLoaded();
    // The add-note field's [[ autocomplete offers the Brain's topics, kept
    // current by every change the cards make.
    effect(() => this.topicAutocomplete.setTopics(this.brain.topics()));
    this.noteActions.attach({
      replace: (note) => this.store.replaceNote(note),
      remove: (noteId) => this.store.removeNote(noteId),
    });
    this.libraryChanges
      .watch()
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.changePending.set(true));

    effect(() => {
      if (!this.changePending() || this.showMetadataModal() || this.deleting()) return;
      const id = this.store.book()?.id;
      if (!id) return;
      this.changePending.set(false);
      untracked(() => this.store.refreshInBackground(id));
    });

    effect(() => {
    const book = this.store.book();
    if (book) this.preferences.setActiveEditionId(book.workId, book.id);
    });

    effect(() => {
    const book = this.store.book();
    const review = book?.personalReview ?? null;
    // The review's own TEXT is the key, not its length: an edit that swaps one
    // review for another of the same length still changes how many lines it
    // occupies, and a length-only key would skip that re-measure.
    const key = book && review ? `${book.id}:${review}` : null;

    if (!key) {
      this.measuredReviewKey = undefined;
      this.reviewOverflows.set(null);
      this.isReviewExpanded.set(false);
      this.stopObservingReview();
      return;
    }

    // Only re-measure when the review itself changes; a resize re-measures via
    // the observer below, and re-reading the same book must not reset the
    // reader's "Show more" choice.
    if (key === this.measuredReviewKey) return;
    this.measuredReviewKey = key;
    this.isReviewExpanded.set(false);
    afterNextRender(() => this.measureReviewOverflow(), { injector: this.injector });
    });
  }

  /** A pending work link/unlink awaiting confirmation (issue #143). */
  pendingWorkAction = signal<PendingWorkAction | null>(null);

  /** The format a cover-less book's placeholder names. */
  readonly coverFormat = computed(() => bookCoverFormat(this.store.book()));

  /** The delete confirmation's words; a linked book is only removed from Nostos. */
  deleteCopy = computed(() => bookDeleteCopy(this.store.book()?.title, this.isLinkedExternally()));

  /** True when the hero's decorative cover art failed to load — the band falls back to flat colour. */
  heroArtFailed = signal(false);

  /**
   * True once the hero art has painted, so the band can fade it in.
   *
   * The art is a 128 KB webp. Measured on a throttled phone-class connection it
   * lands **~1.7 s after** the page's entrance fade has finished — i.e. it drops
   * into an already-visible, already-opaque band, which reads as a jolt rather
   * than as the entrance. On localhost the file is ready before the first frame,
   * so the defect is invisible there and only shows up on a real device.
   */
  heroArtLoaded = signal(false);

  /** True once the stage cover has painted, for the same reason as above. */

  onHeroArtError(): void {
    this.heroArtFailed.set(true);
  }

  onHeroArtLoad(): void {
    this.heroArtLoaded.set(true);
  }


  /**
   * URL for the hero band's art layers. Prefers the 640px thumbnail: the art is
   * defocused to the point where detail is irrelevant, and this keeps two
   * full-bleed layers cheap. Null when the book has no cover, which drops the
   * band back to a gradient.
   */
  readonly heroArtUrl = computed<string | null>(() => coverImageUrl(this.store.book(), 640));

  /** The full-size cover, under its version so the browser can keep it. */
  readonly coverSrc = computed<string | null>(() => coverImageUrl(this.store.book()));

  newNote = model<string>('');

  /**
   * The book's reading status, worked out from all its editions (the Library's
   * filters work it out the same way). Reading wins over Finished, so a reread
   * shows as Reading; see `bookReadingStatusOf`.
   */
  private readonly bookStatus = computed(() =>
    bookReadingStatusOf(this.store.book(), this.store.book()?.otherEditions));

  /** The status chip's label and colour. */
  readonly readingStatus = computed<{ label: string; modifier: string }>(() => {
    const status = this.bookStatus().status;
    return { label: READING_STATUS_LABEL[status], modifier: status === 'notstarted' ? 'not-started' : status };
  });

  // Template Refs for hidden file inputs
  @ViewChild('coverInput') coverInput!: ElementRef<HTMLInputElement>;
  @ViewChild('fileInput') fileInput!: ElementRef<HTMLInputElement>;

  /**
   * Measures the review paragraph and sets `reviewOverflows`.
   *
   * `scrollHeight` is the only honest source here: it KEEPS reporting the full
   * content height while the clamp is clipping the visible box, so the paragraph
   * can be measured in the collapsed state and nothing has to be shown expanded
   * first. Verified on the live Devils review (410px of content, a 307px clamped
   * box, `scrollHeight` still 410) and against every clamp arrangement — the
   * clamp on a wrapper, directly on the paragraph, `-webkit-line-clamp` and
   * `max-height`. Measuring a clone, or releasing and reapplying the clamp, is
   * therefore unnecessary: there is no flicker to avoid.
   */
  private measureReviewOverflow(): void {
    const el = this.reviewText?.nativeElement;
    if (!el) {
      // The paragraph is not in the DOM (no review, or the block is not
      // rendered yet). Stay undecided rather than guessing from the text.
      this.reviewOverflows.set(null);
      return;
    }

    this.reviewOverflows.set(this.reviewLineCount(el) > REVIEW_COLLAPSE_LINES);
    this.observeReview(el);
  }

  /** The review's full rendered height expressed in lines. */
  private reviewLineCount(el: HTMLElement): number {
    const cs = getComputedStyle(el);
    const lh = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.6;
    return lh > 0 ? el.scrollHeight / lh : 0;
  }

  /** Re-measures when the review's box changes width (see `observeReview`). */
  private observedReviewWidth?: number;

  /**
   * Re-measure when the review's own box changes SIZE. The column changes width
   * across the responsive breakpoints, and a fixed line threshold is a different
   * pixel height at each width, so the verdict has to be re-taken on resize.
   *
   * Re-entrancy, named because it is the trap here: the observer also fires for
   * the height change our own clamp causes. Measured `scrollHeight` ignores the
   * clamp (above), so that delivery recomputes the SAME verdict, the signal does
   * not change, and it settles on the second pass instead of looping. The width
   * comparison is a second belt-and-braces guard for the same thing.
   * Always disconnects first: `@ViewChild` hands back a NEW element when the
   * review changes, and an observer left on the old one would keep re-measuring
   * a detached node.
   */
  private observeReview(el: HTMLElement): void {
    if (typeof ResizeObserver === 'undefined') return;
    this.reviewResizeObserver?.disconnect();
    this.observedReviewWidth = el.clientWidth;
    this.reviewResizeObserver = new ResizeObserver(() => {
      const current = this.reviewText?.nativeElement;
      if (!current || current.clientWidth === this.observedReviewWidth) return;
      this.observedReviewWidth = current.clientWidth;
      this.reviewOverflows.set(this.reviewLineCount(current) > REVIEW_COLLAPSE_LINES);
    });
    this.reviewResizeObserver.observe(el);
  }

  private stopObservingReview(): void {
    this.reviewResizeObserver?.disconnect();
    this.reviewResizeObserver = undefined;
    this.observedReviewWidth = undefined;
  }

  ngOnDestroy(): void {
    this.stopObservingReview();
  }

  readonly externalMediaLookup = effect((onCleanup) => {
    const supported = this.cloudEntry.supportsExternalMediaLibraries();
    const book = this.store.book();
    if (!supported || !book?.hasFile) {
      this.externalMedia.set(null);
      return;
    }

    const lookup = this.externalMediaApi.book(book.id).subscribe({
      next: (state) => this.externalMedia.set(state),
      // The page works without this: it only loses the linked-file wording.
      error: () => this.externalMedia.set(null),
    });
    onCleanup(() => lookup.unsubscribe());
  });

  ngOnInit(): void {
    this.route.paramMap.subscribe((params) => {
      const id = params.get('id');
      if (id) {
        this.routeBookId = id;
        this.notesExpanded.set(false);
        // A rating row asked for but never used goes with the page it was asked on.
        this.ratingRowFor.set(null);
        this.store.loadAllData(id);
      }
    });
  }

  /** "Try again" after the book failed to load. */
  retryLoad(): void {
    if (this.routeBookId) this.store.retry(this.routeBookId);
  }

  /** The book the route asked for, kept for "Try again". */
  private routeBookId: string | null = null;

  // --- UI Actions (Delegating to Store) ---

  toggleFavorite() {
    this.store.toggleFavorite();
  }
  toggleFinished() {
    this.store.toggleFinished();
  }
  onRate(rating: number) {
    this.store.rate(rating);
  }

  /** The reading status choices, in the order a book moves through them. */
  readonly statusOptions: readonly DropdownOption[] = [
    { value: 'notstarted', label: READING_STATUS_LABEL.notstarted, icon: 'circle-dashed' },
    { value: 'reading', label: READING_STATUS_LABEL.reading, icon: 'book-open' },
    { value: 'finished', label: READING_STATUS_LABEL.finished, icon: 'check-circle' },
  ];

  /** How far in, shown inside the Reading pill ("70%"). Only while reading:
   *  "Finished · 100%" would say the same thing twice. It is the edition on
   *  screen's when that one is being read, else the one read most recently. */
  readonly statusDetail = computed(() => {
    const book = this.store.book();
    const { status, percent, percentEditionId } = this.bookStatus();
    // A paper book without a page count has a page, not a percentage: its 1%
    // only exists so the Library counts it as Reading (issue #1091).
    if (book?.type === 'physical' && !book.pageCount && this.paperPage() !== null && percentEditionId === book.id) {
      return null;
    }
    return status === 'reading' && percent ? `${percent}%` : null;
  });

  // --- PAPER BOOK PAGE (issue #1091) ---

  /** True on a paper book's page, where the page stepper shows. */
  readonly isPaperBook = computed(() => this.store.book()?.type === 'physical');

  /** The page the reader is at, or null before one is set. */
  readonly paperPage = computed(() => readPaperPage(this.store.book()?.lastLocation));

  readonly paperPageCount = computed(() => {
    const count = this.store.book()?.pageCount;
    return count && count > 0 ? count : null;
  });

  stepPaperPage(delta: number): void {
    const current = this.paperPage();
    // The first + from no page opens the book at page 1.
    const next = current === null ? 1 : current + delta;
    this.store.setPaperPage(next);
  }

  /** Enter or blur in the page field. An empty or zero entry changes nothing. */
  commitPaperPage(input: HTMLInputElement): void {
    const typed = Number.parseInt(input.value, 10);
    if (!Number.isFinite(typed) || typed < 1) {
      input.value = this.paperPage()?.toString() ?? '';
      return;
    }
    if (typed !== this.paperPage()) this.store.setPaperPage(typed);
    // Show the clamped value even when the store kept the same page.
    input.value = this.paperPage()?.toString() ?? '';
  }

  // --- PHOTO QUOTE (issue #1292) ---

  /** The photographed page, while its quote is being picked. */
  readonly photoQuotePhoto = signal<Blob | null>(null);
  readonly photoQuoteLanguage = computed(() => tesseractLanguage(this.store.book()?.language));

  @ViewChild('photoQuoteInput') photoQuoteInput?: ElementRef<HTMLInputElement>;

  /** Opens the camera on a phone, the file picker elsewhere. */
  takePhotoQuote(): void {
    const input = this.photoQuoteInput?.nativeElement;
    if (!input) return;
    input.value = '';
    input.click();
  }

  onPhotoQuoteChosen(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    // Cleared at once, so picking the same photo again is still a change.
    input.value = '';
    if (file) this.photoQuotePhoto.set(file);
  }

  closePhotoQuote(): void {
    this.photoQuotePhoto.set(null);
  }

  savePhotoQuote(quote: PhotoQuote): void {
    this.store.addPhotoQuote(quote, () => this.photoQuotePhoto.set(null));
  }

  /** The current status as a dropdown value. */
  readonly statusValue = computed(() => {
    const modifier = this.readingStatus().modifier;
    return modifier === 'not-started' ? 'notstarted' : modifier;
  });

  selectStatusOption(target: string): void {
    if (target !== 'notstarted' && target !== 'reading' && target !== 'finished') return;
    if (target === this.statusValue()) return;
    // Only a reset discards something (the saved position), so only it asks.
    // Finished and Reading are one tap, as in the Library.
    if (target === 'notstarted') this.pendingStatus.set(target);
    else this.store.setFinished(target === 'finished');
  }

  /**
   * The reset question, for the shared confirm modal. A book with several
   * editions starts over in all of them, and the question says how many.
   */
  readonly statusConfirm = computed<StatusConfirmCopy | null>(() => {
    if (this.pendingStatus() !== 'notstarted') return null;
    const others = this.store.book()?.otherEditions?.length ?? 0;
    const place =
      others === 0
        ? 'clear your saved page position'
        : `clear your place in ${others === 1 ? 'both editions' : `all ${others + 1} editions`} of this book`;
    return {
      heading: 'Reset to not started?',
      description:
        `This will override your current progress and ${place}. ` +
        'The next time you open this book, it will start from the beginning.',
      confirmLabel: 'Reset progress',
      tone: 'danger',
    };
  });

  cancelStatusChange(): void {
    if (this.store.resettingProgress()) return;
    this.pendingStatus.set(null);
  }

  /** Stays open until the server answers; the store toasts the outcome. */
  confirmStatusChange(): void {
    if (this.pendingStatus() !== 'notstarted' || this.store.resettingProgress()) return;
    this.store.resetProgress(() => this.pendingStatus.set(null));
  }

  addNote(): void {
    const content = this.newNote().trim();
    if (!content) return;
    this.store.addNote(content, () => {
      this.newNote.set('');
      this.brain.refresh();
    });
  }

  /** True while the cover-remove question is up (asked through ConfirmModal). */
  readonly coverDeletePending = signal(false);

  // --- MANUAL WORK MEMBERSHIP (issue #143) ---
  // The surface itself is a modal (EditionsModal); this component owns the state
  // and the mutations, keeping work membership out of the Edit Book form, whose
  // Save/Cancel contract cannot express an immediate relationship change.

  /** Whether the editions & works modal is up. */
  readonly showEditionsModal = signal(false);

  /** Whether the hero Edit chooser is open. */

  /**
   * A name for an edition that arrived without one. Defensive only: every server
   * response carries `title` now, but a cached/older payload must not render the
   * detach row as a blank line.
   */
  private getEditionTitleOrFallback(edition: EditionSummaryDto): string {
    const format = edition.format?.trim().toUpperCase();
    return format ? `${UNKNOWN_BOOK} (${format})` : UNKNOWN_BOOK;
  }

  /** Search text for the "link another book" picker. */
  readonly linkQuery = signal('');

  /**
   * Every book in the current work, current book included.
   *
   * Each sibling is named from its OWN summary (`title`/`author`), which is why
   * those fields were added to `EditionSummaryDto`: an unlink row reading the
   * current book's title would name the wrong book as the one being detached.
   */
  readonly workMembers = computed<WorkMember[]>(() => {
    const book = this.store.book();
    if (!book) return [];

    const current: WorkMember = {
      id: book.id,
      title: book.title,
      author: book.author,
      format: this.getEditionLabel(book),
      formatIcon: this.getEditionConcept(book.type, this.getEditionLabel(book)),
    };
    const others = (book.otherEditions ?? []).map<WorkMember>((edition) => ({
      id: edition.id,
      title: edition.title?.trim() || this.getEditionTitleOrFallback(edition),
      author: edition.author ?? null,
      format: this.getSummaryEditionLabel(edition),
      formatIcon: this.getEditionConcept(edition.type, this.getSummaryEditionLabel(edition)),
    }));
    return [current, ...others];
  });

  /**
   * The hero edition switcher, the one place to switch edition (issue #1062)
   * and the only list of the work's editions on the page. Empty for a
   * single-edition book, so nothing renders. Labels and order: #1086.
   */
  readonly editionSwitchOptions = computed(() => buildEditionSwitchOptions(this.store.book()));

  // --- EDITION RATING AND REVIEW ---
  // The rating, favourite and review are the book's, so they do not move when
  // another edition is opened. An edition may hold a rating of its own (the
  // narration, for an audiobook): a quiet exception that appears on the page
  // only once it is set, or asked for from the edition menu.

  /** The edition whose rating row was asked for and has no rating yet. */
  private readonly ratingRowFor = signal<string | null>(null);

  /** The row's label: "Narration" or "This edition". */
  readonly editionRatingLabel = computed(() => {
    const noun = editionRatingNoun(this.store.book()?.type ?? '');
    return noun === 'narration' ? 'Narration' : 'This edition';
  });

  /** The row under the verdict: once rated, it stays; asked for, it stays until the page is left. */
  readonly editionRatingShown = computed(() => {
    const book = this.store.book();
    return (
      this.editionSwitchOptions().length > 0 &&
      !!book &&
      ((book.editionRating ?? 0) > 0 || this.ratingRowFor() === book.id)
    );
  });

  /** The menu's last row, or none for a single-edition book. */
  readonly editionRatingAction = computed<EditionRatingAction | null>(() => {
    const book = this.store.book();
    if (!book || this.editionSwitchOptions().length === 0) return null;
    const audio = this.isAudioBook(book);
    if ((book.editionRating ?? 0) > 0) {
      return { label: audio ? 'Clear narration rating' : 'Clear edition rating' };
    }
    return { label: audio ? 'Rate the narration' : 'Rate this edition' };
  });

  /** The menu's rating row: reveals the stars, or clears the rating and hides them. */
  editionRatingActionChosen(): void {
    const book = this.store.book();
    if (!book) return;
    if ((book.editionRating ?? 0) > 0) {
      this.store.rateEdition(0);
      this.ratingRowFor.set(null);
    } else {
      this.ratingRowFor.set(book.id);
    }
  }

  onRateEdition(rating: number): void {
    this.store.rateEdition(rating);
  }

  /**
   * Reviews that editions keep of their own, which happens when books that
   * disagreed were grouped. Every edition's, the one on screen included.
   */
  readonly editionReviews = computed(() => {
    const book = this.store.book();
    if (!book) return [];
    const others = book.otherEditions ?? [];
    const formats = new Map<string, BookFormat>([
      [book.id, formatOf(book)],
      ...others.map((edition): [string, BookFormat] => [edition.id, formatOf(edition)]),
    ]);
    const reviews = [
      { id: book.id, text: book.editionReview ?? '' },
      ...others.map((edition) => ({ id: edition.id, text: edition.personalReview ?? '' })),
    ];
    return reviews
      .filter((review) => review.text.trim() !== '')
      .map((review) => ({ ...review, ...formats.get(review.id)! }));
  });

  removeEditionReview(editionId: string): void {
    this.store.removeEditionReview(editionId);
  }

  /**
   * Candidates with their format named. A work of several editions has no one
   * format, so it keeps its "N editions" count instead.
   */
  readonly linkCandidates = computed<LinkCandidate[]>(() =>
    this.store.linkCandidates().map((candidate) => {
      if (!candidate.type || (candidate.editionCount ?? 1) > 1) return candidate;
      const format = this.buildEditionLabel(candidate.type, candidate.fileName);
      return { ...candidate, format, formatIcon: this.getEditionConcept(candidate.type, format) };
    }),
  );

  /** Open the membership modal, loading candidates for the picker. */
  openEditions(): void {
    this.showEditionsModal.set(true);
    // Candidates are fetched on open, so a book-detail visit never costs a
    // library query it does not need.
    this.store.loadLinkCandidates(this.linkQuery());
  }

  closeEditions(): void {
    if (this.store.linkingWork()) return;
    this.showEditionsModal.set(false);
  }

  onLinkQueryChange(query: string): void {
    this.linkQuery.set(query);
    this.store.loadLinkCandidates(query);
  }

  /** A link always merges whole groups, so confirm what will actually happen. */
  askLink(candidate: LinkableBookDto): void {
    const current = this.store.book();
    if (!current) return;

    const groupSize = candidate.editionCount ?? 1;
    const merge = groupSize > 1 || (current.editionCount ?? 1) > 1;
    this.pendingWorkAction.set({
      kind: 'link',
      bookId: candidate.id,
      label: candidate.title,
      merge,
    });
  }

  /** Splitting a book out of a group is worth confirming too. */
  askUnlink(member: WorkMember): void {
    this.pendingWorkAction.set({ kind: 'unlink', bookId: member.id, label: member.title, merge: false });
  }

  cancelWorkAction(): void {
    if (this.store.linkingWork()) return;
    this.pendingWorkAction.set(null);
  }

  confirmWorkAction(): void {
    const action = this.pendingWorkAction();
    if (!action || this.store.linkingWork()) return;

    const done = () => this.pendingWorkAction.set(null);
    if (action.kind === 'link') this.store.linkToWork(action.bookId, done);
    else this.store.unlinkFromWork(action.bookId, done);
  }

  /** Composed here so the shared confirm modal stays generic. */
  workActionHeading = computed(() => {
    const action = this.pendingWorkAction();
    if (!action) return '';
    return action.kind === 'link'
      ? `Link “${action.label}” to this edition set?`
      : `Unlink “${action.label}”?`;
  });

  workActionDescription = computed(() => {
    const action = this.pendingWorkAction();
    if (!action) return '';
    if (action.kind === 'unlink') {
      return (
        'This edition becomes a book of its own. It keeps its file, reading progress, notes and collections, ' +
        "and gets a copy of this book's rating, favourite and review, unless it has a rating or review of its own."
      );
    }
    const combined =
      "Their rating, favourite and review are combined into the book's; a rating or review that differs stays with its edition.";
    return action.merge
      ? 'Both edition sets and everything already grouped with them become one book. ' +
          `Every edition keeps its file, progress, notes and collections. ${combined}`
      : `Both books become editions of one book. Each keeps its file, progress, notes and collections. ${combined}`;
  });

  // --- File Actions ---

  onCoverSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) this.store.uploadCover(file);
  }

  deleteCover() {
    if (!this.store.book()?.coverUrl) return;
    this.coverDeletePending.set(true);
  }

  cancelCoverDelete(): void {
    if (this.store.removingCover()) return;
    this.coverDeletePending.set(false);
  }

  confirmCoverDelete(): void {
    if (!this.coverDeletePending()) return;
    this.store.deleteCover(() => this.coverDeletePending.set(false));
  }

  onFileUploadSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (file && !this.store.isImporting()) this.store.uploadFile(file);
  }

  openDeleteConfirm(): void {
    if (this.deleting()) return;
    this.closeMetadataModal();
    this.showDeleteConfirm.set(true);
  }

  cancelDeleteConfirm(): void {
    if (this.deleting()) return;
    this.showDeleteConfirm.set(false);
  }

  /**
   * Deletes the current book after an explicit in-app confirmation, then returns to
   * the Library. A local pending signal guards against duplicate submissions.
   */
  confirmDeleteBook(): void {
    const b = this.store.book();
    if (!b || this.deleting()) return;

    this.deleting.set(true);
    this.booksService.delete(b.id).subscribe({
      next: () => {
        this.toast.success(bookDeletedToast(b.title, this.isLinkedExternally()));
        this.showDeleteConfirm.set(false);
        void this.router.navigate(['/library']);
      },
      error: () => {
        this.deleting.set(false);
        this.toast.error('Could not delete the book');
      },
    });
  }

  deleteBook(): void {
    this.openDeleteConfirm();
  }

  // --- Navigation & Helpers ---

  switchEdition(id: string): void {
    this.preferences.setActiveEditionId(this.store.book()?.workId, id);
    void this.router.navigate(['/library', id]);
  }

  getEditionLabel(book: Book): string {
    return this.buildEditionLabel(
      book.type,
      book.fileName,
      undefined,
      book.duration,
      book.narrator,
      book.edition,
    );
  }

  getSummaryEditionLabel(edition: EditionSummaryDto): string {
    return this.buildEditionLabel(
      edition.type,
      edition.fileName,
      edition.format,
      edition.duration,
      edition.narrator,
      edition.edition,
    );
  }

  /** The format concept for a member row's icon, read from the same label the row shows. */
  private getEditionConcept(type: string, label: string): WorkMember['formatIcon'] {
    const normalizedType = type.toLowerCase();
    if (normalizedType === 'audiobook' || normalizedType === 'audio') return 'audiobook';
    if (normalizedType === 'physical') return 'physical';
    return label.startsWith('PDF') ? 'pdf' : 'ebook';
  }

  private buildEditionLabel(
    type: string,
    fileName?: string | null,
    format?: string,
    duration?: string | null,
    narrator?: string | null,
    edition?: string | null,
  ): string {
    const isAudio = formatOf({ type }).concept === 'audiobook';
    const label = formatOf({ type, format, fileName }).format;

    const details = isAudio
      ? [duration, narrator, edition]
      : [edition];
    const detailText = details.filter((detail): detail is string => !!detail?.trim()).join(', ');

    return detailText ? `${label} (${detailText})` : label;
  }

  /** The review is only collapsible once it has been measured as overflowing. */
  readonly isReviewCollapsible = computed(() => this.reviewOverflows() === true);

  /** The clamp applies only while a long review is collapsed. */
  readonly isReviewClamped = computed(() => this.reviewOverflows() === true && !this.isReviewExpanded());

  /**
   * Expand/collapse a long review. Local presentation state ONLY — the stored
   * review is never truncated, rewritten or refetched (issue #159).
   */
  toggleReview(): void {
    if (!this.isReviewCollapsible()) return;
    this.isReviewExpanded.update((expanded) => !expanded);
  }

  toggleDescription() {
    this.isDescriptionExpanded.update((v) => !v);
  }
  openMetadataModal() {
    if (this.store.book()) this.showMetadataModal.set(true);
  }
  closeMetadataModal() {
    this.showMetadataModal.set(false);
  }

  onBookUpdated(updatedBook: Book): void {
    this.store.book.set(updatedBook);
  }

  /**
   * What a multi-track audiobook's download is: every track in one ZIP. Shown
   * on the button so a several-hundred-megabyte download is not a surprise.
   * Null for a single-file book, whose button needs no explanation.
   */
  downloadHint(book: Book | null): string | null {
    const tracks = book?.tracks;
    if (!tracks?.length) return null;
    const bytes = tracks.reduce((total, track) => total + track.bytes, 0);
    return `${tracks.length} tracks · ${formatBytes(bytes)}`;
  }

  downloadFile() {
    const id = this.store.book()?.id;
    if (id) window.open(`/api/books/${id}/file/download`, '_blank');
  }

  openReader() {
    const id = this.store.book()?.id;
    if (id) this.router.navigate(['/read', id]);
  }

  triggerCoverPicker() {
    this.coverInput?.nativeElement.click();
  }

  triggerFilePicker() {
    if (this.store.isImporting() || this.store.uploadingFile()) return;
    this.fileInput?.nativeElement.click();
  }

  getCollectionName(id: string | null | undefined): string {
    if (!id) return '—';
    const col = this.store.collections().find((c) => c.id === id);
    return col ? col.name : '—';
  }

  /**
   * Every collection this book belongs to, by name. Membership is the set; a
   * collection that no longer exists is skipped rather than rendered as a dash.
   */
  getCollectionNames(book: Book): string[] {
    const byId = new Map(this.store.collections().map((c) => [c.id, c.name]));
    return (book.collectionIds ?? [])
      .map((id) => byId.get(id))
      .filter((name): name is string => !!name);
  }

  isAudioBook(book: Book | null): boolean {
    return book?.type === 'audiobook';
  }
}
